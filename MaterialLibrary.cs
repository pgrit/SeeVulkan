using SeeSharp.Images;
using RgbImage = SimpleImageIO.RgbImage;
using MonochromeImage = SimpleImageIO.MonochromeImage;

namespace SeeVulkan;

class MaterialLibrary
{
    List<Material> materials = [];

    Dictionary<SeeSharp.Shading.Materials.Material, int> known = [];

    public int Convert(SeeSharp.Shading.Materials.Material material)
    {
        if (known.TryGetValue(material, out int idx))
            return idx;

        Material result = new();

        if (material is SeeSharp.Shading.Materials.DiffuseMaterial diffuseMtl)
        {
            result.BaseColor = MapTextureOrColor(diffuseMtl.MaterialParameters.BaseColor);
            result.Roughness = new(1, 1);
            result.Roughness[0, 0] = 1.0f;
            result.Metallic = new(1, 1);
            result.Metallic[0, 0] = 0.0f;
            result.SpecularTintStrength = 1;
            result.Anisotropic = 0;
            result.SpecularTransmittance = 0;
            result.IndexOfRefraction = 1;
            result.Thin = false;
            result.DiffuseTransmittance = 0;
        }
        else if (material is SeeSharp.Shading.Materials.GenericMaterial genericMtl)
        {
            result.BaseColor = MapTextureOrColor(genericMtl.MaterialParameters.BaseColor);
            result.Roughness = MapTextureOrScalar(genericMtl.MaterialParameters.Roughness);
            result.Metallic = new(1, 1);
            result.Metallic[0, 0] = genericMtl.MaterialParameters.Metallic;
            result.SpecularTintStrength = genericMtl.MaterialParameters.SpecularTintStrength;
            result.Anisotropic = genericMtl.MaterialParameters.Anisotropic;
            result.SpecularTransmittance = genericMtl.MaterialParameters.SpecularTransmittance;
            result.IndexOfRefraction = genericMtl.MaterialParameters.IndexOfRefraction;
            result.Thin = genericMtl.MaterialParameters.Thin;
            result.DiffuseTransmittance = genericMtl.MaterialParameters.DiffuseTransmittance;
        }

        materials.Add(result);
        idx = materials.Count - 1;
        known.Add(material, idx);
        return idx;
    }

    private RgbImage MapTextureOrColor(TextureRgb texture)
    {
        var image = texture.Image as RgbImage;
        if (texture.IsConstant)
        {
            image = new(1, 1);
            image[0, 0] = texture.Lookup(new());
        }
        return image;
    }

    private MonochromeImage MapTextureOrScalar(TextureMono texture)
    {
        var image = texture.Image as MonochromeImage;
        if (texture.IsConstant)
        {
            image = new(1, 1);
            image[0, 0] = texture.Lookup(new());
        }
        return image;
    }

    public void Prepare(VulkanRayDevice rayDevice)
    {
        // TODO prepare all materials

        // TODO create material buffer on the device
    }
}
