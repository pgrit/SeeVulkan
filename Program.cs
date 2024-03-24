﻿using SeeVulkan;

var options = WindowOptions.DefaultVulkan with {
    Size = new(800, 600),
    Title = "SeeVulkan",
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

var device = new VulkanRayDevice(window);
var swapChain = new SwapChain(device);

// TODO resize these storage images upon window resize
var renderTarget = new StorageImage(device, Format.R32G32B32A32Sfloat);
var toneMapTarget = new StorageImage(device, swapChain.ImageFormat);

var mesh1 = new MeshAccel(device, [
    new( 1.0f,  1.0f, 0.0f),
    new(-1.0f,  1.0f, 0.0f),
    new( 0.0f, -1.0f, 0.0f),
], [
    0, 1, 2
]);

var mesh2 = new MeshAccel(device, [
    new( 1.0f, -1.0f, 0.0f),
    new(-1.0f, -1.0f, 0.0f),
    new( 0.0f,  1.0f, 0.0f),
], [
    0, 1, 2
]);

var topLevelAccel = new TopLevelAccel(device, [mesh1, mesh2]);

var rtPipe = new RayTracingPipeline(device, topLevelAccel, renderTarget.ImageView);

window.FramebufferResize += newSize => {
    swapChain.NotifyResize();
};
window.Render += swapChain.DrawFrame;
window.Run();

rtPipe.Dispose();

mesh2.Dispose();
mesh1.Dispose();
topLevelAccel.Dispose();

toneMapTarget.Dispose();
renderTarget.Dispose();

swapChain.Dispose();
device.Dispose();
window.Dispose();


// NEXT UP: separate the tone mapping into a pipe, make a "renderpipe" that puts them together, then put everything into one "raytracer" object


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





