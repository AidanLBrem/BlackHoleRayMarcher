Shader "Custom/RayTracer"
{   
    SubShader
    {
        HLSLINCLUDE
        ENDHLSL

        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        LOD 100
        ZWrite Off Cull Off
        Blend Off
        Pass
        {
            Name "RayTracer"

            HLSLPROGRAM
            #include "UnityCG.cginc"
            #include "Math.hlsl"
            #include "StarRenderer.hlsl"
            #include "GGX.hlsl"
            #include "LUTConversion.hlsl"
            #include "AtmosphereicScattering.hlsl"
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0
            #pragma enable_d3d11_debug_symbols
            #pragma shader_feature_local TEST_SPHERE
            #pragma shader_feature_local TEST_TRIANGLE
            #pragma shader_feature_local USE_LUT
            #pragma shader_feature_local ENABLE_LENSING
            #pragma shader_feature_local USE_TLAS
            #pragma shader_feature_local USE_REDSHIFTING
			struct appdata
			{
				float4 vertex : POSITION;
				float2 uv : TEXCOORD0;
			};
            
            //Cold, though be careful about storing it
			struct RayTracingMaterial
			{
				float4 color;
                float4 emissiveColor;
                float4 specularColor;
                float emissionStrength;
                float roughness;
			    float metallicity;
			};
            //hot - used a lot for collision detection. 
            struct HitInfo 
            {
                bool didHit;
                float distance;
                float3 hitPoint;
                float u;
                float v;
                uint triIndex;
                uint objectType;
                int objectIndex;
            };
            
            //Medium, used for node distance calculations
            struct AABBHitInfo {
                bool didHit;
                float distance;
            };
            struct Ray
            {
                float3 position;
                float3 direction; 
                float energy;
            };
            struct PixelMarcher
            {
                Ray ray;
                bool hitBlackHole;
                bool rayEarlyKill;
                float3 incomingLight;
                float3 rayColor;
                uint numBounces;
                uint triTests;
            };
            //cold
            struct Sphere
			{
				float3 position;
				float radius;
				RayTracingMaterial material;
			};
            //EXTREMELY HOT. Keep as SMALL AS POSSIBLE
            struct Triangle {
                uint baseIndex;
                
                float3 edgeAB;
                float3 edgeAC;
                float3 geometricNormal;
            };

            //Cold, only a few allocated
            struct Mesh
            {
                float4x4 localToWorldMatrix;
                float4x4 worldToLocalMatrix;
                RayTracingMaterial material;

                uint firstBVHNodeIndex;

                float AABBLeftX;
                float AABBLeftY;
                float AABBLeftZ;
                float AABBRightX;
                float AABBRightY;
                float AABBRightZ;
            };

           
            //Medium, many thousands allocated, used during BVH traversal
            struct BVHNode {
                int left;
                int right;
                uint firstIndex;
                uint count;

                float AABBLeftX;
                float AABBLeftY;
                float AABBLeftZ;
                float AABBRightX;
                float AABBRightY;
                float AABBRightZ;
            };
            //Cold
            struct BlackHole
            {
                float3 position;
                float SchwartzchildRadius;
                float blackHoleSOIMultiplier;
                float blackHoleMass;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };
            #define debug_steps 100
            #define MAX_MESHES 64 //change this based on scenes!
            float3 ViewParams;
            float4x4 CameraLocalToWorldMatrix;
            float3 CameraWorldPos;
            float CameraFar;
            float CameraNear;
            float blackHoleSOIMultiplier;
            int MarchStepsCount;
            int RaysPerPixel;
            StructuredBuffer<Sphere> Spheres;
            StructuredBuffer<BlackHole> BlackHoles;
            StructuredBuffer<Mesh> Instances;
            StructuredBuffer<float3> Vertices;
            StructuredBuffer<float3> Normals;
            StructuredBuffer<Triangle> Triangles;
            StructuredBuffer<uint> TriangleIndices;
            StructuredBuffer<BVHNode> BVHNodes;
            StructuredBuffer<BVHNode> TLASNodes;
            StructuredBuffer<uint> TLASRefs;
            int TLASRootIndex;
            int numTLASNodes;
            int numInstances;
            int numSpheres;
            int numBlackHoles;
            //int numTriangles;
            int maxBounces;
            int numMeshes;
            sampler2D _MainTexOld;
            int numRenderedFrames;
            float stepSize;
            int emergencyBreakMaxSteps;
            const float G = 1.975813844e-32;
            const float C = 0.430467210276;
            float triTests = 0;
            float BVHTests = 0;
             // Params (set from C#)
            float3 GalaxyNormal = float3(0.0,1.0,0.0); // unit normal to Milky Way plane
            float  BandHalfAngleDeg = 12.0;      // half-width of the band in degrees
            float  BandSoftDeg      = 6.0;       // feather on both sides
            float  BandBoost        = 3.0;       // how much denser/brighter in the band
            float3 galaxyCenterDir = float3(0.0,0.0,1.0);
            float _BHLUT_RMinOverRs;
            float _BHLUT_RMaxOverRs;
            float _BHLUT_LogEpsilonOverRs;
            float _BHLUT_MuResolution;
            sampler2D _BlackHoleBendLUT;
            float bendStrength = 1.0; //trying to figure out why our estimate is 27% weaker than it should be, for now mess with multiplier
            float _BHLUT_Width;
            float _BHLUT_Height;
            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex.xyz);
                o.uv = v.uv;
                return o;
            }
            struct BlackHoleDecision
            {
                bool affectsRay;
                bool shouldMarch;
                float marchEntryT;
                float impactParameter;
                float deflectionAngle;
                float3 weakBentDir;
            };

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

            BlackHoleDecision EvaluateBlackHoleForRay(Ray ray, BlackHole blackHole)
            {
                BlackHoleDecision result = (BlackHoleDecision)0;
                result.marchEntryT = 3.402823e+38;
                result.weakBentDir = normalize(ray.direction);

                float3 d = normalize(ray.direction);
                float3 rel = ray.position - blackHole.position;
                float rs = blackHole.SchwartzchildRadius;

                float marchImpactThreshold = max(rs * blackHole.blackHoleSOIMultiplier, 4.0 * rs);

                float r0 = length(rel);
                bool startInsideMarchRegion = (r0 < marchImpactThreshold);

                // MUST come before tClosest early-out
                if (startInsideMarchRegion)
                {
                    result.affectsRay = true;
                    result.shouldMarch = true;
                    result.marchEntryT = 0.0;
                    result.impactParameter = length(cross(rel, d));
                    result.deflectionAngle = 0.0;
                    result.weakBentDir = d;
                    return result;
                }

                float tClosest = -dot(rel, d);
                if (tClosest <= 0.0)
                    return result;

                float3 closestVec = rel + d * tClosest;
                float b = length(closestVec);
                float safeB = max(b, 1e-6);

                result.affectsRay = true;
                result.impactParameter = b;

                float alpha = 2.0 * rs / safeB;
                result.deflectionAngle = alpha;

                float3 bendDir = float3(0,0,0);
                if (b > 1e-6)
                    bendDir = -closestVec / b;

                result.weakBentDir = normalize(d * cos(alpha) + bendDir * sin(alpha));

                if (b < marchImpactThreshold)
                {
                    result.shouldMarch = true;

                    float tEntry = RaySphereEntryDistance(ray.position, d, blackHole.position, marchImpactThreshold);
                    result.marchEntryT = (tEntry >= 0.0) ? tEntry : 0.0;
                }

                return result;
            }
            AABBHitInfo RayAABB(float3 rayOrigin, float3 rayDirection, float3 inverseDirection, float AABBLeftX, float AABBLeftY, float AABBLeftZ, float AABBRightX, float AABBRightY, float AABBRightZ, float distanceToBeat) {
                float3 invDir = inverseDirection;
                float3 boxMin = float3(AABBLeftX,  AABBLeftY,  AABBLeftZ);
                float3 boxMax = float3(AABBRightX, AABBRightY, AABBRightZ);
                float3 tMin = (boxMin - rayOrigin) * invDir;
                float3 tMax = (boxMax - rayOrigin) * invDir;
                float3 t1 = min(tMin, tMax);
                float3 t2 = max(tMin, tMax);
                float tNear = max(max(t1.x, t1.y), t1.z);
                float tFar = min(min(t2.x, t2.y), t2.z);
                AABBHitInfo hitInfo = (AABBHitInfo)0;
                hitInfo.didHit = (tNear <= tFar) && (tFar >= 0.0) && (tNear <= distanceToBeat);
                hitInfo.distance = max(tNear, 0.0);
                return hitInfo;
            }
            HitInfo raySphere(Ray ray, float3 sphereCenter, float sphereRadius) {
				HitInfo hitInfo = (HitInfo)0;
				float3 offsetRayOrigin = ray.position - sphereCenter;
				// From the equation: sqrLength(rayOrigin + rayDir * dst) = radius^2
				// Solving for dst results in a quadratic equation with coefficients:
				float a = dot(ray.direction, ray.direction); // a = 1 (assuming unit vector)
				float b = 2 * dot(offsetRayOrigin, ray.direction);
				float c = dot(offsetRayOrigin, offsetRayOrigin) - sphereRadius * sphereRadius;
				// Quadratic discriminant
				float discriminant = b * b - 4 * a * c; 

				// No solution when d < 0 (ray misses sphere)
				if (discriminant >=  0) {
					// Distance to nearest intersection point (from quadratic formula)
					float dst = (-b - sqrt(discriminant)) / (2 * a);

					// Ignore intersections that occur behind the ray
					if (dst >= 0) {
						hitInfo.didHit = true;
						hitInfo.distance = dst;
						hitInfo.hitPoint = ray.position + ray.direction * dst;
					}
				}
				return hitInfo;
            }
            //EXTREMELY HOT. Most runtime is spent in rayTriangle!
			HitInfo rayTriangle(Ray ray, Triangle tri)
			{
				//float3 ao = ray.position - Vertices[tri.vertexIndex1];
                //float3 edgeAB = v1 - v0;
                //float3 edgeAC = v2 - v0;
                
                float3 edgeAB = tri.edgeAB;
                float3 edgeAC = tri.edgeAC;
				float3 geometricNormal = tri.geometricNormal;

                /*float3 v1 = Vertices[TriangleIndices[tri.baseIndex]];
                float3 v2 = Vertices[TriangleIndices[tri.baseIndex+1]];
                float3 v3 = Vertices[TriangleIndices[tri.baseIndex+2]];
                float3 edgeAB = v2 - v1;
                float3 edgeAC = v3 - v1;
                float3 geometricNormal = cross(edgeAB, edgeAC);*/
				float determinant = -dot(ray.direction, geometricNormal);
                if (determinant <=  0) {
                    return (HitInfo)0;
                }
				float invDet = 1 / determinant;
				// Calculate dst to triangle & barycentric coordinates of intersection point
                uint vertex1 = TriangleIndices[tri.baseIndex];
				float3 ao = ray.position - Vertices[vertex1];
				float dst = dot(ao, geometricNormal) * invDet;
                if (dst <  0) {
                    return (HitInfo)0;
                }
                float3 dao = cross(ao, ray.direction);
				float u = dot(edgeAC, dao) * invDet;
                if (u <  0) {
                    return (HitInfo)0;
                }
				float v = -dot(edgeAB, dao) * invDet;
                if (v < 0) {
                    return (HitInfo)0;
                }
                if (u + v > 1.0) {
                    return (HitInfo)0;
                }
				// Initialize hit info
				HitInfo hitInfo = (HitInfo)0;
				hitInfo.didHit = true;
				hitInfo.hitPoint = ray.position + ray.direction * dst;
                
				hitInfo.distance = dst;
                hitInfo.u = u;
                hitInfo.v = v;
				return hitInfo;
			}
            
            HitInfo checkSphereCollisions(Ray ray) {
                HitInfo closest = (HitInfo)0;

                closest.distance = 3.402823e+38;

                for (int i = 0; i < numSpheres; i++) {
                    Sphere sphere = Spheres[i];
                    HitInfo hit = raySphere(ray, sphere.position, sphere.radius);
                    if (hit.didHit && hit.distance < closest.distance) {
                        closest = hit;
                        closest.objectType = 0;
                        closest.objectIndex = -1;
                    }
                }

                return closest;
            }
            HitInfo TraverseInstanceBLAS(Ray ray, uint instanceIndex, float worldTMax)
            {
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

                int stack[64];
                float stackT[64];
                int sp = 0;

                uint nodeIdx = mesh.firstBVHNodeIndex;
                float currentTNear = -1.0;
        
                for (;;)
                {
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
                        }

                        if (sp == 0) break;
                        sp--;
                        nodeIdx = stack[sp];
                        currentTNear = stackT[sp];
                        continue;
                    }

                    BVHNode leftNode = BVHNodes[node.left];
                    BVHNode rightNode = BVHNodes[node.right];
                    
                    AABBHitInfo lh = RayAABB(
                        localRay.position, localRay.direction, inverseDirection,
                        leftNode.AABBLeftX, leftNode.AABBLeftY, leftNode.AABBLeftZ,
                        leftNode.AABBRightX, leftNode.AABBRightY, leftNode.AABBRightZ,
                        bestLocalT
                    );

                    AABBHitInfo rh = RayAABB(
                        localRay.position, localRay.direction, inverseDirection,
                        rightNode.AABBLeftX, rightNode.AABBLeftY, rightNode.AABBLeftZ,
                        rightNode.AABBRightX, rightNode.AABBRightY, rightNode.AABBRightZ,
                        bestLocalT
                    );

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

                    if (farHit && sp < 64)
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
            bool IntersectInstanceRootTight(Ray worldRay, Mesh mesh, float worldTMax, out Ray localRay, out float rootWorldT)
            {
                localRay = (Ray)0;
                rootWorldT = 0.0;

                localRay.position = mul(mesh.worldToLocalMatrix, float4(worldRay.position, 1)).xyz;

                float3 localDirUn = mul(mesh.worldToLocalMatrix, float4(worldRay.direction, 0)).xyz;
                float dirScale = length(localDirUn);
                if (dirScale < 1e-12)
                    return false;

                localRay.direction = localDirUn / dirScale;
                float3 inverseDirection = sign(localRay.direction) / max(abs(localRay.direction), 1e-8);

                BVHNode root = BVHNodes[mesh.firstBVHNodeIndex];

                AABBHitInfo rootHit = RayAABB(
                    localRay.position, localRay.direction, inverseDirection,
                    root.AABBLeftX, root.AABBLeftY, root.AABBLeftZ,
                    root.AABBRightX, root.AABBRightY, root.AABBRightZ,
                    worldTMax * dirScale
                );

                if (!rootHit.didHit)
                    return false;

                rootWorldT = rootHit.distance / dirScale;
                return rootWorldT <= worldTMax;
            }
            
            
                        
            HitInfo checkMeshCollisions(Ray ray, float worldTMax)
            {
                HitInfo closest = (HitInfo)0;
                if (numMeshes <= 0 || numInstances <= 0 || numTLASNodes <= 0 || TLASRootIndex < 0)
                    return closest;
                #ifdef USE_TLAS
                float bestWorldT = 3.402823e+38;

                int stack[64];
                float stackT[64];
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
                            HitInfo h = TraverseInstanceBLAS(ray, instanceIndex, min(worldTMax, bestWorldT));
                            if (h.didHit && h.distance < bestWorldT)
                            {
                                bestWorldT = h.distance;
                                closest = h;
                            }
                        }

                        if (sp == 0) break;
                        sp--;
                        nodeIdx = stack[sp];
                        currentTNear = stackT[sp];
                        continue;
                    }

                    BVHNode leftNode = TLASNodes[node.left];
                    BVHNode rightNode = TLASNodes[node.right];

                    AABBHitInfo lh = RayAABB(
                        ray.position, ray.direction, inverseDirection,
                        leftNode.AABBLeftX, leftNode.AABBLeftY, leftNode.AABBLeftZ,
                        leftNode.AABBRightX, leftNode.AABBRightY, leftNode.AABBRightZ,
                        min(worldTMax, bestWorldT)
                    );

                    AABBHitInfo rh = RayAABB(
                        ray.position, ray.direction, inverseDirection,
                        rightNode.AABBLeftX, rightNode.AABBLeftY, rightNode.AABBLeftZ,
                        rightNode.AABBRightX, rightNode.AABBRightY, rightNode.AABBRightZ,
                        min(worldTMax, bestWorldT)
                    );

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

                    if (farHit && sp < 64)
                    {
                        stack[sp] = farIdx;
                        stackT[sp] = farT;
                        sp++;
                    }

                    nodeIdx = nearIdx;
                    currentTNear = nearT;
                }

                return closest;
                #endif
                #ifndef USE_TLAS
               
                //the closest distance against ALL meshes, world space
                float bestWorldT = 3.402823e+38;
                
                for (int i = 0; i < numMeshes; i++)
                {
                    Mesh mesh = Instances[i];

                    // Build local ray
                    Ray localRay = ray;
                    localRay.position = mul(mesh.worldToLocalMatrix, float4(ray.position, 1)).xyz;

                    float3 localDirUn = mul(mesh.worldToLocalMatrix, float4(ray.direction, 0));
                    float  dirScale   = length(localDirUn);
                    if (dirScale <  1e-12) continue;

                    localRay.direction       = localDirUn / dirScale;
                    float3 inverseDirection = 1.0 / localRay.direction;
                    
                    //the best candidate in this mesh, local space
                    float bestLocalT = min(worldTMax, bestWorldT) * dirScale;

                    // Stack traversal (local-t!)
                    int  stack[64];
                    float stackT[64];
                    int sp = 0;

                    uint nodeIdx = mesh.firstBVHNodeIndex;
                    float currentTNear = -1.0;

                    for (;;)
                    {
                        // prune against bestLocalT/localTMax
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
                                HitInfo h = rayTriangle(localRay, tri);     // h.distance is LOCAL t
                                h.triIndex = j;
                                triTests++;
                                if (!h.didHit) continue;
                                //first, check to make sure that this is the best local candidate
                                float localT = h.distance;
                                if (localT > bestLocalT) continue;

                                // Convert to world t, no need to guard because if localT < bestLocalT, localT / dirScale should be < worldT
                                float worldT = localT / dirScale;

                                // Compute world hitpoint consistently
                                float3 hitWorld = ray.position + ray.direction * worldT;

                                bestWorldT = worldT;
                                bestLocalT = localT;

                                closest = h;
                                closest.hitPoint = hitWorld;
                                closest.distance = bestWorldT;
                                closest.objectType = 2;
                                closest.objectIndex = i;
                            }

                            if (sp == 0) break;
                            sp--;
                            nodeIdx = stack[sp];
                            currentTNear = stackT[sp];
                            continue;
                        }

                        // Internal: test children AABBs in LOCAL t
                        BVHNode leftNode  = BVHNodes[node.left];
                        BVHNode rightNode = BVHNodes[node.right];
                        

                        AABBHitInfo lh = RayAABB(localRay.position, localRay.direction, inverseDirection,
                                                 leftNode.AABBLeftX, leftNode.AABBLeftY, leftNode.AABBLeftZ,
                                                 leftNode.AABBRightX, leftNode.AABBRightY, leftNode.AABBRightZ,
                                                 bestLocalT);

                        AABBHitInfo rh = RayAABB(localRay.position, localRay.direction, inverseDirection,
                                                 rightNode.AABBLeftX, rightNode.AABBLeftY, rightNode.AABBLeftZ,
                                                 rightNode.AABBRightX, rightNode.AABBRightY, rightNode.AABBRightZ,
                                                 bestLocalT);
                        BVHTests += 2;
                        if (!lh.didHit && !rh.didHit)
                        {
                            if (sp == 0) break;
                            sp--;
                            nodeIdx = stack[sp];
                            currentTNear = stackT[sp];
                            continue;
                        }

                        // nearer-first
                        bool leftFirst = lh.didHit && (!rh.didHit || lh.distance <= rh.distance);

                        int nearIdx  = leftFirst ? node.left  : node.right;
                        float nearT   = leftFirst ? lh.distance : rh.distance;
                        int farIdx   = leftFirst ? node.right : node.left;
                        float farT    = leftFirst ? rh.distance : lh.distance;
                        bool farHit   = leftFirst ? rh.didHit : lh.didHit;

                        if (farHit && sp < 64)
                        {
                            stack[sp] = farIdx;
                            stackT[sp] = farT;
                            sp++;
                        }

                        nodeIdx = nearIdx;
                        currentTNear = nearT;
                    }
                    
                }

                return closest;
                #endif
            }
           
            float3 ApplyFakeRelativisticToneShift(float3 color, float g, float strength)
            {
                float3 redTint  = float3(1.00, 0.38, 0.12);
                float3 blueTint = float3(0.45, 0.68, 1.00);

                float tRed  = saturate((1.0 - g) * 2.0) * strength;
                float tBlue = saturate((g - 1.0) * 2.0) * strength;

                float3 outColor = color;
                outColor = lerp(outColor, outColor * redTint,  tRed);
                outColor = lerp(outColor, outColor * blueTint, tBlue);
                return outColor;
            }
            
            //Moderate temp. Will run at most numBounces * numRays per pixel
            PixelMarcher handleReflection(PixelMarcher ray, inout uint rngState, HitInfo hitInfo)
            {

                ray.numBounces++;

                RayTracingMaterial material = Instances[hitInfo.objectIndex].material;
                if (material.emissionStrength > 0.0)
                {
                    
                    float g = 1 / ray.ray.energy;
                    #ifndef USE_REDSHIFTING
                    g = 1;
                    #endif
                    float3 c = ApplyFakeRelativisticToneShift(material.emissiveColor.rgb, g, 1.0);
                    float3 emittedLight = c * material.emissionStrength * (g * g * g);
                    ray.incomingLight += emittedLight * ray.rayColor;
                    ray.rayEarlyKill = true;
                    return ray;
                }
                uint triIndex = hitInfo.triIndex;
                Triangle tri = Triangles[triIndex];
                uint index1 = TriangleIndices[tri.baseIndex];
                uint index2 = TriangleIndices[tri.baseIndex+1];
                uint index3 = TriangleIndices[tri.baseIndex+2];
                float3 n1 = Normals[index1];
                float3 n2 = Normals[index2];
                float3 n3 = Normals[index3];
                float3 N = normalize(
                    n1 * (1 - (hitInfo.u + hitInfo.v)) +
                    n2 * hitInfo.u +
                    n3 * hitInfo.v
                );
                Mesh mesh = Instances[hitInfo.objectIndex];
                float3x3 nMat = transpose((float3x3)mesh.worldToLocalMatrix);   
                N = safeNormalize(mul(nMat, N));
                float3 geomNormalLocal = normalize(Triangles[hitInfo.triIndex].geometricNormal);
                float3 Ng = safeNormalize(mul(nMat, geomNormalLocal));
                if (dot(Ng, ray.ray.direction) >  0)
                    Ng = -Ng;
                if (dot(N, Ng) <  0)
                {
                    N = -N;
                }
                if (bad3(ray.ray.direction) || bad3(ray.rayColor) || bad3(N) || bad3(Ng))
                {
                    ray.incomingLight = float3(1,0,1);
                    ray.rayEarlyKill = true;
                    return ray;
                }
                /*float3 toColor = N;
                
                ray.rayColor = float4(toColor * 0.5 + 0.5, 1);
                ray.incomingLight = ray.rayColor;
                return ray;*/

                // Standard metallic workflow
                float3 baseColor = material.color;
                float metallic = saturate(material.metallicity);
                float roughness = saturate(material.roughness);

                float3 dielectricF0 = float3(0.04, 0.04, 0.04);
                float3 F0 = lerp(dielectricF0, baseColor, metallic);

                float3 V = safeNormalize(-ray.ray.direction);
                float NdotV = saturate(dot(N, V));

                float3 F_pick = FresnelSchlick(NdotV, F0);
                float3 kd = (1.0 - F_pick) * (1.0 - metallic);
                float3 diffuseBRDF = kd * baseColor / PI;

                // Branch probability heuristic
                float specularWeight = saturate(luminance(F_pick));
                float diffuseWeight = max(luminance(diffuseBRDF), 1e-4);

                float totalWeight = specularWeight + diffuseWeight;
                if (totalWeight < 0)
                {
                    ray.rayEarlyKill = true;
                    return ray;
                }

                float specularChance = clamp(specularWeight / totalWeight, 0.001, 0.999);

                float choose = randomValue(rngState);

                if (choose < specularChance)
                {
                    float2 xi = float2(randomValue(rngState), randomValue(rngState));
                    float3 H = sampleGGX_H(xi, roughness, N);
                    float3 L = reflect(-V, H);
                    L = safeNormalize(L);

                    float NdotL = saturate(dot(N, L));
                    float NdotH = saturate(dot(N, H));
                    float VdotH = saturate(dot(V, H));

                    if (NdotL <= 0 || NdotV <= 0 || VdotH <= 0)
                    {
                        ray.rayEarlyKill = true;
                        return ray;
                    }
                    if (dot(L, Ng) <= 0)
                    {
                        ray.rayEarlyKill = true;
                        return ray;
                    }

                    float3 F_spec = FresnelSchlick(VdotH, F0);
                    float D = D_GGX(NdotH, roughness);
                    float G = G_SmithGGX(NdotV, NdotL, roughness);

                    float3 specBRDF = (F_spec * D * G) / max(4.0 * NdotV * NdotL, 1e-8);

                    float pdf_H = D * NdotH;
                    float pdf_L = pdf_H / max(4.0 * VdotH, 1e-8);
                    float branchPdf = specularChance * max(pdf_L, 1e-8);

                    ray.ray.direction = L;
                    ray.rayColor *= specBRDF * NdotL / branchPdf;
                }
                else
                {
                    // ----- DIFFUSE BRANCH: cosine-weighted hemisphere -----
                    float2 xi = float2(randomValue(rngState), randomValue(rngState));
                    float3 L = toWorld(sampleCosineHemisphere(xi), N);

                    float NdotL = saturate(dot(N, L));
                    if (NdotL <= 0)
                    {
                        ray.rayEarlyKill = true;
                        return ray;
                    }
                    if (dot(L, Ng) <= 0)
                    {
                        ray.rayEarlyKill = true;
                        return ray;
                    }
                    
                    float pdf_L = NdotL / PI;
                    float branchPdf = (1.0 - specularChance) * max(pdf_L, 1e-8);

                    ray.ray.direction = L;
                    ray.rayColor *= diffuseBRDF * NdotL / branchPdf;
                }

                ray.ray.direction = safeNormalize(ray.ray.direction);
                
                ray.ray.position = hitInfo.hitPoint + (N * 1e-4);

                // Russian roulette
                
                float p = saturate(dot(ray.rayColor, float3(0.2126, 0.7152, 0.0722)));
                if (randomValue(rngState) > p) {
                    ray.rayEarlyKill = true;
                    return ray;
                }
                
                ray.rayColor /= p;

                return ray;
            }
            
            
            Sphere checkInsideSphere(float3 position) {
                for (int i = 0; i < numSpheres; i++) {
                    Sphere sphere = Spheres[i];
                    if (length(position - sphere.position) < sphere.radius / 2) {
                        return sphere;
                    }   
                }
                return (Sphere)0;
            }
            

            HitInfo queryCollisions(Ray ray, float tMax) {
                HitInfo closest = (HitInfo)0;
                closest.distance = 3.402823e+38;
                #ifdef TEST_SPHERE
                HitInfo s = checkSphereCollisions(ray);
                if (s.didHit && s.distance <= tMax && s.distance < closest.distance) {
                    closest = s;
                }
                #endif
                #ifdef TEST_TRIANGLE
                HitInfo m = checkMeshCollisions(ray, tMax);
                if (m.didHit && m.distance <= tMax && m.distance < closest.distance) {
                    closest = m;
                }
                #endif

                return closest;
            }
            

            float estimateNearestCollidableObjectDistance(Ray ray) {
                float closestDistance = 3.402823e+38;
                for (int i = 0; i < numSpheres; i++) {
                    Sphere sphere = Spheres[i];
                    float distance = nearestPointOnSphere(ray.position, sphere.position, sphere.radius);
                    if (distance < closestDistance) {
                        closestDistance = distance;
                    }
                }
                for (int j = 0; j < numBlackHoles; j++) {
                    BlackHole blackHole = BlackHoles[j];
                    float distance = nearestPointOnSphere(ray.position, blackHole.position, blackHole.SchwartzchildRadius);
                    if (distance < closestDistance) {
                        closestDistance = distance;
                    }
                }
                //float meshDistance = nearestAlongRayAABB_BVH(ray, closestDistance);
                //if (meshDistance < closestDistance) {
                //    closestDistance = meshDistance;
                //}
                return closestDistance;
            }

            float3 schwarzschildGeodesicAccel(float3 worldPos, BlackHole blackHole, float h2)
            {
                float3 x = worldPos - blackHole.position;   // BH -> photon
                float r2 = dot(x, x);
                float r = sqrt(r2);

                // Avoid singular blow-up exactly at / inside horizon.
                float rs = blackHole.SchwartzchildRadius;
                r = max(r, rs * 1.0001);
                r2 = r * r;

                float M = blackHole.blackHoleMass;

                // x'' = -3 M h^2 x / r^5
                float r5 = r2 * r2 * r;
                float coeff = -3.0 * M * h2 / r5;

                return coeff * x;
            }
            float SampleBHLUT_Bilinear(float u, float v)
            {
                float2 texSize = float2(_BHLUT_Width, _BHLUT_Height);
                float2 uv = float2(u, v);

                // Convert uv to texel space, centered so floor() gives texel corner
                float2 p = uv * texSize - 0.5;
                float2 i = floor(p);
                float2 f = frac(p);

                float2 uv00 = (i + float2(0.5, 0.5)) / texSize;
                float2 uv10 = (i + float2(1.5, 0.5)) / texSize;
                float2 uv01 = (i + float2(0.5, 1.5)) / texSize;
                float2 uv11 = (i + float2(1.5, 1.5)) / texSize;

                // Clamp to avoid edge wrap issues
                float2 halfTexel = 0.5 / texSize;
                uv00 = clamp(uv00, halfTexel, 1.0 - halfTexel);
                uv10 = clamp(uv10, halfTexel, 1.0 - halfTexel);
                uv01 = clamp(uv01, halfTexel, 1.0 - halfTexel);
                uv11 = clamp(uv11, halfTexel, 1.0 - halfTexel);

                float s00 = tex2Dlod(_BlackHoleBendLUT, float4(uv00, 0, 0)).g;
                float s10 = tex2Dlod(_BlackHoleBendLUT, float4(uv10, 0, 0)).g;
                float s01 = tex2Dlod(_BlackHoleBendLUT, float4(uv01, 0, 0)).g;
                float s11 = tex2Dlod(_BlackHoleBendLUT, float4(uv11, 0, 0)).g;

                float sx0 = lerp(s00, s10, f.x);
                float sx1 = lerp(s01, s11, f.x);
                return lerp(sx0, sx1, f.y);
            }
            Ray ApplyBlackHoleBendLUT(
                Ray ray,
                BlackHole blackHole,
                float stepLen,
                out float3 moveDir)
            {
                float3 rel = ray.position - blackHole.position;
                float r = length(rel);
                moveDir = ray.direction;

                if (r <= 0)
                    return ray;

                float3 rayDir    = normalize(ray.direction);
                float3 radialOut = rel / r;
                float3 radialIn  = -radialOut;

                float mu = dot(rayDir, radialOut);

                float u = RadiusToLUT_U(r, blackHole.SchwartzchildRadius,
                                        _BHLUT_RMinOverRs, _BHLUT_RMaxOverRs,
                                        _BHLUT_LogEpsilonOverRs);
                float v = MuToLUT_V(mu, _BHLUT_MuResolution);

                
                float2 uv = float2(u, v);
                float2 ddxUV = float2(0.0, 0.0);
                float2 ddyUV = float2(0.0, 0.0);
                float dPhiDs = SampleBHLUT_Bilinear(u, v);
                
                float dTheta = dPhiDs * stepLen;

                float3 bendDir = radialIn - dot(radialIn, rayDir) * rayDir;
                float  bendLen = length(bendDir);

                if (bendLen > 0)
                {
                    bendDir /= bendLen;

                    float3 midDir = normalize(rayDir + bendDir * (0.5 * dTheta));
                    float3 endDir = normalize(rayDir + bendDir * dTheta);

                    moveDir           = midDir;
                    ray.direction     = endDir;
                }
                return ray;
            }

            Ray blackHoleRK4Check(
                Ray ray,
                BlackHole blackHole,
                float estimatedDistance)
            {
                float k = estimatedDistance * stepSize;
                k = max(k, 0.01);

                float3 pos0 = ray.position;
                float3 vel0 = ray.direction; // treat direction as geodesic tangent
                vel0 = normalize(vel0);

                // Conserved h^2 for this geodesic step.
                // Since |vel0| = 1 here, |x x v| is the flat-space impact parameter.
                float3 rel0 = pos0 - blackHole.position;
                float3 L0 = cross(rel0, vel0);
                float h2 = dot(L0, L0);

                // ----- RK4 on:
                // p' = v
                // v' = a(p) = -3 M h^2 x / r^5
                // -----

                // k1
                float3 dp1 = vel0;
                float3 dv1 = schwarzschildGeodesicAccel(pos0, blackHole, h2);

                // k2
                float3 pos1 = pos0 + dp1 * (0.5 * k);
                float3 vel1 = vel0 + dv1 * (0.5 * k);
                float3 dp2 = vel1;
                float3 dv2 = schwarzschildGeodesicAccel(pos1, blackHole, h2);

                // k3
                float3 pos2 = pos0 + dp2 * (0.5 * k);
                float3 vel2 = vel0 + dv2 * (0.5 * k);
                float3 dp3 = vel2;
                float3 dv3 = schwarzschildGeodesicAccel(pos2, blackHole, h2);

                // k4
                float3 pos3 = pos0 + dp3 * k;
                float3 vel3 = vel0 + dv3 * k;
                float3 dp4 = vel3;
                float3 dv4 = schwarzschildGeodesicAccel(pos3, blackHole, h2);

                float3 posEnd = pos0 + (k / 6.0) * (dp1 + 2.0 * dp2 + 2.0 * dp3 + dp4);
                float3 velEnd = vel0 + (k / 6.0) * (dv1 + 2.0 * dv2 + 2.0 * dv3 + dv4);
                

                float3 chord = posEnd - pos0;
                float chordLen = length(chord);
                float3 chordDir = (chord/chordLen);

                ray.position = pos0;
                ray.direction = chordDir;
                
                return ray;
            }
            
            PixelMarcher marchNearBlackHole(PixelMarcher ray, BlackHole blackHole, inout uint rngState)
            {
                int emergencyBreak = 0;
                float rs = blackHole.SchwartzchildRadius;

                // Reused field: now interpreted as impact threshold multiplier.
                // We use the same value as the handoff shell radius.
                float marchShellRadius = max(rs * blackHole.blackHoleSOIMultiplier, 4.0 * rs);

                while (true)
                {
                    float3 rel = ray.ray.position - blackHole.position;
                    float distanceToBlackHole = length(rel);

                    if (distanceToBlackHole < rs)
                    {
                        ray.hitBlackHole = true;
                        return ray;
                    }

                    // Exit once we've escaped beyond the shell and are moving outward.
                    if (distanceToBlackHole > marchShellRadius)
                    {
                        float3 radialOut = rel / max(distanceToBlackHole, 1e-8);
                        if (dot(ray.ray.direction, radialOut) > 0.0)
                            break;
                    }

                    float distFromHorizon = nearestPointOnSphere(ray.ray.position, blackHole.position, rs);
                    float adaptiveStep = distFromHorizon * stepSize;
                    float minStep = 0.01;
                    float stepLen = sqrt(adaptiveStep * adaptiveStep + minStep * minStep);

                    float gtt_old = ComputeGtt(distanceToBlackHole, rs);

                    #ifdef ENABLE_LENSING
                        #ifdef USE_LUT
                            float3 moveDir;
                            ray.ray = ApplyBlackHoleBendLUT(ray.ray, blackHole, stepLen, moveDir);
                        #else
                            ray.ray = blackHoleRK4Check(ray.ray, blackHole, stepLen);
                        #endif
                    #endif

                    HitInfo h = queryCollisions(ray.ray, stepLen);

                    emergencyBreak++;
                    if (emergencyBreak > emergencyBreakMaxSteps)
                    {
                        ray.rayEarlyKill = true;
                        return ray;
                    }

                    if (h.didHit)
                    {
                        ray = handleReflection(ray, rngState, h);
                        if (ray.rayEarlyKill)
                            return ray;
                    }
                    else
                    {
                        ray.ray.position += ray.ray.direction * stepLen;
                    }

                    float newDistance = length(ray.ray.position - blackHole.position);
                    float gtt_new = ComputeGtt(newDistance, rs);

                    #ifdef ENABLE_LENSING
                        #ifdef USE_REDSHIFTING
                            ray.ray.energy *= sqrt(gtt_old / gtt_new);
                        #endif
                    #endif
                }

                return ray;
            }
            float3 getAngularGrid(float3 rayDirection)
            {
                float3 dir = normalize(rayDirection);

                float phi = atan2(dir.z, dir.x);
                float theta = acos(clamp(dir.y,-1.0,1.0));

                float u = phi/(2.0*PI)+0.5;
                float v = theta/PI;

                float lonLines = 24.0;
                float latLines = 24.0;

                float2 grid = float2(u*lonLines, v*latLines);
                float2 cell = abs(frac(grid)-0.5);

                float thickness = 0.01;

                float lon = smoothstep(thickness,0.0,cell.x);
                float lat = smoothstep(thickness,0.0,cell.y);

                float line1 = max(lon,lat);

                float3 color = float3(u, v, 1.0-u);

                return color * line1;
            }
            
            
            float3 trace(float3 viewPoint, inout uint rngState)
            {
                if (isNan(stepSize)) {
                    return float3(1,1,1);
                }
                PixelMarcher pixel_marcher;
                Ray RayToStore;
                RayToStore.position = viewPoint;
                RayToStore.direction = normalize(RayToStore.position - CameraWorldPos);
                RayToStore.energy = 1;
                pixel_marcher.hitBlackHole = false;
                pixel_marcher.rayColor = 1;
                pixel_marcher.incomingLight = 0;
                pixel_marcher.rayEarlyKill  = false;
                pixel_marcher.numBounces = 0;
                pixel_marcher.triTests = 0;
                pixel_marcher.ray = RayToStore;
                int maxIterations = 1000;
                int iterations = 0;
                //ray.debugSOISteps = 0;
                //ray.maxNumTriangleTests = 0;
                while (pixel_marcher.numBounces < maxBounces && iterations < maxIterations) {
                    Ray ray = pixel_marcher.ray;

                    // First: compute weak-field bends and decide whether any BH requires marching.
                    float nearestMarchT = 3.402823e+38;
                    int nearestMarchBH = -1;

                    // Apply weak-field bends for BHs that do NOT require marching.
                    // For BHs that DO require marching, keep track of the earliest handoff point.
                    for (int bh = 0; bh < numBlackHoles; bh++)
                    {
                        BlackHoleDecision bhDecision = EvaluateBlackHoleForRay(ray, BlackHoles[bh]);

                        if (!bhDecision.affectsRay)
                            continue;

                        if (bhDecision.shouldMarch)
                        {
                            if (bhDecision.marchEntryT < nearestMarchT)
                            {
                                nearestMarchT = bhDecision.marchEntryT;
                                nearestMarchBH = bh;
                            }
                        }
                        else
                        {
                            ray.direction = bhDecision.weakBentDir;
                        }
                    }

                    // Commit the weak-field bent direction before collision tests
                    pixel_marcher.ray.direction = normalize(ray.direction);
                    ray = pixel_marcher.ray;

                    // Only look for object hits up to the nearest BH march handoff, if any.
                    float sceneTMax = (nearestMarchBH != -1) ? nearestMarchT : 100000.0;
                    HitInfo objectHitInfo = queryCollisions(ray, sceneTMax);

                    // Case 1: object hit before any BH handoff
                    if (objectHitInfo.didHit)
                    {
                        pixel_marcher = handleReflection(pixel_marcher, rngState, objectHitInfo);
                    }
                    // Case 2: no object hit, but a BH needs strong-field marching
                    else if (nearestMarchBH != -1)
                    {
                        //pixel_marcher.incomingLight += float3(0.1, 0, 0);
                        pixel_marcher.ray.position = ray.position + ray.direction * max(nearestMarchT, 1e-4);
                        pixel_marcher = marchNearBlackHole(pixel_marcher, BlackHoles[nearestMarchBH], rngState);

                        if (pixel_marcher.hitBlackHole)
                            return float3(0,0,0);
                    }
                    // Case 3: nothing hit at all, use background
                    else
                    {
                        float g = 1.0 / ray.energy;
                        float3 starColor = getStarField(ray.direction);

                        #ifndef USE_REDSHIFTING
                            g = 1.0;
                        #endif

                        float3 c = ApplyFakeRelativisticToneShift(starColor, g, 1.0);
                        pixel_marcher.incomingLight += c * (g * g * g);
                        break;
                    }
                    if (pixel_marcher.rayEarlyKill) {
                        //return float3(BVHTests / 100, BVHTests / 100, BVHTests / 100);
                        //return float3(triTests / 1000, triTests / 1000, triTests / 1000);
                        //return float3(10000,0,10000);
                        break;
                    }
                    
                    iterations++;

                    
                }    
                //return float3(BVHTests / 100, BVHTests / 100, BVHTests / 100);
                //return float3(triTests / 1000, triTests / 1000, triTests / 1000);
                if (bad3(pixel_marcher.incomingLight)) {
                    return float4(10000000,0,10000000,1);
                }
                
                if (any(isNan(pixel_marcher.ray.energy)))
                {
                    return float4(10000000,0,10000000,1);
                }
                #ifdef DEBUG_ENABLE_ENERGY_VISUALIZATION
                float g = 1.0 / max(pixel_marcher.ray.energy, 1e-6);
                float d = log2(g);   // 0 = no shift, positive = blueshift, negative = redshift

                if (d > 0.0)
                {
                    return float3(0.0, 0.0, saturate(d * 4.0)); // blue = blueshift
                }
                else
                {
                    return float3(saturate(-d * 4.0), 0.0, 0.0); // red = redshift
                }
                #endif
                return pixel_marcher.incomingLight;
            }
            
            float4 frag (v2f i) : SV_Target
            {
                
                uint2 numPixels = _ScreenParams.xy;
                uint2 pixelCoord = i.uv * numPixels;
                uint pixelIndex = pixelCoord.y * numPixels.x + pixelCoord.x;


                float3 totalIncomingLight = 0;
                uint rngState = pixelIndex + (uint)numRenderedFrames * 829123;
                //uint rngState = pixelIndex; //enable me for pix debugging
                for (int x = 0; x < RaysPerPixel; x++) {
                    uint sampleIndex = (uint)numRenderedFrames * (uint)RaysPerPixel + (uint)x + 1u;
                    float2 h = halton2(sampleIndex);

                    // per-pixel Cranley–Patterson rotation
                    uint hash = pixelIndex * 1664525u + 1013904223u;
                    float2 rot = float2((hash & 0xFFFFu) / 65536.0, (hash >> 16) / 65536.0);
                    h = frac(h + rot);

                    // subpixel jitter
                    float2 jitterUV = (h - 0.5) / (float2)_ScreenParams.xy;
                    float3 vpLocal = float3((i.uv + jitterUV) - 0.5, 1) * ViewParams;
                    float3 vp = mul(CameraLocalToWorldMatrix, float4(vpLocal, 1)).xyz;
                    
                    float3 color = trace(vp, rngState);
                    float luma = dot(color, float3(0.2126, 0.7152, 0.0722));
                    float maxLuma = 10.0;

                    if (luma > maxLuma)
                    {
                        color *= maxLuma / luma;
                    }
                    totalIncomingLight += color;
                }
                float3 pixelCol = totalIncomingLight / RaysPerPixel;
                if (any(isinf(pixelCol))) {
                    return float4(10000000,0,10000000,1);
                }
                return float4(pixelCol, 1);
            }
            ENDHLSL
        }
    }
}
