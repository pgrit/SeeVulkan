using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.Input;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;

void CheckResult(Result result, string methodName)
{
    if (result != Result.Success)
        throw new Exception($"{methodName} failed with return code {result}");
}

var options = WindowOptions.DefaultVulkan with {
    Size = new(800, 600),
    Title = "Silk.NET Ray tracing",
};

var window = Window.Create(options);
window.Initialize();
if (window.VkSurface is null)
    throw new Exception("Windowing platform doesn't support Vulkan.");

window.Load += () => {
    var input = window.CreateInput();
    for (int i = 0; i < input.Keyboards.Count; i++)
    {
        input.Keyboards[i].KeyDown += (kbd, key, _) => {
            if (key == Key.Escape)
            {
                window.Close();
            }
        };
    }
};
window.Update += _ => { };
window.Render += _ => { };
window.FramebufferResize += newSize => { };

var vk = Vk.GetApi();

unsafe Instance CreateInstance()
{
    ApplicationInfo appInfo = new()
    {
        SType = StructureType.ApplicationInfo,
        PApplicationName = (byte*)Marshal.StringToHGlobalAnsi("Silk.NET Ray tracing"),
        ApplicationVersion = new Version32(1, 0, 0),
        PEngineName = (byte*)Marshal.StringToHGlobalAnsi("Silk.NET Ray tracing"),
        EngineVersion = new Version32(1, 0, 0),
        ApiVersion = Vk.Version13
    };

    InstanceCreateInfo createInfo = new()
    {
        SType = StructureType.InstanceCreateInfo,
        PApplicationInfo = &appInfo,
        EnabledLayerCount = 0,
    };
    createInfo.PpEnabledExtensionNames = window.VkSurface.GetRequiredExtensions(out createInfo.EnabledExtensionCount);

    var result = vk.CreateInstance(createInfo, null, out var instance);
    if (result != Result.Success)
        throw new Exception($"CreateInstance failed with code {result}");

    Marshal.FreeHGlobal((IntPtr)appInfo.PApplicationName);
    Marshal.FreeHGlobal((IntPtr)appInfo.PEngineName);

    return instance;
}
var instance = CreateInstance();

unsafe (KhrSurface, SurfaceKHR) CreateWindowSurface()
{
    if (!vk.TryGetInstanceExtension<KhrSurface>(instance, out var khrSurface))
        throw new NotSupportedException("KHR_surface extension not found.");

    var surface = window.VkSurface.Create<AllocationCallbacks>(instance.ToHandle(), null).ToSurface();

    return (khrSurface, surface);
}
var (khrSurface, surface) = CreateWindowSurface();

unsafe uint? FindQueueFamilyIndex(PhysicalDevice device)
{
    uint queueFamilyCount = 0;
    vk.GetPhysicalDeviceQueueFamilyProperties(device, ref queueFamilyCount, null);

    var queueFamilies = new QueueFamilyProperties[queueFamilyCount];
    fixed (QueueFamilyProperties* queueFamiliesPtr = queueFamilies)
        vk.GetPhysicalDeviceQueueFamilyProperties(device, ref queueFamilyCount, queueFamiliesPtr);

    for (uint i = 0; i < queueFamilies.Length; ++i)
    {
        khrSurface.GetPhysicalDeviceSurfaceSupport(device, i, surface, out var presentSupport);

        var flags = queueFamilies[i].QueueFlags;
        if (flags.HasFlag(QueueFlags.ComputeBit) && flags.HasFlag(QueueFlags.GraphicsBit) && presentSupport)
            return i;
    }
    return null;
}

PhysicalDevice InitPhysicalDevice()
{
    // Pick the first device that supports the desired queue
    foreach (var device in vk.GetPhysicalDevices(instance))
        if (FindQueueFamilyIndex(device).HasValue) return device;

    throw new Exception("No suitable GPU found");
}
var physicalDevice = InitPhysicalDevice();

