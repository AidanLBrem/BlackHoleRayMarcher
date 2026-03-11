// GGX / Trowbridge-Reitz half-vector sampling
float3 sampleGGX_H(float2 xi, float roughness, float3 N)
{
    // Clamp to avoid singular behavior at 0
    float a = max(0.001, roughness * roughness);
    float a2 = a * a;

    float phi = 2.0 * PI * xi.x;
    float cosTheta = sqrt((1.0 - xi.y) / (1.0 + (a2 - 1.0) * xi.y));
    float sinTheta = sqrt(max(0.0, 1.0 - cosTheta * cosTheta));

    float3 Hlocal = float3(
        sinTheta * cos(phi),
        sinTheta * sin(phi),
        cosTheta
    );

    return toWorld(Hlocal, N);
}

float D_GGX(float NdotH, float roughness)
{
    float a = max(0.001, roughness * roughness);
    float a2 = a * a;
    float denom = (NdotH * NdotH) * (a2 - 1.0) + 1.0;
    return a2 / max(PI * denom * denom, 0);
}

float G1_SmithGGX(float NdotX, float roughness)
{
    float a = max(0.001, roughness * roughness);
    float a2 = a * a;
    float denom = NdotX + sqrt(a2 + (1.0 - a2) * NdotX * NdotX);
    return (2.0 * NdotX) / max(denom, 0);
}

float G_SmithGGX(float NdotV, float NdotL, float roughness)
{
    return G1_SmithGGX(NdotV, roughness) * G1_SmithGGX(NdotL, roughness);
}

float3 FresnelSchlick(float cosTheta, float3 F0)
{
    return F0 + (1.0 - F0) * pow(1.0 - cosTheta, 5.0);
}

float luminance(float3 c)
{
    return dot(c, float3(0.2126, 0.7152, 0.0722));
}