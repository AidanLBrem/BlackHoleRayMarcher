
int numLightSources;
StructuredBuffer<uint> LightSources;
float3 RandomPointOnTriangleWorld(Triangle tri, float4x4 localToWorld, inout uint rngState)
{
    float r1 = randomValue(rngState);
    float r2 = randomValue(rngState);
    
    if (r1 + r2 > 1.0) { r1 = 1.0 - r1; r2 = 1.0 - r2; }
    
    float3 v0Local = Vertices[TriangleIndices[tri.baseIndex]];
    float3 localPoint = v0Local + r1 * tri.edgeAB + r2 * tri.edgeAC;
    
    return mul(localToWorld, float4(localPoint, 1.0)).xyz;
}
float3 RandomPointOnTriangle(Triangle tri, inout uint rngState)
{
    float r1 = randomValue(rngState);
    float r2 = randomValue(rngState);
    
    if (r1 + r2 > 1.0) { r1 = 1.0 - r1; r2 = 1.0 - r2; }
    
    float3 v0Local = Vertices[TriangleIndices[tri.baseIndex]];
    float3 localPoint = v0Local + r1 * tri.edgeAB + r2 * tri.edgeAC;
    
    return localPoint;
}
//first pass: just pick a random light source and try to hit it. Return the emission if it does
float3 NEE(float3 position, float3 N, inout uint rngState)
{
    int lightSourceIndex = LightSources[randomRange(rngState, 0, numLightSources)];
    int instanceBVHIndex = Instances[lightSourceIndex].firstBVHNodeIndex;

    int start = BVHNodes[instanceBVHIndex].firstIndex;
    int count = BVHNodes[instanceBVHIndex].count;
    int triIndex = randomRange(rngState, start, start + count);
    
    Triangle triangleToAimFor = Triangles[triIndex];
    float3 randomPositionOnTriangle = RandomPointOnTriangleWorld(triangleToAimFor, Instances[lightSourceIndex].localToWorldMatrix, rngState);
    
    float3 dir = randomPositionOnTriangle - position;
    float dist = length(dir);
    float3 dirNorm = dir / dist;
    
    float NdotL = saturate(dot(N, dirNorm));
    if (NdotL <= 0) return float3(0, 0, 0);

    float3 lightNormalLocal = normalize(cross(triangleToAimFor.edgeAB, triangleToAimFor.edgeAC));
    float3x3 normalMatrix = transpose((float3x3)Instances[lightSourceIndex].worldToLocalMatrix);
    float3 lightNormal = normalize(mul(normalMatrix, lightNormalLocal));

    // abs() because we allow back face hits — light emits from both sides
    float cosLight = abs(dot(-dirNorm, lightNormal));
    
    Ray NEERay;
    NEERay.position = position;
    NEERay.direction = dirNorm;
    HitInfo hitInfo = queryCollisions(NEERay, dist + 1e-3, true);

    if (!hitInfo.didHit || hitInfo.objectIndex != lightSourceIndex)
        return float3(0, 0, 0);
    RayTracingMaterial mat = Instances[lightSourceIndex].material;
    float3 emission = mat.emissiveColor.rgb * mat.emissionStrength;
    
    return emission * NdotL * cosLight;
}