namespace SeeVulkan;

unsafe class ToneMapPipeline : VulkanComponent, IDisposable
{
    private Shader tmShader;
    private DescriptorSetLayout descriptorSetLayout;
    private PipelineLayout pipelineLayout;
    private Pipeline pipeline;
    private DescriptorPool descriptorPool;
    private VulkanBuffer uniformBuffer;
    DescriptorSet descriptorSet;

    public void Dispose()
    {
        uniformBuffer.Dispose();
        vk.DestroyDescriptorPool(device, descriptorPool, null);
        vk.DestroyPipeline(device, pipeline, null);
        tmShader.Dispose();
        vk.DestroyPipelineLayout(device, pipelineLayout, null);
        vk.DestroyDescriptorSetLayout(device, descriptorSetLayout, null);
    }

    public ToneMapPipeline(VulkanRayDevice rayDevice, StorageImage renderImage, StorageImage toneMappedImage, bool swapChainIsLinear)
    : base(rayDevice)
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
        CheckResult(vk.CreateDescriptorSetLayout(device, descSetCreateInfo, null, out descriptorSetLayout), nameof(vk.CreateDescriptorSetLayout));

        fixed (DescriptorSetLayout* pSetLayout = &descriptorSetLayout)
        {
            PipelineLayoutCreateInfo pipelineLayoutCI = new()
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = pSetLayout
            };
            CheckResult(vk.CreatePipelineLayout(device, pipelineLayoutCI, null, out pipelineLayout), nameof(vk.CreatePipelineLayout));
        }

        tmShader = new Shader(rayDevice, ReadResourceText("Shaders.tonemap.comp"), "comp");

        ComputePipelineCreateInfo computePipelineCreateInfo = new()
        {
            SType = StructureType.ComputePipelineCreateInfo,
            Layout = pipelineLayout,
            Stage = new()
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ComputeBit,
                Module = tmShader.ShaderModule,
                PName = (byte*)SilkMarshal.StringToPtr("main")
            },
        };
        CheckResult(vk.CreateComputePipelines(device, new PipelineCache(), 1, &computePipelineCreateInfo, null, out pipeline),
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
        CheckResult(vk.CreateDescriptorPool(device, &descriptorPoolCreateInfo, null, out descriptorPool),
            nameof(vk.CreateDescriptorPool));

        fixed (DescriptorSetLayout* pSetLayout = &descriptorSetLayout)
        {
            DescriptorSetAllocateInfo descriptorSetAllocateInfo = new()
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = descriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = pSetLayout
            };
            CheckResult(vk.AllocateDescriptorSets(device, &descriptorSetAllocateInfo, out descriptorSet),
                nameof(vk.AllocateDescriptorSets));
        }

        DescriptorImageInfo inputImageDescriptor = new()
        {
            ImageView = renderImage.ImageView,
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
            ImageView = toneMappedImage.ImageView,
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
        uniformBuffer = VulkanBuffer.Make<Bool32>(rayDevice,
            BufferUsageFlags.UniformBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            isLinearUniform
        );

        DescriptorBufferInfo uniformDescriptor = new()
        {
            Buffer = uniformBuffer.Buffer,
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
    }

    public void MakeCommands(CommandBuffer commandBuffer, uint width, uint height)
    {
        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, pipeline);
        var tmDescSet = descriptorSet;
        vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Compute, pipelineLayout, 0, 1, &tmDescSet, 0, 0);
        vk.CmdDispatch(commandBuffer, width, height, 1);
    }
}





