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
