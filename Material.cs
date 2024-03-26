using RgbImage = SimpleImageIO.RgbImage;
using MonochromeImage = SimpleImageIO.MonochromeImage;

namespace SeeVulkan;

struct MaterialParameters
{
    public uint BaseColorIdx;
    public uint RoughnessIdx;
    public uint MetallicIdx;
    public float SpecularTintStrength;
    public float Anisotropic;
    public float SpecularTransmittance;
    public float IndexOfRefraction;
    public bool Thin;
    public float DiffuseTransmittance;
}

unsafe class Material
{
    public RgbImage BaseColor;
    public MonochromeImage Roughness;
    public MonochromeImage Metallic;
    public float SpecularTintStrength = 1;
    public float Anisotropic = 0;
    public float SpecularTransmittance = 0;
    public float IndexOfRefraction = 1;
    public bool Thin = false;
    public float DiffuseTransmittance = 0;

    public Texture BaseColorTexture;

    public void Prepare(VulkanRayDevice rayDevice)
    {
        BaseColorTexture = new(rayDevice, BaseColor);
        // TODO prep all textures

        // Get this data on the device - how?
    }
}
