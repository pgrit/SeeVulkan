﻿using System;
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
using Silk.NET.Windowing;

using Buffer = Silk.NET.Vulkan.Buffer;

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

    PhysicalDeviceFeatures deviceFeatures = new();

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

    DeviceCreateInfo createInfo = new()
    {
        SType = StructureType.DeviceCreateInfo,
        QueueCreateInfoCount = (uint)uniqueQueueFamilies.Length,
        PQueueCreateInfos = queueCreateInfos,

        PEnabledFeatures = &deviceFeatures,
        EnabledLayerCount = 0,

        EnabledExtensionCount = (uint)extensions.Length,
        PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions)
    };

    CheckResult(vk.CreateDevice(physicalDevice, in createInfo, null, out var device), nameof(vk.CreateDevice));

    vk.GetDeviceQueue(device, graphicsIdx, 0, out var graphicsQueue);
    vk.GetDeviceQueue(device, presentIdx, 0, out var presentQueue);

    SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);

    return (device, graphicsQueue, presentQueue);
}
var (device, graphicsQueue, presentQueue) = CreateLogicalDevice();

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

unsafe RenderPass CreateRenderPass()
{
    AttachmentDescription colorAttachment = new()
    {
        Format = swapChainImageFormat,
        Samples = SampleCountFlags.Count1Bit,
        LoadOp = AttachmentLoadOp.Clear,
        StoreOp = AttachmentStoreOp.Store,
        StencilLoadOp = AttachmentLoadOp.DontCare,
        InitialLayout = ImageLayout.Undefined,
        FinalLayout = ImageLayout.PresentSrcKhr,
    };

    AttachmentReference colorAttachmentRef = new()
    {
        Attachment = 0,
        Layout = ImageLayout.ColorAttachmentOptimal,
    };

    SubpassDescription subpass = new()
    {
        PipelineBindPoint = PipelineBindPoint.Graphics,
        ColorAttachmentCount = 1,
        PColorAttachments = &colorAttachmentRef,
    };

    RenderPassCreateInfo renderPassInfo = new()
    {
        SType = StructureType.RenderPassCreateInfo,
        AttachmentCount = 1,
        PAttachments = &colorAttachment,
        SubpassCount = 1,
        PSubpasses = &subpass,
    };

    CheckResult(vk.CreateRenderPass(device, renderPassInfo, null, out var renderPass), nameof(vk.CreateRenderPass));

    return renderPass;
}
var renderPass = CreateRenderPass();

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

