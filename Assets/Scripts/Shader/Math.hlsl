#define PI 3.1415926
#include "UnityShaderVariables.cginc"
const float2x2 bayerMatrix2x2 = float2x2(
    0.0, 2.0,
    3.0, 1.0
) / 4.0;
const float4x4 bayerMatrix4x4 = float4x4
(
0.0,  8.0,  2.0, 10.0,
12.0, 4.0,  14.0, 6.0,
3.0,  11.0, 1.0, 9.0,
15.0, 7.0,  13.0, 5.0
) / 16.0;
float randomValue(inout uint state) {
    state *= state * 747796405 + 2891336453;
    uint result = ((state >> ((state >> 28) + 4)) ^ state) * 277803737;

    result = (result >> 22) ^ result;
    return result / 4294967295.0;
}
uint randomUint(inout uint state) {
    state = state * 747796405 + 2891336453;
    uint result = ((state >> ((state >> 28) + 4)) ^ state) * 277803737;
    result = (result >> 22) ^ result;
    return result;
}
int randomRange(inout uint state, int minVal, int maxVal) {
    uint range = (uint)(maxVal - minVal);
    return minVal + (int)(randomUint(state) % range);
}

bool isNan(float value)
{
    uint bits = asuint(value);
    uint exponent = (bits << 1) >> 24;
    uint mantissa = (bits << 9) >> 9;
    return exponent == 255 && mantissa > 0;
}
bool bad3(float3 v)
{
    return any(isNan(v)) || any(isinf(v));
}


float2 halton2(uint idx)
{
	float h2 = 0.0, f2 = 0.5;
	uint i = idx;
	[loop] while (i > 0u) { h2 += (i & 1u) ? f2 : 0.0; f2 *= 0.5; i >>= 1; }

	float h3 = 0.0, f3 = 1.0 / 3.0;
	i = idx;
	[loop] while (i > 0u) { h3 += float(i % 3u) * f3; f3 *= (1.0 / 3.0); i /= 3u; }

	return float2(h2, h3);
}


float3 safeNormalize(float3 v)
{
    float len2 = dot(v, v);
    return (len2 > 1e-20) ? v * rsqrt(len2) : float3(0, 1, 0);
}

void buildOrthonormalBasis(float3 N, out float3 T, out float3 B)
{
    float3 up = abs(N.z) < 0.999 ? float3(0, 0, 1) : float3(1, 0, 0);
    T = safeNormalize(cross(up, N));
    B = cross(N, T);
}

float3 sampleCosineHemisphere(float2 xi)
{
    float r = sqrt(xi.x);
    float phi = 2.0 * PI * xi.y;

    float x = r * cos(phi);
    float y = r * sin(phi);
    float z = sqrt(max(0.0, 1.0 - x * x - y * y));

    return float3(x, y, z);
}

float3 toWorld(float3 localDir, float3 N)
{
    float3 T, B;
    buildOrthonormalBasis(N, T, B);
    return safeNormalize(T * localDir.x + B * localDir.y + N * localDir.z);
}

float distanceTo(float3 position, float3 position2) {
    return length(position - position2);
}

float3 rayTo(float3 position, float3 position2) {
    return (position - position2);
}

float pointAABBDistance(float3 p, float aabbMinX, float aabbMinY, float aabbMinZ, float aabbMaxX, float aabbMaxY, float aabbMaxZ) {
    float3 AABBCenter = float3(
        (aabbMinX + aabbMaxX) * 0.5,
        (aabbMinY + aabbMaxY) * 0.5,
        (aabbMinZ + aabbMaxZ) * 0.5
    );
    return length(p - AABBCenter);
}

float aabbUnsignedDistance(float3 p, float3 bmin, float3 bmax) {
    float3 d1 = p - bmin;
    float3 d2 = bmax - p;
    return abs(min(min(d1.x, d2.x),
        min(min(d1.y, d2.y),
            min(d1.z, d2.z))));
}

float nearestPointOnSphere(float3 p, float3 sphereCenter, float sphereRadius) {
    float distanceToCenter = length(p - sphereCenter);
    return max(0.0, distanceToCenter - sphereRadius);

}

// Simple float3 -> float hash
float hash(float3 p)
{
    return frac(sin(dot(p, float3(12.9898, 78.233, 45.164))) * 43758.5453123);
}

// SmoothStep implementation (to avoid relying on intrinsic availability)
float SmoothStep(float edge0, float edge1, float x)
{
    float t = saturate((x - edge0) / max(edge1 - edge0, 0));
    return t * t * (3.0 - 2.0 * t);
}

float3 DebugDirection(float3 dir)
{
    dir = normalize(dir);
    return 0.5 + 0.5 * dir;
}


float RaySphereEntryDistance(float3 rayOrigin, float3 rayDir, float3 sphereCenter, float sphereRadius)
{
    float3 oc = rayOrigin - sphereCenter;
    float B = dot(oc, rayDir);
    float Cq = dot(oc, oc) - sphereRadius * sphereRadius;
    float disc = B * B - Cq;

    if (disc < 0.0)
        return -1.0;

    float s = sqrt(disc);
    float t0 = -B - s;
    float t1 = -B + s;

    bool inside = dot(oc, oc) <= sphereRadius * sphereRadius;
    if (inside)
        return 0.0;

    if (t0 >= 0.0) return t0;
    if (t1 >= 0.0) return t1;
    return -1.0;
}
            
float RaySphereExitDistance(
    float3 rayOrigin,
    float3 rayDirection,
    float3 sphereCenter,
    float sphereRadius)
{
    float3 oc = rayOrigin - sphereCenter;
    float b = dot(oc, rayDirection);
    float c = dot(oc, oc) - sphereRadius * sphereRadius;
    float h = b * b - c;

    if (h < 0.0)
        return -1.0;

    float s = sqrt(h);
    float t1 = -b + s;

    // If inside sphere, t1 is the forward exit.
    bool inside = (dot(oc, oc) < sphereRadius * sphereRadius);

    if (inside)
        return t1;
    

    return -1.0;
}

float3 orderedDither(float2 uv , float3 color, float lum, int matrixSize)
{
    float threshold = 0;
    int x,y;
    switch (matrixSize)
    {
    case 2:
        x = int(uv.x * _ScreenParams.x) % 2;
        y = int(uv.y * _ScreenParams.y) % 2;   
        threshold = bayerMatrix2x2[y][x];
        break;
    case 4:
        x = int(uv.x * _ScreenParams.x) % 4;
        y = int(uv.y * _ScreenParams.y) % 4;
        threshold = bayerMatrix4x4[y][x];
        break;
    }
    if (lum < threshold)
    {
        return float3(0,0,0);
    }
    return color;
}
