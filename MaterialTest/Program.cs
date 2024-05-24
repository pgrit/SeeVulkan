using SeeVulkan;

SceneRegistry.AddSourceRelativeToScript("./Scenes");

var scene = SceneRegistry.LoadScene("MaterialTester", maxDepth: 2).MakeScene();
var mtl = scene.Meshes[0].Material as GenericMaterial;

mtl = new(new GenericMaterial.Parameters()
{
    Roughness = new(0.4f),
    Anisotropic = 0.0f,
    Metallic = 0.2f,
    IndexOfRefraction = 1.45f,
    SpecularTintStrength = 0.6f,
    SpecularTransmittance = 0.2f,
    BaseColor = new(RgbColor.White * 0.9f)
});

RgbImage envmap = new(1, 1);
envmap.Fill(1.0f, 1.0f, 1.0f);

// Render reference with SeeSharp and send it to tev
const int width = 640;
const int height = 480;
scene.FrameBuffer = new(width, height, "Results/SeeSharp.exr", FrameBuffer.Flags.Recommended | FrameBuffer.Flags.SendToTev);
scene.Prepare();
new PathTracer()
{
    TotalSpp = 1,
    NumShadowRays = 1,
    EnableBsdfDI = true,
    MaxDepth = 10,
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

Renderer.RenderImage(width, height, 64,
    meshes, UpdateCameraMatrices, ShaderDirectory.MakeRelativeToScript("../SeeVulkan/Shaders"), materialLibrary, emitters)
.WriteToFile("Results/SeeVulkan.exr");

TevIpc.ShowImage("Results/SeeVulkan.exr", new RgbImage("Results/SeeVulkan.exr"));