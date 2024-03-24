using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Vulkan.Extensions;
using Silk.NET.Windowing;

using Buffer = Silk.NET.Vulkan.Buffer;
using Silk.NET.Vulkan.Extensions.EXT;

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

window.Initialize();
if (window.VkSurface is null)
    throw new Exception("Windowing platform doesn't support Vulkan.");

var vk = Vk.GetApi();

string[] validationLayers = [
    "VK_LAYER_KHRONOS_validation"
];
const bool EnableValidationLayers = true;

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

    string[] extensions = SilkMarshal.PtrToStringArray(
        (nint)window.VkSurface.GetRequiredExtensions(out var windowExtCount), (int)windowExtCount);
    extensions = extensions.Append(ExtDebugUtils.ExtensionName).ToArray();

    InstanceCreateInfo createInfo = new()
    {
        SType = StructureType.InstanceCreateInfo,
        PApplicationInfo = &appInfo,
        EnabledLayerCount = 0,
        PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions),
        EnabledExtensionCount = (uint)extensions.Length,
    };

    uint DebugCallback(DebugUtilsMessageSeverityFlagsEXT severity, DebugUtilsMessageTypeFlagsEXT typeFlags, DebugUtilsMessengerCallbackDataEXT* pCallbackData, void* pUserData)
    {
        switch (severity)
        {
            case DebugUtilsMessageSeverityFlagsEXT.WarningBitExt:
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write("[WARN] ");
                break;

            case DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt:
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("[ERROR] ");
                break;

            default:
                return Vk.False;
        }
        Console.WriteLine(Marshal.PtrToStringAnsi((nint)pCallbackData->PMessage));
        Console.ResetColor();
        return Vk.False;
    }

    if (EnableValidationLayers)
    {
        createInfo.EnabledLayerCount = (uint)validationLayers.Length;
        createInfo.PpEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(validationLayers);

        DebugUtilsMessengerCreateInfoEXT debugCreateInfo = new()
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt
                | DebugUtilsMessageSeverityFlagsEXT.WarningBitExt
                | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt
                | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt
                | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt,
            PfnUserCallback = (DebugUtilsMessengerCallbackFunctionEXT)DebugCallback
        };

        createInfo.PNext = &debugCreateInfo;
    }

    CheckResult(vk.CreateInstance(createInfo, null, out var instance), nameof(vk.CreateInstance));

    SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);
    Marshal.FreeHGlobal((IntPtr)appInfo.PApplicationName);
    Marshal.FreeHGlobal((IntPtr)appInfo.PEngineName);
    if (EnableValidationLayers)
        SilkMarshal.Free((nint)createInfo.PpEnabledLayerNames);

    if (EnableValidationLayers)
    {
        if (!vk.TryGetInstanceExtension(instance, out ExtDebugUtils debugUtils))
            return instance;

        DebugUtilsMessengerCreateInfoEXT dbgCreateInfo = new()
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt
                | DebugUtilsMessageSeverityFlagsEXT.WarningBitExt
                | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt
                | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt
                | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt,
            PfnUserCallback = (DebugUtilsMessengerCallbackFunctionEXT)DebugCallback
        };

        CheckResult(debugUtils.CreateDebugUtilsMessenger(instance, dbgCreateInfo, null, out var debugMessenger), nameof(debugUtils.CreateDebugUtilsMessenger));
    }

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

unsafe (uint Graphics, uint Present) FindQueueFamilyIndex(PhysicalDevice device)
{
    uint queueFamilyCount = 0;
    vk.GetPhysicalDeviceQueueFamilyProperties(device, ref queueFamilyCount, null);

    var queueFamilies = new QueueFamilyProperties[queueFamilyCount];
    fixed (QueueFamilyProperties* queueFamiliesPtr = queueFamilies)
        vk.GetPhysicalDeviceQueueFamilyProperties(device, ref queueFamilyCount, queueFamiliesPtr);

    uint? presentIdx = null;
    uint? graphicsIdx = null;
    for (uint i = 0; i < queueFamilies.Length; ++i)
    {
        khrSurface.GetPhysicalDeviceSurfaceSupport(device, i, surface, out var presentSupport);
        if (presentSupport)
            presentIdx = i;

        var flags = queueFamilies[i].QueueFlags;
        if (flags.HasFlag(QueueFlags.ComputeBit) && flags.HasFlag(QueueFlags.GraphicsBit))
            graphicsIdx = i;

        if (presentIdx.HasValue && graphicsIdx.HasValue)
            break;
    }
    return (graphicsIdx.Value, presentIdx.Value);
}

unsafe PhysicalDevice InitPhysicalDevice()
{
    var devices = vk.GetPhysicalDevices(instance);

    bool foundDiscreteGpu = false;
    ulong bestSize = 0;
    PhysicalDevice bestDevice = new();
    foreach (var device in devices)
    {
        vk.GetPhysicalDeviceProperties(device, out var props);

        // Only consider integrated GPUs if no discrete one has been found
        if (props.DeviceType == PhysicalDeviceType.DiscreteGpu)
            foundDiscreteGpu = true;
        else if (foundDiscreteGpu)
            continue;

        // Pick the device with most memory (heuristic to guess the fastest one)
        vk.GetPhysicalDeviceMemoryProperties(device, out var memoryProps);
        for (int i = 0; i < memoryProps.MemoryHeapCount; ++i)
        {
            if (memoryProps.MemoryHeaps[i].Flags.HasFlag(MemoryHeapFlags.DeviceLocalBit))
            {
                if (memoryProps.MemoryHeaps[i].Size > bestSize)
                {
                    bestSize = memoryProps.MemoryHeaps[i].Size;
                    bestDevice = device;
                }
            }
        }
    }

    return bestDevice;
}
var physicalDevice = InitPhysicalDevice();
vk.GetPhysicalDeviceMemoryProperties(physicalDevice, out var memoryProperties);

unsafe (Device, Queue, Queue) CreateLogicalDevice()
{
    var (graphicsIdx, presentIdx) = FindQueueFamilyIndex(physicalDevice);

    uint[] uniqueQueueFamilies = new[] { graphicsIdx, presentIdx }.Distinct().ToArray();

    using var mem = GlobalMemory.Allocate(uniqueQueueFamilies.Length * sizeof(DeviceQueueCreateInfo));
    var queueCreateInfos = (DeviceQueueCreateInfo*)Unsafe.AsPointer(ref mem.GetPinnableReference());

    float queuePriority = 1.0f;
    for (int i = 0; i < uniqueQueueFamilies.Length; i++)
    {
        queueCreateInfos[i] = new()
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = uniqueQueueFamilies[i],
            QueueCount = 1,
            PQueuePriorities = &queuePriority
        };
    }

    string[] extensions = [
        KhrSwapchain.ExtensionName,

        // Extensions for ray tracing
        KhrAccelerationStructure.ExtensionName,
        KhrRayTracingPipeline.ExtensionName,
        KhrBufferDeviceAddress.ExtensionName,
        KhrDeferredHostOperations.ExtensionName,
        "VK_EXT_descriptor_indexing",
        "VK_KHR_spirv_1_4",
        "VK_KHR_shader_float_controls",
    ];


    PhysicalDeviceBufferDeviceAddressFeatures addrFeatures = new()
    {
        SType = StructureType.PhysicalDeviceBufferDeviceAddressFeatures,
        BufferDeviceAddress = true
    };

    PhysicalDeviceRayTracingPipelineFeaturesKHR enabledRayTracingPipelineFeatures = new()
    {
        SType = StructureType.PhysicalDeviceRayTracingPipelineFeaturesKhr,
        RayTracingPipeline = true,
        PNext = &addrFeatures
    };

    PhysicalDeviceAccelerationStructureFeaturesKHR enabledAccelerationStructureFeatures = new()
    {
        SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr,
        AccelerationStructure = true,
        PNext = &enabledRayTracingPipelineFeatures
    };

    PhysicalDeviceVulkan13Features vulkan13Features = new()
    {
        SType = StructureType.PhysicalDeviceVulkan13Features,
        PNext = &enabledAccelerationStructureFeatures,
        Maintenance4 = true
    };

    PhysicalDeviceFeatures2 physicalDeviceFeatures2 = new()
    {
        SType = StructureType.PhysicalDeviceFeatures2,
        PNext = &vulkan13Features,
        Features = new PhysicalDeviceFeatures()
    };

    DeviceCreateInfo createInfo = new()
    {
        SType = StructureType.DeviceCreateInfo,
        QueueCreateInfoCount = (uint)uniqueQueueFamilies.Length,
        PQueueCreateInfos = queueCreateInfos,

        PEnabledFeatures = null,
        PNext = &physicalDeviceFeatures2,
        EnabledLayerCount = 0,

        EnabledExtensionCount = (uint)extensions.Length,
        PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions)
    };

    if (EnableValidationLayers)
    {
        createInfo.EnabledLayerCount = (uint)validationLayers.Length;
        createInfo.PpEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(validationLayers);
    }

    CheckResult(vk.CreateDevice(physicalDevice, in createInfo, null, out var device), nameof(vk.CreateDevice));

    vk.GetDeviceQueue(device, graphicsIdx, 0, out var graphicsQueue);
    vk.GetDeviceQueue(device, presentIdx, 0, out var presentQueue);

    SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);
    if (EnableValidationLayers)
        SilkMarshal.Free((nint)createInfo.PpEnabledLayerNames);

    return (device, graphicsQueue, presentQueue);
}
var (device, graphicsQueue, presentQueue) = CreateLogicalDevice();

