using SeeVulkan;
using Silk.NET.Input;
using Silk.NET.Windowing;

SceneRegistry.AddSourceRelativeToScript("./Scenes");

var scene = SceneRegistry.LoadScene("MaterialTester", maxDepth: 2).MakeScene();

scene.Meshes[0].Material = new GenericMaterial(new GenericMaterial.Parameters()
{
    Roughness = new(0.2f),
    Anisotropic = 0.83f,
    Metallic = 0.9f,
    IndexOfRefraction = 1.45f,
    SpecularTintStrength = 1.0f,
    SpecularTransmittance = 0.0f,
    BaseColor = new(new RgbColor(0.3f, 0.7f, 0.9f))
});

RgbImage envmap = new(1, 1);
envmap.Fill(1.0f, 1.0f, 1.0f);
scene.Background = new EnvironmentMap(envmap);

// Render reference with SeeSharp and send it to tev
const int width = 640;
const int height = 480;
scene.FrameBuffer = new(width, height, "Results/SeeSharp.exr", FrameBuffer.Flags.Recommended | FrameBuffer.Flags.SendToTev);
scene.Prepare();
new PathTracer()
{
    TotalSpp = 16,
    NumShadowRays = 0,
    EnableBsdfDI = true,
    MaxDepth = 3,
}.Render(scene);
scene.FrameBuffer.WriteToFile();

// Render the same scene with SeeVulkan
MaterialLibrary materialLibrary = new();

var meshes = new SeeVulkan.Mesh[scene.Meshes.Count];
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
    meshes, UpdateCameraMatrices, ShaderDirectory.MakeRelativeToScript("../SeeVulkan/Shaders"), materialLibrary, emitters)
.WriteToFile("Results/SeeVulkan.exr");

TevIpc.ShowImage("Results/SeeVulkan.exr", new RgbImage("Results/SeeVulkan.exr"));




// // Live view
// var options = WindowOptions.DefaultVulkan with {
//     Size = new(800, 600),
//     Title = "SeeVulkan",
// };

// var window = Window.Create(options);
// window.Initialize();
// window.MakeCornersSquare();

// if (window.VkSurface is null)
//     throw new Exception("Windowing platform doesn't support Vulkan.");

// var renderer = new Renderer(window, false, meshes, UpdateCameraMatrices,
//     ShaderDirectory.MakeRelativeToScript("../SeeVulkan/Shaders"),
//     materialLibrary, emitters);

// renderer.Throttle = true;

// var input = window.CreateInput();
// for (int i = 0; i < input.Keyboards.Count; i++)
// {
//     input.Keyboards[i].KeyDown += (kbd, key, _) => {
//         if (key == Key.Escape)
//         {
//             window.Close();
//         }
//         else if (key == Key.T)
//         {
//             renderer.SendToTev();
//         }
//         else if (key == Key.S)
//         {
//             renderer.SaveToFile();
//         }
//         else if (key == Key.R)
//         {
//             renderer.Restart();
//         }
//         else if (key == Key.P)
//         {
//             renderer.Throttle = !renderer.Throttle;
//         }
//     };
// }

// window.Run();

// renderer.Dispose();
// window.Dispose();