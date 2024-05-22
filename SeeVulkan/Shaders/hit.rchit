#version 460
#extension GL_EXT_ray_tracing : require
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : require
#extension GL_EXT_buffer_reference2 : require
#extension GL_EXT_scalar_block_layout : require
#extension GL_EXT_shader_explicit_arithmetic_types_int64 : require

#include "types.glsl"
#include "rng.glsl"
#include "meshdata.glsl"

layout(location = 0) rayPayloadInEXT RayPayload payload;

hitAttributeEXT vec2 attribs;

void main()
{
    const int primId = gl_PrimitiveID;

    PerMeshData meshData = getMeshData(gl_InstanceID);

    Vertices verts = Vertices(meshData.vertexBufferAddress);
    Indices indices = Indices(meshData.indexBufferAddress);
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

    // Sensible offset for ray tracing to avoid self intersections
    // (math borrowed from the Embree example renderer)
    float errorOffset = max(max(abs(hitp.x), abs(hitp.y)), max(abs(hitp.z), gl_HitTEXT)) * 32.0 * 1.19209e-07;

    payload.hit = HitData(hitp, gl_HitTEXT, normal, uv, errorOffset, meshData.materialId, gl_InstanceID, primId,
        meshData.emission);
}