namespace SeeVulkan;

unsafe class SwapChain : VulkanComponent, IDisposable
{
    const int MAX_FRAMES_IN_FLIGHT = 2;

    public Image[] Images;
    public ImageView[] ImageViews;
    public Extent2D Extent;
    public Format ImageFormat;
    public bool IsLinearColorSpace;

    public CommandBuffer[] CommandBuffers;

    bool enableHDR;

    void CreateSwapChain(bool enableHDR)
    {
        var khrSurface = rayDevice.KhrSurface;
        var physicalDevice = rayDevice.PhysicalDevice;
        var surface = rayDevice.Surface;

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

        // TODO requesting ExtendedSrgb on Windows automatically turns HDR mode on. This might not be desired
        //      by the user, so we should find a way to only pick these color spaces if HDR is already on
        (Format Format, ColorSpaceKHR ColorSpace)[] preferredFormats =
        enableHDR ? [
            (Format.R32G32B32A32Sfloat, ColorSpaceKHR.SpaceExtendedSrgbLinearExt),
            (Format.R16G16B16A16Sfloat, ColorSpaceKHR.SpaceExtendedSrgbLinearExt),
            (Format.B8G8R8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr)
        ] : [
            (Format.B8G8R8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr)
        ];

        SurfaceFormatKHR SelectFormat()
        {
            for (int i = 0; i < preferredFormats.Length; ++i)
            {
                foreach (var f in formats)
                {
                    if (f.Format == preferredFormats[i].Format && f.ColorSpace == preferredFormats[i].ColorSpace)
                        return f;
                }
            }
            return formats[0];
        }
        var surfaceFormat = SelectFormat();

        var presentMode = presentModes.FirstOrDefault(
            mode => mode == PresentModeKHR.MailboxKhr,
            PresentModeKHR.FifoKhr
        );

        Extent = capabilities.CurrentExtent;
        if (capabilities.CurrentExtent.Width == uint.MaxValue)
        {
            Extent = new()
            {
                Width = Math.Clamp((uint)rayDevice.Window.FramebufferSize.X, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
                Height = Math.Clamp((uint)rayDevice.Window.FramebufferSize.Y, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height)
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
            ImageExtent = Extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = presentMode,
            Clipped = true,
            OldSwapchain = default
        };

        if (!vk.TryGetDeviceExtension(rayDevice.Instance, rayDevice.Device, out khrSwapChain))
            throw new NotSupportedException("VK_KHR_swapchain extension not found.");

        CheckResult(khrSwapChain.CreateSwapchain(rayDevice.Device, swapchainCreateInfo, null, out swapChain), nameof(khrSwapChain.CreateSwapchain));

        khrSwapChain.GetSwapchainImages(rayDevice.Device, swapChain, ref imageCount, null);
        Images = new Image[imageCount];
        fixed (Image* swapChainImagesPtr = Images)
            khrSwapChain.GetSwapchainImages(rayDevice.Device, swapChain, ref imageCount, swapChainImagesPtr);

        ImageFormat = surfaceFormat.Format;
        IsLinearColorSpace = surfaceFormat.ColorSpace == ColorSpaceKHR.SpaceExtendedSrgbLinearExt;

        CommandBuffers = new CommandBuffer[imageCount];
    }

    KhrSwapchain khrSwapChain;
    SwapchainKHR swapChain;

    void CreateImageViews()
    {
        ImageViews = new ImageView[Images.Length];

        for (int i = 0; i < Images.Length; i++)
        {
            ImageViewCreateInfo createInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = Images[i],
                ViewType = ImageViewType.Type2D,
                Format = ImageFormat,
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

            CheckResult(vk.CreateImageView(rayDevice.Device, createInfo, null, out ImageViews[i]),
                nameof(vk.CreateImageView));
        }
    }

    void CreateSyncObjects()
    {
        imageAvailableSemaphores = new Semaphore[MAX_FRAMES_IN_FLIGHT];
        renderFinishedSemaphores = new Semaphore[MAX_FRAMES_IN_FLIGHT];
        inFlightFences = new Fence[MAX_FRAMES_IN_FLIGHT];
        imagesInFlight = new Fence[Images.Length];

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
            CheckResult(vk.CreateSemaphore(rayDevice.Device, semaphoreInfo, null, out imageAvailableSemaphores[i]), nameof(vk.CreateSemaphore));
            CheckResult(vk.CreateSemaphore(rayDevice.Device, semaphoreInfo, null, out renderFinishedSemaphores[i]), nameof(vk.CreateSemaphore));
            CheckResult(vk.CreateFence(rayDevice.Device, fenceInfo, null, out inFlightFences[i]), nameof(vk.CreateFence));
        }
    }

    public SwapChain(VulkanRayDevice rayDevice, bool enableHDR) : base(rayDevice)
    {
        this.enableHDR = enableHDR;
        CreateSwapChain(enableHDR);
        CreateImageViews();
        CreateSyncObjects();
    }

    int currentFrame = 0;
    bool resized = false;
    private Semaphore[] imageAvailableSemaphores;
    private Semaphore[] renderFinishedSemaphores;
    private Fence[] inFlightFences;
    private Fence[] imagesInFlight;

    public uint Width => Extent.Width;
    public uint Height => Extent.Height;

    public void NotifyResize()
    => resized = true;

    public event Action OnRecreate;

    public void DrawFrame(double delta)
    {
        vk.WaitForFences(rayDevice.Device, 1, inFlightFences[currentFrame], true, ulong.MaxValue);

        if (resized) // First check for resize to avoid unnecessary validation error by AcquireNextImage below
        {
            Recreate();
            resized = false;
            return;
        }

        uint imageIndex = 0;
        var result = khrSwapChain.AcquireNextImage(rayDevice.Device, swapChain, ulong.MaxValue, imageAvailableSemaphores[currentFrame], default, ref imageIndex);
        if (result == Result.ErrorOutOfDateKhr || result == Result.SuboptimalKhr)
        {
            Recreate();
            resized = false;
            return;
        }

        if (imagesInFlight[imageIndex].Handle != default)
        {
            vk.WaitForFences(rayDevice.Device, 1, imagesInFlight[imageIndex], true, ulong.MaxValue);
        }
        imagesInFlight[imageIndex] = inFlightFences[currentFrame];

        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
        };

        var waitSemaphores = stackalloc[] { imageAvailableSemaphores[currentFrame] };
        var waitStages = stackalloc[] { PipelineStageFlags.ColorAttachmentOutputBit };

        var buffer = CommandBuffers[imageIndex];

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

        vk.ResetFences(rayDevice.Device, 1, inFlightFences[currentFrame]);

        CheckResult(vk.QueueSubmit(rayDevice.GraphicsQueue, 1, submitInfo, inFlightFences[currentFrame]), nameof(vk.QueueSubmit));

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

        khrSwapChain.QueuePresent(rayDevice.PresentQueue, presentInfo);

        currentFrame = (currentFrame + 1) % MAX_FRAMES_IN_FLIGHT;
    }