unsafe (Pipeline, PipelineLayout) CreatePipeline()
{
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

    PipelineVertexInputStateCreateInfo vertexInputInfo = new()
    {
        SType = StructureType.PipelineVertexInputStateCreateInfo,
        VertexBindingDescriptionCount = 0,
        VertexAttributeDescriptionCount = 0,
    };

    PipelineInputAssemblyStateCreateInfo inputAssembly = new()
    {
        SType = StructureType.PipelineInputAssemblyStateCreateInfo,
        Topology = PrimitiveTopology.TriangleList,
        PrimitiveRestartEnable = false,
    };

    Viewport viewport = new()
    {
        X = 0,
        Y = 0,
        Width = swapChainExtent.Width,
        Height = swapChainExtent.Height,
        MinDepth = 0,
        MaxDepth = 1,
    };

    Rect2D scissor = new()
    {
        Offset = { X = 0, Y = 0 },
        Extent = swapChainExtent,
    };

    PipelineViewportStateCreateInfo viewportState = new()
    {
        SType = StructureType.PipelineViewportStateCreateInfo,
        ViewportCount = 1,
        PViewports = &viewport,
        ScissorCount = 1,
        PScissors = &scissor,
    };

    PipelineRasterizationStateCreateInfo rasterizer = new()
    {
        SType = StructureType.PipelineRasterizationStateCreateInfo,
        DepthClampEnable = false,
        RasterizerDiscardEnable = false,
        PolygonMode = PolygonMode.Fill,
        LineWidth = 1,
        CullMode = CullModeFlags.BackBit,
        FrontFace = FrontFace.Clockwise,
        DepthBiasEnable = false,
    };

    PipelineMultisampleStateCreateInfo multisampling = new()
    {
        SType = StructureType.PipelineMultisampleStateCreateInfo,
        SampleShadingEnable = false,
        RasterizationSamples = SampleCountFlags.Count1Bit,
    };

    PipelineColorBlendAttachmentState colorBlendAttachment = new()
    {
        ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
        BlendEnable = false,
    };

    PipelineColorBlendStateCreateInfo colorBlending = new()
    {
        SType = StructureType.PipelineColorBlendStateCreateInfo,
        LogicOpEnable = false,
        LogicOp = LogicOp.Copy,
        AttachmentCount = 1,
        PAttachments = &colorBlendAttachment,
    };

    colorBlending.BlendConstants[0] = 0;
    colorBlending.BlendConstants[1] = 0;
    colorBlending.BlendConstants[2] = 0;
    colorBlending.BlendConstants[3] = 0;

    PipelineLayoutCreateInfo pipelineLayoutInfo = new()
    {
        SType = StructureType.PipelineLayoutCreateInfo,
        SetLayoutCount = 0,
        PushConstantRangeCount = 0,
    };

    CheckResult(vk.CreatePipelineLayout(device, pipelineLayoutInfo, null, out var pipelineLayout), nameof(vk.CreatePipelineLayout));

    GraphicsPipelineCreateInfo pipelineInfo = new()
    {
        SType = StructureType.GraphicsPipelineCreateInfo,
        StageCount = 2,
        PStages = shaderStages,
        PVertexInputState = &vertexInputInfo,
        PInputAssemblyState = &inputAssembly,
        PViewportState = &viewportState,
        PRasterizationState = &rasterizer,
        PMultisampleState = &multisampling,
        PColorBlendState = &colorBlending,
        Layout = pipelineLayout,
        RenderPass = renderPass,
        Subpass = 0,
        BasePipelineHandle = default
    };

    CheckResult(vk.CreateGraphicsPipelines(device, default, 1, pipelineInfo, null, out var pipeline), nameof(vk.CreateGraphicsPipelines));

    vk.DestroyShaderModule(device, fragShaderModule, null);
    vk.DestroyShaderModule(device, vertShaderModule, null);

    SilkMarshal.Free((nint)vertShaderStageInfo.PName);
    SilkMarshal.Free((nint)fragShaderStageInfo.PName);

    return (pipeline, pipelineLayout);
}
var (pipeline, pipelineLayout) = CreatePipeline();

unsafe Framebuffer[] CreateFramebuffers()
{
    var swapChainFramebuffers = new Framebuffer[swapChainImageViews.Length];

    for (int i = 0; i < swapChainImageViews.Length; i++)
    {
        var attachment = swapChainImageViews[i];

        FramebufferCreateInfo framebufferInfo = new()
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = renderPass,
            AttachmentCount = 1,
            PAttachments = &attachment,
            Width = swapChainExtent.Width,
            Height = swapChainExtent.Height,
            Layers = 1,
        };

        CheckResult(vk.CreateFramebuffer(device, framebufferInfo, null, out swapChainFramebuffers[i]), nameof(vk.CreateFramebuffer));
    }

    return swapChainFramebuffers;
}
var framebuffers = CreateFramebuffers();

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
    foreach (var buffer in framebuffers)
        vk.DestroyFramebuffer(device, buffer, null);
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