unsafe (Device, Queue) CreateLogicalDevice()
{
    uint queueIdx = FindQueueFamilyIndex(physicalDevice).Value;

    float queuePriority = 1.0f;
    DeviceQueueCreateInfo queueCreateInfo = new()
    {
        SType = StructureType.DeviceQueueCreateInfo,
        QueueFamilyIndex = queueIdx,
        QueueCount = 1,
        PQueuePriorities = &queuePriority
    };

    PhysicalDeviceFeatures deviceFeatures = new();

    string[] extensions = [
        KhrSwapchain.ExtensionName
    ];
    DeviceCreateInfo createInfo = new()
    {
        SType = StructureType.DeviceCreateInfo,
        QueueCreateInfoCount = 1,
        PQueueCreateInfos = &queueCreateInfo,
        PEnabledFeatures = &deviceFeatures,
        EnabledLayerCount = 0,
        EnabledExtensionCount = (uint)extensions.Length,
        PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions)
    };

    var retcode = vk.CreateDevice(physicalDevice, in createInfo, null, out var device);
    if (retcode != Result.Success)
    {
        throw new Exception($"CreateDevice failed with {retcode}");
    }

    vk.GetDeviceQueue(device, queueIdx, 0, out var queue);

    SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);

    return (device, queue);
}
var (device, queue) = CreateLogicalDevice();

unsafe (KhrSwapchain, SwapchainKHR, Image[], Format, Extent2D) CreateSwapChain()
{
    khrSurface.GetPhysicalDeviceSurfaceCapabilities(physicalDevice, surface, out var capabilities);

    uint formatCount = 0;
    khrSurface.GetPhysicalDeviceSurfaceFormats(physicalDevice, surface, ref formatCount, null);

    var formats = new SurfaceFormatKHR[formatCount];
    if (formatCount != 0)
        fixed (SurfaceFormatKHR* formatsPtr = formats)
            khrSurface.GetPhysicalDeviceSurfaceFormats(physicalDevice, surface, ref formatCount, formatsPtr);

    uint presentModeCount = 0;
    khrSurface.GetPhysicalDeviceSurfacePresentModes(physicalDevice, surface, ref presentModeCount, null);
    var presentModes = new PresentModeKHR[presentModeCount];
    if (presentModeCount != 0)
        fixed (PresentModeKHR* formatsPtr = presentModes)
            khrSurface.GetPhysicalDeviceSurfacePresentModes(physicalDevice, surface, ref presentModeCount, formatsPtr);

    var surfaceFormat = formats.FirstOrDefault(
        format => format.Format == Format.B8G8R8A8Srgb && format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr,
        formats[0]
    );

    var presentMode = presentModes.FirstOrDefault(
        mode => mode == PresentModeKHR.MailboxKhr,
        PresentModeKHR.FifoKhr
    );

    var extent = capabilities.CurrentExtent;
    if (capabilities.CurrentExtent.Width == uint.MaxValue)
    {
        extent = new()
        {
            Width = Math.Clamp((uint)window.FramebufferSize.X, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
            Height = Math.Clamp((uint)window.FramebufferSize.Y, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height)
        };
    }

    var imageCount = Math.Min(capabilities.MinImageCount + 1, capabilities.MaxImageCount);

    SwapchainCreateInfoKHR swapchainCreateInfo = new()
    {
        SType = StructureType.SwapchainCreateInfoKhr,
        Surface = surface,

        MinImageCount = imageCount,
        ImageFormat = surfaceFormat.Format,
        ImageColorSpace = surfaceFormat.ColorSpace,
        ImageExtent = extent,
        ImageArrayLayers = 1,
        ImageUsage = ImageUsageFlags.ColorAttachmentBit,
        ImageSharingMode = SharingMode.Exclusive,
        PreTransform = capabilities.CurrentTransform,
        CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
        PresentMode = presentMode,
        Clipped = true,
        OldSwapchain = default
    };

    if (!vk.TryGetDeviceExtension(instance, device, out KhrSwapchain khrSwapChain))
        throw new NotSupportedException("VK_KHR_swapchain extension not found.");

    CheckResult(khrSwapChain.CreateSwapchain(device, swapchainCreateInfo, null, out var swapChain), nameof(khrSwapChain.CreateSwapchain));

    khrSwapChain.GetSwapchainImages(device, swapChain, ref imageCount, null);
    var swapChainImages = new Image[imageCount];
    fixed (Image* swapChainImagesPtr = swapChainImages)
        khrSwapChain.GetSwapchainImages(device, swapChain, ref imageCount, swapChainImagesPtr);

    var swapChainImageFormat = surfaceFormat.Format;
    var swapChainExtent = extent;

    return (khrSwapChain, swapChain, swapChainImages, swapChainImageFormat, swapChainExtent);
}
var (khrSwapChain, swapChain, swapChainImages, swapChainImageFormat, swapChainExtent) = CreateSwapChain();

unsafe ImageView[] CreateImageViews()
{
    var swapChainImageViews = new ImageView[swapChainImages.Length];

    for (int i = 0; i < swapChainImages.Length; i++)
    {
        ImageViewCreateInfo createInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = swapChainImages[i],
            ViewType = ImageViewType.Type2D,
            Format = swapChainImageFormat,
            Components =
            {
                R = ComponentSwizzle.Identity,
                G = ComponentSwizzle.Identity,
                B = ComponentSwizzle.Identity,
                A = ComponentSwizzle.Identity,
            },
            SubresourceRange =
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            }

        };

        CheckResult(vk.CreateImageView(device, createInfo, null, out swapChainImageViews[i]), nameof(vk.CreateImageView));
    }

    return swapChainImageViews;
}
var swapChainImageViews = CreateImageViews();