unsafe (KhrSwapchain, SwapchainKHR, Image[], Format, ColorSpaceKHR, Extent2D) CreateSwapChain()
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

    // TODO how to only use HDR if HDR mode is on (possibly Windows specific)
    (Format Format, ColorSpaceKHR ColorSpace)[] preferredFormats = [
        (Format.R32G32B32A32Sfloat, ColorSpaceKHR.SpaceExtendedSrgbLinearExt),
        (Format.R16G16B16A16Sfloat, ColorSpaceKHR.SpaceExtendedSrgbLinearExt),
        (Format.B8G8R8A8Srgb, ColorSpaceKHR.SpaceSrgbNonlinearKhr)
    ];

    SurfaceFormatKHR SelectFormat()
    {
        for (int i = 0; i < preferredFormats.Length; ++i)
        {
            foreach (var f in formats)
            {
                if (f.Format == preferredFormats[i].Format && f.ColorSpace == preferredFormats[i].ColorSpace)
                    return f;
            }
        }
        return formats[0];
    }
    var surfaceFormat = SelectFormat();

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
        ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit,
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

    return (khrSwapChain, swapChain, swapChainImages, swapChainImageFormat, surfaceFormat.ColorSpace, swapChainExtent);
}
var (khrSwapChain, swapChain, swapChainImages, swapChainImageFormat, swapChainColorSpace, swapChainExtent) = CreateSwapChain();

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

unsafe ShaderModule CreateShaderModule(byte[] code)
{
    fixed (byte* codePtr = code)
    {
        ShaderModuleCreateInfo createInfo = new()
        {
            SType = StructureType.ShaderModuleCreateInfo,
            CodeSize = (nuint)code.Length,
            PCode = (uint*)codePtr
        };
        CheckResult(vk.CreateShaderModule(device, createInfo, null, out var shaderModule), nameof(vk.CreateShaderModule));
        return shaderModule;
    }
}

byte[] CompileShader(string code, string ext)
{
    var guid = Guid.NewGuid();
    string infile = $"{guid}.{ext}";
    string outfile = $"{guid}.spv";

    File.WriteAllText(infile, code);

    byte[] bytes = null;
    try
    {
        string glslcExe = "glslc"; // TODO assumes it is in the path - better solution?
        var p = System.Diagnostics.Process.Start(glslcExe, ["--target-env=vulkan1.3", infile, "-o", outfile]);
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new Exception("glslc shader compilation failed");
        bytes = File.ReadAllBytes(outfile);
    }
    finally
    {
        if (File.Exists(infile)) File.Delete(infile);
        if (File.Exists(outfile)) File.Delete(outfile);
    }

    return bytes;
}

unsafe CommandPool CreateCommandPool()
{
    uint queueIdx = FindQueueFamilyIndex(physicalDevice).Graphics;

    CommandPoolCreateInfo poolInfo = new()
    {
        SType = StructureType.CommandPoolCreateInfo,
        QueueFamilyIndex = queueIdx,
    };

    CheckResult(vk.CreateCommandPool(device, poolInfo, null, out var commandPool), nameof(vk.CreateCommandPool));
    return commandPool;
}
var commandPool = CreateCommandPool();

unsafe void CleanUpSwapchain()
{
    // foreach (var buffer in framebuffers)
    //     vk.DestroyFramebuffer(device, buffer, null);
    foreach (var imageView in swapChainImageViews)
        vk.DestroyImageView(device, imageView, null);
    khrSwapChain.DestroySwapchain(device, swapChain, null);
}

uint? GetMemoryTypeIdx(uint typeBits, MemoryPropertyFlags memoryPropertyFlags)
{
    for (uint i = 0; i < memoryProperties.MemoryTypeCount; i++)
    {
        if ((typeBits & 1) == 1)
        {
            if ((memoryProperties.MemoryTypes[(int)i].PropertyFlags & memoryPropertyFlags) == memoryPropertyFlags)
            {
                return i;
            }
        }
        typeBits >>= 1;
    }

    return null;
}

unsafe Buffer CreateBuffer<T>(BufferUsageFlags usageFlags, MemoryPropertyFlags memoryPropertyFlags, Span<T> data)
{
#pragma warning disable CS8500 // We assume sizeof(T) is fine, so let's ignore the warning
    ulong size = (ulong)(sizeof(T) * data.Length);
#pragma warning restore CS8500

    BufferCreateInfo bufferCreateInfo = new()
    {
        SType = StructureType.BufferCreateInfo,
        SharingMode = SharingMode.Exclusive,
        Size = size,
        Usage = usageFlags
    };
    CheckResult(vk.CreateBuffer(device, bufferCreateInfo, null, out var buffer), nameof(vk.CreateBuffer));

    vk.GetBufferMemoryRequirements(device, buffer, out var memoryRequirements);
    MemoryAllocateInfo allocateInfo = new()
    {
        SType = StructureType.MemoryAllocateInfo,
        AllocationSize = memoryRequirements.Size,
        MemoryTypeIndex = GetMemoryTypeIdx(memoryRequirements.MemoryTypeBits, memoryPropertyFlags).Value
    };
    MemoryAllocateFlagsInfoKHR allocFlagsInfo = new();
    if (usageFlags.HasFlag(BufferUsageFlags.ShaderDeviceAddressBit)) {
        allocFlagsInfo.SType = StructureType.MemoryAllocateFlagsInfoKhr;
        allocFlagsInfo.Flags = MemoryAllocateFlags.AddressBitKhr;
        allocateInfo.PNext = &allocFlagsInfo;
    }
    vk.AllocateMemory(device, allocateInfo, null, out var memory);

    void *mapped;
    CheckResult(vk.MapMemory(device, memory, 0, size, 0, &mapped), nameof(vk.MapMemory));
    data.CopyTo(new Span<T>(mapped, (int)size));
    vk.UnmapMemory(device, memory);

    CheckResult(vk.BindBufferMemory(device, buffer, memory, 0), nameof(vk.BindBufferMemory));

    return buffer;
}

unsafe void DeleteBuffer(DeviceMemory memory, Buffer buffer)
{
    vk.FreeMemory(device, memory, null);
    vk.DestroyBuffer(device, buffer, null);
}

ulong GetBufferDeviceAddress(Buffer buffer)
{
    BufferDeviceAddressInfo bufferDeviceAI = new() {
        SType = StructureType.BufferDeviceAddressInfo,
        Buffer = buffer,
    };
    return vk.GetBufferDeviceAddress(device, bufferDeviceAI);
}

if (!vk.TryGetDeviceExtension(instance, device, out KhrAccelerationStructure accel))
    throw new Exception($"Could not load device extension: {KhrAccelerationStructure.ExtensionName} - is it in the list of requested extensions?");

unsafe Buffer CreateAccelBuffer(ulong size)
{
    BufferCreateInfo bufferCreateInfo = new() {
        SType = StructureType.BufferCreateInfo,
        Size = size,
        Usage = BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
    };
    CheckResult(vk.CreateBuffer(device, bufferCreateInfo, null, out var buffer), nameof(vk.CreateBuffer));

    vk.GetBufferMemoryRequirements(device, buffer, out var memoryRequirements);

    MemoryAllocateFlagsInfo memoryAllocateFlagsInfo = new() {
        SType = StructureType.MemoryAllocateFlagsInfo,
        Flags = MemoryAllocateFlags.AddressBitKhr,
    };

    MemoryAllocateInfo memoryAllocateInfo = new()
    {
        SType = StructureType.MemoryAllocateInfo,
        PNext = &memoryAllocateFlagsInfo,
        AllocationSize = memoryRequirements.Size,
        MemoryTypeIndex = GetMemoryTypeIdx(memoryRequirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit).Value
    };
    CheckResult(vk.AllocateMemory(device, &memoryAllocateInfo, null, out var memory), nameof(vk.AllocateMemory));
    CheckResult(vk.BindBufferMemory(device, buffer, memory, 0), nameof(vk.BindBufferMemory));

    return buffer;
}

