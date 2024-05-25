#version 460
#extension GL_EXT_ray_tracing : require
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : require
#extension GL_EXT_buffer_reference2 : require
#extension GL_EXT_scalar_block_layout : require
#extension GL_EXT_shader_explicit_arithmetic_types_int64 : require

#include "types.glsl"
#include "meshdata.glsl"

layout(location = 0) rayPayloadInEXT RayPayload payload;

hitAttributeEXT vec2 attribs;

void computeBasisVectors(vec3 normal, out mat3 shadingToWorld, out mat3 worldToShading) {
    vec3 tangent;
    if (abs(normal.x) > abs(normal.y)) {
        float denom = sqrt(normal.x * normal.x + normal.z * normal.z);
        tangent = vec3(-normal.z, 0.0, normal.x) / denom;
    } else {
        float denom = sqrt(normal.y * normal.y + normal.z * normal.z);
        tangent = vec3(0.0, normal.z, -normal.y) / denom;
    }
    vec3 binormal = cross(normal, tangent);
    shadingToWorld = mat3(tangent, binormal, normal);
    worldToShading = transpose(shadingToWorld);
}

void main()
{
    const int primId = gl_PrimitiveID;

    Triangle tri = getTriangle(gl_InstanceID, primId);

    const vec3 barycentricCoords = vec3(1.0 - attribs.x - attribs.y, attribs.x, attribs.y);

    vec3 normal =
        barycentricCoords.x * tri.v1.normal +
        barycentricCoords.y * tri.v2.normal +
        barycentricCoords.z * tri.v3.normal;
    normal = normalize(normal);

    vec2 uv =
        barycentricCoords.x * tri.v1.uv +
        barycentricCoords.y * tri.v2.uv +
        barycentricCoords.z * tri.v3.uv;

    vec3 hitp = gl_WorldRayOriginEXT + gl_WorldRayDirectionEXT * gl_HitTEXT;

    // Sensible offset for ray tracing to avoid self intersections
    // (math borrowed from the Embree example renderer)
    float errorOffset = max(max(abs(hitp.x), abs(hitp.y)), max(abs(hitp.z), gl_HitTEXT)) * 32.0 * 1.19209e-07;

    mat3 shadingToWorld, worldToShading;
    computeBasisVectors(normal, shadingToWorld, worldToShading);

    payload.hit = HitData(hitp, gl_HitTEXT, uv, errorOffset, tri, shadingToWorld, worldToShading);
}