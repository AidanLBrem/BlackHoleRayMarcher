
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
float3 NEE(float3 position, inout uint rngState)
{
    //get the light source we are aiming at: WORKS
    int lightSourceIndex = LightSources[randomRange(rngState, 0, numLightSources)];
    //get the BVH node of what we are aiming at: WORKS
    int instanceBVHIndex = Instances[lightSourceIndex].firstBVHNodeIndex;

    int start = BVHNodes[instanceBVHIndex].firstIndex;
    int last = BVHNodes[instanceBVHIndex].firstIndex + BVHNodes[instanceBVHIndex].count;
    
    Triangle triangleToAimFor = Triangles[randomRange(rngState, start, last)];
    float3 randomPositionOnTriangle = RandomPointOnTriangleWorld(triangleToAimFor, Instances[lightSourceIndex].localToWorldMatrix, rngState);
    Ray NEERay;
    NEERay.position = position;
    float3 dir = randomPositionOnTriangle - position;
    float dist = length(dir);
    NEERay.direction = dir / dist;
    HitInfo hitInfo = queryCollisions(NEERay, dist, true);
    //return float3(NEERay.direction.x,NEERay.direction.y,NEERay.direction.z);
    if (hitInfo.didHit && hitInfo.objectIndex == lightSourceIndex)
    {
        
        //return Instances[hitInfo.objectIndex].material.color;
        RayTracingMaterial mat = Instances[lightSourceIndex].material;
        return mat.emissiveColor * mat.emissionStrength;
    }
    return float3(0,0,0);
    
}