unsafe (Buffer Buffer, DeviceMemory Memory, ulong DeviceAddress) CreateScratchBuffer(ulong size)
{
    BufferCreateInfo bufferCreateInfo = new()
    {
        SType = StructureType.BufferCreateInfo,
        Size = size,
        Usage = BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit
    };
    CheckResult(vk.CreateBuffer(device, &bufferCreateInfo, null, out var buffer), nameof(vk.CreateBuffer));

    vk.GetBufferMemoryRequirements(device, buffer, out var memoryRequirements);

    MemoryAllocateFlagsInfo memoryAllocateFlagsInfo = new()
    {
        SType = StructureType.MemoryAllocateFlagsInfo,
        Flags = MemoryAllocateFlags.AddressBitKhr
    };

    MemoryAllocateInfo memoryAllocateInfo = new()
    {
        SType = StructureType.MemoryAllocateInfo,
        PNext = &memoryAllocateFlagsInfo,
        AllocationSize = memoryRequirements.Size,
        MemoryTypeIndex = GetMemoryTypeIdx(memoryRequirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit).Value,
    };
    CheckResult(vk.AllocateMemory(device, &memoryAllocateInfo, null, out var memory), nameof(vk.AllocateMemory));
    CheckResult(vk.BindBufferMemory(device, buffer, memory, 0), nameof(vk.BindBufferMemory));

    var deviceAddress = GetBufferDeviceAddress(buffer);

    return (buffer, memory, deviceAddress);
}

unsafe CommandBuffer StartOneTimeCommand()
{
    CommandBufferAllocateInfo allocInfo = new()
    {
        SType = StructureType.CommandBufferAllocateInfo,
        CommandPool = commandPool,
        Level = CommandBufferLevel.Primary,
        CommandBufferCount = 1,
    };
    CheckResult(vk.AllocateCommandBuffers(device, allocInfo, out var commandBuffer), nameof(vk.AllocateCommandBuffers));

    CommandBufferBeginInfo cmdBufInfo = new() {
        SType = StructureType.CommandBufferBeginInfo,
        Flags = CommandBufferUsageFlags.OneTimeSubmitBit
    };
    CheckResult(vk.BeginCommandBuffer(commandBuffer, &cmdBufInfo), nameof(vk.BeginCommandBuffer));

    return commandBuffer;
}

unsafe void RunAndDeleteOneTimeCommand(CommandBuffer commandBuffer, Queue queue)
{
    CheckResult(vk.EndCommandBuffer(commandBuffer), nameof(vk.EndCommandBuffer));

    SubmitInfo submitInfo = new() {
        SType = StructureType.SubmitInfo,
        CommandBufferCount = 1,
        PCommandBuffers = &commandBuffer,
    };

    FenceCreateInfo fenceInfo = new() {
        SType = StructureType.FenceCreateInfo,
        Flags = FenceCreateFlags.None,
    };
    CheckResult(vk.CreateFence(device, fenceInfo, null, out var fence), nameof(vk.CreateFence));

    CheckResult(vk.QueueSubmit(queue, 1, &submitInfo, fence), nameof(vk.QueueSubmit));
    CheckResult(vk.WaitForFences(device, 1, &fence, true, ulong.MaxValue), nameof(vk.WaitForFences));

    vk.DestroyFence(device, fence, null);
    vk.FreeCommandBuffers(device, commandPool, 1, &commandBuffer);
}

unsafe ulong CreateBottomLevelAccelerationStructure()
{
    Vector3[] vertices = [
        new( 1.0f,  1.0f, 0.0f),
        new(-1.0f,  1.0f, 0.0f),
        new( 0.0f, -1.0f, 0.0f),
    ];

    uint[] indices = [
        0, 1, 2
    ];
    uint numTriangles = (uint)indices.Length / 3;

    TransformMatrixKHR matrix;
    new Span<float>(matrix.Matrix, 12).Clear();
    matrix.Matrix[0] = 1.0f;
    matrix.Matrix[5] = 1.0f;
    matrix.Matrix[10] = 1.0f;

    var vertexBuffer = CreateBuffer(
        BufferUsageFlags.ShaderDeviceAddressBit | BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr,
        MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
        vertices.AsSpan()
    );

    var indexBuffer = CreateBuffer(
        BufferUsageFlags.ShaderDeviceAddressBit | BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr,
        MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
        indices.AsSpan()
    );

    var transformBuffer = CreateBuffer(
        BufferUsageFlags.ShaderDeviceAddressBit | BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr,
        MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
        new Span<float>(matrix.Matrix, 12)
    );

    DeviceOrHostAddressConstKHR vertexBufferDeviceAddress = new(GetBufferDeviceAddress(vertexBuffer));
    DeviceOrHostAddressConstKHR indexBufferDeviceAddress = new(GetBufferDeviceAddress(indexBuffer));
    DeviceOrHostAddressConstKHR transformBufferDeviceAddress = new(GetBufferDeviceAddress(transformBuffer));

    AccelerationStructureGeometryKHR accelerationStructureGeometry = new() {
        SType = StructureType.AccelerationStructureGeometryKhr,
        Flags = GeometryFlagsKHR.OpaqueBitKhr,
        GeometryType = GeometryTypeKHR.TrianglesKhr,
        Geometry = new() {
            Triangles = new() {
                SType = StructureType.AccelerationStructureGeometryTrianglesDataKhr,
                VertexFormat = Format.R32G32B32Sfloat,
                VertexData = vertexBufferDeviceAddress,
                MaxVertex = (uint)vertices.Length - 1,
                VertexStride = (ulong)sizeof(Vector3),
                IndexType = IndexType.Uint32,
                IndexData = indexBufferDeviceAddress,
                TransformData = transformBufferDeviceAddress
            }
        },
    };

    AccelerationStructureBuildGeometryInfoKHR accelBuildGeometryInfo = new() {
        SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
        Type = AccelerationStructureTypeKHR.BottomLevelKhr,
        Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
        GeometryCount = 1,
        PGeometries = &accelerationStructureGeometry,
    };

    accel.GetAccelerationStructureBuildSizes(device, AccelerationStructureBuildTypeKHR.DeviceKhr,
        accelBuildGeometryInfo, numTriangles, out var buildSizeInfo);

    var bottomLvlAccelBuffer = CreateAccelBuffer(buildSizeInfo.AccelerationStructureSize);

    AccelerationStructureCreateInfoKHR accelerationStructureCreateInfo = new()
    {
        SType = StructureType.AccelerationStructureCreateInfoKhr,
        Buffer = bottomLvlAccelBuffer,
        Size = buildSizeInfo.AccelerationStructureSize,
        Type = AccelerationStructureTypeKHR.BottomLevelKhr
    };
    accel.CreateAccelerationStructure(device, &accelerationStructureCreateInfo, null, out var bottomLvlAccelHandle);

    var scratchBuffer = CreateScratchBuffer(buildSizeInfo.BuildScratchSize);

    accelBuildGeometryInfo = accelBuildGeometryInfo with
    {
        Mode = BuildAccelerationStructureModeKHR.BuildKhr,
        DstAccelerationStructure = bottomLvlAccelHandle,
        PGeometries = &accelerationStructureGeometry,
        ScratchData = new()
        {
            DeviceAddress = scratchBuffer.DeviceAddress
        }
    };

    AccelerationStructureBuildRangeInfoKHR accelBuildRange = new()
    {
        PrimitiveCount = numTriangles,
        PrimitiveOffset = 0,
        FirstVertex = 0,
        TransformOffset = 0
    };

    var commandBuffer = StartOneTimeCommand();

    var buildInfoArray = stackalloc[] { accelBuildGeometryInfo };
    var buildRangeInfoArray = stackalloc[] { &accelBuildRange };
    accel.CmdBuildAccelerationStructures(commandBuffer, 1, buildInfoArray, buildRangeInfoArray);

    RunAndDeleteOneTimeCommand(commandBuffer, graphicsQueue);

    AccelerationStructureDeviceAddressInfoKHR accelerationDeviceAddressInfo = new()
    {
        SType = StructureType.AccelerationStructureDeviceAddressInfoKhr,
        AccelerationStructure = bottomLvlAccelHandle
    };
    var deviceAddress = accel.GetAccelerationStructureDeviceAddress(device, &accelerationDeviceAddressInfo);

    DeleteBuffer(scratchBuffer.Memory, scratchBuffer.Buffer);

    return deviceAddress;
}
var bottomLevelAccelDeviceAddress = CreateBottomLevelAccelerationStructure();

// TODO free bottom level accel buffers etc.

