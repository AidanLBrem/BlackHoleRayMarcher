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
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0
            #pragma enable_d3d11_debug_symbols
            #pragma shader_feature_local TEST_SPHERE
            #pragma shader_feature_local TEST_TRIANGLE
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
                float smoothness;
			};

            struct HitInfo 
            {
                bool didHit;
                float distance;
                float3 hitPoint;
                float3 normal;
                RayTracingMaterial material;
                int objectType;
                int objectIndex;
                //bool blackHoleOverride;
            };

            struct AABBHitInfo {
                bool didHit;
                float distance;
            };

            struct Ray
            {
                float3 position;
                float3 direction;
                float3 inverseDirection;   
                bool hitBlackHole;
                bool debugEarlyBreakout;
                //bool debugNaN;
                //bool debugCollision;
                //bool debugInsideSphere;
                //bool debugWasInsideBlackHole;
                //bool marchingMode;
                //int debugSOISteps;
                //float numCollisions;
                float3 incomingLight;
                float3 rayColor;
                bool rayEarlyKill;
                int numBounces;
                int rayTriTests;    
            };
            struct Sphere
			{
				float3 position;
				float radius;
				RayTracingMaterial material;
			};

            /*struct Triangle {
                int vertexIndex1;
                int vertexIndex2;
                int vertexIndex3;
                int normalIndex1;
                int normalIndex2;
                int normalIndex3;
                float3 edgeAB;
                float3 edgeAC;
            };*/

            struct Triangle {
                float3 vertex1;
                int normalIndex1;
                int normalIndex2;
                int normalIndex3;
                float3 edgeAB;
                float3 edgeAC;
            };


            struct Mesh {
                int indexOffset;
                int triangleCount;
                RayTracingMaterial material;
                float AABBLeftX;
                float AABBLeftY;
                float AABBLeftZ;
                float AABBRightX;
                float AABBRightY;
                float AABBRightZ;
                int firstBVHNodeIndex;
                int largestAxis;
                float4x4 localToWorldMatrix;
                float4x4 worldToLocalMatrix;
            };
            
            struct BVHNode {
                int left;
                int right;
                int firstTriangleIndex;
                int triangleCount;

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
                float radius;
                float blackHoleSOIMultiplier;
                float blackHoleMass;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };
            #define debug_steps 100
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
            StructuredBuffer<Mesh> Meshes;
            StructuredBuffer<float3> Vertices;
            StructuredBuffer<float3> Normals;
            StructuredBuffer<Triangle> Triangles;
            StructuredBuffer<BVHNode> BVHNodes;
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
            float randomValue(inout uint state) {
                state *= state * 747796405 + 2891336453;
                uint result = ((state >> ((state >> 28) + 4)) ^ state) * 277803737;

                result = (result >> 22) ^ result;
                return result / 4294967295.0;
            }

            float randomValueNormalDistribution(inout uint state) {
                float theta = 2 * 3.1415926 * randomValue(state);
                float rho = sqrt(-2 * log(max(randomValue(state), 1e-6)));
                return rho * cos(theta);
            }

            float3 randomDirection(inout uint state) {
                float x = randomValueNormalDistribution(state);
                float y = randomValueNormalDistribution(state);
                float z = randomValueNormalDistribution(state);
                return normalize(float3(x,y,z));
            }
            
            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex.xyz);
                o.uv = v.uv;
                return o;
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
				if (discriminant >= 0) {
					// Distance to nearest intersection point (from quadratic formula)
					float dst = (-b - sqrt(discriminant)) / (2 * a);

					// Ignore intersections that occur behind the ray
					if (dst >= 0) {
						hitInfo.didHit = true;
						hitInfo.distance = dst;
						hitInfo.hitPoint = ray.position + ray.direction * dst;
						hitInfo.normal = normalize(hitInfo.hitPoint - sphereCenter);
					}
				}
				return hitInfo;
            }

			HitInfo rayTriangle(Ray ray, Triangle tri)
			{
				float3 ao = ray.position - tri.vertex1;
				//float3 ao = ray.position - Vertices[tri.vertexIndex1];
				float3 normalVector = cross(tri.edgeAB, tri.edgeAC);

				float determinant = -dot(ray.direction, normalVector);
                if (abs(determinant) <= 1e-6) {
                    return (HitInfo)0;
                }
				float invDet = 1 / determinant;
				
				// Calculate dst to triangle & barycentric coordinates of intersection point
                float3 dao = cross(ao, ray.direction);
				float dst = dot(ao, normalVector) * invDet;
                if (dst < 0.0) {
                    return (HitInfo)0;
                }
				float u = dot(tri.edgeAC, dao) * invDet;
                if (u < 0) {
                    return (HitInfo)0;
                }
				float v = -dot(tri.edgeAB, dao) * invDet;
                if (v < 0) {
                    return (HitInfo)0;
                }
				float w = 1 - u - v;
                if (w < 0) {
                    return (HitInfo)0;
                }
				// Initialize hit info
				HitInfo hitInfo = (HitInfo)0;
				hitInfo.didHit = determinant >= 1E-6 && dst >= 0 && u >= 0 && v >= 0 && w >= 0;
				hitInfo.hitPoint = ray.position + ray.direction * dst;
				hitInfo.normal = normalize(Normals[tri.normalIndex1] * w + Normals[tri.normalIndex2] * u + Normals[tri.normalIndex3] * v);
				hitInfo.distance = dst;
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
                        closest.material = sphere.material;
                        closest.objectType = 0;
                        closest.objectIndex = -1;
                    }
                }

                return closest;
            }

                HitInfo checkMeshCollisions(Ray ray, float worldTMax)
                {
                    HitInfo closest = (HitInfo)0;
                    float bestWorldT = 3.402823e+38;

                    // clamp max distance we’ll accept for meshes
                    float worldLimit = min(worldTMax, bestWorldT);

                    for (int i = 0; i < numMeshes; i++)
                    {
                        Mesh mesh = Meshes[i];

                        // Build local ray
                        Ray localRay = ray;
                        localRay.position = mul(mesh.worldToLocalMatrix, float4(ray.position, 1)).xyz;

                        float3 localDirUn = mul((float3x3)mesh.worldToLocalMatrix, ray.direction);
                        float  dirScale   = length(localDirUn);
                        if (dirScale < 1e-8) continue;

                        localRay.direction       = localDirUn / dirScale;
                        localRay.inverseDirection = 1.0 / localRay.direction;

                        // Convert world max distance to local max distance
                        float localTMax  = worldLimit * dirScale;
                        float bestLocalT = bestWorldT * dirScale;

                        // Stack traversal (local-t!)
                        uint  stack[128];
                        float stackT[128];
                        int sp = 0;

                        uint nodeIdx = mesh.firstBVHNodeIndex;
                        float currentTNear = -1.0;

                        for (;;)
                        {
                            // prune against bestLocalT/localTMax
                            float tBeat = min(localTMax, bestLocalT);

                            if (currentTNear > tBeat)
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
                                for (int j = node.firstTriangleIndex; j < node.firstTriangleIndex + node.triangleCount; j++)
                                {
                                    Triangle tri = Triangles[j];
                                    HitInfo h = rayTriangle(localRay, tri);     // h.distance is LOCAL t
                                    triTests++;
                                    if (!h.didHit) continue;

                                    float localT = h.distance;
                                    if (localT < 0 || localT > tBeat) continue;
                                    if (localT >= bestLocalT) continue;

                                    // Convert to world t
                                    float worldT = localT / dirScale;
                                    if (worldT < 0 || worldT >= bestWorldT) continue;

                                    // Compute world hitpoint consistently
                                    float3 hitWorld = ray.position + ray.direction * worldT;

                                    // World normal (inverse-transpose)
                                    float3x3 nMat = transpose((float3x3)mesh.worldToLocalMatrix);
                                    float3 nWorld = normalize(mul(nMat, h.normal));

                                    bestWorldT = worldT;
                                    bestLocalT = localT;

                                    closest = h;
                                    closest.hitPoint = hitWorld;
                                    closest.normal   = nWorld;
                                    closest.distance = bestWorldT;
                                    closest.material = mesh.material;
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

                            float tBeat2 = min(localTMax, bestLocalT);

                            AABBHitInfo lh = RayAABB(localRay.position, localRay.direction, localRay.inverseDirection,
                                                     leftNode.AABBLeftX, leftNode.AABBLeftY, leftNode.AABBLeftZ,
                                                     leftNode.AABBRightX, leftNode.AABBRightY, leftNode.AABBRightZ,
                                                     tBeat2);
    
                            AABBHitInfo rh = RayAABB(localRay.position, localRay.direction, localRay.inverseDirection,
                                                     rightNode.AABBLeftX, rightNode.AABBLeftY, rightNode.AABBLeftZ,
                                                     rightNode.AABBRightX, rightNode.AABBRightY, rightNode.AABBRightZ,
                                                     tBeat2);
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

                            uint nearIdx  = leftFirst ? node.left  : node.right;
                            float nearT   = leftFirst ? lh.distance : rh.distance;
                            uint farIdx   = leftFirst ? node.right : node.left;
                            float farT    = leftFirst ? rh.distance : lh.distance;
                            bool farHit   = leftFirst ? rh.didHit : lh.didHit;

                            if (farHit && farT <= tBeat2 && sp < 64)
                            {
                                stack[sp] = farIdx;
                                stackT[sp] = farT;
                                sp++;
                            }

                            nodeIdx = nearIdx;
                            currentTNear = nearT;
                        }

                        // tighten for next meshes
                        worldLimit = min(worldTMax, bestWorldT);
                    }

                    return closest;
                }

            HitInfo checkBlackHoleSOICollisions(Ray ray) {
                HitInfo closest = (HitInfo)0;
                closest.distance = 3.402823e+38;

                for (int i = 0; i < numBlackHoles; i++) {
                    BlackHole blackHole = BlackHoles[i];
                    HitInfo hit = raySphere(ray, blackHole.position, blackHole.radius * blackHole.blackHoleSOIMultiplier);
                    if (hit.didHit && hit.distance < closest.distance) {
                        closest = hit;
                        closest.objectType = 1;
                        closest.objectIndex = i;
                    }

                }

                return closest;
            }
            Ray handleReflection(Ray ray, inout uint rngState, HitInfo hitInfo) {
                ray.numBounces++;
                RayTracingMaterial material = hitInfo.material;
                float3 diffuseDir = normalize(hitInfo.normal + randomDirection(rngState));
                float3 specularDir = reflect(ray.direction, hitInfo.normal);
                ray.direction = normalize(lerp(diffuseDir, specularDir, material.smoothness));
                ray.inverseDirection = 1.0 / ray.direction;
                ray.position = hitInfo.hitPoint + hitInfo.normal * 1e-4;  // tune for your scene scale
                float3 emittedLight = material.emissiveColor * material.emissionStrength;
                ray.incomingLight += emittedLight * ray.rayColor;
                ray.rayColor *= material.color;
                float p = max(ray.rayColor.r, max(ray.rayColor.g, ray.rayColor.b));
                if (material.emissionStrength > 0) {
                    ray.rayEarlyKill = true;
                }
                if (randomValue(rngState) >= p) {
                    ray.rayEarlyKill = true;
                }
                ray.rayColor *= 1.0 / p;
                return ray;
            }
            

            int blackHoleOverrideCheck(Ray ray) {
                for (int i = 0; i < numBlackHoles; i++) {
                    BlackHole blackHole = BlackHoles[i];
                    float3 d = ray.position - blackHole.position;
                    float rr = blackHole.radius * blackHole.blackHoleSOIMultiplier;
                    if (dot(d,d) < rr * rr) {
                        return i;
                    }
                }
                return -1;
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

            float distanceTo(float3 position, float3 position2) {
                return length(position - position2);
            }

            float3 rayTo(float3 position, float3 position2) {
                return (position - position2);
            }

            float blackHoleAcceleration(float3 position, BlackHole blackHole) {
                float3 d = blackHole.position - position;
                float r2 = max(dot(d,d), 1e-12);
                return blackHole.radius / r2;
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
            float pointTriangleDistance(float3 p, Triangle tri)
            {
                    float3 n = normalize(cross(tri.edgeAB, tri.edgeAC));
                    return abs(dot(p - tri.vertex1, n));
            }

            float nearestAlongRayAABB_BVH(Ray ray, float bestInit) {
                float closestDistance = bestInit;
                for (int i = 0; i < numMeshes; i++) {
                    Mesh mesh = Meshes[i];
                    BVHNode root = BVHNodes[mesh.firstBVHNodeIndex];
                    Ray localRay = ray;
                    localRay.position = mul(mesh.worldToLocalMatrix, float4(ray.position, 1)).xyz;
                    float distanceToRootFace = aabbUnsignedDistance(localRay.position, float3(root.AABBLeftX, root.AABBLeftY, root.AABBLeftZ), float3(root.AABBRightX, root.AABBRightY, root.AABBRightZ));
                    if (distanceToRootFace > 0) {
                        closestDistance = min(closestDistance, distanceToRootFace);
                        if (closestDistance < 0.25) {
                            return 0.25;
                        }
                    }

                    else {
                        return 0.25;
                    }

                }
                return closestDistance;
            }

            float nearestPointOnSphere(float3 p, float3 sphereCenter, float sphereRadius) {
                float distanceToCenter = length(p - sphereCenter);
                return max(0.0, distanceToCenter - sphereRadius);

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
                    float distance = nearestPointOnSphere(ray.position, blackHole.position, blackHole.radius);
                    if (distance < closestDistance) {
                        closestDistance = distance;
                    }
                }
                float meshDistance = nearestAlongRayAABB_BVH(ray, closestDistance);
                if (meshDistance < closestDistance) {
                    closestDistance = meshDistance;
                }
                return closestDistance;
            }


            Ray blackHoleRK4Check(Ray ray, BlackHole blackHole, inout uint rngState, float estimatedDistance, float3 directionToBlackHole, inout float chordLen, inout float3 dirEnd) {
                    
                    float k = estimatedDistance * stepSize;
                    k = max(k, 0.25);
                    //estimateNearestCollidableObjectDistance(ray);
                    //ray.debugSOISteps++;
                    //RK4
                    float3 pos0 = ray.position;
                    float3 dir0 = ray.direction;

                    //k1
                    float3 aV1 = blackHoleAcceleration(pos0, blackHole) * directionToBlackHole;

                    //k2
                    float3 dir1 = normalize(dir0 + aV1 * (0.5 * k));
                    float3 pos1 = pos0 + dir0 * (0.5 * k);
                    float3 aV2 = blackHoleAcceleration(pos1, blackHole) * normalize(rayTo(blackHole.position, pos1));

                    //k3
                    float3 dir2 = normalize(dir0 + aV2 * (0.5 * k));
                    float3 pos2 = pos0 + dir1 * (0.5 * k);
                    float3 aV3 = blackHoleAcceleration(pos2, blackHole) * normalize(rayTo(blackHole.position, pos2));
                    
                    //k4
                    float3 dir3 = normalize(dir0 + aV3 * k);
                    float3 pos3 = pos0 + dir2 * k;
                    float3 aV4 = blackHoleAcceleration(pos3, blackHole) * normalize(rayTo(blackHole.position, pos3));

                    //combine
                    float3 posEnd = pos0 + (k / 6.0) * (dir0 + 2.0 * dir1 + 2.0 * dir2 + dir3);
                    dirEnd = normalize(dir0 + (k / 6.0) * (aV1 + 2.0 * aV2 + 2.0 * aV3 + aV4));

                    float3 chord = posEnd - pos0;
                    chordLen = length(chord);
                    float3 chordDir = chordLen > 0 ? chord / chordLen : dir0;

                    // Query along the chord (no mutations)
                    ray.position  = pos0;
                    ray.direction = chordDir;
                    ray.inverseDirection = 1.0 / chordDir;
                    return ray;
            }

            Ray marchInsideBlackHoleSOI(Ray ray, BlackHole blackHole, inout uint rngState) {
                //ray.marchingMode = true;
                int emergencyBreak = 0;
                float3 rayToBlackHole = blackHole.position - ray.position;
                float distanceToBlackHole = length(rayToBlackHole);
                float3 directionToBlackHole = normalize(rayToBlackHole);
                while (distanceToBlackHole <= blackHole.radius * blackHole.blackHoleSOIMultiplier) {
                    if (distanceToBlackHole < blackHole.radius) {
                        ray.hitBlackHole = true;
                        return ray;
                    }
                    float chordLen = 0;
                    float3 dirEnd = 0;
                    float estimatedDistance = estimateNearestCollidableObjectDistance(ray);
                    //float estimatedDistance = nearestPointOnSphere(ray.position, blackHole.position, blackHole.radius);
                    ray = blackHoleRK4Check(ray, blackHole, rngState, estimatedDistance, directionToBlackHole, chordLen, dirEnd);
                    HitInfo h = (HitInfo)0;
                    //if (chordLen * 1.01 > estimatedDistance) {
                        h = queryCollisions(ray, chordLen);
                    //}
                    if (ray.debugEarlyBreakout) {
                        return ray;
                    }
                    emergencyBreak++;

                    if (emergencyBreak > emergencyBreakMaxSteps) {
                        ray.debugEarlyBreakout = true;
                        return ray;
                    }
                    if (h.didHit) {
                        ray = handleReflection(ray, rngState, h);
                        if (ray.rayEarlyKill) {
                            return ray;
                        }
                    }

                    // No hit: advance integrator
                    else {
                        ray.position = ray.position + ray.direction * chordLen;
                        ray.direction = dirEnd;
                        ray.inverseDirection = 1.0 / dirEnd;
                    }

                    /*if (checkInsideSphere(ray.position).radius > 0) {
                        ray.debugInsideSphere = true;
                        return;
                    }*/

                    rayToBlackHole = blackHole.position - ray.position;
                    distanceToBlackHole = length(rayToBlackHole);
                    directionToBlackHole = normalize(rayToBlackHole);
                }
                return ray;
            }

            // Simple float3 -> float hash
            float hash(float3 p)
            {
                return frac(sin(dot(p, float3(12.9898, 78.233, 45.164))) * 43758.5453123);
            }

            // SmoothStep implementation (to avoid relying on intrinsic availability)
            float SmoothStep(float edge0, float edge1, float x)
            {
                float t = saturate((x - edge0) / max(edge1 - edge0, 1e-5));
                return t * t * (3.0 - 2.0 * t);
            }

            float3 getStarField(float3 rayDirection)
            {
                float3 normalizedDir = normalize(rayDirection);

                // Create multiple layers of stars at different scales
                float star1 = hash(floor(normalizedDir * 100.0));
                float star2 = hash(floor(normalizedDir * 200.0));
                float star3 = hash(floor(normalizedDir * 500.0));

                // Different star densities and brightnesses
                float bigStars    = SmoothStep(0.997, 0.999, star1) * 1.0;
                float mediumStars = SmoothStep(0.993, 0.997, star2) * 0.8;
                float smallStars  = SmoothStep(0.990, 0.995, star3) * 0.6;

                float totalStars = bigStars + mediumStars + smallStars;

                // Enhanced color variation with multiple hash values
                float3 starColor = float3(1.0, 1.0, 1.0);
                if (totalStars > 0.0)
                {
                    // Use different seeds for color components with more spread
                    float colorSeed1 = hash(normalizedDir * 123.456);
                    float colorSeed2 = hash(normalizedDir * 789.012);
                    float colorSeed3 = hash(normalizedDir * 345.678);

                    // Start with white
                    starColor = float3(1.0, 1.0, 1.0);

                    // Apply distinct star type colors
                    if (colorSeed1 > 0.9)
                    {
                        starColor = float3(1.0, 0.4, 0.2); // Red giants
                    }
                    else if (colorSeed1 > 0.8)
                    {
                        starColor = float3(1.0, 0.6, 0.3); // Orange stars
                    }
                    else if (colorSeed1 > 0.7)
                    {
                        starColor = float3(1.0, 0.9, 0.7); // Yellow stars
                    }
                    else if (colorSeed1 > 0.6)
                    {
                        starColor = float3(0.9, 0.95, 1.0); // White stars
                    }
                    else if (colorSeed1 > 0.5)
                    {
                        starColor = float3(0.7, 0.8, 1.0); // Blue-white stars
                    }
                    else if (colorSeed1 > 0.4)
                    {
                        starColor = float3(0.5, 0.7, 1.0); // Blue stars
                    }
                    else
                    {
                        // Subtle variation to remaining white stars
                        float redVar   = 0.9 + 0.2 * colorSeed2;
                        float greenVar = 0.9 + 0.2 * colorSeed3;
                        float blueVar  = 0.9 + 0.2 * hash(normalizedDir * 456.789);
                        starColor = float3(redVar, greenVar, blueVar);
                    }
                }

                return starColor * totalStars;
            }
            float3 trace(float3 viewPoint, inout uint rngState) {
                if (isnan(stepSize)) {
                    return float3(1,1,1);
                }
                Ray ray;
                ray.position = viewPoint;
                ray.direction = normalize(ray.position - CameraWorldPos);
                ray.inverseDirection = 1.0 / ray.direction;
                ray.hitBlackHole = false;
                ray.debugEarlyBreakout = false;
                ray.rayColor = 1;
                ray.incomingLight = 0;
                ray.rayEarlyKill  = false;
                ray.numBounces = 0;
                ray.rayTriTests = 0;
                int maxIterations = 1000;
                int iterations = 0;
                //ray.debugSOISteps = 0;
                //ray.maxNumTriangleTests = 0;
                while (ray.numBounces < maxBounces && iterations < maxIterations) {
                    /*if (checkInsideSphere(ray.position).radius > 0) {
                        return float3(0,0,1);
                    }*/
                    HitInfo hitInfo = (HitInfo)0;
                    hitInfo.objectType = -1;
                    int blackHoleOverrideIndex = blackHoleOverrideCheck(ray);  
                    if (blackHoleOverrideIndex == -1) {
                        HitInfo objectHitInfo = queryCollisions(ray, 100000);
                        HitInfo blackHoleHitInfo = checkBlackHoleSOICollisions(ray);

                        if (!objectHitInfo.didHit && !blackHoleHitInfo.didHit) { //no hit
                            //incomingLight += getStarField(ray.direction);
                            //incomingLight += float3(0,0,1);
                            ray.incomingLight += float3(0.02,0.02,0.02);
                            break;
                        }
                        if (!objectHitInfo.didHit) { //black hole hit but sphere did not
                            hitInfo = blackHoleHitInfo;
                        } else { //both hit, choose the closest
                            if (objectHitInfo.distance < blackHoleHitInfo.distance) {
                                hitInfo = objectHitInfo;
                            } else {
                                hitInfo = blackHoleHitInfo;
                            }
                        }
                    }            
                    if (hitInfo.objectType == 1 || blackHoleOverrideIndex != -1) {
                        int blackHoleIndex = blackHoleOverrideIndex == -1 ? hitInfo.objectIndex : blackHoleOverrideIndex;
                        //ray.debugWasInsideBlackHole = true;
                        if (blackHoleOverrideIndex == -1) {
                            ray.position = hitInfo.hitPoint + ray.direction * 1e-3;
                        }
                        
                        //float3 rayEntryDirection = ray.direction;
                        ray = marchInsideBlackHoleSOI(ray, BlackHoles[blackHoleIndex], rngState);
                        //ray.marchingMode = false;
                        //float3 exitDirection = ray.direction;
                        //float angleDif = acos(clamp(dot(rayEntryDirection, exitDirection), -1.0, 1.0));
                        //float normalizedAngleDif = clamp(angleDif / 3.14151926, 0.0, 1.0);
                        if (ray.hitBlackHole) {
                            return float3(0,0,0);
                        }
                        if (ray.debugEarlyBreakout) {
                            return ray.incomingLight;
                        }
                        //if (ray.debugCollision) {
                        //    return float3(1,0,0);
                        //}
                        //if (ray.debugInsideSphere) {
                        //    return float3(0,0,1);
                        //}
                        //return float3(normalizedAngleDif, 0, 0);
                    }

                    else if (hitInfo.objectType == 0 || hitInfo.objectType == 2) {
                        //return float3(1,0,1);
                        ray = handleReflection(ray, rngState, hitInfo);
                        /*if (checkInsideSphere(ray.position).radius > 0) {
                            return float3(0,0,1);
                        }*/

                    }
                    if (ray.rayEarlyKill) {
                        //return float3(BVHTests / 100, BVHTests / 100, BVHTests / 100);
                        //return float3(triTests / 1000, triTests / 1000, triTests / 1000);
                        return ray.incomingLight;
                    }

                    
                }    
                //return float3(BVHTests / 100, BVHTests / 100, BVHTests / 100);
                //return float3(triTests / 1000, triTests / 1000, triTests / 1000);
                return ray.incomingLight;
            }



            float4 frag (v2f i) : SV_Target
            {
                float4 oldRender = tex2D(_MainTexOld, i.uv);
                
                float marchStart = CameraNear;
                float marchEnd = CameraFar;
                uint2 numPixels = _ScreenParams.xy;
                uint2 pixelCoord = i.uv * numPixels;
                uint pixelIndex = pixelCoord.y * numPixels.x + pixelCoord.x;


                float3 totalIncomingLight = 0;
                float3 total = 0;
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
                    
                    totalIncomingLight += trace(vp, rngState);
                }
                float3 pixelCol = totalIncomingLight / RaysPerPixel;
                if (any(isnan(pixelCol))) {
                    return float4(1,0,0,1);
                }
                return float4(pixelCol, 1);
            }
            ENDHLSL
        }
    }
}
