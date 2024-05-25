struct MaterialLocalParams {
    vec3 diffuseReflectance;
    float roughness;
    float metallic;
    vec3 specularReflectanceAtNormal;
    vec3 specularTint;
    float alphaX, alphaY;
    vec3 specularTransmittance;
    float probDiffuse, probReflect, probTransmit;
};

float luminance(vec3 clr) {
    return 0.212671 * clr.x + 0.71516 * clr.y + 0.072169 * clr.z;
}

float schlickFresnelWeight(float cosTheta) {
    float m = clamp(1 - cosTheta, 0, 1);
    float m2 = m * m;
    return m2 * m2 * m;
}

vec3 fresnelSchlick(vec3 reflAtNormal, float cosTheta) {
    return mix(reflAtNormal, vec3(1, 1, 1), schlickFresnelWeight(cosTheta));
}

float fresnelDielectric(float cosThetaI, float etaI, float etaT) {
    cosThetaI = clamp(cosThetaI, -1, 1);
    // Potentially swap indices of refraction
    bool entering = cosThetaI > 0;
    if (!entering) {
        float temp = etaI;
        etaI = etaT;
        etaT = temp;
        cosThetaI = abs(cosThetaI);
    }

    // Compute _cosThetaT_ using Snell's law
    float sinThetaI = sqrt(max(0, 1 - cosThetaI * cosThetaI));
    float sinThetaT = etaI / etaT * sinThetaI;

    // Handle total internal reflection
    if (sinThetaT >= 1) return 1;
    float cosThetaT = sqrt(max(0, 1 - sinThetaT * sinThetaT));
    float Rparl = (etaT * cosThetaI - etaI * cosThetaT) /
            (etaT * cosThetaI + etaI * cosThetaT);
    float Rperp = (etaI * cosThetaI - etaT * cosThetaT) /
            (etaI * cosThetaI + etaT * cosThetaT);
    return (Rparl * Rparl + Rperp * Rperp) / 2;
}

bool sameHemisphere(vec3 dirA, vec3 dirB) {
    return dirA.z * dirB.z > 0;
}

float cosThetaSqr(vec3 direction) {
    return direction.z * direction.z;
}

float sinThetaSqr(vec3 direction) {
    return max(0, 1 - cosThetaSqr(direction));
}

float tanTheta(vec3 direction) {
    return sqrt(sinThetaSqr(direction)) / direction.z;
}

float tanThetaSqr(vec3 direction) {
    return sinThetaSqr(direction) / cosThetaSqr(direction);
}

float cosPhi(vec3 direction) {
    float sinTheta = sqrt(sinThetaSqr(direction));
    return sinTheta == 0 ? 1 : clamp(direction.x / sinTheta, -1, 1);
}

float sinPhi(vec3 direction) {
    float sinTheta = sqrt(sinThetaSqr(direction));
    return sinTheta == 0 ? 0 : clamp(direction.y / sinTheta, -1, 1);
}

float ggxNormalDistribution(float alphaX, float alphaY, vec3 normal) {
    float tan2Theta = tanThetaSqr(normal);
    if (isinf(tan2Theta))
        return 0;

    float cos4Theta = cosThetaSqr(normal) * cosThetaSqr(normal);
    float c = cosPhi(normal) / alphaX;
    float s = sinPhi(normal) / alphaY;
    float e = tan2Theta * (c * c + s * s);

    return 1 / (PI * alphaX * alphaY * cos4Theta * (1 + e) * (1 + e));
}

float ggxMaskingRatio(float alphaX, float alphaY, vec3 outDir) {
    float absTanTheta = abs(tanTheta(outDir));
    if (isinf(absTanTheta)) return 0;
    float c = cosPhi(outDir) * alphaX;
    float s = sinPhi(outDir) * alphaY;
    float a = sqrt(c * c + s * s) * absTanTheta;
    return (-1 + sqrt(1 + a * a)) / 2;
}

float ggxMaskingShadowing(float alphaX, float alphaY, vec3 outDir, vec3 inDir) {
    return 1 / (1 + ggxMaskingRatio(alphaX, alphaY, outDir) + ggxMaskingRatio(alphaX, alphaY, inDir));
}

float ggxMaskingShadowing(float alphaX, float alphaY, vec3 outDir) {
    return 1 / (1 + ggxMaskingRatio(alphaX, alphaY, outDir));
}

float ggxPdf(float alphaX, float alphaY, vec3 outDir, vec3 normal) {
    if (!sameHemisphere(outDir, normal)) return 0;
    return ggxMaskingShadowing(alphaX, alphaY, outDir) * max(0, dot(outDir, normal))
        * ggxNormalDistribution(alphaX, alphaY, normal) / abs(outDir.z);
}