unsafe (AccelerationStructureKHR , ulong) CreateTopLevelAccelerationStructure()
{
    TransformMatrixKHR matrix;
    new Span<float>(matrix.Matrix, 12).Clear();
    matrix.Matrix[0] = 1.0f;
    matrix.Matrix[5] = 1.0f;
    matrix.Matrix[10] = 1.0f;

    AccelerationStructureInstanceKHR instance = new()
    {
        Transform = matrix,
        InstanceCustomIndex = 0,
        Mask = 0xFF,
        InstanceShaderBindingTableRecordOffset = 0,
        Flags = GeometryInstanceFlagsKHR.TriangleFacingCullDisableBitKhr,
        AccelerationStructureReference = bottomLevelAccelDeviceAddress
    };

    Span<AccelerationStructureInstanceKHR> instances = [ instance ];
    var instancesBuffer = CreateBuffer(
        BufferUsageFlags.ShaderDeviceAddressBit | BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr,
        MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
        instances
    );

    AccelerationStructureGeometryKHR accelerationStructureGeometry = new()
    {
        SType = StructureType.AccelerationStructureGeometryKhr,
        GeometryType = GeometryTypeKHR.InstancesKhr,
        Flags = GeometryFlagsKHR.OpaqueBitKhr,
        Geometry = new() {
            Instances = new() {
                SType = StructureType.AccelerationStructureGeometryInstancesDataKhr,
                ArrayOfPointers = false,
                Data = new(GetBufferDeviceAddress(instancesBuffer))
            }
        }
    };

    AccelerationStructureBuildGeometryInfoKHR accelBuildGeometryInfo = new()
    {
        SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
        Type = AccelerationStructureTypeKHR.TopLevelKhr,
        Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
        GeometryCount = 1,
        PGeometries = &accelerationStructureGeometry
    };

    accel.GetAccelerationStructureBuildSizes(device, AccelerationStructureBuildTypeKHR.DeviceKhr,
        &accelBuildGeometryInfo, 1, out var accelBuildSizesInfo);
    var accelBuffer = CreateAccelBuffer(accelBuildSizesInfo.AccelerationStructureSize);

    AccelerationStructureCreateInfoKHR createInfo = new()
    {
        SType = StructureType.AccelerationStructureCreateInfoKhr,
        Buffer = accelBuffer,
        Size = accelBuildSizesInfo.AccelerationStructureSize,
        Type = AccelerationStructureTypeKHR.TopLevelKhr,
    };
    accel.CreateAccelerationStructure(device, createInfo, null, out var topLevelAccel);
    accelBuildGeometryInfo.DstAccelerationStructure = topLevelAccel;

    var scratchBuffer = CreateScratchBuffer(accelBuildSizesInfo.BuildScratchSize);
    accelBuildGeometryInfo.ScratchData = new() { DeviceAddress = scratchBuffer.DeviceAddress };

    AccelerationStructureBuildRangeInfoKHR accelerationStructureBuildRangeInfo = new()
    {
        PrimitiveCount = 1,
        PrimitiveOffset = 0,
        FirstVertex = 0,
        TransformOffset = 0
    };

    var commandBuffer = StartOneTimeCommand();

    var buildInfoArray = stackalloc[] { accelBuildGeometryInfo };
    var buildRangeInfoArray = stackalloc[] { &accelerationStructureBuildRangeInfo };
    accel.CmdBuildAccelerationStructures(commandBuffer, 1, buildInfoArray, buildRangeInfoArray);

    RunAndDeleteOneTimeCommand(commandBuffer, graphicsQueue);

    AccelerationStructureDeviceAddressInfoKHR accelerationDeviceAddressInfo = new()
    {
        SType = StructureType.AccelerationStructureDeviceAddressInfoKhr,
        AccelerationStructure = topLevelAccel
    };
    var deviceAddress = accel.GetAccelerationStructureDeviceAddress(device, &accelerationDeviceAddressInfo);

    DeleteBuffer(scratchBuffer.Memory, scratchBuffer.Buffer);

    return (topLevelAccel, deviceAddress);
}
var (topLevelAccel, topLevelAccelDeviceAddress) = CreateTopLevelAccelerationStructure();
// TODO free top level accel data

// TODO recreate storage image upon resize
// TODO clean up storage image

ImageSubresourceRange subresourceRange = new()
{
    AspectMask = ImageAspectFlags.ColorBit,
    BaseMipLevel = 0,
    LevelCount = 1,
    LayerCount = 1
};

unsafe (Image, ImageView) CreateStorageImage(Format colorFormat)
{
    ImageCreateInfo createInfo = new()
    {
        SType = StructureType.ImageCreateInfo,
        ImageType = ImageType.Type2D,
        Format = colorFormat,
        Extent = new() {
            Width = (uint)window.FramebufferSize.X,
            Height = (uint)window.FramebufferSize.Y,
            Depth = 1
        },
        MipLevels = 1,
        ArrayLayers = 1,
        Samples = SampleCountFlags.Count1Bit,
        Tiling = ImageTiling.Optimal,
        Usage = ImageUsageFlags.TransferSrcBit | ImageUsageFlags.StorageBit,
        InitialLayout = ImageLayout.Undefined
    };
    CheckResult(vk.CreateImage(device, &createInfo, null, out var image), nameof(vk.CreateImage));

    vk.GetImageMemoryRequirements(device, image, out var memReqs);
    MemoryAllocateInfo memoryAllocateInfo = new() {
        SType = StructureType.MemoryAllocateInfo,
        AllocationSize = memReqs.Size,
        MemoryTypeIndex = GetMemoryTypeIdx(memReqs.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit).Value
    };

    CheckResult(vk.AllocateMemory(device, &memoryAllocateInfo, null, out var memory), nameof(vk.AllocateMemory));
    CheckResult(vk.BindImageMemory(device, image, memory, 0), nameof(vk.BindImageMemory));

    ImageViewCreateInfo imageView = new()
    {
        SType = StructureType.ImageViewCreateInfo,
        ViewType = ImageViewType.Type2D,
        Format = colorFormat,
        SubresourceRange = subresourceRange,
        Image = image
    };
    CheckResult(vk.CreateImageView(device, &imageView, null, out var view), nameof(vk.CreateImageView));

    var commandBuffer = StartOneTimeCommand();

    ImageMemoryBarrier imageMemoryBarrier = new()
    {
        SType = StructureType.ImageMemoryBarrier,
        OldLayout = ImageLayout.Undefined,
        NewLayout = ImageLayout.General,
        Image = image,
        SubresourceRange = subresourceRange
    };

    vk.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
        0, 0, null, 0, null, 1, &imageMemoryBarrier);

    RunAndDeleteOneTimeCommand(commandBuffer, graphicsQueue);

    return (image, view);
}
var (storageImage, storageImageView) = CreateStorageImage(Format.R32G32B32A32Sfloat);
var (toneMappedImage, toneMappedImageView) = CreateStorageImage(swapChainImageFormat);
bool swapChainIsLinear = swapChainColorSpace == ColorSpaceKHR.SpaceExtendedSrgbLinearExt;

// TODO do we need to release some memory for the storage image?
// TODO resize / recreate storage image upon window resize

unsafe void CreateUniformBuffer()
{
    // TODO this is where we create buffers for shader uniforms (e.g., camera and light source data)

    // VK_CHECK_RESULT(vulkanDevice->createBuffer(
    //     VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT,
    //     VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT,
    //     &ubo,
    //     sizeof(uniformData),
    //     &uniformData));
    // VK_CHECK_RESULT(ubo.map());

    // uniformData.projInverse = glm::inverse(camera.matrices.perspective);
    // uniformData.viewInverse = glm::inverse(camera.matrices.view);
    // memcpy(ubo.mapped, &uniformData, sizeof(uniformData));
}
CreateUniformBuffer();

const uint VK_SHADER_UNUSED_KHR = ~0U;
if (!vk.TryGetDeviceExtension(instance, device, out KhrRayTracingPipeline rayPipe))
    throw new NotSupportedException($"{KhrRayTracingPipeline.ExtensionName} extension not found.");

uint AlignedSize(uint value, uint alignment) => (value + alignment - 1) & ~(alignment - 1);

PhysicalDeviceRayTracingPipelinePropertiesKHR rtPipeProps;
unsafe
{
    PhysicalDeviceRayTracingPipelinePropertiesKHR pipeProps = new() {
        SType = StructureType.PhysicalDeviceRayTracingPipelinePropertiesKhr
    };
    PhysicalDeviceProperties2 devProps = new()
    {
        SType = StructureType.PhysicalDeviceProperties2,
        PNext = &pipeProps
    };
    vk.GetPhysicalDeviceProperties2(physicalDevice, &devProps);
    rtPipeProps = pipeProps;
}