unsafe (Image, ImageView) CreateStorageImage()
{
    // TODO is this format always supported?
    const Format colorFormat = Format.R32G32B32A32Sfloat;

    ImageCreateInfo createInfo = new()
    {
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
        SubresourceRange = new() {
            AspectMask =  ImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1,
        },
        Image = image
    };
    CheckResult(vk.CreateImageView(device, &imageView, null, out var view), nameof(vk.CreateImageView));

    var commandBuffer = StartOneTimeCommand();

    ImageSubresourceRange subresourceRange = new()
    {
        AspectMask = ImageAspectFlags.ColorBit,
        BaseMipLevel = 0,
        LevelCount = 1,
        LayerCount = 1
    };

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
var (storageImage, storageImageView) = CreateStorageImage();

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

unsafe Pipeline CreateRayTracingPipeline()
{
    DescriptorSetLayoutBinding accelLayoutBinding = new()
    {
        Binding = 0,
        DescriptorType = DescriptorType.AccelerationStructureKhr,
        DescriptorCount = 1,
        StageFlags = ShaderStageFlags.RaygenBitKhr,
    };

    DescriptorSetLayoutBinding resultImageLayoutBinding = new()
    {
        Binding = 1,
        DescriptorType = DescriptorType.StorageImage,
        DescriptorCount = 1,
        StageFlags = ShaderStageFlags.RaygenBitKhr
    };

    DescriptorSetLayoutBinding uniformBufferBinding = new()
    {
        Binding = 2,
        DescriptorType = DescriptorType.UniformBuffer,
        DescriptorCount = 1,
        StageFlags = ShaderStageFlags.RaygenBitKhr
    };

    var bindings = stackalloc[] {
        accelLayoutBinding,
        resultImageLayoutBinding,
        // uniformBufferBinding
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
    List<PipelineShaderStageCreateInfo> shaderStages = [];
    List<RayTracingShaderGroupCreateInfoKHR> shaderGroups = [];

    {
        var module = CreateShaderModule(CompileShader(
            """
            #version 460
            #extension GL_EXT_ray_tracing : enable

            layout(binding = 0, set = 0) uniform accelerationStructureEXT topLevelAS;
            layout(binding = 1, set = 0, rgba8) uniform image2D image;

            layout(location = 0) rayPayloadEXT vec3 hitValue;

            void main()
            {
                const vec2 pixelCenter = vec2(gl_LaunchIDEXT.xy) + vec2(0.5);
                const vec2 inUV = pixelCenter/vec2(gl_LaunchSizeEXT.xy);
                vec2 d = inUV * 2.0 - 1.0;

                vec4 origin = vec4(0, 0, 0, 1);
                vec4 direction = vec4(0, 0, 1, 1);

                float tmin = 0.0;
                float tmax = 10000.0;

                hitValue = vec3(0.0);

                traceRayEXT(topLevelAS, gl_RayFlagsOpaqueEXT, 0xff, 0, 0, 0, origin.xyz, tmin, direction.xyz, tmax, 0);

                imageStore(image, ivec2(gl_LaunchIDEXT.xy), vec4(hitValue, 0.0));
            }
            """, "rgen"
        ));

        shaderStages.Add(new()
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = module,
            PName = (byte*)SilkMarshal.StringToPtr("main")
        });

        shaderGroups.Add(new()
        {
            SType = StructureType.RayTracingShaderGroupCreateInfoKhr,
            Type = RayTracingShaderGroupTypeKHR.GeneralKhr,
            GeneralShader = i,
            ClosestHitShader = VK_SHADER_UNUSED_KHR,
            AnyHitShader = VK_SHADER_UNUSED_KHR,
            IntersectionShader = VK_SHADER_UNUSED_KHR
        });

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
                hitValue = vec3(0.0, 0.0, 0.2);
            }
            """, "rmiss"
        ));

        shaderStages.Add(new()
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = module,
            PName = (byte*)SilkMarshal.StringToPtr("main")
        });

        shaderGroups.Add(new()
        {
            SType = StructureType.RayTracingShaderGroupCreateInfoKhr,
            Type = RayTracingShaderGroupTypeKHR.GeneralKhr,
            GeneralShader = i,
            ClosestHitShader = VK_SHADER_UNUSED_KHR,
            AnyHitShader = VK_SHADER_UNUSED_KHR,
            IntersectionShader = VK_SHADER_UNUSED_KHR
        });

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
                hitValue = barycentricCoords;
            }
            """, "rchit"
        ));

        shaderStages.Add(new()
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = module,
            PName = (byte*)SilkMarshal.StringToPtr("main")
        });

        shaderGroups.Add(new()
        {
            SType = StructureType.RayTracingShaderGroupCreateInfoKhr,
            Type = RayTracingShaderGroupTypeKHR.GeneralKhr,
            GeneralShader = VK_SHADER_UNUSED_KHR,
            ClosestHitShader = i,
            AnyHitShader = VK_SHADER_UNUSED_KHR,
            IntersectionShader = VK_SHADER_UNUSED_KHR
        });

        ++i;
    }

    Pipeline pipeline;
    fixed(PipelineShaderStageCreateInfo* stages = shaderStages.ToArray())
        fixed(RayTracingShaderGroupCreateInfoKHR* groups = shaderGroups.ToArray())
        {
            RayTracingPipelineCreateInfoKHR rayTracingPipelineCI = new()
            {
                SType = StructureType.RayTracingPipelineCreateInfoKhr,
                StageCount = (uint)shaderStages.Count,
                PStages = stages,
                GroupCount = (uint)shaderGroups.Count,
                PGroups = groups,
                MaxPipelineRayRecursionDepth = 1,
                Layout = pipelineLayout
            };
            CheckResult(rayPipe.CreateRayTracingPipelines(device, new DeferredOperationKHR(), new PipelineCache(),
                1, &rayTracingPipelineCI, null, out pipeline), nameof(rayPipe.CreateRayTracingPipelines));
        }

    // Create the shader binding table
    PhysicalDeviceRayTracingPipelinePropertiesKHR rtPipeProps = new() {
        SType = StructureType.PhysicalDeviceRayTracingPipelinePropertiesKhr
    };
    PhysicalDeviceProperties2 devProps = new()
    {
        SType = StructureType.PhysicalDeviceProperties2,
        PNext = &rtPipeProps
    };
    vk.GetPhysicalDeviceProperties2(physicalDevice, &devProps);

    uint AlignedSize(uint value, uint alignment) => (value + alignment - 1) & ~(alignment - 1);

    uint handleSize = rtPipeProps.ShaderGroupHandleSize;
    uint handleSizeAligned = AlignedSize(rtPipeProps.ShaderGroupHandleSize, rtPipeProps.ShaderGroupHandleAlignment);
    uint groupCount = (uint)shaderGroups.Count;
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
        PImageInfo = &storageImageDescriptor
    };

    // WriteDescriptorSet uniformBufferWrite = new()
    // {
    //     SType = StructureType.WriteDescriptorSet,
    //     DstSet = descriptorSet,
    //     DescriptorType = DescriptorType.UniformBuffer,
    //     DstBinding = 2,
    //     PBufferInfo = &uniformDescriptor
    // }

    var writeDescriptorSets = stackalloc WriteDescriptorSet[] {
        accelerationStructureWrite,
        resultImageWrite,
        // uniformBufferWrite
    };
    uint numDescriptorSets = 2; // TODO make sure to update in tandem with above

    vk.UpdateDescriptorSets(device, numDescriptorSets, writeDescriptorSets, 0, null);

    return pipeline;
}
var rayPipeline = CreateRayTracingPipeline();
// TODO clean-up for the RT pipeline and shader binding table and descriptor sets