MaterialLocalParams mtlComputeLocalParams(Material material, HitData hit, vec3 outDir) {
    vec3 baseClr = texture(textures[material.BaseColorIdx], hit.uv).xyz;
    float metallic = texture(textures[material.MetallicIdx], hit.uv).x;
    float roughness = texture(textures[material.RoughnessIdx], hit.uv).x;
    float diffuseWeight = (1 - metallic) * (1 - material.SpecularTransmittance);

    float lum = luminance(baseClr);
    vec3 clrTint = lum > 0 ? baseClr / lum : vec3(1, 1, 1);
    vec3 specTint = mix(vec3(1, 1, 1), clrTint, material.SpecularTintStrength);

    float r = (material.IndexOfRefraction - 1) / (material.IndexOfRefraction + 1);
    vec3 specularReflectanceAtNormal = mix(specTint * r * r, baseClr, metallic);

    float aspect = sqrt(1 - material.Anisotropic * 0.9);
    float alphaX = max(0.001, roughness * roughness / aspect);
    float alphaY = max(0.001, roughness * roughness * aspect);

    // Compute selection probabilities
    vec3 diel = vec3(fresnelDielectric(outDir.z, 1, material.IndexOfRefraction));
    vec3 schlick = fresnelSchlick(specularReflectanceAtNormal, outDir.z);
    vec3 fresnel = mix(diel, schlick, metallic);
    float f = clamp((fresnel.x + fresnel.y + fresnel.z) / 3, 0.2, 0.8);
    float specularWeight = (1 - diffuseWeight) * f;
    float transmissionWeight = (1 - diffuseWeight) * (1 - f) * material.SpecularTransmittance;
    float norm = 1 / (specularWeight + transmissionWeight + diffuseWeight);

    return MaterialLocalParams(
        baseClr * diffuseWeight,
        roughness,
        metallic,
        specularReflectanceAtNormal,
        specTint,
        alphaX, alphaY,
        baseClr * material.SpecularTransmittance,
        diffuseWeight * norm, specularWeight * norm, transmissionWeight * norm
    );
}

struct MaterialEvaluation {
    vec3 bsdf;
    float pdf;
};