unsafe (Pipeline, PipelineLayout, DescriptorSet, Buffer, Buffer, Buffer) CreateRayTracingPipeline()
{
    var bindings = stackalloc DescriptorSetLayoutBinding[] {
        new()
        {
            Binding = 0,
            DescriptorType = DescriptorType.AccelerationStructureKhr,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.RaygenBitKhr,
        },
        new()
        {
            Binding = 1,
            DescriptorType = DescriptorType.StorageImage,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.RaygenBitKhr
        },
        // new()
        // {
        //     Binding = 2,
        //     DescriptorType = DescriptorType.UniformBuffer,
        //     DescriptorCount = 1,
        //     StageFlags = ShaderStageFlags.RaygenBitKhr
        // }
    };
    uint numBindings = 2; // TODO don't forget to update in tandem
    DescriptorSetLayoutCreateInfo descSetCreateInfo = new()
    {
        SType =  StructureType.DescriptorSetLayoutCreateInfo,
        BindingCount = numBindings,
        PBindings = bindings
    };
    CheckResult(vk.CreateDescriptorSetLayout(device, descSetCreateInfo, null, out var descriptorSetLayout), nameof(vk.CreateDescriptorSetLayout));

    PipelineLayoutCreateInfo pipelineLayoutCI = new()
    {
        SType = StructureType.PipelineLayoutCreateInfo,
        SetLayoutCount = 1,
        PSetLayouts = &descriptorSetLayout
    };
    CheckResult(vk.CreatePipelineLayout(device, pipelineLayoutCI, null, out var pipelineLayout), nameof(vk.CreatePipelineLayout));

    uint i = 0;
    uint numStages = 3;
    uint groupCount = numStages;
    var shaderStages = stackalloc PipelineShaderStageCreateInfo[(int)numStages];
    var shaderGroups = stackalloc RayTracingShaderGroupCreateInfoKHR[(int)numStages];

    {
        var module = CreateShaderModule(CompileShader(
            """
            #version 460
            #extension GL_EXT_ray_tracing : enable

            layout(binding = 0, set = 0) uniform accelerationStructureEXT topLevelAS;
            layout(binding = 1, set = 0, rgba32f) uniform image2D image;

            layout(location = 0) rayPayloadEXT vec3 hitValue;

            void main()
            {
                const vec2 pixelCenter = vec2(gl_LaunchIDEXT.xy) + vec2(0.5);
                const vec2 imagePos = pixelCenter / vec2(gl_LaunchSizeEXT.xy) * 2.0 - 1.0;

                vec4 origin = vec4(imagePos.x, imagePos.y, -10, 1);
                vec4 direction = vec4(0, 0, 1, 1);

                float tmin = 0.0;
                float tmax = 10000.0;

                hitValue = vec3(0.0);

                traceRayEXT(topLevelAS, gl_RayFlagsOpaqueEXT, 0xff, 0, 0, 0, origin.xyz, tmin, direction.xyz, tmax, 0);

                imageStore(image, ivec2(gl_LaunchIDEXT.xy), vec4(hitValue, 0.0));
            }
            """, "rgen"
        ));

        shaderStages[i] = new()
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.RaygenBitKhr,
            Module = module,
            PName = (byte*)SilkMarshal.StringToPtr("main")
        };

        shaderGroups[i] = new()
        {
            SType = StructureType.RayTracingShaderGroupCreateInfoKhr,
            Type = RayTracingShaderGroupTypeKHR.GeneralKhr,
            GeneralShader = i,
            ClosestHitShader = VK_SHADER_UNUSED_KHR,
            AnyHitShader = VK_SHADER_UNUSED_KHR,
            IntersectionShader = VK_SHADER_UNUSED_KHR
        };

        ++i;
    }

    {
        var module = CreateShaderModule(CompileShader(
            """
            #version 460
            #extension GL_EXT_ray_tracing : enable

            layout(location = 0) rayPayloadInEXT vec3 hitValue;

            void main()
            {
                hitValue = vec3(0.0, 0.0, 0.0);
            }
            """, "rmiss"
        ));

        shaderStages[i] = new()
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.MissBitKhr,
            Module = module,
            PName = (byte*)SilkMarshal.StringToPtr("main")
        };

        shaderGroups[i] = new()
        {
            SType = StructureType.RayTracingShaderGroupCreateInfoKhr,
            Type = RayTracingShaderGroupTypeKHR.GeneralKhr,
            GeneralShader = i,
            ClosestHitShader = VK_SHADER_UNUSED_KHR,
            AnyHitShader = VK_SHADER_UNUSED_KHR,
            IntersectionShader = VK_SHADER_UNUSED_KHR
        };

        ++i;
    }

    {
        var module = CreateShaderModule(CompileShader(
            """
            #version 460
            #extension GL_EXT_ray_tracing : enable
            #extension GL_EXT_nonuniform_qualifier : enable

            layout(location = 0) rayPayloadInEXT vec3 hitValue;
            hitAttributeEXT vec2 attribs;

            void main()
            {
                const vec3 barycentricCoords = vec3(1.0f - attribs.x - attribs.y, attribs.x, attribs.y);
                hitValue = barycentricCoords * 5.0;
            }
            """, "rchit"
        ));

        shaderStages[i] = new()
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.ClosestHitBitKhr,
            Module = module,
            PName = (byte*)SilkMarshal.StringToPtr("main")
        };

        shaderGroups[i] = new()
        {
            SType = StructureType.RayTracingShaderGroupCreateInfoKhr,
            Type = RayTracingShaderGroupTypeKHR.TrianglesHitGroupKhr,
            GeneralShader = VK_SHADER_UNUSED_KHR,
            ClosestHitShader = i,
            AnyHitShader = VK_SHADER_UNUSED_KHR,
            IntersectionShader = VK_SHADER_UNUSED_KHR
        };

        ++i;
    }

    RayTracingPipelineCreateInfoKHR rayTracingPipelineCI = new()
    {
        SType = StructureType.RayTracingPipelineCreateInfoKhr,
        StageCount = numStages,
        PStages = shaderStages,
        GroupCount = groupCount,
        PGroups = shaderGroups,
        MaxPipelineRayRecursionDepth = 1,
        Layout = pipelineLayout
    };
    CheckResult(rayPipe.CreateRayTracingPipelines(device, new DeferredOperationKHR(), new PipelineCache(),
        1, &rayTracingPipelineCI, null, out var pipeline), nameof(rayPipe.CreateRayTracingPipelines));

    // Create the shader binding table
    uint handleSize = rtPipeProps.ShaderGroupHandleSize;
    uint handleSizeAligned = AlignedSize(rtPipeProps.ShaderGroupHandleSize, rtPipeProps.ShaderGroupHandleAlignment);
    uint sbtSize = groupCount * handleSizeAligned;

    byte[] shaderHandleStorage = new byte[sbtSize];
    fixed (byte* storage = shaderHandleStorage)
    {
        CheckResult(rayPipe.GetRayTracingShaderGroupHandles(device, pipeline, 0, groupCount, sbtSize, storage),
            nameof(rayPipe.GetRayTracingShaderGroupHandles));
    }

    const BufferUsageFlags bufferUsageFlags = BufferUsageFlags.ShaderBindingTableBitKhr | BufferUsageFlags.ShaderDeviceAddressBit;
    const MemoryPropertyFlags memoryUsageFlags = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
    var raygenShaderBindingTable = CreateBuffer(bufferUsageFlags, memoryUsageFlags, new Span<byte>(shaderHandleStorage, 0, (int)handleSize));
    var missShaderBindingTable = CreateBuffer(bufferUsageFlags, memoryUsageFlags, new Span<byte>(shaderHandleStorage, (int)handleSizeAligned, (int)handleSize));
    var hitShaderBindingTable = CreateBuffer(bufferUsageFlags, memoryUsageFlags, new Span<byte>(shaderHandleStorage, (int)handleSizeAligned * 2, (int)handleSize));

    // Create descriptor sets
    var poolSizes = stackalloc DescriptorPoolSize[] {
        new() {
            Type = DescriptorType.AccelerationStructureKhr,
            DescriptorCount = 1
        },
        new() {
            Type = DescriptorType.StorageImage,
            DescriptorCount = 1
        },
        // new() {
        //     Type = DescriptorType.UniformBuffer,
        //     DescriptorCount = 1
        // },
    };

    DescriptorPoolCreateInfo descriptorPoolCreateInfo = new() {
        SType = StructureType.DescriptorPoolCreateInfo,
        MaxSets = 1,
        PoolSizeCount = 2, // TODO make sure not to forget me :(
        PPoolSizes = poolSizes
    };
    CheckResult(vk.CreateDescriptorPool(device, &descriptorPoolCreateInfo, null, out var descriptorPool),
        nameof(vk.CreateDescriptorPool));

    DescriptorSetAllocateInfo descriptorSetAllocateInfo = new() {
        SType = StructureType.DescriptorSetAllocateInfo,
        DescriptorPool = descriptorPool,
        DescriptorSetCount = 1,
        PSetLayouts = &descriptorSetLayout
    };
    CheckResult(vk.AllocateDescriptorSets(device, &descriptorSetAllocateInfo, out var descriptorSet),
        nameof(vk.AllocateDescriptorSets));

    var topLvl = topLevelAccel;
    WriteDescriptorSetAccelerationStructureKHR descriptorAccelerationStructureInfo = new()
    {
        SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
        AccelerationStructureCount = 1,
        PAccelerationStructures = &topLvl
    };

    WriteDescriptorSet accelerationStructureWrite = new()
    {
        SType = StructureType.WriteDescriptorSet,
        PNext = &descriptorAccelerationStructureInfo,
        DstSet = descriptorSet,
        DstBinding = 0,
        DescriptorCount = 1,
        DescriptorType = DescriptorType.AccelerationStructureKhr
    };

    DescriptorImageInfo storageImageDescriptor = new()
    {
        ImageView = storageImageView,
        ImageLayout = ImageLayout.General,
    };

    WriteDescriptorSet resultImageWrite = new()
    {
        SType = StructureType.WriteDescriptorSet,
        DstSet = descriptorSet,
        DescriptorType = DescriptorType.StorageImage,
        DstBinding = 1,
        DescriptorCount = 1,
        PImageInfo = &storageImageDescriptor
    };

    // WriteDescriptorSet uniformBufferWrite = new()
    // {
    //     SType = StructureType.WriteDescriptorSet,
    //     DstSet = descriptorSet,
    //     DescriptorType = DescriptorType.UniformBuffer,
    //     DstBinding = 2,
    //     DescriptorCount = 1,
    //     PBufferInfo = &uniformDescriptor
    // }

    var writeDescriptorSets = stackalloc WriteDescriptorSet[] {
        accelerationStructureWrite,
        resultImageWrite,
        // uniformBufferWrite
    };
    uint numDescriptorSets = 2; // TODO make sure to update in tandem with above

    vk.UpdateDescriptorSets(device, numDescriptorSets, writeDescriptorSets, 0, null);

    return (pipeline, pipelineLayout, descriptorSet, raygenShaderBindingTable, missShaderBindingTable, hitShaderBindingTable);
}
var (rayPipeline, rayPipelineLayout, descriptorSet, raygenShaderBindingTable, missShaderBindingTable, hitShaderBindingTable) = CreateRayTracingPipeline();
// TODO clean-up for the RT pipeline and shader binding table and descriptor sets

