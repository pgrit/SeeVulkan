using System.Net.Sockets;
using SeeSharp.Common;

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
    MaterialLibrary materialLibrary;

    public void SendToTev()
    {
        var img = renderTarget.CopyToHost();
        try
        {
            SimpleImageIO.TevIpc.ShowImage("SeeVulkan", img);
        }
        catch(SocketException)
        {
            Logger.Warning("Could not reach tev on localhost using the default port. Is it running?");
        }
    }

    public void SaveToFile(string filename = "SeeVulkan.exr")
    {
        var img = renderTarget.CopyToHost();
        img.WriteToFile(filename);
    }

    public Renderer(IWindow window, ReadOnlySpan<Mesh> meshes, Matrix4x4 camToWorld, Matrix4x4 viewToCam, ShaderDirectory shaderDirectory, MaterialLibrary materialLibrary)
    {
        this.materialLibrary = materialLibrary;

        device = new VulkanRayDevice(window);
        swapChain = new SwapChain(device);

        renderTarget = new StorageImage(device, Format.R32G32B32A32Sfloat);
        toneMapTarget = new StorageImage(device, swapChain.ImageFormat);

        meshAccels = new MeshAccel[meshes.Length];
        for (int i = 0; i < meshes.Length; ++i)
            meshAccels[i] = new MeshAccel(device, meshes[i]);

        topLevelAccel = new TopLevelAccel(device, meshAccels);

        materialLibrary.Prepare(device);

        rtPipe = new RayTracingPipeline(device, topLevelAccel, renderTarget.ImageView, camToWorld, viewToCam, shaderDirectory, meshAccels, materialLibrary);
        tmPipe = new ToneMapPipeline(device, renderTarget, toneMapTarget, swapChain.IsLinearColorSpace, shaderDirectory);
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
            // TODO update camera parameters based on new resolution - callback instead of direct matrix transfer?
            rtPipe = new RayTracingPipeline(device, topLevelAccel, renderTarget.ImageView, camToWorld, viewToCam, shaderDirectory, meshAccels, materialLibrary);
            tmPipe = new ToneMapPipeline(device, renderTarget, toneMapTarget, swapChain.IsLinearColorSpace, shaderDirectory);
            pipe = new RenderPipeline(device, swapChain, rtPipe, tmPipe, renderTarget, toneMapTarget);
        };

        window.FramebufferResize += newSize => swapChain.NotifyResize();

        double fpsInterval = 1.0;
        double timeToFpsUpdate = fpsInterval;

        double shaderInterval = 4;
        double timeToShaderScan = shaderInterval;

        window.Render += elapsed =>
        {
            swapChain.DrawFrame(elapsed);

            timeToFpsUpdate -= elapsed;
            if (timeToFpsUpdate < 0.0)
            {
                double fps = 1.0 / (elapsed / 1.0);
                timeToFpsUpdate = fpsInterval;
                window.Title = $"SeeVulkan - {fps:.} fps";
            }

            timeToShaderScan -= elapsed;
            if (timeToShaderScan < 0.0)
            {
                if (shaderDirectory.ScanForUpdates())
                    swapChain.NotifyResize(); // TODO we don't need to realloc everything... but this is safe and easy
            }
        };
    }

    public void Dispose()
    {
        device.Vk.DeviceWaitIdle(device.Device);

        pipe.Dispose();
        tmPipe.Dispose();
        rtPipe.Dispose();

        materialLibrary.Dispose();

        foreach (var m in meshAccels)
            m.Dispose();
        topLevelAccel.Dispose();

        toneMapTarget.Dispose();
        renderTarget.Dispose();

        swapChain.Dispose();
        device.Dispose();
    }
}


