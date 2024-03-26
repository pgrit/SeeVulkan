
hitAttributeEXT vec2 attribs;

layout(push_constant) uniform AddrBufferAddr {
    uint64_t addr;
} addrBuffer;

struct PerMeshData {
    uint64_t vertexBufferAddress;
    uint64_t indexBufferAddress;
    uint materialId;
};

layout(buffer_reference, scalar) buffer MeshBufferRefs {
    PerMeshData r[];
};

struct Vertex {
    vec3 pos;
    vec3 normal;
    vec2 uv;
};

layout(buffer_reference, scalar) buffer Vertices {
    Vertex v[];
};

layout(buffer_reference, scalar) buffer Indices {
    uint i[];
};

struct HitData {
    vec3 pos;
    vec3 normal;
    vec2 uv;
    uint materialId;
};

HitData computeHitData() {
    const int primId = gl_PrimitiveID;
    const int meshId = gl_InstanceID;

    MeshBufferRefs refs = MeshBufferRefs(addrBuffer.addr);
    PerMeshData meshBufs = refs.r[meshId];
    Vertices verts = Vertices(meshBufs.vertexBufferAddress);
    Indices indices = Indices(meshBufs.indexBufferAddress);
    Vertex v1 = verts.v[indices.i[primId * 3 + 0]];
    Vertex v2 = verts.v[indices.i[primId * 3 + 1]];
    Vertex v3 = verts.v[indices.i[primId * 3 + 2]];

    const vec3 barycentricCoords = vec3(1.0 - attribs.x - attribs.y, attribs.x, attribs.y);

    vec3 normal =
        barycentricCoords.x * v1.normal +
        barycentricCoords.y * v2.normal +
        barycentricCoords.z * v3.normal;

    vec2 uv =
        barycentricCoords.x * v1.uv +
        barycentricCoords.y * v2.uv +
        barycentricCoords.z * v3.uv;

    vec3 hitp = gl_WorldRayOriginEXT + gl_WorldRayDirectionEXT * gl_HitTEXT;

    return HitData(hitp, normal, uv, meshBufs.materialId);
}