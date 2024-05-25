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

void main()
{
    const int primId = gl_PrimitiveID;

    Triangle tri = getTriangle(gl_InstanceID, primId);

    const vec3 barycentricCoords = vec3(1.0 - attribs.x - attribs.y, attribs.x, attribs.y);

    vec3 normal =
        barycentricCoords.x * tri.v1.normal +
        barycentricCoords.y * tri.v2.normal +
        barycentricCoords.z * tri.v3.normal;

    vec2 uv =
        barycentricCoords.x * tri.v1.uv +
        barycentricCoords.y * tri.v2.uv +
        barycentricCoords.z * tri.v3.uv;

    vec3 hitp = gl_WorldRayOriginEXT + gl_WorldRayDirectionEXT * gl_HitTEXT;

    // Sensible offset for ray tracing to avoid self intersections
    // (math borrowed from the Embree example renderer)
    float errorOffset = max(max(abs(hitp.x), abs(hitp.y)), max(abs(hitp.z), gl_HitTEXT)) * 32.0 * 1.19209e-07;

    payload.hit = HitData(hitp, gl_HitTEXT, normal, tri.geomNormal, uv, errorOffset, tri.materialId, gl_InstanceID, primId,
        tri.emission);
}