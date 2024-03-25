namespace SeeVulkan;

unsafe class StorageImage : VulkanComponent, IDisposable
{
    public Image Image;
    public ImageView ImageView;

    private DeviceMemory memory;

    IWindow window => rayDevice.Window;

    public void CopyToHost()
    {
        int width = window.FramebufferSize.X;
        int height = window.FramebufferSize.Y;

        // Create a new temp image with the right layout, memory type, tiling, etc
        ImageCreateInfo imageCreateCI = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = Format.R32G32B32A32Sfloat,
            Extent = new((uint)width, (uint)height, 1),
            ArrayLayers = 1,
            MipLevels = 1,
            InitialLayout = ImageLayout.Undefined,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Linear,
            Usage = ImageUsageFlags.TransferDstBit
        };
		CheckResult(vk.CreateImage(device, &imageCreateCI, null, out var tmpImage), nameof(vk.CreateImage));

		vk.GetImageMemoryRequirements(device, tmpImage, out var memReqs);
        MemoryAllocateInfo memAllocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReqs.Size,
            MemoryTypeIndex = rayDevice.GetMemoryTypeIdx(memReqs.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit).Value
        };
        CheckResult(vk.AllocateMemory(device, memAllocInfo, null, out var tmpMemory), nameof(vk.AllocateMemory));
		CheckResult(vk.BindImageMemory(device, tmpImage, tmpMemory, 0), nameof(vk.BindImageMemory));

        // Copy original image into this buffer
        var cmdBuffer = rayDevice.StartOneTimeCommand();

        ImageSubresourceRange subresourceRange = new()
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1,
        };

        ImageMemoryBarrier transferSrc = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.General,
            NewLayout = ImageLayout.TransferSrcOptimal,
            Image = Image,
            SubresourceRange = subresourceRange
        };
        vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
            0, 0, null, 0, null, 1, &transferSrc);

        ImageMemoryBarrier transferDst = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.TransferDstOptimal,
            Image = tmpImage,
            SubresourceRange = subresourceRange
        };
        vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
            0, 0, null, 0, null, 1, &transferDst);

        ImageSubresourceLayers subLayers = new()
        {
            AspectMask = ImageAspectFlags.ColorBit,
            MipLevel = 0,
            BaseArrayLayer = 0,
            LayerCount = 1,
        };

        ImageCopy region = new()
        {
            SrcOffset = new(0, 0, 0),
            SrcSubresource = subLayers,
            DstOffset = new(0, 0, 0),
            DstSubresource = subLayers,
            Extent = new((uint)window.FramebufferSize.X, (uint)window.FramebufferSize.Y, 1)
        };

        vk.CmdCopyImage(cmdBuffer, Image, ImageLayout.TransferSrcOptimal, tmpImage,
            ImageLayout.TransferDstOptimal, [region]);

        ImageMemoryBarrier resetSrc = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.TransferSrcOptimal,
            NewLayout = ImageLayout.General,
            Image = Image,
            SubresourceRange = subresourceRange
        };
        vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
            0, 0, null, 0, null, 1, &resetSrc);

        ImageMemoryBarrier prepDst = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.TransferDstOptimal,
            NewLayout = ImageLayout.General,
            Image = tmpImage,
            SubresourceRange = subresourceRange
        };
        vk.CmdPipelineBarrier(cmdBuffer, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
            0, 0, null, 0, null, 1, &prepDst);

        rayDevice.RunAndDeleteOneTimeCommand(cmdBuffer, rayDevice.GraphicsQueue);

        // Download the newly made image from the host
        ImageSubresource subResource = new()
        {
            AspectMask = ImageAspectFlags.ColorBit,
            ArrayLayer = 0,
            MipLevel = 0
        };
        vk.GetImageSubresourceLayout(device, tmpImage, &subResource, out var subResourceLayout);

        byte* pData = null;
        vk.MapMemory(device, tmpMemory, 0, VK_WHOLE_SIZE, 0, (void**)&pData);
        pData += subResourceLayout.Offset;

        // We assume RGBA ordering
        var outImage = new SimpleImageIO.Image(width, height, 4);
        for (int y = 0; y < height; y++)
        {
            float* row = (float*)pData;
            for (int x = 0; x < width; x++)
            {
                outImage[x, y, 0] = *row++;
                outImage[x, y, 1] = *row++;
                outImage[x, y, 2] = *row++;
                outImage[x, y, 3] = *row++;
            }
            pData += subResourceLayout.RowPitch;
        }
        SimpleImageIO.TevIpc.ShowImage("testframe", outImage);

        vk.FreeMemory(device, tmpMemory, null);
        vk.DestroyImage(device, tmpImage, null);
    }

    public StorageImage(VulkanRayDevice rayDevice, Format colorFormat)
    : base(rayDevice)
    {
        this.rayDevice = rayDevice;

        ImageUsageFlags usage = ImageUsageFlags.TransferSrcBit | ImageUsageFlags.StorageBit;
        MemoryPropertyFlags memFlags = MemoryPropertyFlags.DeviceLocalBit;

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
            Usage = usage,
            InitialLayout = ImageLayout.Undefined
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

        ImageSubresourceRange subresourceRange = new()
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1,
        };

        ImageViewCreateInfo imageView = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            ViewType = ImageViewType.Type2D,
            Format = colorFormat,
            SubresourceRange = subresourceRange,
            Image = Image
        };
        CheckResult(vk.CreateImageView(device, &imageView, null, out ImageView), nameof(vk.CreateImageView));

        var commandBuffer = rayDevice.StartOneTimeCommand();

        ImageMemoryBarrier imageMemoryBarrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.General,
            Image = Image,
            SubresourceRange = subresourceRange
        };

        vk.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
            0, 0, null, 0, null, 1, &imageMemoryBarrier);

        rayDevice.RunAndDeleteOneTimeCommand(commandBuffer, rayDevice.GraphicsQueue);
    }

    public void Dispose()
    {
        vk.DestroyImageView(device, ImageView, null);
        vk.FreeMemory(device, memory, null);
        vk.DestroyImage(device, Image, null);
    }
}