namespace SeeVulkan;

unsafe class Texture : VulkanComponent, IDisposable
{
    public Image Image;
    public ImageView ImageView;

    private DeviceMemory memory;
    public Sampler Sampler;

    public Texture(VulkanRayDevice rayDevice, SimpleImageIO.Image sourceImage) : base(rayDevice)
    {
        // Create the final texture image and its memory
        Format format = sourceImage.NumChannels switch
        {
            1 => Format.R16Sfloat,
            3 => Format.R16G16B16A16Sfloat,
            4 => Format.R16G16B16A16Sfloat,
            _ => throw new ArgumentOutOfRangeException(nameof(sourceImage))
        };

        ImageUsageFlags usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit;
        MemoryPropertyFlags memFlags = MemoryPropertyFlags.DeviceLocalBit;

        // TODO for now we assume that this format is always supported. We can use this to check and then
        //      switch to an alternative (e.g., 8bit per channel RGBA_UNORM)
        // vk.GetPhysicalDeviceImageFormatProperties(rayDevice.PhysicalDevice, format, ImageType.Type2D,
        //     ImageTiling.Optimal, usage, 0, out var formatProps);

        ImageCreateInfo createInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new() {
                Width = (uint)sourceImage.Width,
                Height = (uint)sourceImage.Height,
                Depth = 1
            },
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            InitialLayout = ImageLayout.Undefined,
            SharingMode = SharingMode.Exclusive
        };
        CheckResult(vk.CreateImage(device, &createInfo, null, out Image), nameof(vk.CreateImage));

        vk.GetImageMemoryRequirements(device, Image, out var memReqs);
        MemoryAllocateInfo memoryAllocateInfo = new() {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReqs.Size,
            MemoryTypeIndex = rayDevice.GetMemoryTypeIdx(memReqs.MemoryTypeBits, memFlags).Value
        };

        CheckResult(vk.AllocateMemory(device, &memoryAllocateInfo, null, out memory), nameof(vk.AllocateMemory));
        CheckResult(vk.BindImageMemory(device, Image, memory, 0), nameof(vk.BindImageMemory));

        // Create a staging buffer to transfer our texture from the host
        // var imgData = new ReadOnlySpan<float>(sourceImage.DataPointer.ToPointer(),
        //     sourceImage.Width * sourceImage.Height * sourceImage.NumChannels * sizeof(float));

        int numChan = sourceImage.NumChannels == 1 ? 1 : 4;
        Half[] imgData = new Half[sourceImage.Width * sourceImage.Height * numChan];
        int idx = 0;
        for (int row = 0; row < sourceImage.Height; ++row)
        {
            for (int col = 0; col < sourceImage.Width; ++col)
            {
                for (int chan = 0; chan < sourceImage.NumChannels; ++chan)
                {
                    imgData[idx++] = (Half)sourceImage[col, row, chan];
                }

                // Add alpha channel if the texture does not have it (format on the GPU requires it)
                if (sourceImage.NumChannels == 3)
                    imgData[idx++] = (Half)1;
            }
        }

        using var stagingBuffer = VulkanBuffer.Make<Half>(rayDevice, BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            imgData);

        var cmdBuffer = rayDevice.StartOneTimeCommand();

        ImageSubresourceRange subresourceRange = new()
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1,
        };

        ImageMemoryBarrier prepDst = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.TransferDstOptimal,
            Image = Image,
            SubresourceRange = subresourceRange
        };
        vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
            0, 0, null, 0, null, 1, &prepDst);

        BufferImageCopy region = new()
        {
            BufferImageHeight = (uint)sourceImage.Height,
            BufferRowLength = (uint)sourceImage.Width,
            BufferOffset = 0,
            ImageSubresource = new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseArrayLayer = 0,
                LayerCount = 1,
                MipLevel = 0,
            },
            ImageExtent = new((uint)sourceImage.Width, (uint)sourceImage.Height, 1),
            ImageOffset = new(0, 0, 0)
        };
        vk.CmdCopyBufferToImage(
            cmdBuffer,
            stagingBuffer.Buffer,
            Image,
            ImageLayout.TransferDstOptimal,
            1,
            &region
        );

        ImageMemoryBarrier finalLayout = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.TransferDstOptimal,
            NewLayout = ImageLayout.ReadOnlyOptimal,
            Image = Image,
            SubresourceRange = subresourceRange
        };
        vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
            0, 0, null, 0, null, 1, &finalLayout);

        rayDevice.RunAndDeleteOneTimeCommand(cmdBuffer, rayDevice.GraphicsQueue);

        ImageViewCreateInfo imageView = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = subresourceRange,
            Image = Image
        };
        CheckResult(vk.CreateImageView(device, &imageView, null, out ImageView), nameof(vk.CreateImageView));

        SamplerCreateInfo samplerInfo = new()
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            AddressModeU = SamplerAddressMode.Repeat, // TODO switch between repeat and clamp based on mtl config!
            AddressModeV = SamplerAddressMode.Repeat,
            AddressModeW = SamplerAddressMode.Repeat,
            AnisotropyEnable = false,
            MaxAnisotropy = 0,
            BorderColor = BorderColor.IntOpaqueBlack,
            UnnormalizedCoordinates = false,
            CompareEnable = false,
            CompareOp = CompareOp.Always,
            MipmapMode = SamplerMipmapMode.Linear,
            MipLodBias = 0.0f,
            MinLod = 0.0f,
            MaxLod = 0.0f
        };

        CheckResult(vk.CreateSampler(device, &samplerInfo, null, out Sampler), nameof(vk.CreateSampler));
    }

    public void Dispose()
    {
        vk.DestroySampler(device, Sampler, null);
        vk.DestroyImageView(device, ImageView, null);
        vk.FreeMemory(device, memory, null);
        vk.DestroyImage(device, Image, null);
    }
}
