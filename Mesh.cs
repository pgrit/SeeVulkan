namespace SeeVulkan;

struct Mesh(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<uint> indices)
{
    public readonly Vector3[] Vertices = vertices.ToArray();
    public readonly uint[] Indices = indices.ToArray();
}
