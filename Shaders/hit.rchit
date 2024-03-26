#version 460
#extension GL_EXT_ray_tracing : require
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : require
#extension GL_EXT_buffer_reference2 : require
#extension GL_EXT_scalar_block_layout : require
#extension GL_EXT_shader_explicit_arithmetic_types_int64 : require

#include "types.glsl"
#include "hit_data.glsl"

layout(location = 0) rayPayloadInEXT RayPayload payload;

void main()
{
    HitData hit = computeHitData();

    payload.weight = vec3(hit.uv, 0.5);
    // payload.weight = vec3(abs(dot(hit.normal, -gl_WorldRayDirectionEXT)));
}