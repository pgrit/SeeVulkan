using SeeSharp.Images;
using RgbImage = SimpleImageIO.RgbImage;
using MonochromeImage = SimpleImageIO.MonochromeImage;

namespace SeeVulkan;

class MaterialLibrary : IDisposable
{
    List<MaterialParameters> materials = [];
    List<SimpleImageIO.Image> rawTextures = [];

    Dictionary<SeeSharp.Shading.Materials.Material, int> known = [];

    public int Convert(SeeSharp.Shading.Materials.Material material)
    {
        if (known.TryGetValue(material, out int idx))
            return idx;

        MaterialParameters result = new();

        if (material is SeeSharp.Shading.Materials.DiffuseMaterial diffuseMtl)
        {
            result.BaseColorIdx = AddTexture(MapTextureOrColor(diffuseMtl.MaterialParameters.BaseColor));

            MonochromeImage rough = new(1, 1);
            rough.Fill(1.0f);
            result.RoughnessIdx = AddTexture(rough); // TODO could reuse once via caching...

            MonochromeImage metallic = new(1, 1);
            metallic.Fill(1.0f);
            result.MetallicIdx = AddTexture(metallic); // TODO could reuse once via caching...

            result.SpecularTintStrength = 1;
            result.Anisotropic = 0;
            result.SpecularTransmittance = 0;
            result.IndexOfRefraction = 1;
            result.Thin = false;
            result.DiffuseTransmittance = 0;
        }
        else if (material is SeeSharp.Shading.Materials.GenericMaterial genericMtl)
        {
            result.BaseColorIdx = AddTexture(MapTextureOrColor(genericMtl.MaterialParameters.BaseColor));
            result.RoughnessIdx = AddTexture(MapTextureOrScalar(genericMtl.MaterialParameters.Roughness));

            MonochromeImage metallic = new(1, 1);
            metallic.Fill(genericMtl.MaterialParameters.Metallic);
            result.MetallicIdx = AddTexture(metallic);

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

    uint AddTexture(SimpleImageIO.Image texture)
    {
        rawTextures.Add(texture);
        return (uint)rawTextures.Count - 1;
    }

    public VulkanBuffer MaterialBuffer;
    public uint NumMaterials => (uint)materials.Count;
    public uint NumTextures => (uint)Textures.Length;
    public Texture[] Textures;

    public void Prepare(VulkanRayDevice rayDevice)
    {
        Textures = new Texture[rawTextures.Count];
        for (int i = 0; i < rawTextures.Count; ++i)
            Textures[i] = new(rayDevice, rawTextures[i]);

        MaterialBuffer = VulkanBuffer.Make<MaterialParameters>(rayDevice,
            BufferUsageFlags.StorageBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            materials.ToArray()
        );
    }

    public void Dispose()
    {
        MaterialBuffer.Dispose();
        foreach (var t in Textures)
            t.Dispose();
    }
}