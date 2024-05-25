using System.CommandLine;
using SeeSharp.Cameras;
using SeeSharp.Experiments;
using SeeVulkan;
using SimpleImageIO;

bool enableHDR = true;

var rootCommand = new RootCommand("SeeVulkan -- a SeeSharp compatible GPU ray tracer");
var hdrOption = new Option<bool>(
    "--hdr",
    () => true,
    "Turns HDR display support on or off");
rootCommand.AddOption(hdrOption);
rootCommand.SetHandler((useHdr) =>
{
    enableHDR = useHdr;
}, hdrOption);
rootCommand.Invoke(args);

SceneRegistry.AddSourceRelativeToScript("../Scenes");
// var scene = SceneRegistry.LoadScene("RgbSofa").MakeScene();
var scene = SceneRegistry.LoadScene("DebugScene").MakeScene();

// Render reference image with SeeSharp
const int width = 640;
const int height = 480;
scene.FrameBuffer = new(width, height, "Results/SeeSharp.exr", SeeSharp.Images.FrameBuffer.Flags.Recommended | SeeSharp.Images.FrameBuffer.Flags.SendToTev);
scene.Prepare();
new SeeSharp.Integrators.PathTracer()
{
    TotalSpp = 16,
    NumShadowRays = 1,
    EnableBsdfDI = true,
    RenderTechniquePyramid = true,
    MaxDepth = 10,
}.Render(scene);
scene.FrameBuffer.WriteToFile();

MaterialLibrary materialLibrary = new();

var meshes = new Mesh[scene.Meshes.Count];
for (int i = 0; i < scene.Meshes.Count; ++i)
    meshes[i] = new(scene.Meshes[i], materialLibrary);

var emitters = new EmitterData();
emitters.Convert(scene);

(Matrix4x4 CamToWorld, Matrix4x4 ViewToCam) UpdateCameraMatrices(int width, int height)
{
    var camera = scene.Camera as PerspectiveCamera;
    camera.UpdateResolution(width, height);
    return (camera.CameraToWorld, camera.ViewToCamera);
}

Renderer.RenderImage(width, height, 16,
    meshes, UpdateCameraMatrices, ShaderDirectory.MakeRelativeToScript("./Shaders"), materialLibrary, emitters)
.WriteToFile("Results/SeeVulkan.exr");
TevIpc.ShowImage("Results/SeeVulkan.exr", new RgbImage("Results/SeeVulkan.exr"));

var options = WindowOptions.DefaultVulkan with {
    Size = new(width, height),
    Title = "SeeVulkan",
};

var window = Window.Create(options);
window.Initialize();
window.MakeCornersSquare();

if (window.VkSurface is null)
    throw new Exception("Windowing platform doesn't support Vulkan.");

var renderer = new Renderer(window, enableHDR, meshes, UpdateCameraMatrices,
    ShaderDirectory.MakeRelativeToScript("./Shaders"),
    materialLibrary, emitters);

var input = window.CreateInput();
for (int i = 0; i < input.Keyboards.Count; i++)
{
    input.Keyboards[i].KeyDown += (kbd, key, _) => {
        if (key == Key.Escape)
        {
            window.Close();
        }
        else if (key == Key.T)
        {
            renderer.SendToTev();
        }
        else if (key == Key.S)
        {
            renderer.SaveToFile();
        }
        else if (key == Key.R)
        {
            renderer.Restart();
        }
        else if (key == Key.P)
        {
            renderer.Throttle = !renderer.Throttle;
        }
    };
}

window.Run();

renderer.Dispose();
window.Dispose();