unsafe CommandBuffer[] CreateCommandBuffers()
{
    var commandBuffers = new CommandBuffer[framebuffers.Length];

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

        RenderPassBeginInfo renderPassInfo = new()
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = renderPass,
            Framebuffer = framebuffers[i],
            RenderArea =
            {
                Offset = { X = 0, Y = 0 },
                Extent = swapChainExtent,
            }
        };

        ClearValue clearColor = new()
        {
            Color = new() { Float32_0 = 0, Float32_1 = 0, Float32_2 = 0, Float32_3 = 1 },
        };

        renderPassInfo.ClearValueCount = 1;
        renderPassInfo.PClearValues = &clearColor;

        vk.CmdBeginRenderPass(commandBuffers[i], &renderPassInfo, SubpassContents.Inline);

        vk.CmdBindPipeline(commandBuffers[i], PipelineBindPoint.Graphics, pipeline);

        vk.CmdDraw(commandBuffers[i], 3, 1, 0, 0);

        vk.CmdEndRenderPass(commandBuffers[i]);

        CheckResult(vk.EndCommandBuffer(commandBuffers[i]), nameof(vk.EndCommandBuffer));
    }

    return commandBuffers;
}
var commandBuffers = CreateCommandBuffers();

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

    (khrSwapChain, swapChain, swapChainImages, swapChainImageFormat, swapChainExtent) = CreateSwapChain();
    swapChainImageViews = CreateImageViews();
    renderPass = CreateRenderPass();
    (pipeline, pipelineLayout) = CreatePipeline();
    framebuffers = CreateFramebuffers();
    commandBuffers = CreateCommandBuffers();

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

    vk.DestroyPipelineLayout(device, pipelineLayout, null);
    vk.DestroyRenderPass(device, renderPass, null);
    vk.DestroyDevice(device, null);
    khrSurface.DestroySurface(instance, surface, null);
    vk.DestroyInstance(instance, null);
}
vk.Dispose();

window.Dispose();
