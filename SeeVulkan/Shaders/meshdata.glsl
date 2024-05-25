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

PerMeshData getMeshData(uint meshId) {
    PerMeshDataBuffer perMeshDataBuffer = PerMeshDataBuffer(perMeshDataBufferAddress);
    return perMeshDataBuffer.data[meshId];
}

struct Triangle {
    Vertex v1;
    Vertex v2;
    Vertex v3;
    float area;
    vec3 geomNormal;
    uint materialId;
    MeshEmission emission;
};

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

    return Triangle(v1, v2, v3, area, n / lenN, meshData.materialId, meshData.emission);
}