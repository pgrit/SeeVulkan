namespace SeeVulkan;

class Renderer : IDisposable
{
    VulkanRayDevice device;
    SwapChain swapChain;
    StorageImage renderTarget;
    StorageImage toneMapTarget;
    TopLevelAccel topLevelAccel;
    RayTracingPipeline rtPipe;
    ToneMapPipeline tmPipe;
    RenderPipeline pipe;

    MeshAccel[] meshAccels;

    public Renderer(IWindow window, ReadOnlySpan<Mesh> meshes)
    {
        device = new VulkanRayDevice(window);
        swapChain = new SwapChain(device);

        renderTarget = new StorageImage(device, Format.R32G32B32A32Sfloat);
        toneMapTarget = new StorageImage(device, swapChain.ImageFormat);

        meshAccels = new MeshAccel[meshes.Length];
        for (int i = 0; i < meshes.Length; ++i)
            meshAccels[i] = new MeshAccel(device, meshes[i].Vertices, meshes[i].Indices);

        topLevelAccel = new TopLevelAccel(device, meshAccels);

        rtPipe = new RayTracingPipeline(device, topLevelAccel, renderTarget.ImageView);
        tmPipe = new ToneMapPipeline(device, renderTarget, toneMapTarget, swapChain.IsLinearColorSpace);
        pipe = new RenderPipeline(device, swapChain, rtPipe, tmPipe, renderTarget, toneMapTarget);

        swapChain.OnRecreate += () =>
        {
            renderTarget.Dispose();
            toneMapTarget.Dispose();
            renderTarget = new StorageImage(device, Format.R32G32B32A32Sfloat);
            toneMapTarget = new StorageImage(device, swapChain.ImageFormat);

            rtPipe.Dispose();
            tmPipe.Dispose();
            pipe.Dispose();
            rtPipe = new RayTracingPipeline(device, topLevelAccel, renderTarget.ImageView);
            tmPipe = new ToneMapPipeline(device, renderTarget, toneMapTarget, swapChain.IsLinearColorSpace);
            pipe = new RenderPipeline(device, swapChain, rtPipe, tmPipe, renderTarget, toneMapTarget);
        };

        window.FramebufferResize += newSize => swapChain.NotifyResize();

        double fpsInterval = 2.0;
        double timeToFpsUpdate = fpsInterval;
        window.Render += elapsed =>
        {
            swapChain.DrawFrame(elapsed);

            timeToFpsUpdate -= elapsed;
            if (timeToFpsUpdate < 0.0)
            {
                double fps = 1.0 / (elapsed / 1.0);
                Console.WriteLine(fps);
                timeToFpsUpdate = fpsInterval;
            }
        };
    }

    public void Dispose()
    {
        device.Vk.DeviceWaitIdle(device.Device);

        pipe.Dispose();
        tmPipe.Dispose();
        rtPipe.Dispose();

        foreach (var m in meshAccels)
            m.Dispose();
        topLevelAccel.Dispose();

        toneMapTarget.Dispose();
        renderTarget.Dispose();

        swapChain.Dispose();
        device.Dispose();
    }
}


