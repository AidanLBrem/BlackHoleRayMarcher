struct LightSource
{
    int instanceIndex;
    float totalArea;
    int triStart;
    int triCount;
};

struct LightTriangleData
{
    float worldSpaceArea;
    float3 worldNormal;
};

int numLightSources;
StructuredBuffer<LightSource> LightSources;
StructuredBuffer<int> LightTriangleIndices;
StructuredBuffer<LightTriangleData> LightTrianglesData;

float3 RandomPointOnTriangleWorld(Triangle tri, float4x4 localToWorld, inout uint rngState)
{
    float r1 = randomValue(rngState);
    float r2 = randomValue(rngState);

    if (r1 + r2 > 1.0) { r1 = 1.0 - r1; r2 = 1.0 - r2; }

    float3 v0Local = Vertices[TriangleIndices[tri.baseIndex]];
    float3 localPoint = v0Local + r1 * tri.edgeAB + r2 * tri.edgeAC;

    return mul(localToWorld, float4(localPoint, 1.0)).xyz;
}

float3 NEE(float3 position, float3 N, inout uint rngState)
{
    // Select light -- emission check before any heavy fetches
    LightSource light = LightSources[randomRange(rngState, 0, numLightSources)];
    int lightIdx = light.instanceIndex;
    RayTracingMaterial mat = Instances[lightIdx].material;
    if (mat.emissionStrength <= 0) return float3(0, 0, 0);

    // Sample triangle from flat list -- no BVH lookup, works for quads and any mesh
    int flatIdx = randomRange(rngState, light.triStart, light.triStart + light.triCount);
    int triIdx = LightTriangleIndices[flatIdx];
    Triangle tri = Triangles[triIdx];

    // Sample point on triangle
    float3 samplePos = RandomPointOnTriangleWorld(tri, Instances[lightIdx].localToWorldMatrix, rngState);
    float3 toLight = samplePos - position;
    float dist = length(toLight);
    float3 dirNorm = toLight / dist;

    float NdotL = dot(N, dirNorm);
    if (NdotL <= 0) return float3(0, 0, 0);

    // Shadow ray -- bias along ray direction, tighter max dist
    Ray shadowRay;
    shadowRay.position = position + dirNorm * 1e-4;
    shadowRay.direction = dirNorm;
    HitInfo hit = queryCollisions(shadowRay, dist - 1e-4, true);
    if (!hit.didHit || hit.objectIndex != lightIdx) return float3(0, 0, 0);

    // Fetch cold light data only on confirmed unoccluded hit -- cache miss paid rarely
    LightTriangleData lightData = LightTrianglesData[hit.triIndex];
    float cosLight = dot(-dirNorm, lightData.worldNormal);
    if (cosLight <= 0) return float3(0, 0, 0);

    float3 emission = mat.emissiveColor.rgb * mat.emissionStrength;
    float geometryTerm = (NdotL * cosLight) / (dist * dist);

    // pdf = 1 / (triCount * triArea), estimator cancels to:
    return emission * geometryTerm * lightData.worldSpaceArea
           * (float)(light.triCount * numLightSources);
}