unsafe void CreateGraphicsPipeline()
{
    ShaderModule CreateShaderModule(byte[] code)
    {
        ShaderModuleCreateInfo createInfo = new()
        {
            SType = StructureType.ShaderModuleCreateInfo,
            CodeSize = (nuint)code.Length,
        };

        ShaderModule shaderModule;

        fixed (byte* codePtr = code)
        {
            createInfo.PCode = (uint*)codePtr;

            if (vk.CreateShaderModule(device, createInfo, null, out shaderModule) != Result.Success)
            {
                throw new Exception();
            }
        }

        return shaderModule;

    }

    byte[] CompileShader(string code, string ext)
    {
        string glslcExe = "glslc"; // TODO assumes it is in the path - better solution?
        string infile = $"{Guid.NewGuid()}.{ext}";
        File.WriteAllText(infile, code);
        string outfile = $"{Guid.NewGuid()}.spv";
        System.Diagnostics.Process.Start(glslcExe, [infile, "-o", outfile]).WaitForExit();
        var bytes = File.ReadAllBytes(outfile);
        File.Delete(infile);
        File.Delete(outfile);
        return bytes;
    }

    var vertShaderModule = CreateShaderModule(CompileShader(
        """
        #version 450

        layout(location = 0) out vec3 fragColor;

        vec2 positions[3] = vec2[](
            vec2(0.0, -0.5),
            vec2(0.5, 0.5),
            vec2(-0.5, 0.5)
        );

        vec3 colors[3] = vec3[](
            vec3(1.0, 0.0, 0.0),
            vec3(0.0, 1.0, 0.0),
            vec3(0.0, 0.0, 1.0)
        );

        void main() {
            gl_Position = vec4(positions[gl_VertexIndex], 0.0, 1.0);
            fragColor = colors[gl_VertexIndex];
        }
        """, "vert"
    ));

    var fragShaderModule = CreateShaderModule(CompileShader(
        """
        #version 450

        layout(location = 0) in vec3 fragColor;

        layout(location = 0) out vec4 outColor;

        void main() {
            outColor = vec4(fragColor, 1.0);
        }
        """, "frag"
    ));

    PipelineShaderStageCreateInfo vertShaderStageInfo = new()
    {
        SType = StructureType.PipelineShaderStageCreateInfo,
        Stage = ShaderStageFlags.VertexBit,
        Module = vertShaderModule,
        PName = (byte*)SilkMarshal.StringToPtr("main")
    };

    PipelineShaderStageCreateInfo fragShaderStageInfo = new()
    {
        SType = StructureType.PipelineShaderStageCreateInfo,
        Stage = ShaderStageFlags.FragmentBit,
        Module = fragShaderModule,
        PName = (byte*)SilkMarshal.StringToPtr("main")
    };

    var shaderStages = stackalloc[]
    {
        vertShaderStageInfo,
        fragShaderStageInfo
    };

    vk.DestroyShaderModule(device, fragShaderModule, null);
    vk.DestroyShaderModule(device, vertShaderModule, null);

    SilkMarshal.Free((nint)vertShaderStageInfo.PName);
    SilkMarshal.Free((nint)fragShaderStageInfo.PName);
}
CreateGraphicsPipeline();

window.Run();

unsafe {
    foreach (var imageView in swapChainImageViews)
        vk.DestroyImageView(device, imageView, null);
    khrSwapChain.DestroySwapchain(device, swapChain, null);
    vk.DestroyDevice(device, null);
    khrSurface.DestroySurface(instance, surface, null);
    vk.DestroyInstance(instance, null);
}
vk.Dispose();

window.Dispose();
