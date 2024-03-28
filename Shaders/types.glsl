struct Material {
    uint BaseColorIdx;
    uint RoughnessIdx;
    uint MetallicIdx;
    float SpecularTintStrength;
    float Anisotropic;
    float SpecularTransmittance;
    float IndexOfRefraction;
    bool Thin;
    float DiffuseTransmittance;
};

struct MeshEmission {
    vec3 radiance;
};

struct HitData {
    vec3 pos;
    float dist;
    vec3 normal;
    vec2 uv;
    float errorOffset;
    uint materialIdx;
    MeshEmission emission;
};

struct RayPayload {
    uint rngState;
    vec3 weight;
    HitData hit;
};

const float PI = 3.1415926;

bool isBlack(vec3 radiance) {
    return radiance.x == 0.0 && radiance.y == 0.0 && radiance.z == 0.0;
}