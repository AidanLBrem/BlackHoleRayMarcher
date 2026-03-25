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
            #pragma shader_feature_local APPLY_RAYLEIGH
            #pragma shader_feature_local APPLY_MIE
            #pragma shader_feature_local APPLY_SUNDISK
            #pragma shader_feature_local APPLY_SCATTERING
            #pragma shader_feature_local APPLY_SUN_LIGHTING
            #pragma shader_feature_local IMPACT_PARAMETER_DEBUG
            #pragma shader_feature_local ORBITAL_PLANE_TEST_POSSIBLE
            #pragma shader_feature_local DEBUG_DISPLAY_TRIANGLE_TESTS
            #pragma shader_feature_local DEBUG_DISPLAY_BVH_NODES_VISITED
            #pragma shader_feature_local DEBUG_DISPLAY_TLAS_NODE_VISITS
            #pragma shader_feature_local DEBUG_DISPLAY_BLAS_NODE_VISITS
            #pragma shader_feature_local DEBUG_DISPLAY_INSTANCE_BLAS_TRAVERSALS
            #pragma shader_feature_local DEBUG_DISPLAY_TLAS_LEAF_REFS

			struct appdata
			{
				float4 vertex : POSITION;
				float2 uv : TEXCOORD0;
			};
            
            struct RayTracingMaterial
			{
				float4 color;
                float4 emissiveColor;
                float4 specularColor;
                float emissionStrength;
                float roughness;
			    float metallicity;
			};

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

                float3 lastHitNormal;
                float3 lastHitAlbedo;
                bool hasLastHit;
            };

            struct Sphere
			{
				float3 position;
				float radius;
				RayTracingMaterial material;
			};

            struct Triangle
            {
                uint baseIndex;
                float3 edgeAB;
                float3 edgeAC;
                //float3 geometricNormal;
            };

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

            struct BVHNode
            {
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

            struct BlackHole
            {
                float3 position;
                float SchwartzchildRadius;
                float blackHoleSOIMultiplier;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            #define debug_steps 100
            #define MAX_MESHES 64

            float3 ViewParams;
            float4x4 CameraLocalToWorldMatrix;
            float3 CameraWorldPos;
            float3 sunDirection;
            float CameraFar;
            float CameraNear;
            float blackHoleSOIMultiplier;
            int MarchStepsCount;
            int RaysPerPixel;
            int framesPerScatter;

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
            int maxBounces;
            int numMeshes;

            sampler2D _MainTexOld;
            int numRenderedFrames;
            float stepSize;
            int emergencyBreakMaxSteps;

            const float G = 1.975813844e-32;
            const float C = 0.430467210276;

            int triTests = 0;
            int triTestsSaturation = 1;

            int BVHTests = 0;
            int BVHTestsSaturation = 1;

            int TLASNodeVisits = 0;
            int TLASNodeVisitsSaturation = 1;

            int BLASNodeVisits = 0;
            int BLASNodeVisitsSaturation = 1;

            int InstanceBLASTraversals = 0;
            int InstanceBLASTraversalsSaturation = 1;

            int TLASLeafRefsVisited = 0;
            int TLASLeafRefsVisitedSaturation = 1;

            float strongFieldCurvatureRadPetMeterCutoff;
            float inScatteringPoints;

            struct OrbitalPlaneParameters
            {
                float3 localOrbitalPlaneNormal;
                float localPlaneD;
            };
            
            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex.xyz);
                o.uv = v.uv;
                return o;
            }

            struct MultiBlackHoleDecision
            {
                float3 weakBendDirection;
                float nearestMarchDist;
                bool insideAnySOI;
                int activeStrongFieldMarchCount;
                int activeStrongFieldMarchers[32];
            };

            struct BlackHoleDecision
            {
                bool affectsRay;
                bool shouldMarch;
                float marchEntryT;
                float impactParameter;
                float deflectionAngle;
                float3 weakBentDir;
            };
            
            OrbitalPlaneParameters GetLocalOrbitalPlaneParameters(float3 rayPosition, float3 bhPosition, float3 rayDirection, float4x4 worldToLocalMatrix)
            {
                float3 orbitalPlaneNormal = normalize(cross(rayTo(rayPosition, bhPosition), rayDirection));
                float3 orbitalPlaneNormalLocal = normalize(mul((float3x3)worldToLocalMatrix, orbitalPlaneNormal));
                    
                float3 localOrigin = mul(worldToLocalMatrix, float4(rayPosition, 1.0)).xyz;
                float planeD = dot(orbitalPlaneNormalLocal, localOrigin);

                OrbitalPlaneParameters result;
                result.localOrbitalPlaneNormal = orbitalPlaneNormalLocal;
                result.localPlaneD = planeD;
                return result;
            }
            
            OrbitalPlaneParameters GetWorldOrbitalPlaneParameters(float3 rayPosition, float3 bhPosition, float3 rayDirection)
            {
                float3 orbitalPlaneNormal = normalize(cross(rayTo(rayPosition, bhPosition), rayDirection));
                float planeD = dot(orbitalPlaneNormal, rayPosition);

                OrbitalPlaneParameters result;
                result.localOrbitalPlaneNormal = orbitalPlaneNormal;
                result.localPlaneD = planeD;
                return result;
            }

            BlackHoleDecision EvaluateBlackHoleForRay(Ray ray, BlackHole blackHole)
            {
                BlackHoleDecision result = (BlackHoleDecision)0;
                result.marchEntryT = 3.402823e+38;
                result.weakBentDir = ray.direction;

                float3 d = ray.direction;
                float3 rel = ray.position - blackHole.position;
                float rs = blackHole.SchwartzchildRadius;

                float marchImpactThreshold = max(rs * blackHole.blackHoleSOIMultiplier, 4.0 * rs);

                float r0 = length(rel);
                bool startInsideMarchRegion = (r0 < marchImpactThreshold);

                if (startInsideMarchRegion)
                {
                    result.affectsRay = true;
                    result.shouldMarch = true;
                    result.marchEntryT = 0.0;
                    float tClosestInside = -dot(rel, d);
                    float3 closestVecInside = rel + d * tClosestInside;
                    result.impactParameter = length(closestVecInside);
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
                    result.weakBentDir = d;
                    float tEntry = RaySphereEntryDistance(ray.position, d, blackHole.position, marchImpactThreshold);
                    result.marchEntryT = (tEntry >= 0.0) ? tEntry : 0.0;
                }

                return result;
            }
            
            MultiBlackHoleDecision FindAllStrongFieldMarchers(Ray ray, MultiBlackHoleDecision result, int indexStart)
            {
                for (int i = indexStart + 1; i < numBlackHoles; i++)
                {
                    BlackHoleDecision d = EvaluateBlackHoleForRay(ray, BlackHoles[i]);
                    if (!d.affectsRay)
                        continue;

                    if (d.marchEntryT == 0 && d.shouldMarch)
                    {
                        result.activeStrongFieldMarchers[result.activeStrongFieldMarchCount++] = i;   
                    }
                }
                
                return result;
            }
            
            AABBHitInfo RayAABB(float3 rayOrigin, float3 rayDirection, float3 inverseDirection, float3 boxMin, float3 boxMax, float distanceToBeat)
            {
                float3 invDir = inverseDirection;
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

            bool BoxLiesWithinOrbitalPlane(OrbitalPlaneParameters orbitalPlaneParameters, float3 boxMin, float3 boxMax)
            {
                float3 boxCenter = (boxMin + boxMax) * 0.5;
                float3 boxHalfExtents = (boxMax - boxMin) * 0.5;
                float d = dot(orbitalPlaneParameters.localOrbitalPlaneNormal, boxCenter) - orbitalPlaneParameters.localPlaneD;
                float r = dot(abs(orbitalPlaneParameters.localOrbitalPlaneNormal), boxHalfExtents);
                return abs(d) <= r;
            }

            AABBHitInfo RayHitsBox(
                OrbitalPlaneParameters orbitalPlaneParameters,
                float3 rayOrigin,
                float3 rayDirection,
                float3 inverseDirection,
                float AABBLeftX,
                float AABBLeftY,
                float AABBLeftZ,
                float AABBRightX,
                float AABBRightY,
                float AABBRightZ,
                float distanceToBeat)
            {
                float3 boxMin = float3(AABBLeftX,  AABBLeftY,  AABBLeftZ);
                float3 boxMax = float3(AABBRightX, AABBRightY, AABBRightZ);

                if (!BoxLiesWithinOrbitalPlane(orbitalPlaneParameters, boxMin, boxMax))
                    return (AABBHitInfo)0;
                
                return RayAABB(rayOrigin, rayDirection, inverseDirection, boxMin, boxMax, distanceToBeat);
            }

            AABBHitInfo RayHitsBox(
                float3 rayOrigin,
                float3 rayDirection,
                float3 inverseDirection,
                float AABBLeftX,
                float AABBLeftY,
                float AABBLeftZ,
                float AABBRightX,
                float AABBRightY,
                float AABBRightZ,
                float distanceToBeat)
            {
                float3 boxMin = float3(AABBLeftX,  AABBLeftY,  AABBLeftZ);
                float3 boxMax = float3(AABBRightX, AABBRightY, AABBRightZ);
                return RayAABB(rayOrigin, rayDirection, inverseDirection, boxMin, boxMax, distanceToBeat);
            }

            HitInfo raySphere(Ray ray, float3 sphereCenter, float sphereRadius)
            {
				HitInfo hitInfo = (HitInfo)0;
				float3 offsetRayOrigin = ray.position - sphereCenter;

				float a = dot(ray.direction, ray.direction);
				float b = 2 * dot(offsetRayOrigin, ray.direction);
				float c = dot(offsetRayOrigin, offsetRayOrigin) - sphereRadius * sphereRadius;
				float discriminant = b * b - 4 * a * c;

				if (discriminant >=  0)
                {
					float dst = (-b - sqrt(discriminant)) / (2 * a);

					if (dst >= 0)
                    {
						hitInfo.didHit = true;
						hitInfo.distance = dst;
						hitInfo.hitPoint = ray.position + ray.direction * dst;
					}
				}

				return hitInfo;
            }

			HitInfo rayTriangle(Ray ray, Triangle tri)
			{
                float3 edgeAB = tri.edgeAB;
                float3 edgeAC = tri.edgeAC;
				float3 geometricNormal = cross(tri.edgeAB, tri.edgeAC);

				float determinant = -dot(ray.direction, geometricNormal);
                if (determinant <=  0)
                    return (HitInfo)0;

				float invDet = 1 / determinant;
                uint vertex1 = TriangleIndices[tri.baseIndex];
				float3 ao = ray.position - Vertices[vertex1];
				float dst = dot(ao, geometricNormal) * invDet;
                if (dst <  0)
                    return (HitInfo)0;

                float3 dao = cross(ao, ray.direction);
				float u = dot(edgeAC, dao) * invDet;
                if (u <  0)
                    return (HitInfo)0;

				float v = -dot(edgeAB, dao) * invDet;
                if (v < 0)
                    return (HitInfo)0;

                if (u + v > 1.0)
                    return (HitInfo)0;

				HitInfo hitInfo = (HitInfo)0;
				hitInfo.didHit = true;
				hitInfo.hitPoint = ray.position + ray.direction * dst;
				hitInfo.distance = dst;
                hitInfo.u = u;
                hitInfo.v = v;
				return hitInfo;
			}
            
            HitInfo checkSphereCollisions(Ray ray)
            {
                HitInfo closest = (HitInfo)0;
                closest.distance = 3.402823e+38;

                for (int i = 0; i < numSpheres; i++)
                {
                    Sphere sphere = Spheres[i];
                    HitInfo hit = raySphere(ray, sphere.position, sphere.radius);
                    if (hit.didHit && hit.distance < closest.distance)
                    {
                        closest = hit;
                        closest.objectType = 0;
                        closest.objectIndex = -1;
                    }
                }

                return closest;
            }

            HitInfo TraverseInstanceBLAS(Ray ray, uint instanceIndex, float worldTMax, bool findClosestCollisionOnly)
            {
                InstanceBLASTraversals++;

                HitInfo closest = (HitInfo)0;
                float bestWorldT = 3.402823e+38;

                Mesh mesh = Instances[instanceIndex];

                #ifdef ORBITAL_PLANE_TEST_POSSIBLE
                    OrbitalPlaneParameters orbitalPlaneParameters =
                        GetLocalOrbitalPlaneParameters(ray.position, BlackHoles[0].position, ray.direction, mesh.worldToLocalMatrix);
                #endif

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
                        orbitalPlaneParameters,
                        localRay.position, localRay.direction, inverseDirection, 
                        leftNode.AABBLeftX, leftNode.AABBLeftY, leftNode.AABBLeftZ,
                        leftNode.AABBRightX, leftNode.AABBRightY, leftNode.AABBRightZ,
                        bestLocalT);

                    AABBHitInfo rh = RayHitsBox(
                        orbitalPlaneParameters,
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

                #ifdef ORBITAL_PLANE_TEST_POSSIBLE
                    OrbitalPlaneParameters orbitalPlaneParameters =
                        GetWorldOrbitalPlaneParameters(ray.position, BlackHoles[0].position, ray.direction);
                #endif

                for (;;)
                {
                    BVHTests++;
                    TLASNodeVisits++;

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
                            TLASLeafRefsVisited++;

                            uint instanceIndex = TLASRefs[j];
                            HitInfo h = TraverseInstanceBLAS(ray, instanceIndex, min(worldTMax, bestWorldT), findClosestCollisionOnly);

                            if (h.didHit && h.distance < bestWorldT)
                            {
                                bestWorldT = h.distance;
                                closest = h;
                                if (findClosestCollisionOnly)
                                    return closest;
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

                    #ifdef ORBITAL_PLANE_TEST_POSSIBLE
                    AABBHitInfo lh = RayHitsBox(
                        orbitalPlaneParameters,
                        ray.position, ray.direction, inverseDirection, 
                        leftNode.AABBLeftX, leftNode.AABBLeftY, leftNode.AABBLeftZ,
                        leftNode.AABBRightX, leftNode.AABBRightY, leftNode.AABBRightZ,
                        bestWorldT);

                    AABBHitInfo rh = RayHitsBox(
                        orbitalPlaneParameters,
                        ray.position, ray.direction, inverseDirection, 
                        rightNode.AABBLeftX, rightNode.AABBLeftY, rightNode.AABBLeftZ,
                        rightNode.AABBRightX, rightNode.AABBRightY, rightNode.AABBRightZ,
                        bestWorldT);
                    #else
                    AABBHitInfo lh = RayHitsBox(
                        ray.position, ray.direction, inverseDirection, 
                        leftNode.AABBLeftX, leftNode.AABBLeftY, leftNode.AABBLeftZ,
                        leftNode.AABBRightX, leftNode.AABBRightY, leftNode.AABBRightZ,
                        bestWorldT);

                    AABBHitInfo rh = RayHitsBox(
                        ray.position, ray.direction, inverseDirection, 
                        rightNode.AABBLeftX, rightNode.AABBLeftY, rightNode.AABBLeftZ,
                        rightNode.AABBRightX, rightNode.AABBRightY, rightNode.AABBRightZ,
                        bestWorldT);
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

                    #ifdef ORBITAL_PLANE_TEST_POSSIBLE
                        OrbitalPlaneParameters orbitalPlaneParameters =
                            GetLocalOrbitalPlaneParameters(ray.position, BlackHoles[0].position, ray.direction, mesh.worldToLocalMatrix);
                    #endif

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
                                h.triIndex = j;
                                triTests++;
                                if (!h.didHit) continue;

                                float localT = h.distance;
                                if (localT > bestLocalT) continue;

                                float worldT = localT / dirScale;
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

                        BVHNode leftNode  = BVHNodes[node.left];
                        BVHNode rightNode = BVHNodes[node.right];

                        #ifdef ORBITAL_PLANE_TEST_POSSIBLE
                        AABBHitInfo lh = RayHitsBox(
                            orbitalPlaneParameters,
                            localRay.position, localRay.direction, inverseDirection, 
                            leftNode.AABBLeftX, leftNode.AABBLeftY, leftNode.AABBLeftZ,
                            leftNode.AABBRightX, leftNode.AABBRightY, leftNode.AABBRightZ,
                            bestLocalT);

                        AABBHitInfo rh = RayHitsBox(
                            orbitalPlaneParameters,
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
                }

                return closest;
                #endif
            }
           
            float3 ApplyFakeRelativisticToneShift(float3 color, float g, float strength)
            {
                float3 outColor = color;
                #ifdef USE_REDSHIFTING
                float3 redTint  = float3(1.00, 0.38, 0.12);
                float3 blueTint = float3(0.45, 0.68, 1.00);

                float tRed  = saturate((1.0 - g) * 2.0) * strength;
                float tBlue = saturate((g - 1.0) * 2.0) * strength;


                outColor = lerp(outColor, outColor * redTint,  tRed);
                outColor = lerp(outColor, outColor * blueTint, tBlue);
                #endif
                return outColor;
            }
            
            Sphere checkInsideSphere(float3 position)
            {
                for (int i = 0; i < numSpheres; i++)
                {
                    Sphere sphere = Spheres[i];
                    if (length(position - sphere.position) < sphere.radius / 2)
                        return sphere;
                }
                return (Sphere)0;
            }
            
            HitInfo queryCollisions(Ray ray, float tMax, bool findFirstCollisionOnly)
            {
                HitInfo closest = (HitInfo)0;
                closest.distance = 3.402823e+38;

                #ifdef TEST_SPHERE
                HitInfo s = checkSphereCollisions(ray);
                if (s.didHit && s.distance <= tMax && s.distance < closest.distance)
                {
                    closest = s;
                    if (findFirstCollisionOnly)
                        return s;
                }
                #endif

                #ifdef TEST_TRIANGLE
                HitInfo m = checkMeshCollisions(ray, tMax, findFirstCollisionOnly);
                if (m.didHit && m.distance <= tMax && m.distance < closest.distance)
                    closest = m;
                #endif

                return closest;
            }

            #include "AtmosphereicScattering.hlsl"

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
                uint index2 = TriangleIndices[tri.baseIndex + 1];
                uint index3 = TriangleIndices[tri.baseIndex + 2];

                float3 n1 = Normals[index1];
                float3 n2 = Normals[index2];
                float3 n3 = Normals[index3];

                float3 N = normalize(
                    n1 * (1 - (hitInfo.u + hitInfo.v)) +
                    n2 * hitInfo.u +
                    n3 * hitInfo.v
                );

                ray.lastHitAlbedo = material.color;
                ray.hasLastHit = true;

                Mesh mesh = Instances[hitInfo.objectIndex];
                float3x3 nMat = transpose((float3x3)mesh.worldToLocalMatrix);
                N = safeNormalize(mul(nMat, N));
                ray.lastHitNormal = N;

                //float3 geomNormalLocal = normalize(Triangles[hitInfo.triIndex].geometricNormal);
                float3 geomNormalLocal = normalize(cross(Triangles[hitInfo.triIndex].edgeAB, Triangles[hitInfo.triIndex].edgeAC));
                float3 Ng = safeNormalize(mul(nMat, geomNormalLocal));

                if (dot(Ng, ray.ray.direction) > 0)
                    Ng = -Ng;

                if (dot(N, Ng) < 0)
                    N = -N;

                if (bad3(ray.ray.direction) || bad3(ray.rayColor) || bad3(N) || bad3(Ng))
                {
                    ray.incomingLight = float3(1, 0, 1);
                    ray.rayEarlyKill = true;
                    return ray;
                }

                float3 baseColor = material.color;
                float metallic = saturate(material.metallicity);
                float roughness = saturate(material.roughness);

                float3 dielectricF0 = float3(0.04, 0.04, 0.04);
                float3 F0 = lerp(dielectricF0, baseColor, metallic);

                float3 V = -ray.ray.direction;
                float NdotV = saturate(dot(N, V));

                float3 F_pick = FresnelSchlick(NdotV, F0);
                float3 kd = (1.0 - F_pick) * (1.0 - metallic);
                float3 diffuseBRDF = kd * baseColor / PI;

                float specularWeight = saturate(luminance(F_pick));
                float diffuseWeight = max(luminance(diffuseBRDF), 1e-4);

                float totalWeight = specularWeight + diffuseWeight;
                if (totalWeight <= 1e-8)
                {
                    ray.rayEarlyKill = true;
                    return ray;
                }
                
                #ifdef APPLY_SUN_LIGHTING
                ray.incomingLight += evaluateDirectSunAtHit(hitInfo.hitPoint, N, Ng, V, material.color, material.metallicity, material.roughness, F0);
                #endif

                #ifdef APPLY_SCATTERING
                if (ray.numBounces == 1)
                {
                    ray.incomingLight += calculateLight(ray.ray.position, ray.ray.direction, hitInfo.distance, rngState, 1);
                }
                #endif

                float specularChance = clamp(specularWeight / totalWeight, 0.001, 0.999);
                float choose = randomValue(rngState);

                if (choose < specularChance)
                {
                    float2 xi = float2(randomValue(rngState), randomValue(rngState));
                    float3 H = sampleGGX_H(xi, roughness, N);
                    float3 L = reflect(-V, H);

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
                
                ray.ray.position = hitInfo.hitPoint + (Ng * 1e-4);

                float p = max(saturate(dot(ray.rayColor, float3(0.2126, 0.7152, 0.0722))), 1e-4);
                if (randomValue(rngState) > p)
                {
                    ray.rayEarlyKill = true;
                    return ray;
                }

                ray.rayColor /= p;
                return ray;
            }

            float estimateNearestCollidableObjectDistance(Ray ray)
            {
                float closestDistance = 3.402823e+38;

                for (int i = 0; i < numSpheres; i++)
                {
                    Sphere sphere = Spheres[i];
                    float distance = nearestPointOnSphere(ray.position, sphere.position, sphere.radius);
                    if (distance < closestDistance)
                        closestDistance = distance;
                }

                for (int j = 0; j < numBlackHoles; j++)
                {
                    BlackHole blackHole = BlackHoles[j];
                    float distance = nearestPointOnSphere(ray.position, blackHole.position, blackHole.SchwartzchildRadius);
                    if (distance < closestDistance)
                        closestDistance = distance;
                }

                return closestDistance;
            }
            
            float3 getAngularGrid(float3 rayDirection)
            {
                float3 dir = normalize(rayDirection);

                float phi = atan2(dir.z, dir.x);
                float theta = acos(clamp(dir.y,-1.0,1.0));

                float u = phi / (2.0 * PI) + 0.5;
                float v = theta / PI;

                float lonLines = 24.0;
                float latLines = 24.0;

                float2 grid = float2(u * lonLines, v * latLines);
                float2 cell = abs(frac(grid) - 0.5);

                float thickness = 0.01;
                float lon = smoothstep(thickness, 0.0, cell.x);
                float lat = smoothstep(thickness, 0.0, cell.y);
                float line1 = max(lon, lat);

                float3 color = float3(u, v, 1.0 - u);
                return color * line1;
            }

            float3 DebugRayBending(PixelMarcher before, PixelMarcher after, BlackHole blackHole)
            {
                float3 dirBefore = before.ray.direction;
                float3 dirAfter  = after.ray.direction;

                float cosAngle = clamp(dot(dirBefore, dirAfter), -1.0, 1.0);
                float bendAngleDeg = acos(cosAngle) * (180.0 / UNITY_PI);

                float rBefore = length(before.ray.position - blackHole.position);
                float rAfter  = length(after.ray.position  - blackHole.position);

                float rs = blackHole.SchwartzchildRadius;
                float marchShell = max(rs * blackHole.blackHoleSOIMultiplier, 4.0 * rs);

                float t = saturate(bendAngleDeg / 180.0);
                float3 color = lerp(float3(0,0,1), lerp(float3(0,1,0), float3(1,0,0), t*2), step(0.5, t));

                return color;
            }

            #include "BlackHoleMarch2D.hlsl"

            float3 trace(float3 viewPoint, inout uint rngState)
            {
                if (isNan(stepSize))
                    return float3(1,1,1);

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
                pixel_marcher.ray = RayToStore;

                while (pixel_marcher.numBounces < maxBounces)
                {
                    if (pixel_marcher.rayEarlyKill)
                        break;

                    #ifdef ENABLE_LENSING
                    if (numBlackHoles > 0)
                    {
                        pixel_marcher = marchAllBlackHoles(pixel_marcher, rngState);

                        if (pixel_marcher.hitBlackHole)
                        {
                            pixel_marcher.incomingLight = float3(0,0,0);
                            break;
                        }
                        if (pixel_marcher.rayEarlyKill || pixel_marcher.numBounces >= maxBounces)
                        {
                            break;
                        }
                    }
                    #endif

                    HitInfo objectHitInfo = queryCollisions(pixel_marcher.ray, 3.402823e+38, false);
                    if (objectHitInfo.didHit)
                    {
                        pixel_marcher = handleReflection(pixel_marcher, rngState, objectHitInfo);
                        continue;
                    }

                    float3 rayDir = pixel_marcher.ray.direction;
                    float atmExit = RaySphereExitDistance(
                        pixel_marcher.ray.position, rayDir, PlanetCenter(), atmosphereRadius);

                    if (atmExit > 0)
                    {
                        #ifdef APPLY_SCATTERING
                        if ((numRenderedFrames % framesPerScatter == 0 || numRenderedFrames == 0))
                        {
                            pixel_marcher.incomingLight += calculateLight(
                                pixel_marcher.ray.position, rayDir, atmExit, rngState, inScatteringPoints);
                        }
                        #endif
                    }
                    else
                    {
                        pixel_marcher.incomingLight += getAngularGrid(rayDir);
                    }

                    break;
                }

                if (bad3(pixel_marcher.incomingLight))
                    return float3(10000000, 0, 10000000);

                if (any(isNan(pixel_marcher.ray.energy)))
                    return float3(10000000, 0, 10000000);

                #ifdef DEBUG_ENABLE_ENERGY_VISUALIZATION
                float d = log2(1.0 / max(pixel_marcher.ray.energy, 1e-6));
                if (d > 0.0)
                    return float3(0.0, 0.0, saturate(d * 4.0));
                else
                    return float3(saturate(-d * 4.0), 0.0, 0.0);
                #endif

                #if defined(DEBUG_DISPLAY_BVH_NODES_VISITED) || \
                    defined(DEBUG_DISPLAY_TRIANGLE_TESTS) || \
                    defined(DEBUG_DISPLAY_TLAS_NODE_VISITS) || \
                    defined(DEBUG_DISPLAY_BLAS_NODE_VISITS) || \
                    defined(DEBUG_DISPLAY_INSTANCE_BLAS_TRAVERSALS) || \
                    defined(DEBUG_DISPLAY_TLAS_LEAF_REFS)
                    pixel_marcher.incomingLight = float3(0,0,0);
                #endif

                float bounceDenom = max(1.0, (float)pixel_marcher.numBounces);

                #ifdef DEBUG_DISPLAY_BVH_NODES_VISITED
                    pixel_marcher.incomingLight.z = (float)BVHTests / max(1.0, (float)BVHTestsSaturation * bounceDenom);
                #endif

                #ifdef DEBUG_DISPLAY_TRIANGLE_TESTS
                    pixel_marcher.incomingLight.r = (float)triTests / max(1.0, (float)triTestsSaturation * bounceDenom);
                #endif

                #ifdef DEBUG_DISPLAY_TLAS_NODE_VISITS
                    pixel_marcher.incomingLight.g = (float)TLASNodeVisits / max(1.0, (float)TLASNodeVisitsSaturation * bounceDenom);
                #endif

                #ifdef DEBUG_DISPLAY_BLAS_NODE_VISITS
                    pixel_marcher.incomingLight.b = (float)BLASNodeVisits / max(1.0, (float)BLASNodeVisitsSaturation * bounceDenom);
                #endif

                #ifdef DEBUG_DISPLAY_INSTANCE_BLAS_TRAVERSALS
                    pixel_marcher.incomingLight.r = (float)InstanceBLASTraversals / max(1.0, (float)InstanceBLASTraversalsSaturation * bounceDenom);
                #endif

                #ifdef DEBUG_DISPLAY_TLAS_LEAF_REFS
                    pixel_marcher.incomingLight.g = (float)TLASLeafRefsVisited / max(1.0, (float)TLASLeafRefsVisitedSaturation * bounceDenom);
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

                for (int x = 0; x < RaysPerPixel; x++)
                {
                    uint sampleIndex = (uint)numRenderedFrames * (uint)RaysPerPixel + (uint)x + 1u;
                    float2 h = halton2(sampleIndex);

                    uint hash = pixelIndex * 1664525u + 1013904223u;
                    float2 rot = float2((hash & 0xFFFFu) / 65536.0, (hash >> 16) / 65536.0);
                    h = frac(h + rot);

                    float2 jitterUV = (h - 0.5) / (float2)_ScreenParams.xy;
                    float3 vpLocal = float3((i.uv + jitterUV) - 0.5, 1) * ViewParams;
                    float3 vp = mul(CameraLocalToWorldMatrix, float4(vpLocal, 1)).xyz;
                    
                    float3 color = trace(vp, rngState);
                    float luma = dot(color, float3(0.2126, 0.7152, 0.0722));
                    float maxLuma = 10.0;

                    if (luma > maxLuma)
                        color *= maxLuma / luma;

                    totalIncomingLight += color;
                }

                float3 pixelCol = totalIncomingLight / RaysPerPixel;
                if (any(isinf(pixelCol)))
                    return float4(10000000,0,10000000,1);

                return float4(pixelCol, 1);
            }

            ENDHLSL
        }
    }
}