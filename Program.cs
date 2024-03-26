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

var scene = SceneRegistry.LoadScene("HomeOffice").MakeScene();

MaterialLibrary materialLibrary = new();

var meshes = new Mesh[scene.Meshes.Count];
for (int i = 0; i < scene.Meshes.Count; ++i)
    meshes[i] = new(scene.Meshes[i], materialLibrary);

var camera = scene.Camera as PerspectiveCamera;
camera.UpdateResolution(window.Size.X, window.Size.Y);
var camToWorld = camera.CameraToWorld;
var viewToCam = camera.ViewToCamera;

var renderer = new Renderer(window, meshes, camToWorld, viewToCam, ShaderDirectory.MakeRelativeToScript("./Shaders"));

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
    };
}

window.Run();

renderer.Dispose();
window.Dispose();
