#version 460
#extension GL_EXT_ray_tracing : enable

struct RayPayload {
    vec3 weight;
};
layout(location = 0) rayPayloadInEXT RayPayload payload;

void main()
{
    payload.weight = vec3(0.1, 0.3, 0.5);
}