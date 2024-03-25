using System.Reflection;

namespace SeeVulkan;

unsafe class RayTracingPipeline : VulkanComponent, IDisposable
{
    private DescriptorSetLayout descriptorSetLayout;
    private PipelineLayout pipelineLayout;
    private Shader rgenShader;
    private Shader rmissShader;
    private Shader rchitShader;
    private Pipeline pipeline;

    VulkanBuffer raygenShaderBindingTable, missShaderBindingTable, hitShaderBindingTable;

    KhrRayTracingPipeline rayPipe;
    private DescriptorPool descriptorPool;
    private uint handleSizeAligned;
    private DescriptorSet descriptorSet;
    private VulkanBuffer uniformBuffer;

    public RayTracingPipeline(VulkanRayDevice rayDevice, TopLevelAccel topLevelAccel, ImageView storageImageView,
        Matrix4x4 camToWorld, Matrix4x4 viewToCam)
    : base(rayDevice)
    {
        if (!vk.TryGetDeviceExtension(rayDevice.Instance, device, out rayPipe))
            throw new NotSupportedException($"{KhrRayTracingPipeline.ExtensionName} extension not found.");

        PhysicalDeviceRayTracingPipelinePropertiesKHR rayPipeProps = new() {
            SType = StructureType.PhysicalDeviceRayTracingPipelinePropertiesKhr
        };
        PhysicalDeviceProperties2 devProps = new()
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &rayPipeProps
        };
        vk.GetPhysicalDeviceProperties2(rayDevice.PhysicalDevice, &devProps);

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
            new()
            {
                Binding = 2,
                DescriptorType = DescriptorType.UniformBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.RaygenBitKhr
            }
        };
        uint numBindings = 3;
        DescriptorSetLayoutCreateInfo descSetCreateInfo = new()
        {
            SType =  StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = numBindings,
            PBindings = bindings
        };
        CheckResult(vk.CreateDescriptorSetLayout(device, descSetCreateInfo, null, out descriptorSetLayout), nameof(vk.CreateDescriptorSetLayout));

        var descSet = descriptorSetLayout;
        PipelineLayoutCreateInfo pipelineLayoutCI = new()
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &descSet
        };
        CheckResult(vk.CreatePipelineLayout(device, pipelineLayoutCI, null, out pipelineLayout), nameof(vk.CreatePipelineLayout));

        uint i = 0;
        uint numStages = 3;
        uint groupCount = numStages;
        var shaderStages = stackalloc PipelineShaderStageCreateInfo[(int)numStages];
        var shaderGroups = stackalloc RayTracingShaderGroupCreateInfoKHR[(int)numStages];

        rgenShader = new Shader(rayDevice, ReadResourceText("Shaders.raygen.rgen"), "rgen");

        shaderStages[i] = new()
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.RaygenBitKhr,
            Module = rgenShader.ShaderModule,
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


        rmissShader = new Shader(rayDevice, ReadResourceText("Shaders.miss.rmiss"), "rmiss");
        shaderStages[i] = new()
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.MissBitKhr,
            Module = rmissShader.ShaderModule,
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

        rchitShader = new Shader(rayDevice, ReadResourceText("Shaders.hit.rchit"), "rchit");
        shaderStages[i] = new()
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.ClosestHitBitKhr,
            Module = rchitShader.ShaderModule,
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
            1, &rayTracingPipelineCI, null, out pipeline), nameof(rayPipe.CreateRayTracingPipelines));

        // Create the shader binding table
        uint handleSize = rayPipeProps.ShaderGroupHandleSize;
        handleSizeAligned = AlignedSize(rayPipeProps.ShaderGroupHandleSize, rayPipeProps.ShaderGroupHandleAlignment);
        uint sbtSize = groupCount * handleSizeAligned;

        byte[] shaderHandleStorage = new byte[sbtSize];
        fixed (byte* storage = shaderHandleStorage)
        {
            CheckResult(rayPipe.GetRayTracingShaderGroupHandles(device, pipeline, 0, groupCount, sbtSize, storage),
                nameof(rayPipe.GetRayTracingShaderGroupHandles));
        }

        const BufferUsageFlags bufferUsageFlags = BufferUsageFlags.ShaderBindingTableBitKhr | BufferUsageFlags.ShaderDeviceAddressBit;
        const MemoryPropertyFlags memoryUsageFlags = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
        raygenShaderBindingTable = VulkanBuffer.Make<byte>(rayDevice, bufferUsageFlags, memoryUsageFlags, new Span<byte>(shaderHandleStorage, 0, (int)handleSize));
        missShaderBindingTable = VulkanBuffer.Make<byte>(rayDevice, bufferUsageFlags, memoryUsageFlags, new Span<byte>(shaderHandleStorage, (int)handleSizeAligned, (int)handleSize));
        hitShaderBindingTable = VulkanBuffer.Make<byte>(rayDevice, bufferUsageFlags, memoryUsageFlags, new Span<byte>(shaderHandleStorage, (int)handleSizeAligned * 2, (int)handleSize));

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
        CheckResult(vk.CreateDescriptorPool(device, &descriptorPoolCreateInfo, null, out descriptorPool),
            nameof(vk.CreateDescriptorPool));

        DescriptorSetAllocateInfo descriptorSetAllocateInfo = new() {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &descSet
        };
        CheckResult(vk.AllocateDescriptorSets(device, &descriptorSetAllocateInfo, out descriptorSet),
            nameof(vk.AllocateDescriptorSets));

        var topLvl = topLevelAccel.Handle;
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

        Span<float> matrixData = stackalloc float[4*4*2];
        i = 0;
        for (int row = 0; row < 4; ++row)
            for (int col = 0; col < 4; ++col)
                matrixData[(int)i++] = camToWorld[row, col];
        for (int row = 0; row < 4; ++row)
            for (int col = 0; col < 4; ++col)
                matrixData[(int)i++] = viewToCam[row, col];

        uniformBuffer = VulkanBuffer.Make<float>(rayDevice,
            BufferUsageFlags.UniformBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            matrixData
        );

        DescriptorBufferInfo uniformDescriptor = new()
        {
            Buffer = uniformBuffer.Buffer,
            Offset = 0,
            Range = (ulong)matrixData.Length * sizeof(float)
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
            accelerationStructureWrite,
            resultImageWrite,
            uniformBufferWrite
        };
        uint numDescriptorSets = 3;

        vk.UpdateDescriptorSets(device, numDescriptorSets, writeDescriptorSets, 0, null);
    }

    public void MakeCommands(CommandBuffer commandBuffer, uint width, uint height)
    {
        StridedDeviceAddressRegionKHR raygenShaderSbtEntry = new()
        {
            DeviceAddress = raygenShaderBindingTable.DeviceAddress,
            Stride = handleSizeAligned,
            Size = handleSizeAligned
        };

        StridedDeviceAddressRegionKHR missShaderSbtEntry = new()
        {
            DeviceAddress = missShaderBindingTable.DeviceAddress,
            Stride = handleSizeAligned,
            Size = handleSizeAligned
        };

        StridedDeviceAddressRegionKHR hitShaderSbtEntry = new()
        {
            DeviceAddress = hitShaderBindingTable.DeviceAddress,
            Stride = handleSizeAligned,
            Size = handleSizeAligned
        };

        StridedDeviceAddressRegionKHR callableShaderSbtEntry = new();

        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.RayTracingKhr, pipeline);
        var descSet = descriptorSet;
        vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.RayTracingKhr, pipelineLayout, 0, 1, &descSet, 0, 0);

        rayPipe.CmdTraceRays(
            commandBuffer,
            &raygenShaderSbtEntry,
            &missShaderSbtEntry,
            &hitShaderSbtEntry,
            &callableShaderSbtEntry,
            width,
            height,
            1);
    }

    public void Dispose()
    {
        uniformBuffer.Dispose();
        vk.DestroyDescriptorPool(device, descriptorPool, null);
        raygenShaderBindingTable.Dispose();
        missShaderBindingTable.Dispose();
        hitShaderBindingTable.Dispose();
        rgenShader.Dispose();
        rmissShader.Dispose();
        rchitShader.Dispose();
        vk.DestroyPipeline(device, pipeline, null);
        vk.DestroyPipelineLayout(device, pipelineLayout, null);
        vk.DestroyDescriptorSetLayout(device, descriptorSetLayout, null);
        rayPipe.Dispose();
    }
}