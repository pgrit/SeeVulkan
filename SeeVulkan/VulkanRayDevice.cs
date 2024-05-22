namespace SeeVulkan;

unsafe class VulkanRayDevice : IDisposable
{
    const bool EnableValidationLayers = true;

    Vk vk = Vk.GetApi();
    public Vk Vk => vk;

    static string[] validationLayers = [
        "VK_LAYER_KHRONOS_validation"
    ];

    public Instance Instance;
    public IWindow Window;
    public KhrSurface KhrSurface;
    public SurfaceKHR Surface;
    public PhysicalDevice PhysicalDevice;
    public PhysicalDeviceMemoryProperties MemoryProperties;
    public Device Device;
    public Queue GraphicsQueue, PresentQueue;
    public CommandPool CommandPool;
    private ExtDebugUtils debugUtils;
    private DebugUtilsMessengerEXT debugMessenger;

    static uint DebugCallback(DebugUtilsMessageSeverityFlagsEXT severity, DebugUtilsMessageTypeFlagsEXT typeFlags, DebugUtilsMessengerCallbackDataEXT* pCallbackData, void* pUserData)
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

    void CreateInstance()
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

        string[] surfaceExtensions = Window == null ? [] :
            surfaceExtensions = SilkMarshal.PtrToStringArray(
                (nint)Window.VkSurface.GetRequiredExtensions(out var windowExtCount), (int)windowExtCount);

        string[] extensions = [.. surfaceExtensions, ExtDebugUtils.ExtensionName];

