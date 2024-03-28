using SeeSharp.Cameras;
using SeeSharp.Experiments;
using SeeVulkan;

var options = WindowOptions.DefaultVulkan with {
    Size = new(800, 600),
    Title = "SeeVulkan",
};

var window = Window.Create(options);
window.Initialize();
window.MakeCornersSquare();

if (window.VkSurface is null)
    throw new Exception("Windowing platform doesn't support Vulkan.");

var scene = SceneRegistry.LoadScene("VeachMis").MakeScene();

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

var renderer = new Renderer(window, meshes, UpdateCameraMatrices,
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