unsafe (Pipeline, PipelineLayout, DescriptorSet) CreateToneMapPipeline()
{
    var bindings = stackalloc DescriptorSetLayoutBinding[] {
        new()
        {
            Binding = 0,
            DescriptorType = DescriptorType.StorageImage,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.ComputeBit,
        },
        new()
        {
            Binding = 1,
            DescriptorType = DescriptorType.StorageImage,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.ComputeBit
        },
        new()
        {
            Binding = 2,
            DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.ComputeBit
        }
    };
    uint numBindings = 3;
    DescriptorSetLayoutCreateInfo descSetCreateInfo = new()
    {
        SType = StructureType.DescriptorSetLayoutCreateInfo,
        BindingCount = numBindings,
        PBindings = bindings
    };
    CheckResult(vk.CreateDescriptorSetLayout(device, descSetCreateInfo, null, out var descriptorSetLayout), nameof(vk.CreateDescriptorSetLayout));

    PipelineLayoutCreateInfo pipelineLayoutCI = new()
    {
        SType = StructureType.PipelineLayoutCreateInfo,
        SetLayoutCount = 1,
        PSetLayouts = &descriptorSetLayout
    };
    CheckResult(vk.CreatePipelineLayout(device, pipelineLayoutCI, null, out var pipelineLayout), nameof(vk.CreatePipelineLayout));

    var module = CreateShaderModule(CompileShader(
        """
        #version 460

        layout (binding = 0, rgba32f) uniform readonly image2D inputImage;
        layout (binding = 1, rgba32f) uniform writeonly image2D outputImage;
        layout(binding = 2) uniform params
        {
            bool linearOutput;
        };

        const vec3[] inferno = vec3[](vec3(0.000113157585, 3.6067417E-05, 0.0010732203), vec3(0.00025610387, 0.00017476911, 0.0018801669), vec3(0.0004669041, 0.00036492254, 0.0029950093), vec3(0.00074387394, 0.0006001055, 0.004434588), vec3(0.0010894359, 0.00087344326, 0.006229556), vec3(0.0015088051, 0.001177559, 0.0084160855), vec3(0.0020109902, 0.0015040196, 0.01103472), vec3(0.0026039002, 0.0018438299, 0.0141367605), vec3(0.0033011902, 0.0021884087, 0.017756652), vec3(0.004120199, 0.0025247426, 0.021956626), vec3(0.0050788447, 0.002843587, 0.026762031), vec3(0.006197755, 0.0031312993, 0.032229163), vec3(0.0075137066, 0.0033734855, 0.038373485), vec3(0.009052796, 0.0035592811, 0.045200158), vec3(0.010849649, 0.0036771009, 0.0526849), vec3(0.012937295, 0.0037212505, 0.060759768), vec3(0.015345546, 0.003692564, 0.06930094), vec3(0.018098025, 0.0035980744, 0.07813061), vec3(0.021208616, 0.0034528747, 0.08703171), vec3(0.024674637, 0.003281221, 0.09575732), vec3(0.028485317, 0.0031094865, 0.1040848), vec3(0.032620963, 0.0029626512, 0.11183704), vec3(0.03706181, 0.0028607259, 0.118899666), vec3(0.04178865, 0.0028177549, 0.12522277), vec3(0.046788756, 0.0028406673, 0.13080563), vec3(0.052054882, 0.0029312726, 0.13568167), vec3(0.057582982, 0.003088596, 0.13990062), vec3(0.063372016, 0.0033096694, 0.1435242), vec3(0.06942662, 0.0035893016, 0.14660975), vec3(0.07575159, 0.003922615, 0.14921187), vec3(0.08235316, 0.0043047923, 0.15138116), vec3(0.08923761, 0.0047312886, 0.15316024), vec3(0.09641238, 0.005197805, 0.15458518), vec3(0.10388431, 0.0057007573, 0.15568596), vec3(0.111662984, 0.006236934, 0.1564891), vec3(0.119754754, 0.006803522, 0.157015), vec3(0.12816563, 0.007398661, 0.1572824), vec3(0.1369046, 0.008020017, 0.15730207), vec3(0.14597888, 0.008666312, 0.15708491), vec3(0.15539509, 0.00933656, 0.1566394), vec3(0.16515826, 0.010029963, 0.15597263), vec3(0.17527373, 0.01074636, 0.15509336), vec3(0.18574715, 0.011485828, 0.15400328), vec3(0.19658448, 0.01224859, 0.15270615), vec3(0.20778736, 0.013035462, 0.15120722), vec3(0.21936052, 0.013847772, 0.14951044), vec3(0.23130418, 0.014686857, 0.14761913), vec3(0.24362104, 0.01555458, 0.14553647), vec3(0.25630945, 0.016453197, 0.1432689), vec3(0.26937073, 0.01738554, 0.14082028), vec3(0.2828013, 0.018354744, 0.13819626), vec3(0.29659814, 0.019364305, 0.13540421), vec3(0.3107568, 0.02041837, 0.13245139), vec3(0.32527044, 0.021521611, 0.1293436), vec3(0.34013203, 0.02267913, 0.12609199), vec3(0.35533002, 0.023896633, 0.122706555), vec3(0.37085614, 0.025180677, 0.11919673), vec3(0.38669696, 0.026537934, 0.1155722), vec3(0.4028372, 0.027976284, 0.11184702), vec3(0.41926005, 0.029504173, 0.10803284), vec3(0.43594778, 0.03113045, 0.104143575), vec3(0.4528798, 0.032865155, 0.100191556), vec3(0.47003177, 0.034718376, 0.09618961), vec3(0.48738313, 0.036701936, 0.09215296), vec3(0.50490403, 0.03882792, 0.08809595), vec3(0.52256775, 0.041108996, 0.0840326), vec3(0.5403422, 0.043558538, 0.079977006), vec3(0.5581976, 0.046191607, 0.07594139), vec3(0.57609993, 0.049023066, 0.07193904), vec3(0.59401315, 0.052068174, 0.067982584), vec3(0.6119005, 0.05534375, 0.06408458), vec3(0.6297258, 0.058866736, 0.060255717), vec3(0.64745015, 0.062653854, 0.056505997), vec3(0.6650338, 0.06672315, 0.052845273), vec3(0.682442, 0.071092464, 0.049281087), vec3(0.6996317, 0.0757799, 0.045820754), vec3(0.7165659, 0.08080393, 0.04247028), vec3(0.7332067, 0.08618197, 0.039235454), vec3(0.7495165, 0.091932856, 0.036119446), vec3(0.7654613, 0.098073415, 0.03312616), vec3(0.78100467, 0.1046214, 0.03025803), vec3(0.7961156, 0.111594625, 0.02751637), vec3(0.81076324, 0.11900943, 0.024902778), vec3(0.82491744, 0.12688157, 0.022417907), vec3(0.8385498, 0.13522667, 0.020062417), vec3(0.85163677, 0.14406104, 0.017836837), vec3(0.8641513, 0.15339826, 0.01574141), vec3(0.87607443, 0.1632525, 0.013777557), vec3(0.88738364, 0.1736395, 0.011946086), vec3(0.89805984, 0.18457112, 0.010249078), vec3(0.9080843, 0.19606017, 0.00868905), vec3(0.91744196, 0.20812023, 0.0072697657), vec3(0.926115, 0.22076279, 0.0059960275), vec3(0.93409115, 0.23400038, 0.004873595), vec3(0.9413553, 0.24784505, 0.0039101415), vec3(0.9478939, 0.26230624, 0.0031146826), vec3(0.9536967, 0.2773987, 0.0024980311), vec3(0.95874923, 0.29313073, 0.0020732752), vec3(0.9630435, 0.30951315, 0.001855572), vec3(0.966567, 0.32655644, 0.0018628523), vec3(0.9693118, 0.34427127, 0.0021157656), vec3(0.9712692, 0.3626653, 0.0026382324), vec3(0.97243184, 0.38174677, 0.003457789), vec3(0.97279316, 0.4015281, 0.0046063773), vec3(0.9723491, 0.42201203, 0.006120531), vec3(0.9710965, 0.44320524, 0.008042514), vec3(0.9690359, 0.46511263, 0.010420951), vec3(0.9661676, 0.4877382, 0.013312584), vec3(0.96249765, 0.51108307, 0.01678281), vec3(0.95804363, 0.53513855, 0.020907598), vec3(0.9528328, 0.55990005, 0.025776993), vec3(0.9469009, 0.5853509, 0.0314954), vec3(0.9402773, 0.611476, 0.038189955), vec3(0.9330464, 0.6382382, 0.04600755), vec3(0.9253274, 0.6655842, 0.055122882), vec3(0.91723233, 0.69345576, 0.06575425), vec3(0.9090141, 0.72174126, 0.07814396), vec3(0.90098083, 0.75029904, 0.092582785), vec3(0.89355445, 0.7789444, 0.10940892), vec3(0.8873942, 0.80739737, 0.12894122), vec3(0.8833361, 0.83531785, 0.15145478), vec3(0.8823836, 0.8623218, 0.17706329), vec3(0.8855449, 0.8880533, 0.20560762), vec3(0.89356405, 0.9122939, 0.23662856), vec3(0.90671027, 0.9350294, 0.26947564), vec3(0.92478114, 0.9564351, 0.30354354), vec3(0.9473031, 0.976763, 0.3383212), vec3(0.97372925, 0.99627715, 0.37352222));

        vec3 infernoMap(float min, float max, float value) {
            if (value <= min) return inferno[0];
            if (value >= max) return inferno[inferno.length() - 1];

            // We preserve NaN values in the output
            //if (isNaN(value)) return [ NaN, NaN, NaN ];

            float relative = (value - min) / (max - min) * float(inferno.length() - 1);
            int lower = int(relative);
            int upper = lower + 1;
            float t = relative - float(lower);

            vec3 a = inferno[upper];
            vec3 b = inferno[lower];
            return t * a + (1.0 - t) * b;
        }

        vec3 infernoMap(float min, float max, vec3 v) {
            return infernoMap(min, max, (v.x + v.y + v.z) / 3.0);
        }

        float LinearToSrgb(float linear) {
            if (linear > 0.0031308) {
                return 1.055 * (pow(linear, (1.0 / 2.4))) - 0.055;
            } else {
                return 12.92 * linear;
            }
        }

        float SrgbToLinear(float srgb) {
            if (srgb <= 0.04045) {
                return srgb / 12.92;
            } else {
                return pow((srgb + 0.055) / 1.055, 2.4);
            }
        }

        bool anynan(vec3 v) {
            return (!(v.x < 0.0 || 0.0 < v.x || v.x == 0.0) ||
                    !(v.y < 0.0 || 0.0 < v.y || v.y == 0.0) ||
                    !(v.z < 0.0 || 0.0 < v.z || v.z == 0.0));
        }

        bool anyinf(vec3 v) {
            return isinf(v.x) || isinf(v.y) || isinf(v.z);
        }

        void main()
        {
            const ivec2 pixel = ivec2(gl_GlobalInvocationID.xy);
            vec4 rgba = imageLoad(inputImage, pixel);

            // TODO this is where we do tone mapping now

            if (!linearOutput)
                rgba = vec4(LinearToSrgb(rgba.x), LinearToSrgb(rgba.y), LinearToSrgb(rgba.z), rgba.w);
            imageStore(outputImage, pixel, rgba);
        }
        """, "comp"
    ));

    ComputePipelineCreateInfo computePipelineCreateInfo = new()
    {
        SType = StructureType.ComputePipelineCreateInfo,
        Layout = pipelineLayout,
        Stage = new()
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.ComputeBit,
            Module = module,
            PName = (byte*)SilkMarshal.StringToPtr("main")
        },
    };
    CheckResult(vk.CreateComputePipelines(device, new PipelineCache(), 1, &computePipelineCreateInfo, null, out var pipeline),
        nameof(vk.CreateComputePipelines));

    // Create descriptor sets
    var poolSizes = stackalloc DescriptorPoolSize[] {
        new() {
            Type = DescriptorType.StorageImage,
            DescriptorCount = 1
        },
        new() {
            Type = DescriptorType.StorageImage,
            DescriptorCount = 1
        },
        new() {
            Type = DescriptorType.UniformBuffer,
            DescriptorCount = 1
        },
    };
    uint numPoolSizes = 3;

    DescriptorPoolCreateInfo descriptorPoolCreateInfo = new() {
        SType = StructureType.DescriptorPoolCreateInfo,
        MaxSets = 1,
        PoolSizeCount = numPoolSizes,
        PPoolSizes = poolSizes
    };
    CheckResult(vk.CreateDescriptorPool(device, &descriptorPoolCreateInfo, null, out var descriptorPool),
        nameof(vk.CreateDescriptorPool));

    DescriptorSetAllocateInfo descriptorSetAllocateInfo = new() {
        SType = StructureType.DescriptorSetAllocateInfo,
        DescriptorPool = descriptorPool,
        DescriptorSetCount = 1,
        PSetLayouts = &descriptorSetLayout
    };
    CheckResult(vk.AllocateDescriptorSets(device, &descriptorSetAllocateInfo, out var descriptorSet),
        nameof(vk.AllocateDescriptorSets));

    DescriptorImageInfo inputImageDescriptor = new()
    {
        ImageView = storageImageView,
        ImageLayout = ImageLayout.General,
    };
    WriteDescriptorSet inputImageWrite = new()
    {
        SType = StructureType.WriteDescriptorSet,
        DstSet = descriptorSet,
        DescriptorType = DescriptorType.StorageImage,
        DstBinding = 0,
        DescriptorCount = 1,
        PImageInfo = &inputImageDescriptor
    };

    DescriptorImageInfo outputImageDescriptor = new()
    {
        ImageView = toneMappedImageView,
        ImageLayout = ImageLayout.General,
    };
    WriteDescriptorSet outputImageWrite = new()
    {
        SType = StructureType.WriteDescriptorSet,
        DstSet = descriptorSet,
        DescriptorType = DescriptorType.StorageImage,
        DstBinding = 1,
        DescriptorCount = 1,
        PImageInfo = &outputImageDescriptor
    };

    Span<Bool32> isLinearUniform = [ swapChainIsLinear ];
    var uniformBuffer = CreateBuffer(BufferUsageFlags.UniformBufferBit,
        MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
        isLinearUniform);

    DescriptorBufferInfo uniformDescriptor = new()
    {
        Buffer = uniformBuffer,
        Offset = 0,
        Range = (ulong)isLinearUniform.Length * sizeof(bool)
    };
    WriteDescriptorSet uniformBufferWrite = new()
    {
        SType = StructureType.WriteDescriptorSet,
        DstSet = descriptorSet,
        DescriptorType = DescriptorType.UniformBuffer,
        DstBinding = 2,
        DescriptorCount = 1,
        PBufferInfo = &uniformDescriptor
    };

    var writeDescriptorSets = stackalloc WriteDescriptorSet[] {
        inputImageWrite,
        outputImageWrite,
        uniformBufferWrite
    };
    uint numDescriptorSets = 3;

    vk.UpdateDescriptorSets(device, numDescriptorSets, writeDescriptorSets, 0, null);

    return (pipeline, pipelineLayout, descriptorSet);
}
var (toneMapPipeline, toneMapPipelineLayout, toneMapdescriptorSet) = CreateToneMapPipeline();
// TODO clean-up for the TMO pipeline