// Evaluates the material, returns the product of BSDF value and cosine, the PDFs, and the BSDF value without the cosine
MaterialEvaluation mtlEvaluate(Material material, HitData hit, bool isOnLightPath, vec3 outDir, vec3 inDir) {
    MaterialLocalParams localParams = mtlComputeLocalParams(material, hit, outDir);

    float cosThetaO = abs(outDir.z);
    float cosThetaI = abs(inDir.z);

    vec3 bsdf = vec3(0);

    vec3 geomNormalShade = hit.worldToShading * hit.triangle.geomNormal;
    bool sameGeometricHemisphere = dot(outDir, geomNormalShade) * dot(inDir, geomNormalShade) >= 0;
    bool sameShadingHemisphere = sameHemisphere(outDir, inDir);

    float pdfFwd = 0.0;
    float pdfRev = 0;

    // Evaluate the diffuse component
    float fresnelOut = schlickFresnelWeight(cosThetaO);
    float fresnelIn = schlickFresnelWeight(cosThetaI);
    if (sameShadingHemisphere) {
        if (sameGeometricHemisphere)
            bsdf += localParams.diffuseReflectance / PI * (1 - fresnelOut * 0.5) * (1 - fresnelIn * 0.5);
        pdfFwd += localParams.probDiffuse * cosThetaI / PI;
        pdfRev += localParams.probDiffuse * cosThetaO / PI; // TODO reverse select localParams.prob
    }

    vec3 halfVector = outDir + inDir;
    if (halfVector != vec3(0)) { // Skip degenerate case
        halfVector = normalize(halfVector);

        // retro reflectance component
        float cosThetaD = dot(inDir, halfVector);
        float r = 2 * localParams.roughness * cosThetaD * cosThetaD;
        if (sameShadingHemisphere && sameGeometricHemisphere)
            bsdf += localParams.diffuseReflectance / PI * r * (fresnelOut + fresnelIn + fresnelOut * fresnelIn * (r - 1));

        // Microfacet reflection
        if (cosThetaI != 0 && cosThetaO != 0) // Skip degenerate cases
        {
            // The microfacet model only contributes in the upper hemisphere
            if (sameShadingHemisphere && sameGeometricHemisphere) {
                // For the Fresnel computation only, make sure that wh is in the same hemisphere as the surface normal,
                // so that total internal reflection is handled correctly.
                float cosHalfVectorTIR = dot(inDir, (halfVector.z < 0) ? -halfVector : halfVector);
                vec3 diel = vec3(fresnelDielectric(cosHalfVectorTIR, 1, material.IndexOfRefraction));
                vec3 schlick = fresnelSchlick(localParams.specularReflectanceAtNormal, cosHalfVectorTIR);
                vec3 fresnel = mix(diel, schlick, localParams.metallic);

                bsdf += localParams.specularTint
                    * ggxNormalDistribution(localParams.alphaX, localParams.alphaY, halfVector)
                    * ggxMaskingShadowing(localParams.alphaX, localParams.alphaY, outDir, inDir)
                    * fresnel / (4 * cosThetaI * cosThetaO);
            }

            float reflectJacobianFwd = abs(4 * dot(outDir, halfVector));
            float reflectJacobianRev = abs(4 * cosThetaD);
            if (reflectJacobianFwd != 0.0 && reflectJacobianRev != 0.0) // Prevent NaNs from degenerate cases
            {
                pdfFwd += localParams.probReflect * ggxPdf(localParams.alphaX, localParams.alphaY, outDir, halfVector) / reflectJacobianFwd;
                pdfRev += localParams.probReflect * ggxPdf(localParams.alphaX, localParams.alphaY, inDir, halfVector) / reflectJacobianRev; // TODO reverse select localParams.prob
            }
        }
    }

    float eta = outDir.z > 0 ? material.IndexOfRefraction : (1 / material.IndexOfRefraction);
    vec3 halfVectorTransmit = outDir + inDir * eta;

    if (cosThetaO != 0 && cosThetaI != 0 && halfVector != vec3(0) && halfVectorTransmit != vec3(0)) // Skip degenerate cases
    {
        halfVectorTransmit = normalize(halfVectorTransmit);
        // Flip the half vector to the upper hemisphere for consistent computations
        halfVectorTransmit = (halfVectorTransmit.z < 0) ? -halfVectorTransmit : halfVectorTransmit;

        float sqrtDenom = dot(outDir, halfVectorTransmit) + eta * dot(inDir, halfVectorTransmit);

        // The BSDF value for transmission is only non-zero if the directions are in different hemispheres
        if (!sameShadingHemisphere)
        {
            vec3 F = vec3(fresnelDielectric(dot(outDir, halfVectorTransmit), 1, material.IndexOfRefraction));
            float factor = isOnLightPath ? (1 / eta) : 1;

            float numerator =
                ggxNormalDistribution(localParams.alphaX, localParams.alphaY, halfVectorTransmit)
                * ggxMaskingShadowing(localParams.alphaX, localParams.alphaY, outDir, inDir)
                * eta * eta * abs(dot(inDir, halfVectorTransmit)) * abs(dot(outDir, halfVectorTransmit))
                * factor * factor;

            float denom = inDir.z * outDir.z * sqrtDenom * sqrtDenom;
            bsdf += (vec3(1) - F) * localParams.specularTransmittance * abs(numerator / denom);
        }

        // If total reflection occured, we switch to reflection sampling
        float cosOut = dot(halfVector, outDir);
        if (1 / (eta * eta) * max(0, 1 - cosOut * cosOut) >= 1) // Total internal reflection occurs for this outgoing direction
        {
            float reflectJacobianFwd = abs(4 * dot(outDir, halfVector));
            if (reflectJacobianFwd != 0.0) // Prevent NaNs from degenerate cases
            {
                pdfFwd += localParams.probTransmit * ggxPdf(localParams.alphaX, localParams.alphaY, outDir, halfVector) / reflectJacobianFwd;
            }
        }

        // Same for reversed sampling
        float cosIn = dot(halfVector, inDir);
        float etaIn = inDir.z > 0 ? material.IndexOfRefraction : (1 / material.IndexOfRefraction);
        float cosThetaD = dot(inDir, halfVector);
        if (1 / (etaIn * etaIn) * max(0, 1 - cosIn * cosIn) >= 1) // Total internal reflection occurs for this outgoing direction
        {
            float reflectJacobianRev = abs(4 * cosThetaD);
            if (reflectJacobianRev != 0.0) // Prevent NaNs from degenerate cases
            {
                pdfRev += localParams.probTransmit * ggxPdf(localParams.alphaX, localParams.alphaY, inDir, halfVector) / reflectJacobianRev; // TODO reverse select localParams.prob
            }
        }

        // The transmission PDF
        if (sqrtDenom != 0)  // Prevent NaN in corner case
        {
            vec3 wh = (!sameHemisphere(outDir, halfVectorTransmit)) ? -halfVectorTransmit : halfVectorTransmit;
            float jacobian = eta * eta * max(0, dot(inDir, -wh)) / (sqrtDenom * sqrtDenom);
            pdfFwd += localParams.probTransmit * ggxPdf(localParams.alphaX, localParams.alphaY, outDir, wh) * jacobian;
        }

        // For the reverse PDF, we first need to compute the corresponding half vector
        vec3 halfVectorRev = inDir + outDir * etaIn;
        if (halfVectorRev != vec3(0))  // Prevent NaN if outDir and inDir exactly align
        {
            halfVectorRev = normalize(halfVectorRev);
            halfVectorRev = (!sameHemisphere(inDir, halfVectorRev)) ? -halfVectorRev : halfVectorRev;

            float sqrtDenomIn = dot(inDir, halfVectorRev) + etaIn * dot(outDir, halfVectorRev);
            if (sqrtDenomIn != 0)  // Prevent NaN in corner case
            {
                float jacobian = etaIn * etaIn * max(0, dot(outDir, -halfVectorRev)) / (sqrtDenomIn * sqrtDenomIn);
                pdfRev += localParams.probTransmit * ggxPdf(localParams.alphaX, localParams.alphaY, inDir, halfVectorRev) * jacobian; // TODO reverse select localParams.prob
            }
        }
    }

    return MaterialEvaluation(
        bsdf,
        pdfFwd
    );
}

