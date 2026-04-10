// GGX / Trowbridge-Reitz half-vector sampling
//To be honest, this stuff was mostly taken from a paper
//TODO: Try to understand what is actually going on here
// Visible Normal Distribution Function sampling (Heitz 2018)
// Guarantees NdotL > 0 and VdotH > 0 by construction — no wasted samples
float3 sampleGGX_VNDF(float2 xi, float roughness, float3 N, float3 V)
{
    float a = max(0.001, roughness * roughness);

    // Transform V to local space
    float3 T, B;
    buildOrthonormalBasis(N, T, B);
    float3 Vlocal = float3(dot(V, T), dot(V, B), dot(V, N));

    // Stretch view direction
    float3 Vh = normalize(float3(a * Vlocal.x, a * Vlocal.y, Vlocal.z));

    // Build orthonormal basis around Vh
    float lensq = Vh.x * Vh.x + Vh.y * Vh.y;
    float3 T1 = lensq > 0 ? float3(-Vh.y, Vh.x, 0) / sqrt(lensq) : float3(1, 0, 0);
    float3 T2 = cross(Vh, T1);

    // Sample point on disk
    float r = sqrt(xi.x);
    float phi = 2.0 * PI * xi.y;
    float t1 = r * cos(phi);
    float t2 = r * sin(phi);
    float s = 0.5 * (1.0 + Vh.z);
    t2 = (1.0 - s) * sqrt(1.0 - t1 * t1) + s * t2;

    // Compute half vector in stretched space
    float3 Nh = t1 * T1 + t2 * T2 + sqrt(max(0.0, 1.0 - t1*t1 - t2*t2)) * Vh;

    // Unstretch
    float3 Hlocal = normalize(float3(a * Nh.x, a * Nh.y, max(0.0, Nh.z)));

    // Transform back to world space
    float3 H = normalize(T * Hlocal.x + B * Hlocal.y + N * Hlocal.z);

    // Ensure H is in the same hemisphere as V — guarantees VdotH > 0
    if (dot(H, V) < 0)
        H = -H;

    return H;
}

float D_GGX(float NdotH, float roughness)
{
    float a = max(0.001, roughness * roughness);
    float a2 = a * a;
    float denom = (NdotH * NdotH) * (a2 - 1.0) + 1.0;
    return a2 / max(PI * denom * denom, 1e-8);
}

float G1_SmithGGX(float NdotX, float roughness)
{
    float a = max(0.001, roughness * roughness);
    float a2 = a * a;
    float denom = NdotX + sqrt(a2 + (1.0 - a2) * NdotX * NdotX);
    return (2.0 * NdotX) / max(denom, 1e-8);
}

float G_SmithGGX(float NdotV, float NdotL, float roughness)
{
    return G1_SmithGGX(NdotV, roughness) * G1_SmithGGX(NdotL, roughness);
}

float3 FresnelSchlick(float cosTheta, float3 F0)
{
    return F0 + (1.0 - F0) * pow(max(0.0, 1.0 - cosTheta), 5.0);
}

float luminance(float3 c)
{
    return dot(c, float3(0.2126, 0.7152, 0.0722));
}