const uint FnvOffsetBasis = 2166136261;
const uint FnvPrime = 16777619;

uint fnvHash(uint hash, uint data) {
    hash = (hash * FnvPrime) ^ (data & 0xFF);
    hash = (hash * FnvPrime) ^ ((data >> 8) & 0xFF);
    hash = (hash * FnvPrime) ^ ((data >> 16) & 0xFF);
    hash = (hash * FnvPrime) ^ ((data >> 24) & 0xFF);
    return hash;
}

uint pcgHash(uint val) {
    uint state = val * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28) + 4)) ^ state) * 277803737u;
    return (word >> 22) ^ word;
}

uint hashSeed(uint baseSeed, uint chainIndex, uint sampleIndex) {
    uint hash = fnvHash(FnvOffsetBasis, pcgHash(baseSeed));
    hash = fnvHash(hash, pcgHash(chainIndex));
    hash = fnvHash(hash, pcgHash(sampleIndex));
    return pcgHash(hash);
}

uint rngNext(inout uint state) {
    uint word = ((state >> ((state >> 28) + 4)) ^ state) * 277803737u;
    state = state * 747796405u + 2891336453u;
    return ((word >> 22) ^ word);
}

uint rngNextInt(inout uint state, uint max) {
    // https://arxiv.org/pdf/1805.10941.pdf
    uint x = rngNext(state);
    uint64_t m = uint64_t(x) * max;
    uint l = uint(m);
    if (l < max) {
        uint t = (0u - max) % max;
        while (l < t) {
            x = rngNext(state);
            m = uint64_t(x) * max;
            l = uint(m);
        }
    }
    return uint(m >> 32);
}

float rngNextFloat(inout uint state) {
    return rngNext(state) / float(0xFFFFFFFFu);
}

vec2 rngNextVec2(inout uint state) {
    return vec2(rngNextFloat(state), rngNextFloat(state));
}

vec3 rngNextVec3(inout uint state) {
    return vec3(rngNextFloat(state), rngNextFloat(state), rngNextFloat(state));
}

vec4 rngNextVec4(inout uint state) {
    return vec4(rngNextFloat(state), rngNextFloat(state), rngNextFloat(state), rngNextFloat(state));
}