vec3 sphericalToCartesian(float sintheta, float costheta, float phi) {
    return vec3(
        sintheta * cos(phi),
        sintheta * sin(phi),
        costheta
    );
}

vec3 sampleCosHemisphere(vec2 primary) {
    return sphericalToCartesian(
        sqrt(1 - primary.y),
        sqrt(primary.y),
        2.0 * PI * primary.x
    );
}

vec3 ggxSample(float alphaX, float alphaY, vec3 outDir, vec2 primary) {
    bool flip = false;
    if (outDir.z < 0) {
        flip = true;
        outDir = -outDir;
    }

    // Section 3.2: transforming the view direction to the hemisphere configuration
    vec3 Vh = normalize(vec3(alphaX * outDir.x, alphaY * outDir.y, outDir.z));

    // Section 4.1: orthonormal basis (with special case if cross product is zero)
    float lensq = Vh.x * Vh.x + Vh.y * Vh.y;
    vec3 T1 = lensq > 0 ? vec3(-Vh.y, Vh.x, 0) * inversesqrt(lensq) : vec3(1, 0, 0);
    vec3 T2 = cross(Vh, T1);

    // Section 4.2: parameterization of the projected area
    float r = sqrt(primary.x);
    float phi = 2.0 * PI * primary.y;
    float t1 = r * cos(phi);
    float t2 = r * sin(phi);
    float s = 0.5 * (1.0 + Vh.z);
    t2 = (1.0 - s) * sqrt(1.0 - t1 * t1) + s * t2;

    // Section 4.3: reprojection onto hemisphere
    vec3 Nh = t1 * T1 + t2 * T2 + sqrt(max(0.0, 1.0 - t1 * t1 - t2 * t2)) * Vh;

    // Section 3.4: transforming the normal back to the ellipsoid configuration
    vec3 Ne = normalize(vec3(alphaX * Nh.x, alphaY * Nh.y, max(0.0, Nh.z)));

    return flip ? -Ne : Ne;
}

struct MaterialSample {
    MaterialEvaluation value;
    vec3 inDir;
};

MaterialSample mtlSample(Material material, HitData hit, bool isOnLightPath, vec3 outDir, vec2 primary) {
    MaterialLocalParams localParams = mtlComputeLocalParams(material, hit, outDir);

    // Select a component to sample from and remap the primary sample coordinate
    int c;
    if (primary.x <= localParams.probDiffuse) {
        c = 0;
        primary.x = min(primary.x / localParams.probDiffuse, 1);
    } else if (primary.x <= localParams.probDiffuse + localParams.probReflect) {
        c = 1;
        primary.x = min((primary.x - localParams.probDiffuse) / localParams.probReflect, 1);
    } else {
        c = 2;
        primary.x = min((primary.x - localParams.probDiffuse - localParams.probReflect) / localParams.probTransmit, 1);
    }

    // Sample a direction from the selected component
    vec3 inDir;
    if (c == 0) {
        // Sample diffuse
        inDir = sampleCosHemisphere(primary);
        if (outDir.z < 0) inDir.z *= -1;
    }
    else {
        vec3 halfVector = ggxSample(localParams.alphaX, localParams.alphaY, outDir, primary);

        if (c == 1) {
            inDir = -reflect(outDir, halfVector);
        } else {
            float eta = outDir.z > 0 ? (1 / material.IndexOfRefraction) : material.IndexOfRefraction;
            if (dot(outDir, halfVector) < 0)
                return MaterialSample(MaterialEvaluation(vec3(0), 0), vec3(0)); // prevent NaN
            inDir = refract(-outDir, halfVector, eta);
            if (inDir == vec3(0))
                inDir = -reflect(outDir, halfVector);
        }
    }

    MaterialEvaluation eval = mtlEvaluate(material, hit, isOnLightPath, outDir, inDir);

    return MaterialSample(eval, inDir);
}