        InstanceCreateInfo createInfo = new()
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledLayerCount = 0,
            PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions),
            EnabledExtensionCount = (uint)extensions.Length,
        };

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

        CheckResult(vk.CreateInstance(createInfo, null, out Instance), nameof(vk.CreateInstance));

        SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);
        Marshal.FreeHGlobal((IntPtr)appInfo.PApplicationName);
        Marshal.FreeHGlobal((IntPtr)appInfo.PEngineName);
        if (EnableValidationLayers)
            SilkMarshal.Free((nint)createInfo.PpEnabledLayerNames);

        if (EnableValidationLayers)
        {
            if (!vk.TryGetInstanceExtension(Instance, out debugUtils))
                return;

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

            CheckResult(debugUtils.CreateDebugUtilsMessenger(Instance, dbgCreateInfo, null, out debugMessenger), nameof(debugUtils.CreateDebugUtilsMessenger));
        }
    }

    void CreateWindowSurface()
    {
        if (Window == null)
            return;

        if (!vk.TryGetInstanceExtension(Instance, out KhrSurface))
            throw new NotSupportedException($"{KhrSurface.ExtensionName} extension not found.");

        Surface = Window.VkSurface.Create<AllocationCallbacks>(Instance.ToHandle(), null).ToSurface();
    }

    void PickPhysicalDevice()
    {
        var devices = vk.GetPhysicalDevices(Instance);

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
        PhysicalDevice = bestDevice;
        vk.GetPhysicalDeviceMemoryProperties(PhysicalDevice, out MemoryProperties);
    }

    (uint Graphics, uint Present) FindQueues()
    {
        uint queueFamilyCount = 0;
        vk.GetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, ref queueFamilyCount, null);

        var queueFamilies = new QueueFamilyProperties[queueFamilyCount];
        fixed (QueueFamilyProperties* queueFamiliesPtr = queueFamilies)
            vk.GetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, ref queueFamilyCount, queueFamiliesPtr);

        uint? presentIdx = null;
        uint? graphicsIdx = null;
        for (uint i = 0; i < queueFamilies.Length; ++i)
        {
            Bool32 presentSupport = false;
            KhrSurface?.GetPhysicalDeviceSurfaceSupport(PhysicalDevice, i, Surface, out presentSupport);
            if (presentSupport)
                presentIdx = i;

            var flags = queueFamilies[i].QueueFlags;
            if (flags.HasFlag(QueueFlags.ComputeBit) && flags.HasFlag(QueueFlags.GraphicsBit))
                graphicsIdx = i;

            if (presentIdx.HasValue && graphicsIdx.HasValue)
                break;
        }

        return (graphicsIdx.Value, presentIdx.GetValueOrDefault(uint.MaxValue));
    }

    void CreateLogicalDevice()
    {
        var (graphicsQueueIdx, presentQueueIdx) = FindQueues();
        uint[] uniqueQueueFamilies = new[] { graphicsQueueIdx, presentQueueIdx }.Distinct().ToArray();

        if (Window == null)
            uniqueQueueFamilies = [graphicsQueueIdx];

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

        string[] rayTraceExtensions = [
            KhrAccelerationStructure.ExtensionName,
            KhrRayTracingPipeline.ExtensionName,
            KhrBufferDeviceAddress.ExtensionName,
            KhrDeferredHostOperations.ExtensionName,
            "VK_EXT_descriptor_indexing",
            "VK_KHR_spirv_1_4",
            "VK_KHR_shader_float_controls",
        ];

        string[] extensions = Window == null ? rayTraceExtensions : [.. rayTraceExtensions, KhrSwapchain.ExtensionName];

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
            Maintenance4 = true,
        };

        PhysicalDeviceDescriptorIndexingFeatures descriptorIndexingFeatures = new()
        {
            SType = StructureType.PhysicalDeviceDescriptorIndexingFeatures,
            PNext = &vulkan13Features,
            RuntimeDescriptorArray = true,
        };

        PhysicalDeviceFeatures2 physicalDeviceFeatures2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &descriptorIndexingFeatures,
            Features = new PhysicalDeviceFeatures() {
                ShaderInt64 = true,
            }
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

        CheckResult(vk.CreateDevice(PhysicalDevice, in createInfo, null, out Device), nameof(vk.CreateDevice));

        vk.GetDeviceQueue(Device, graphicsQueueIdx, 0, out GraphicsQueue);

        if (Window != null)
            vk.GetDeviceQueue(Device, presentQueueIdx, 0, out PresentQueue);

        SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);
        if (EnableValidationLayers)
            SilkMarshal.Free((nint)createInfo.PpEnabledLayerNames);

    }

    void CreateCommandPool()
    {
        uint queueIdx = FindQueues().Graphics;

        CommandPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = queueIdx,
        };

        CheckResult(vk.CreateCommandPool(Device, poolInfo, null, out CommandPool), nameof(vk.CreateCommandPool));
    }

    public CommandBuffer StartOneTimeCommand()
    {
        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = CommandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        CheckResult(vk.AllocateCommandBuffers(Device, allocInfo, out var commandBuffer), nameof(vk.AllocateCommandBuffers));

        CommandBufferBeginInfo cmdBufInfo = new() {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };
        CheckResult(vk.BeginCommandBuffer(commandBuffer, &cmdBufInfo), nameof(vk.BeginCommandBuffer));

        return commandBuffer;
    }

    public void RunAndDeleteOneTimeCommand(CommandBuffer commandBuffer, Queue queue)
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
        CheckResult(vk.CreateFence(Device, fenceInfo, null, out var fence), nameof(vk.CreateFence));

        CheckResult(vk.QueueSubmit(queue, 1, &submitInfo, fence), nameof(vk.QueueSubmit));
        CheckResult(vk.WaitForFences(Device, 1, &fence, true, ulong.MaxValue), nameof(vk.WaitForFences));

        vk.DestroyFence(Device, fence, null);
        vk.FreeCommandBuffers(Device, CommandPool, 1, &commandBuffer);
    }

    public uint? GetMemoryTypeIdx(uint typeBits, MemoryPropertyFlags memoryPropertyFlags)
    {
        for (uint i = 0; i < MemoryProperties.MemoryTypeCount; i++)
        {
            if ((typeBits & 1) == 1)
            {
                if ((MemoryProperties.MemoryTypes[(int)i].PropertyFlags & memoryPropertyFlags) == memoryPropertyFlags)
                {
                    return i;
                }
            }
            typeBits >>= 1;
        }

        return null;
    }

    public ulong GetBufferDeviceAddress(Buffer buffer)
    {
        BufferDeviceAddressInfo bufferDeviceAI = new() {
            SType = StructureType.BufferDeviceAddressInfo,
            Buffer = buffer,
        };
        return vk.GetBufferDeviceAddress(Device, bufferDeviceAI);
    }

    public VulkanRayDevice(IWindow window)
    {
        Window = window;
        CreateInstance();
        CreateWindowSurface();
        PickPhysicalDevice();
        FindQueues();
        CreateLogicalDevice();
        CreateCommandPool();
    }

    public void Dispose()
    {
        vk.DestroyCommandPool(Device, CommandPool, null);

        debugUtils?.DestroyDebugUtilsMessenger(Instance, debugMessenger, null);

        vk.DestroyDevice(Device, null);
        KhrSurface?.DestroySurface(Instance, Surface, null);
        vk.DestroyInstance(Instance, null);

        vk.Dispose();
    }
}