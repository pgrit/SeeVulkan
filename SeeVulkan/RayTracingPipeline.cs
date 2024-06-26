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
    private VulkanBuffer perMeshDataBuffer;

    EmitterData emitters;

    public RayTracingPipeline(VulkanRayDevice rayDevice, TopLevelAccel topLevelAccel, ImageView storageImageView,
        Matrix4x4 camToWorld, Matrix4x4 viewToCam, ShaderDirectory shaderDirectory, MeshAccel[] meshAccels,
        MaterialLibrary materials, EmitterData emitters)
    : base(rayDevice)
    {
        this.emitters = emitters;

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
            new() // Camera matrices and frame index
            {
                Binding = 2,
                DescriptorType = DescriptorType.UniformBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.RaygenBitKhr
            },
            new() // Material buffer
            {
                Binding = 3,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ClosestHitBitKhr | ShaderStageFlags.AnyHitBitKhr | ShaderStageFlags.RaygenBitKhr
            },
            new() // Texture buffer
            {
                Binding = 4,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = materials.NumTextures,
                StageFlags = ShaderStageFlags.ClosestHitBitKhr | ShaderStageFlags.AnyHitBitKhr | ShaderStageFlags.RaygenBitKhr,
                PImmutableSamplers = null
            }
        };
        uint numBindings = 5;
        DescriptorSetLayoutCreateInfo descSetCreateInfo = new()
        {
            SType =  StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = numBindings,
            PBindings = bindings
        };
        CheckResult(vk.CreateDescriptorSetLayout(device, descSetCreateInfo, null, out descriptorSetLayout), nameof(vk.CreateDescriptorSetLayout));

        // Buffer that holds all device addresses of all vertex and index buffers. It's address in turn is
        // given to the shader as a push constant
        PerMeshData[] perMeshData = new PerMeshData[meshAccels.Length];
        for (int k = 0; k < perMeshData.Length; ++k)
        {
            perMeshData[k] = new()
            {
                VertexBufferAddress = meshAccels[k].VertexBuffer.DeviceAddress,
                IndexBufferAddress = meshAccels[k].IndexBuffer.DeviceAddress,
                MaterialId = (uint)meshAccels[k].Mesh.MaterialId,
                Emission = emitters.MeshEmissionData[k]
            };
        }
        perMeshDataBuffer = VulkanBuffer.Make<PerMeshData>(rayDevice,
            BufferUsageFlags.ShaderDeviceAddressBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            perMeshData
        );

        PushConstantRange pushConstantRange = new()
        {
            StageFlags = ShaderStageFlags.ClosestHitBitKhr | ShaderStageFlags.AnyHitBitKhr | ShaderStageFlags.RaygenBitKhr,
            Offset = 0,
            Size = sizeof(ulong) // The device address of the per-mesh data buffer
        };

        var descSet = descriptorSetLayout;
        PipelineLayoutCreateInfo pipelineLayoutCI = new()
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &descSet,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushConstantRange
        };
        CheckResult(vk.CreatePipelineLayout(device, pipelineLayoutCI, null, out pipelineLayout), nameof(vk.CreatePipelineLayout));

        uint i = 0;
        uint numStages = 3;
        uint groupCount = numStages;
        var shaderStages = stackalloc PipelineShaderStageCreateInfo[(int)numStages];
        var shaderGroups = stackalloc RayTracingShaderGroupCreateInfoKHR[(int)numStages];

        rgenShader = new Shader(rayDevice, shaderDirectory.ShaderCodes["raygen.rgen"]);

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

        rmissShader = new Shader(rayDevice, shaderDirectory.ShaderCodes["miss.rmiss"]);
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

        rchitShader = new Shader(rayDevice, shaderDirectory.ShaderCodes["hit.rchit"]);
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
            new() { // Camera matrices and frame index
                Type = DescriptorType.UniformBuffer,
                DescriptorCount = 1
            },
            new() { // Material array
                Type = DescriptorType.StorageBuffer,
                DescriptorCount = 1
            },
            new() { // Texture samplers
                Type = DescriptorType.CombinedImageSampler,
                DescriptorCount = materials.NumTextures
            },
        };
        uint numPoolSizes = numBindings;

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

        // Allocate memory for the uniforms (matrices and other info)
        ReadOnlySpan<byte> matrixData = stackalloc byte[sizeof(UniformData)];
        uniformBuffer = VulkanBuffer.Make(rayDevice,
            BufferUsageFlags.UniformBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            matrixData
        );

        DescriptorBufferInfo uniformDescriptor = new()
        {
            Buffer = uniformBuffer.Buffer,
            Offset = 0,
            Range = (ulong)sizeof(UniformData)
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

        // Descriptor set for the material buffer
        DescriptorBufferInfo mtlBufferDescriptor = new()
        {
            Buffer = materials.MaterialBuffer.Buffer,
            Offset = 0,
            Range = (ulong)(materials.NumMaterials * sizeof(MaterialParameters))
        };

        WriteDescriptorSet materialBufferWrite = new()
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DescriptorType = DescriptorType.StorageBuffer,
            DstBinding = 3,
            DescriptorCount = 1,
            PBufferInfo = &mtlBufferDescriptor
        };

        // Descriptor set for the texture samplers
        var textureImageInfos = new DescriptorImageInfo[materials.NumTextures];
        for (int k = 0; k < materials.Textures.Length; ++k)
        {
            textureImageInfos[k] = new()
            {
                ImageLayout = ImageLayout.ReadOnlyOptimal,
                ImageView = materials.Textures[k].ImageView,
                Sampler = materials.Textures[k].Sampler,
            };
        }

        fixed (DescriptorImageInfo* pTextureImageInfos = textureImageInfos)
        {
            WriteDescriptorSet textureSamplerWrite = new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = descriptorSet,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DstBinding = 4,
                DescriptorCount = materials.NumTextures,
                PImageInfo = pTextureImageInfos
            };

            var writeDescriptorSets = stackalloc WriteDescriptorSet[] {
                accelerationStructureWrite,
                resultImageWrite,
                uniformBufferWrite,
                materialBufferWrite,
                textureSamplerWrite
            };
            uint numDescriptorSets = numBindings;

            vk.UpdateDescriptorSets(device, numDescriptorSets, writeDescriptorSets, 0, null);
        }
    }

    struct UniformData
    {
        public fixed float CameraMatrices[4*4*2];
        public uint FrameIdx;
        public ulong EmitterBufferAddress;
        public uint NumEmitter;
    }

    public void UpdateUniforms(Matrix4x4 camToWorld, Matrix4x4 viewToCam, uint frameIdx)
    {
        void* ptr = null;
        vk.MapMemory(device, uniformBuffer.Memory, 0, (ulong)sizeof(UniformData), 0, ref ptr);
        var pData = (UniformData*)ptr;

        int i = 0;
        for (int row = 0; row < 4; ++row)
            for (int col = 0; col < 4; ++col)
                pData->CameraMatrices[i++] = camToWorld[row, col];
        for (int row = 0; row < 4; ++row)
            for (int col = 0; col < 4; ++col)
                pData->CameraMatrices[i++] = viewToCam[row, col];

        pData->FrameIdx = frameIdx;
        pData->EmitterBufferAddress = emitters.EmitterList.DeviceAddress;
        pData->NumEmitter = emitters.NumEmitters;

        vk.UnmapMemory(device, uniformBuffer.Memory);
    }

    struct PerMeshData {
        public ulong VertexBufferAddress;
        public ulong IndexBufferAddress;
        public uint MaterialId;
        public MeshEmission Emission;
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

        ulong addr = perMeshDataBuffer.DeviceAddress;
        vk.CmdPushConstants(commandBuffer, pipelineLayout,
            ShaderStageFlags.ClosestHitBitKhr | ShaderStageFlags.AnyHitBitKhr | ShaderStageFlags.RaygenBitKhr,
            0, sizeof(ulong), &addr);

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
        perMeshDataBuffer.Dispose();
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