Shader "Custom/LinearMarch"
{
    // SubShader 1 — Software BVH fallback (any hardware)
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        ZWrite Off Cull Off
        LOD 200
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0
            #pragma shader_feature_local USE_TLAS

            #include "UnityCG.cginc"
            #include "Math.hlsl"
            #include "PixelFlags.hlsl"
            #include "BasicStructures.hlsl"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex.xyz);
                o.uv = v.uv;
                return o;
            }
            struct control   { uint flags; uint num_bounces; };
            struct ray       { float3 position; float3 direction; };
            struct blackhole
            {
                float3 position;
                float  schwarzchild_radius;
                float  black_hole_soi_multiplier;
            };

            RWStructuredBuffer<control> controls  : register(u1);
            RWStructuredBuffer<ray>     main_rays : register(u2);
            RWStructuredBuffer<HitInfo> hit_infos : register(u3);

            StructuredBuffer<blackhole>  blackholes;
            int   num_black_holes;
            float renderDistance;
            int   TLASRootIndex;
            int   numInstances;
            int   numTLASNodes;
            int   numMeshes;
            int InstanceBLASTraversals;
            int BVHTests;
            int triTests;
            int BLASNodeVisits;
            float _DebugInstancePosX;
            float GetSOIRadius(int i)
            {
                float rs = blackholes[i].schwarzchild_radius;
                return max(rs * blackholes[i].black_hole_soi_multiplier, 4.0 * rs);
            }

            bool RayEntersSOI(float3 origin, float3 dir, float tMax, out float tEntry, out int bhIndex)
            {
                tEntry  = tMax;
                bhIndex = -1;
                for (int i = 0; i < num_black_holes; i++)
                {
                    float t = RaySphereEntryDistance(origin, dir, blackholes[i].position, GetSOIRadius(i));
                    if (t >= 0 && t < tEntry)
                    {
                        tEntry  = t;
                        bhIndex = i;
                    }
                }
                return bhIndex >= 0;
            }
            
            HitInfo TraverseInstanceBLAS(Ray ray, uint instanceIndex, float worldTMax, bool findClosestCollisionOnly)
            {
                InstanceBLASTraversals++;

                HitInfo closest = (HitInfo)0;
                float bestWorldT = 3.402823e+38;

                Mesh mesh = Instances[instanceIndex];
                Ray localRay = ray;
                localRay.position = mul(mesh.worldToLocalMatrix, float4(ray.position, 1)).xyz;

                float3 localDirUn = mul(mesh.worldToLocalMatrix, float4(ray.direction, 0)).xyz;
                float dirScale = length(localDirUn);
                if (dirScale < 1e-12)
                    return closest;

                localRay.direction = localDirUn / dirScale;
                float3 inverseDirection = 1 / localRay.direction;
                float bestLocalT = min(worldTMax, bestWorldT) * dirScale;

                int stack[32];
                float stackT[32];
                int sp = 0;

                uint nodeIdx = mesh.firstBVHNodeIndex;
                float currentTNear = -1.0;
        
                for (;;)
                {
                    BVHTests++;
                    BLASNodeVisits++;

                    if (currentTNear > bestLocalT)
                    {
                        if (sp == 0) break;
                        sp--;
                        nodeIdx = stack[sp];
                        currentTNear = stackT[sp];
                        continue;
                    }

                    BVHNode node = BVHNodes[nodeIdx];
                    bool isLeaf = (node.left == -1) && (node.right == -1);

                    if (isLeaf)
                    {
                        for (uint j = node.firstIndex; j < node.firstIndex + node.count; j++)
                        {
                            Triangle tri = Triangles[j];
                            HitInfo h = rayTriangle(localRay, tri);
                            triTests++;
                            if (!h.didHit) continue;

                            h.triIndex = j;

                            float localT = h.distance;
                            if (localT > bestLocalT) continue;

                            float worldT = localT / dirScale;
                            float3 hitWorld = ray.position + ray.direction * worldT;

                            bestLocalT = localT;
                            bestWorldT = worldT;

                            closest = h;
                            closest.hitPoint = hitWorld;
                            closest.distance = worldT;
                            closest.objectType = 2;
                            closest.objectIndex = instanceIndex;

                            if (findClosestCollisionOnly)
                                return closest;
                        }

                        if (sp == 0) break;
                        sp--;
                        nodeIdx = stack[sp];
                        currentTNear = stackT[sp];
                        continue;
                    }

                    BVHNode leftNode = BVHNodes[node.left];
                    BVHNode rightNode = BVHNodes[node.right];

                    #ifdef ORBITAL_PLANE_TEST_POSSIBLE
                    AABBHitInfo lh = RayHitsBox(
                        localRay.position, localRay.direction, inverseDirection, 
                        leftNode.AABBLeftX, leftNode.AABBLeftY, leftNode.AABBLeftZ,
                        leftNode.AABBRightX, leftNode.AABBRightY, leftNode.AABBRightZ,
                        bestLocalT);

                    AABBHitInfo rh = RayHitsBox(
                        localRay.position, localRay.direction, inverseDirection, 
                        rightNode.AABBLeftX, rightNode.AABBLeftY, rightNode.AABBLeftZ,
                        rightNode.AABBRightX, rightNode.AABBRightY, rightNode.AABBRightZ,
                        bestLocalT);
                    #else
                    AABBHitInfo lh = RayHitsBox(
                        localRay.position, localRay.direction, inverseDirection, 
                        leftNode.AABBLeftX, leftNode.AABBLeftY, leftNode.AABBLeftZ,
                        leftNode.AABBRightX, leftNode.AABBRightY, leftNode.AABBRightZ,
                        bestLocalT);

                    AABBHitInfo rh = RayHitsBox(
                        localRay.position, localRay.direction, inverseDirection, 
                        rightNode.AABBLeftX, rightNode.AABBLeftY, rightNode.AABBLeftZ,
                        rightNode.AABBRightX, rightNode.AABBRightY, rightNode.AABBRightZ,
                        bestLocalT);
                    #endif

                    if (!lh.didHit && !rh.didHit)
                    {
                        if (sp == 0) break;
                        sp--;
                        nodeIdx = stack[sp];
                        currentTNear = stackT[sp];
                        continue;
                    }

                    bool leftFirst = lh.didHit && (!rh.didHit || lh.distance <= rh.distance);

                    int nearIdx  = leftFirst ? node.left  : node.right;
                    float nearT  = leftFirst ? lh.distance : rh.distance;
                    int farIdx   = leftFirst ? node.right : node.left;
                    float farT   = leftFirst ? rh.distance : lh.distance;
                    bool farHit  = leftFirst ? rh.didHit : lh.didHit;

                    if (farHit && sp < 32)
                    {
                        stack[sp] = farIdx;
                        stackT[sp] = farT;
                        sp++;
                    }

                    nodeIdx = nearIdx;
                    currentTNear = nearT;
                }

                return closest;
            }

            HitInfo checkMeshCollisions(Ray ray, float worldTMax, bool findClosestCollisionOnly)
            {
                HitInfo closest = (HitInfo)0;

                if (numMeshes <= 0 || numInstances <= 0 || numTLASNodes <= 0 || TLASRootIndex < 0)
                    return closest;

                #ifdef USE_TLAS
                float bestWorldT = 3.402823e+38;
                int stack[32];
                float stackT[32];
                int sp = 0;
                uint nodeIdx = TLASRootIndex;
                float currentTNear = -1.0;
                float3 inverseDirection = 1 / ray.direction;

                for (;;)
                {
                    if (currentTNear > bestWorldT || currentTNear > worldTMax)
                    {
                        if (sp == 0) break;
                        sp--;
                        nodeIdx = stack[sp];
                        currentTNear = stackT[sp];
                        continue;
                    }

                    BVHNode node = TLASNodes[nodeIdx];
                    bool isLeaf = (node.left == -1) && (node.right == -1);

                    if (isLeaf)
                    {
                        for (uint j = node.firstIndex; j < node.firstIndex + node.count; j++)
                        {
                            uint instanceIndex = TLASRefs[j];
                            HitInfo h = TraverseInstanceBLAS(ray, instanceIndex, min(worldTMax, bestWorldT), findClosestCollisionOnly);
                            if (h.didHit && h.distance < bestWorldT)
                            {
                                bestWorldT = h.distance;
                                closest = h;
                                if (findClosestCollisionOnly) return closest;
                            }
                        }
                        if (sp == 0) break;
                        sp--; nodeIdx = stack[sp]; currentTNear = stackT[sp];
                        continue;
                    }

                    BVHNode leftNode  = TLASNodes[node.left];
                    BVHNode rightNode = TLASNodes[node.right];

                    AABBHitInfo lh = RayHitsBox(ray.position, ray.direction, inverseDirection,
                        leftNode.AABBLeftX,  leftNode.AABBLeftY,  leftNode.AABBLeftZ,
                        leftNode.AABBRightX, leftNode.AABBRightY, leftNode.AABBRightZ, bestWorldT);
                    AABBHitInfo rh = RayHitsBox(ray.position, ray.direction, inverseDirection,
                        rightNode.AABBLeftX,  rightNode.AABBLeftY,  rightNode.AABBLeftZ,
                        rightNode.AABBRightX, rightNode.AABBRightY, rightNode.AABBRightZ, bestWorldT);

                    if (!lh.didHit && !rh.didHit)
                    {
                        if (sp == 0) break;
                        sp--; nodeIdx = stack[sp]; currentTNear = stackT[sp];
                        continue;
                    }

                    bool leftFirst = lh.didHit && (!rh.didHit || lh.distance <= rh.distance);
                    int  nearIdx   = leftFirst ? node.left  : node.right;
                    float nearT    = leftFirst ? lh.distance : rh.distance;
                    int  farIdx    = leftFirst ? node.right : node.left;
                    float farT     = leftFirst ? rh.distance : lh.distance;
                    bool farHit    = leftFirst ? rh.didHit : lh.didHit;

                    if (farHit && sp < 32) { stack[sp] = farIdx; stackT[sp] = farT; sp++; }
                    nodeIdx = nearIdx;
                    currentTNear = nearT;
                }
                return closest;

                #else
                float bestWorldT = 3.402823e+38;
                for (int i = 0; i < numMeshes; i++)
                {
                    Mesh mesh = Instances[i];
                    Ray localRay = ray;

                    float3 localDirUn = mul(mesh.worldToLocalMatrix, float4(ray.direction, 0));
                    float dirScale = length(localDirUn);
                    if (dirScale < 1e-12) continue;

                    localRay.direction = localDirUn / dirScale;
                    float3 inverseDirection = 1.0 / localRay.direction;
                    float bestLocalT = min(worldTMax, bestWorldT) * dirScale;

                    int stack[32];
                    float stackT[32];
                    int sp = 0;
                    uint nodeIdx = mesh.firstBVHNodeIndex;
                    float currentTNear = -1.0;
                    localRay.position = mul(mesh.worldToLocalMatrix, float4(ray.position, 1)).xyz;

                    for (;;)
                    {
                        if (currentTNear > bestLocalT)
                        {
                            if (sp == 0) break;
                            sp--; nodeIdx = stack[sp]; currentTNear = stackT[sp];
                            continue;
                        }

                        BVHNode node = BVHNodes[nodeIdx];
                        bool isLeaf = (node.left == -1) && (node.right == -1);

                        if (isLeaf)
                        {
                            for (uint j = node.firstIndex; j < node.firstIndex + node.count; j++)
                            {
                                Triangle tri = Triangles[j];
                                HitInfo h = rayTriangle(localRay, tri);
                                h.triIndex = j;
                                if (!h.didHit) continue;
                                float localT = h.distance;
                                if (localT > bestLocalT) continue;
                                float worldT = localT / dirScale;
                                bestWorldT = worldT;
                                bestLocalT = localT;
                                closest = h;
                                closest.hitPoint  = ray.position + ray.direction * worldT;
                                closest.distance  = bestWorldT;
                                closest.objectType  = 2;
                                closest.objectIndex = i;
                            }
                            if (sp == 0) break;
                            sp--; nodeIdx = stack[sp]; currentTNear = stackT[sp];
                            continue;
                        }

                        BVHNode leftNode  = BVHNodes[node.left];
                        BVHNode rightNode = BVHNodes[node.right];

                        AABBHitInfo lh = RayHitsBox(localRay.position, localRay.direction, inverseDirection,
                            leftNode.AABBLeftX,  leftNode.AABBLeftY,  leftNode.AABBLeftZ,
                            leftNode.AABBRightX, leftNode.AABBRightY, leftNode.AABBRightZ, bestLocalT);
                        AABBHitInfo rh = RayHitsBox(localRay.position, localRay.direction, inverseDirection,
                            rightNode.AABBLeftX,  rightNode.AABBLeftY,  rightNode.AABBLeftZ,
                            rightNode.AABBRightX, rightNode.AABBRightY, rightNode.AABBRightZ, bestLocalT);

                        if (!lh.didHit && !rh.didHit)
                        {
                            if (sp == 0) break;
                            sp--; nodeIdx = stack[sp]; currentTNear = stackT[sp];
                            continue;
                        }

                        bool leftFirst = lh.didHit && (!rh.didHit || lh.distance <= rh.distance);
                        int  nearIdx   = leftFirst ? node.left  : node.right;
                        float nearT    = leftFirst ? lh.distance : rh.distance;
                        int  farIdx    = leftFirst ? node.right : node.left;
                        float farT     = leftFirst ? rh.distance : lh.distance;
                        bool farHit    = leftFirst ? rh.didHit : lh.didHit;

                        if (farHit && sp < 32) { stack[sp] = farIdx; stackT[sp] = farT; sp++; }
                        nodeIdx = nearIdx;
                        currentTNear = nearT;
                    }
                }
                return closest;
                #endif
            }

            HitInfo TraceRay(float3 origin, float3 direction, float tMax)
            {
                HitInfo result = (HitInfo)0;
                Ray r;
                r.position  = origin;
                r.direction = direction;
                HitInfo hit = checkMeshCollisions(r, tMax, false);
                if (hit.didHit)
                {
                    result.didHit       = true;
                    result.distance     = hit.distance;
                    result.objectIndex  = (uint)hit.objectIndex;
                    result.triIndex     = hit.triIndex;
                    result.u            = hit.u;
                    result.v            = hit.v;
                }
                return result;
            }

            float4 frag(v2f i) : SV_Target
            {
                //return float4(_DebugInstancePosX * 0.1, 0, 0, 1);
                uint pixelIndex = getPixelIndex(i);

                if (!HasFlag(controls[pixelIndex].flags, FLAG_NEEDS_LINEAR_MARCH))
                    return float4(0, 0, 0, 0);

                ray r = main_rays[pixelIndex];

                float tSOI;
                int   bhIdx;
                bool  entersSOI = RayEntersSOI(r.position, r.direction, renderDistance, tSOI, bhIdx);
                float tMax      = entersSOI ? tSOI : renderDistance;

                HitInfo hit = TraceRay(r.position, r.direction, tMax);
                hit_infos[pixelIndex] = hit;
                //return float4(r.direction, 1);
                if (hit.didHit)
                {
                    r.position = r.position + r.direction * hit.distance;
                    main_rays[pixelIndex] = r;
                    controls[pixelIndex].flags = FLAG_NEEDS_SCATTER_LINEAR;
                    
                    return float4(1,0,0,0);
                }
                else if (entersSOI)
                {
                    r.position = r.position + r.direction * tSOI;
                    main_rays[pixelIndex] = r;
                    controls[pixelIndex].flags = FLAG_NEEDS_GEODESIC_MARCH;
                    return float4(0, 1, 0, 0);
                }
                else
                {
                    controls[pixelIndex].flags = FLAG_NEEDS_SKYBOX;
                    return float4(0, 0, 1, 0);
                }
            }
            ENDHLSL
        }
    }
}