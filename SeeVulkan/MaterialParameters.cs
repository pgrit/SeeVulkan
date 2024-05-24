using SeeSharp.Shading.Emitters;
using SimpleImageIO;

namespace SeeVulkan;

public struct MaterialParameters
{
    public uint BaseColorIdx;
    public uint RoughnessIdx;
    public uint MetallicIdx;
    public float SpecularTintStrength;
    public float Anisotropic;
    public float SpecularTransmittance;
    public float IndexOfRefraction;
}

public record struct MeshEmission(Vector3 Radiance)
{
}

public record struct Emitter(uint MeshIdx, uint TriangleIdx)
{
}

public class EmitterData : IDisposable
{
    public void Dispose()
    {
        EmitterList?.Dispose();
    }

    public List<MeshEmission> MeshEmissionData = [];
    List<Emitter> emitters = [];

    public VulkanBuffer EmitterList;

    public uint NumEmitters => (uint)emitters.Count;

    public void Convert(SeeSharp.Scene scene)
    {
        // Emitter mapping is only constructed if the scene was prepared for ray tracing
        if (scene.Raytracer == null)
        {
            scene.FrameBuffer = new(1, 1, "");
            scene.Prepare();
        }

        for (int i = 0; i < scene.Meshes.Count; ++i)
        {
            var meshEmitters = scene.GetMeshEmitters(scene.Meshes[i]);
            if (meshEmitters == null)
            {
                MeshEmissionData.Add(new(RgbColor.Black));
            }
            else
            {
                MeshEmissionData.Add(new((meshEmitters[0] as DiffuseEmitter)?.Radiance ?? RgbColor.Black));

                for (int k = 0; k < meshEmitters.Count; ++k)
                {
                    emitters.Add(new((uint)i, (uint)k));
                }
            }
        }
    }

    public void Prepare(VulkanRayDevice rayDevice)
    {
        if (NumEmitters == 0)
        {
            // Store a fake emitter that will never be used so we don't need special case handling
            // while also not crashing the GPU driver either :)
            EmitterList = VulkanBuffer.Make<Emitter>(rayDevice,
                BufferUsageFlags.ShaderDeviceAddressBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                [ new() ]
            );
        }
        else
        {
            EmitterList = VulkanBuffer.Make<Emitter>(rayDevice,
                BufferUsageFlags.ShaderDeviceAddressBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                emitters.ToArray()
            );
        }
    }
}