    void CleanUp()
    {
        foreach (var imageView in ImageViews)
            vk.DestroyImageView(rayDevice.Device, imageView, null);
        fixed (CommandBuffer* commandBuffersPtr = CommandBuffers)
        {
            vk.FreeCommandBuffers(device, rayDevice.CommandPool, (uint)CommandBuffers.Length, commandBuffersPtr);
        }
        khrSwapChain.DestroySwapchain(rayDevice.Device, swapChain, null);
    }

    void Recreate() {
        var framebufferSize = rayDevice.Window.FramebufferSize;

        while (framebufferSize.X == 0 || framebufferSize.Y == 0)
        {
            framebufferSize = rayDevice.Window.FramebufferSize;
            rayDevice.Window.DoEvents();
        }

        vk.DeviceWaitIdle(rayDevice.Device);

        CleanUp();

        CreateSwapChain(enableHDR);
        CreateImageViews();

        imagesInFlight = new Fence[Images.Length];

        OnRecreate.Invoke();
    }

    public void Dispose()
    {
        CleanUp();
        for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
        {
            vk.DestroySemaphore(rayDevice.Device, renderFinishedSemaphores[i], null);
            vk.DestroySemaphore(rayDevice.Device, imageAvailableSemaphores[i], null);
            vk.DestroyFence(rayDevice.Device, inFlightFences[i], null);
        }
    }
}