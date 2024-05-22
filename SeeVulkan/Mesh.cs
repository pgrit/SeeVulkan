namespace SeeVulkan;

class Mesh
{
    public struct Vertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 TexCoords;
    }

    public readonly Vertex[] Vertices;
    public readonly int[] Indices;

    public int MaterialId;

    public Mesh(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<Vector3> normals, ReadOnlySpan<Vector2> texCoords,
        ReadOnlySpan<int> indices, int materialId)
    {
        MaterialId = materialId;
        Indices = indices.ToArray();
        Vertices = new Vertex[vertices.Length];
        for (int i = 0; i < vertices.Length; ++i)
        {
            Vertices[i] = new()
            {
                Position = vertices[i],
                Normal = normals[i],
                TexCoords = texCoords[i],
            };
        }
    }

    public Mesh(SeeSharp.Geometry.Mesh mesh, MaterialLibrary materialConverter)
    : this(mesh.Vertices, mesh.ShadingNormals, mesh.TextureCoordinates, mesh.Indices, 0)
    {
        MaterialId = materialConverter.Convert(mesh.Material);
    }
}
