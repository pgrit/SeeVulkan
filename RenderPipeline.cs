namespace SeeVulkan;

unsafe class RenderPipeline : VulkanComponent, IDisposable
{
    public void Dispose()
    {
    }

    public RenderPipeline(VulkanRayDevice rayDevice, SwapChain swapChain, RayTracingPipeline rayPipeline,
        ToneMapPipeline toneMapPipeline, StorageImage renderTarget, StorageImage toneMapTarget)
    : base(rayDevice)
    {
        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = rayDevice.CommandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = (uint)swapChain.CommandBuffers.Length,
        };

        fixed (CommandBuffer* commandBuffersPtr = swapChain.CommandBuffers)
            CheckResult(vk.AllocateCommandBuffers(device, allocInfo, commandBuffersPtr), nameof(vk.AllocateCommandBuffers));

        ImageSubresourceRange subresourceRange = new()
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1,
        };

        for (int i = 0; i < swapChain.CommandBuffers.Length; i++)
        {
            CommandBufferBeginInfo beginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
            };

            CheckResult(vk.BeginCommandBuffer(swapChain.CommandBuffers[i], beginInfo), nameof(vk.BeginCommandBuffer));

            rayPipeline.MakeCommands(swapChain.CommandBuffers[i], swapChain.Width, swapChain.Height);

            ImageMemoryBarrier syncRender = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                OldLayout = ImageLayout.General,
                NewLayout = ImageLayout.General,
                Image = renderTarget.Image,
                SubresourceRange = subresourceRange
            };
            vk.CmdPipelineBarrier(swapChain.CommandBuffers[i], PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
                0, 0, null, 0, null, 1, &syncRender);

            toneMapPipeline.MakeCommands(swapChain.CommandBuffers[i], swapChain.Width, swapChain.Height);

            ImageMemoryBarrier curSwapDest = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                OldLayout = ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                Image = swapChain.Images[i],
                SubresourceRange = subresourceRange
            };
            vk.CmdPipelineBarrier(swapChain.CommandBuffers[i], PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
                0, 0, null, 0, null, 1, &curSwapDest);

            ImageMemoryBarrier rtSrc = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                OldLayout = ImageLayout.General,
                NewLayout = ImageLayout.TransferSrcOptimal,
                Image = toneMapTarget.Image,
                SubresourceRange = subresourceRange
            };
            vk.CmdPipelineBarrier(swapChain.CommandBuffers[i], PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
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
                Extent = new(swapChain.Extent.Width, swapChain.Extent.Height, 1)
            };
            vk.CmdCopyImage(swapChain.CommandBuffers[i], toneMapTarget.Image, ImageLayout.TransferSrcOptimal, swapChain.Images[i],
                ImageLayout.TransferDstOptimal, 1, &copyRegion);

            ImageMemoryBarrier swapImgPresent = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.PresentSrcKhr,
                Image = swapChain.Images[i],
                SubresourceRange = subresourceRange
            };
            vk.CmdPipelineBarrier(swapChain.CommandBuffers[i], PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
                0, 0, null, 0, null, 1, &swapImgPresent);

            ImageMemoryBarrier rtImgGeneral = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                OldLayout = ImageLayout.TransferSrcOptimal,
                NewLayout = ImageLayout.General,
                Image = toneMapTarget.Image,
                SubresourceRange = subresourceRange
            };
            vk.CmdPipelineBarrier(swapChain.CommandBuffers[i], PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
                0, 0, null, 0, null, 1, &rtImgGeneral);

            CheckResult(vk.EndCommandBuffer(swapChain.CommandBuffers[i]), nameof(vk.EndCommandBuffer));
        }
    }
}