unsafe CommandBuffer[] BuildCommandBuffersRT()
{
    var commandBuffers = new CommandBuffer[swapChainImages.Length];

    CommandBufferAllocateInfo allocInfo = new()
    {
        SType = StructureType.CommandBufferAllocateInfo,
        CommandPool = commandPool,
        Level = CommandBufferLevel.Primary,
        CommandBufferCount = (uint)commandBuffers.Length,
    };

    fixed (CommandBuffer* commandBuffersPtr = commandBuffers)
        CheckResult(vk.AllocateCommandBuffers(device, allocInfo, commandBuffersPtr), nameof(vk.AllocateCommandBuffers));

    for (int i = 0; i < commandBuffers.Length; i++)
    {
        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
        };

        CheckResult(vk.BeginCommandBuffer(commandBuffers[i], beginInfo), nameof(vk.BeginCommandBuffer));

        uint handleSizeAligned = AlignedSize(rtPipeProps.ShaderGroupHandleSize, rtPipeProps.ShaderGroupHandleAlignment);

        StridedDeviceAddressRegionKHR raygenShaderSbtEntry = new()
        {
            DeviceAddress = GetBufferDeviceAddress(raygenShaderBindingTable),
            Stride = handleSizeAligned,
            Size = handleSizeAligned
        };

        StridedDeviceAddressRegionKHR missShaderSbtEntry = new()
        {
            DeviceAddress = GetBufferDeviceAddress(missShaderBindingTable),
            Stride = handleSizeAligned,
            Size = handleSizeAligned
        };

        StridedDeviceAddressRegionKHR hitShaderSbtEntry = new()
        {
            DeviceAddress = GetBufferDeviceAddress(hitShaderBindingTable),
            Stride = handleSizeAligned,
            Size = handleSizeAligned
        };

        StridedDeviceAddressRegionKHR callableShaderSbtEntry = new();

        vk.CmdBindPipeline(commandBuffers[i], PipelineBindPoint.RayTracingKhr, rayPipeline);
        var descSet = descriptorSet;
        vk.CmdBindDescriptorSets(commandBuffers[i], PipelineBindPoint.RayTracingKhr, rayPipelineLayout, 0, 1, &descSet, 0, 0);

        rayPipe.CmdTraceRays(
            commandBuffers[i],
            &raygenShaderSbtEntry,
            &missShaderSbtEntry,
            &hitShaderSbtEntry,
            &callableShaderSbtEntry,
            swapChainExtent.Width,
            swapChainExtent.Height,
            1);

        ImageMemoryBarrier syncRender = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.General,
            NewLayout = ImageLayout.General,
            Image = storageImage,
            SubresourceRange = subresourceRange
        };
        vk.CmdPipelineBarrier(commandBuffers[i], PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
            0, 0, null, 0, null, 1, &syncRender);

        vk.CmdBindPipeline(commandBuffers[i], PipelineBindPoint.Compute, toneMapPipeline);
        var tmDescSet = toneMapdescriptorSet;
        vk.CmdBindDescriptorSets(commandBuffers[i], PipelineBindPoint.Compute, toneMapPipelineLayout, 0, 1, &tmDescSet, 0, 0);
        vk.CmdDispatch(commandBuffers[i], swapChainExtent.Width, swapChainExtent.Height, 1);

        ImageMemoryBarrier curSwapDest = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.TransferDstOptimal,
            Image = swapChainImages[i],
            SubresourceRange = subresourceRange
        };
        vk.CmdPipelineBarrier(commandBuffers[i], PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
            0, 0, null, 0, null, 1, &curSwapDest);

        ImageMemoryBarrier rtSrc = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.General,
            NewLayout = ImageLayout.TransferSrcOptimal,
            Image = toneMappedImage,
            SubresourceRange = subresourceRange
        };
        vk.CmdPipelineBarrier(commandBuffers[i], PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
            0, 0, null, 0, null, 1, &rtSrc);

        ImageCopy copyRegion = new()
        {
            SrcSubresource = new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseArrayLayer = 0,
                MipLevel = 0,
                LayerCount = 1,
            },
            SrcOffset = new(0, 0, 0),
            DstSubresource = new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseArrayLayer = 0,
                MipLevel = 0,
                LayerCount = 1,
            },
            DstOffset = new(0, 0, 0),
            Extent = new(swapChainExtent.Width, swapChainExtent.Height, 1)
        };
        vk.CmdCopyImage(commandBuffers[i], toneMappedImage, ImageLayout.TransferSrcOptimal, swapChainImages[i],
            ImageLayout.TransferDstOptimal, 1, &copyRegion);

        ImageMemoryBarrier swapImgPresent = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.TransferDstOptimal,
            NewLayout = ImageLayout.PresentSrcKhr,
            Image = swapChainImages[i],
            SubresourceRange = subresourceRange
        };
        vk.CmdPipelineBarrier(commandBuffers[i], PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
            0, 0, null, 0, null, 1, &swapImgPresent);

        ImageMemoryBarrier rtImgGeneral = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.TransferSrcOptimal,
            NewLayout = ImageLayout.General,
            Image = toneMappedImage,
            SubresourceRange = subresourceRange
        };
        vk.CmdPipelineBarrier(commandBuffers[i], PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
            0, 0, null, 0, null, 1, &rtImgGeneral);

        CheckResult(vk.EndCommandBuffer(commandBuffers[i]), nameof(vk.EndCommandBuffer));
    }

    return commandBuffers;
}
var commandBuffers = BuildCommandBuffersRT();

