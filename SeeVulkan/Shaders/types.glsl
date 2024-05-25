struct Material {
    uint BaseColorIdx;
    uint RoughnessIdx;
    uint MetallicIdx;
    float SpecularTintStrength;
    float Anisotropic;
    float SpecularTransmittance;
    float IndexOfRefraction;
};

struct MeshEmission {
    vec3 radiance;
};

const float PI = 3.1415926;

layout(push_constant) uniform PerMeshDataBufferAddress {
    uint64_t perMeshDataBufferAddress;
};

struct PerMeshData {
    uint64_t vertexBufferAddress;
    uint64_t indexBufferAddress;
    uint materialId;
    MeshEmission emission;
};

layout(buffer_reference, scalar) buffer PerMeshDataBuffer {
    PerMeshData data[];
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

struct Triangle {
    Vertex v1;
    Vertex v2;
    Vertex v3;
    float area;
    vec3 geomNormal;
    uint materialId;
    MeshEmission emission;
    uint meshId, triId;
};

struct HitData {
    vec3 pos;
    float dist;
    vec2 uv;
    float errorOffset;
    Triangle triangle;
    mat3 shadingToWorld;
    mat3 worldToShading;
};

struct RayPayload {
    uint rngState;
    vec3 weight;
    HitData hit;
    bool occluded;
};

PerMeshData getMeshData(uint meshId) {
    PerMeshDataBuffer perMeshDataBuffer = PerMeshDataBuffer(perMeshDataBufferAddress);
    return perMeshDataBuffer.data[meshId];
}

Triangle getTriangle(uint meshId, uint triId) {
    PerMeshData meshData = getMeshData(meshId);

    Vertices verts = Vertices(meshData.vertexBufferAddress);
    Indices indices = Indices(meshData.indexBufferAddress);
    Vertex v1 = verts.v[indices.i[triId * 3 + 0]];
    Vertex v2 = verts.v[indices.i[triId * 3 + 1]];
    Vertex v3 = verts.v[indices.i[triId * 3 + 2]];

    vec3 n = cross(v2.pos - v1.pos, v3.pos - v1.pos);
    float lenN = length(n);
    float area = lenN * 0.5;

    return Triangle(v1, v2, v3, area, n / lenN, meshData.materialId, meshData.emission, meshId, triId);
}

bool isBlack(vec3 radiance) {
    return radiance.x == 0.0 && radiance.y == 0.0 && radiance.z == 0.0;
}