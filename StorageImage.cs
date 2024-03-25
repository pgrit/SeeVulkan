namespace SeeVulkan;

unsafe class StorageImage : IDisposable
{
    public Image Image;
    public ImageView ImageView;

    VulkanRayDevice rayDevice;
    private DeviceMemory memory;

    Vk vk => rayDevice.Vk;
    IWindow window => rayDevice.Window;
    Device device => rayDevice.Device;

    public StorageImage(VulkanRayDevice rayDevice, Format colorFormat)
    {
        this.rayDevice = rayDevice;

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
        CheckResult(vk.CreateImage(device, &createInfo, null, out Image), nameof(vk.CreateImage));

        vk.GetImageMemoryRequirements(device, Image, out var memReqs);
        MemoryAllocateInfo memoryAllocateInfo = new() {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReqs.Size,
            MemoryTypeIndex = rayDevice.GetMemoryTypeIdx(memReqs.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit).Value
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