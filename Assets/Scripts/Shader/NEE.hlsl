    struct LightSource
    {
        int instanceIndex;
        float totalArea;
    };

    int numLightSources;
    StructuredBuffer<LightSource> LightSources;

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
        LightSource light = LightSources[randomRange(rngState, 0, numLightSources)];
        int lightSourceIndex = light.instanceIndex;
        int instanceBVHIndex = Instances[lightSourceIndex].firstBVHNodeIndex;
        if (Instances[lightSourceIndex].material.emissionStrength == 0)
        {
            return float3(0,0,0);
        }
        int start = BVHNodes[instanceBVHIndex].firstIndex;
        int count = BVHNodes[instanceBVHIndex].count;
        int triIndex = randomRange(rngState, start, start + count);
        Triangle triangleToAimFor = Triangles[triIndex];
        float3 randomPositionOnTriangle = RandomPointOnTriangleWorld(
            triangleToAimFor, Instances[lightSourceIndex].localToWorldMatrix, rngState);
        
        float3 dir = randomPositionOnTriangle - position;
        float dist = length(dir);
        float3 dirNorm = dir / dist;
        //return float3(dirNorm * 0.5 + 0.5); // visualize direction as color
        float NdotL = saturate(dot(N, dirNorm));
        if (NdotL <= 0) return float3(0, 1000, 0);

        float3 lightNormalLocal = normalize(cross(triangleToAimFor.edgeAB, triangleToAimFor.edgeAC));
        float3x3 normalMatrix = transpose((float3x3)Instances[lightSourceIndex].worldToLocalMatrix);
        float3 lightNormal = normalize(mul(normalMatrix, lightNormalLocal));
        float cosLight = abs(dot(-dirNorm, lightNormal));
        
        Ray NEERay;
        NEERay.position = position;
        NEERay.direction = dirNorm;
        HitInfo hitInfo = queryCollisions(NEERay, dist + 1e-3, true);

        if (!hitInfo.didHit || hitInfo.objectIndex != lightSourceIndex)
            //return Instances[hitInfo.objectIndex].material.color;
            return float3(1000, 0, 0);

        RayTracingMaterial mat = Instances[lightSourceIndex].material;
        float3 emission = mat.emissiveColor.rgb * mat.emissionStrength;

        // geometry term: cosine at light / dist²
        float geometryTerm = cosLight / (dist * dist);

        // full estimator: emission * NdotL * geometryTerm * totalArea / pdf_selection
        // totalArea/pdf_selection = totalArea * numLightSources * count
        // this correctly weights large lights more than small ones
        //return float3(1000,1000,1000);
        return emission * NdotL * geometryTerm * light.totalArea / (float)numLightSources;
    }