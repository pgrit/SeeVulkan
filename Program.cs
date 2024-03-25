using SeeSharp.Cameras;
using SeeSharp.Experiments;
using SeeVulkan;

var options = WindowOptions.DefaultVulkan with {
    Size = new(800, 600),
    Title = "SeeVulkan",
};

var window = Window.Create(options);

window.Load += () => {
    var input = window.CreateInput();
    for (int i = 0; i < input.Keyboards.Count; i++)
    {
        input.Keyboards[i].KeyDown += (kbd, key, _) => {
            if (key == Key.Escape)
            {
                window.Close();
            }
        };
    }
};

window.Initialize();
window.MakeCornersSquare();
if (window.VkSurface is null)
    throw new Exception("Windowing platform doesn't support Vulkan.");

var scene = SceneRegistry.LoadScene("RoughGlassesIndirect").MakeScene();
List<Mesh> meshes = [];
foreach (var m in scene.Meshes)
    meshes.Add(new(m.Vertices, m.Indices.Select(i => (uint)i).ToArray()));

var camera = scene.Camera as PerspectiveCamera;
camera.UpdateResolution(window.Size.X, window.Size.Y);
var camToWorld = camera.CameraToWorld;
var viewToCam = camera.ViewToCamera;

var renderer = new Renderer(window, meshes.ToArray(), camToWorld, viewToCam);

// var mesh1 = new Mesh([
//     new( 1.0f,  1.0f, 0.0f),
//     new(-1.0f,  1.0f, 0.0f),
//     new(-1.0f, -1.0f, 0.0f),
//     new( 1.0f, -1.0f, 0.0f),
// ], [
//     0, 1, 2,
//     0, 2, 3
// ]);
// var mesh2 = new Mesh([
//     new( 0.25f, -0.25f, -0.25f),
//     new(-0.25f, -0.25f, -0.25f),
//     new( 0.0f,  0.25f, -0.25f),
// ], [
//     0, 1, 2
// ]);
// var renderer = new Renderer(window, [mesh1, mesh2]);

window.Run();

renderer.Dispose();
window.Dispose();