const int MAX_FRAMES_IN_FLIGHT = 2;

unsafe (Semaphore[], Semaphore[], Fence[], Fence[]) CreateSyncObjects()
{
    var imageAvailableSemaphores = new Semaphore[MAX_FRAMES_IN_FLIGHT];
    var renderFinishedSemaphores = new Semaphore[MAX_FRAMES_IN_FLIGHT];
    var inFlightFences = new Fence[MAX_FRAMES_IN_FLIGHT];
    var imagesInFlight = new Fence[swapChainImages.Length];

    SemaphoreCreateInfo semaphoreInfo = new()
    {
        SType = StructureType.SemaphoreCreateInfo,
    };

    FenceCreateInfo fenceInfo = new()
    {
        SType = StructureType.FenceCreateInfo,
        Flags = FenceCreateFlags.SignaledBit,
    };

    for (var i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
    {
        CheckResult(vk.CreateSemaphore(device, semaphoreInfo, null, out imageAvailableSemaphores[i]), nameof(vk.CreateSemaphore));
        CheckResult(vk.CreateSemaphore(device, semaphoreInfo, null, out renderFinishedSemaphores[i]), nameof(vk.CreateSemaphore));
        CheckResult(vk.CreateFence(device, fenceInfo, null, out inFlightFences[i]), nameof(vk.CreateFence));
    }
    return (imageAvailableSemaphores, renderFinishedSemaphores, inFlightFences, imagesInFlight);
}
var (imageAvailableSemaphores, renderFinishedSemaphores, inFlightFences, imagesInFlight) = CreateSyncObjects();

void RecreateSwapChain() {
    var framebufferSize = window.FramebufferSize;

    while (framebufferSize.X == 0 || framebufferSize.Y == 0)
    {
        framebufferSize = window.FramebufferSize;
        window.DoEvents();
    }

    vk.DeviceWaitIdle(device);

    CleanUpSwapchain();

    (khrSwapChain, swapChain, swapChainImages, swapChainImageFormat, swapChainColorSpace, swapChainExtent) = CreateSwapChain();
    swapChainImageViews = CreateImageViews();

    imagesInFlight = new Fence[swapChainImages!.Length];
}

bool resized = false;
window.FramebufferResize += newSize => { resized = true; };

int currentFrame = 0;
unsafe void DrawFrame(double delta)
{
    vk.WaitForFences(device, 1, inFlightFences[currentFrame], true, ulong.MaxValue);

    uint imageIndex = 0;
    var result = khrSwapChain.AcquireNextImage(device, swapChain, ulong.MaxValue, imageAvailableSemaphores[currentFrame], default, ref imageIndex);
    if (result == Result.ErrorOutOfDateKhr || result == Result.SuboptimalKhr || resized)
    {
        RecreateSwapChain();
        resized = false;
        return;
    }

    if (imagesInFlight[imageIndex].Handle != default)
    {
        vk.WaitForFences(device, 1, imagesInFlight[imageIndex], true, ulong.MaxValue);
    }
    imagesInFlight[imageIndex] = inFlightFences[currentFrame];

    SubmitInfo submitInfo = new()
    {
        SType = StructureType.SubmitInfo,
    };

    var waitSemaphores = stackalloc[] { imageAvailableSemaphores[currentFrame] };
    var waitStages = stackalloc[] { PipelineStageFlags.ColorAttachmentOutputBit };

    var buffer = commandBuffers[imageIndex];

    submitInfo = submitInfo with
    {
        WaitSemaphoreCount = 1,
        PWaitSemaphores = waitSemaphores,
        PWaitDstStageMask = waitStages,

        CommandBufferCount = 1,
        PCommandBuffers = &buffer
    };

    var signalSemaphores = stackalloc[] { renderFinishedSemaphores[currentFrame] };
    submitInfo = submitInfo with
    {
        SignalSemaphoreCount = 1,
        PSignalSemaphores = signalSemaphores,
    };

    vk.ResetFences(device, 1, inFlightFences[currentFrame]);

    CheckResult(vk.QueueSubmit(graphicsQueue, 1, submitInfo, inFlightFences[currentFrame]), nameof(vk.QueueSubmit));

    var swapChains = stackalloc[] { swapChain };
    PresentInfoKHR presentInfo = new()
    {
        SType = StructureType.PresentInfoKhr,

        WaitSemaphoreCount = 1,
        PWaitSemaphores = signalSemaphores,

        SwapchainCount = 1,
        PSwapchains = swapChains,

        PImageIndices = &imageIndex
    };

    khrSwapChain.QueuePresent(presentQueue, presentInfo);

    currentFrame = (currentFrame + 1) % MAX_FRAMES_IN_FLIGHT;
}
window.Render += DrawFrame;

window.Run();

CleanUpSwapchain();
unsafe {
    for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
    {
        vk.DestroySemaphore(device, renderFinishedSemaphores[i], null);
        vk.DestroySemaphore(device, imageAvailableSemaphores[i], null);
        vk.DestroyFence(device, inFlightFences[i], null);
    }
    vk.DestroyCommandPool(device, commandPool, null);

    vk.DestroyDevice(device, null);
    khrSurface.DestroySurface(instance, surface, null);
    vk.DestroyInstance(instance, null);
}
vk.Dispose();

window.Dispose();
