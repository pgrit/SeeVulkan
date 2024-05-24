using System.Net.Sockets;
using SeeSharp.Common;

namespace SeeVulkan;

public class Renderer : IDisposable
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
    private EmitterData emitters;


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

    public delegate (Matrix4x4 CamToWorld, Matrix4x4 ViewToCam) CameraComputeCallback(int width, int height);

    uint frameIdx = 0;
    public bool Throttle = false;

    public void Restart() => frameIdx = 0;

    public Renderer(IWindow window, bool enableHDR, ReadOnlySpan<Mesh> meshes, CameraComputeCallback computeCamera,
        ShaderDirectory shaderDirectory, MaterialLibrary materialLibrary, EmitterData emitters)
    {
        this.materialLibrary = materialLibrary;
        this.emitters = emitters;

        device = new VulkanRayDevice(window);
        swapChain = new SwapChain(device, enableHDR);

        renderTarget = new StorageImage(device, Format.R32G32B32A32Sfloat, (uint)window.FramebufferSize.X, (uint)window.FramebufferSize.Y);
        toneMapTarget = new StorageImage(device, swapChain.ImageFormat, (uint)window.FramebufferSize.X, (uint)window.FramebufferSize.Y);

        meshAccels = new MeshAccel[meshes.Length];
        for (int i = 0; i < meshes.Length; ++i)
            meshAccels[i] = new MeshAccel(device, meshes[i]);

        topLevelAccel = new TopLevelAccel(device, meshAccels);

        materialLibrary.Prepare(device);
        emitters.Prepare(device);

        var (camToWorld, viewToCam) = computeCamera(window.Size.X, window.Size.Y);

        rtPipe = new RayTracingPipeline(device, topLevelAccel, renderTarget.ImageView, camToWorld, viewToCam, shaderDirectory, meshAccels, materialLibrary, emitters);
        tmPipe = new ToneMapPipeline(device, renderTarget, toneMapTarget, swapChain.IsLinearColorSpace, shaderDirectory);
        pipe = new RenderPipeline(device, swapChain, rtPipe, tmPipe, renderTarget, toneMapTarget);

        swapChain.OnRecreate += () =>
        {
            renderTarget.Dispose();
            toneMapTarget.Dispose();
            renderTarget = new StorageImage(device, Format.R32G32B32A32Sfloat, (uint)window.FramebufferSize.X, (uint)window.FramebufferSize.Y);
            toneMapTarget = new StorageImage(device, swapChain.ImageFormat, (uint)window.FramebufferSize.X, (uint)window.FramebufferSize.Y);

            rtPipe.Dispose();
            tmPipe.Dispose();
            pipe.Dispose();
            // TODO update camera parameters based on new resolution - callback instead of direct matrix transfer?
            rtPipe = new RayTracingPipeline(device, topLevelAccel, renderTarget.ImageView, camToWorld, viewToCam, shaderDirectory, meshAccels, materialLibrary, emitters);
            tmPipe = new ToneMapPipeline(device, renderTarget, toneMapTarget, swapChain.IsLinearColorSpace, shaderDirectory);
            pipe = new RenderPipeline(device, swapChain, rtPipe, tmPipe, renderTarget, toneMapTarget);
        };

        window.FramebufferResize += newSize =>
        {
            swapChain.NotifyResize();
            // TODO should probably be the other way around, renderer being told what to do not asking for update...
            (camToWorld, viewToCam) = computeCamera(window.Size.X, window.Size.Y);
            frameIdx = 0;
        };

        double fpsInterval = 1.0;
        double timeToFpsUpdate = fpsInterval;

        double shaderInterval = 4;
        double timeToShaderScan = shaderInterval;

        window.Render += elapsed =>
        {
            // Throttle the renderer so we don't waste energy while debugging :)
            if (Throttle) System.Threading.Thread.Sleep(100);

            rtPipe.UpdateUniforms(camToWorld, viewToCam, frameIdx++);
            swapChain.DrawFrame(elapsed);

            timeToFpsUpdate -= elapsed;
            if (timeToFpsUpdate < 0.0)
            {
                double fps = 1.0 / (elapsed / 1.0);
                timeToFpsUpdate = fpsInterval;
                window.Title = $"SeeVulkan - {fps:.} fps" + (Throttle ? " (throttled)" : "");
            }

            timeToShaderScan -= elapsed;
            if (timeToShaderScan < 0.0)
            {
                if (shaderDirectory.ScanForUpdates())
                {
                    swapChain.NotifyResize(); // TODO we don't need to realloc everything... but this is safe and easy
                    frameIdx = 0;
                }
            }
        };
    }

    public void Dispose()
    {
        device.Vk.DeviceWaitIdle(device.Device);

        pipe.Dispose();
        tmPipe.Dispose();
        rtPipe.Dispose();

        emitters.Dispose();
        materialLibrary.Dispose();

        foreach (var m in meshAccels)
            m.Dispose();
        topLevelAccel.Dispose();

        toneMapTarget.Dispose();
        renderTarget.Dispose();

        swapChain.Dispose();
        device.Dispose();
    }

    public static unsafe SimpleImageIO.Image RenderImage(uint width, uint height, uint numSamples,
        ReadOnlySpan<Mesh> meshes, CameraComputeCallback computeCamera, ShaderDirectory shaderDirectory,
        MaterialLibrary materialLibrary, EmitterData emitters)
    {
        var device = new VulkanRayDevice(null);
        var vk = device.Vk;

        var renderTarget = new StorageImage(device, Format.R32G32B32A32Sfloat, width, height);

        var meshAccels = new MeshAccel[meshes.Length];
        for (int i = 0; i < meshes.Length; ++i)
            meshAccels[i] = new MeshAccel(device, meshes[i]);
        var topLevelAccel = new TopLevelAccel(device, meshAccels);

        materialLibrary.Prepare(device);
        emitters.Prepare(device);

        var (camToWorld, viewToCam) = computeCamera((int)width, (int)height);

        var rtPipe = new RayTracingPipeline(device, topLevelAccel, renderTarget.ImageView, camToWorld, viewToCam, shaderDirectory, meshAccels, materialLibrary, emitters);

        FenceCreateInfo fenceInfo = new()
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit,
        };
        CheckResult(vk.CreateFence(device.Device, fenceInfo, null, out var fence), nameof(vk.CreateFence));

        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = device.CommandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        CheckResult(vk.AllocateCommandBuffers(device.Device, allocInfo, out var commandBuffer), nameof(vk.AllocateCommandBuffers));

        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
        };
        CheckResult(vk.BeginCommandBuffer(commandBuffer, beginInfo), nameof(vk.BeginCommandBuffer));

        rtPipe.MakeCommands(commandBuffer, width, height);

        CheckResult(vk.EndCommandBuffer(commandBuffer), nameof(vk.EndCommandBuffer));

        var timer = Stopwatch.StartNew();
        for (uint frameIdx = 0; frameIdx < numSamples; ++frameIdx)
        {
            rtPipe.UpdateUniforms(camToWorld, viewToCam, frameIdx);

            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
                WaitSemaphoreCount = 0,
                SignalSemaphoreCount = 0,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer
            };

            vk.ResetFences(device.Device, 1, fence);
            CheckResult(vk.QueueSubmit(device.GraphicsQueue, 1, submitInfo, fence), nameof(vk.QueueSubmit));
            vk.WaitForFences(device.Device, 1, fence, true, ulong.MaxValue);
        }
        Logger.Log($"Done after {timer.ElapsedMilliseconds}ms");

        // TODO clean up properly and in the correct order

        return renderTarget.CopyToHost();
    }
}


