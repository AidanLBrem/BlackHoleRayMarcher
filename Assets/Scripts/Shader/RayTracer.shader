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
            #pragma shader_feature_local USE_LUT
            #pragma shader_feature_local ENABLE_LENSING
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
                //float3 shadingNormal; store u,v and only recompute shading normal when needed
                float u;
                float v;
                int triIndex;
                float3 geometricNormal;
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

            struct Triangle {
                int vertex1; //the position of V1 in the vertex array. Normals and Vertex arrays are indexed the same, so v2/n2 is at vertex1+1 and v3/n3 is at vertex+2 in the corresponding arrays
                int vertex2;
                int vertex3;
                
                float3 edgeAB;
                float3 edgeAC;
                float3 geometricNormal;
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
            #define PI 3.1415629
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
            float _BHLUT_RMinOverRs;
            float _BHLUT_RMaxOverRs;
            float _BHLUT_LogEpsilonOverRs;
            float _BHLUT_MuResolution;
            sampler2D _BlackHoleBendLUT;
            float bendStrength = 1.27; //trying to figure out why our estimate is 27% weaker than it should be, for now mess with multiplier
            
            float RadiusToLUT_U(float r, float rs, float rMinOverRs, float rMaxOverRs, float logEpsilonOverRs)
            {
                float rMin = rMinOverRs * rs;
                float rMax = rMaxOverRs * rs;
                float eps  = logEpsilonOverRs * rs;

                float a = log((rMin - rs) + eps);
                float b = log((rMax - rs) + eps);

                float u = (log((r - rs) + eps) - a) / (b - a);
                return saturate(u);
            }
            
            float MuToLUT_V(float mu, int muResolution)
            {
                // Inverse of (y + 0.5) / muResolution — map abs(mu) into cell-centre space
                float absMu = saturate(abs(mu));
                return (absMu * muResolution - 0.5) / muResolution;
            }
            
            float ComputeMu(float3 rayPos, float3 rayDir, float3 bhPos)
            {
                float3 radialOut = normalize(rayPos - bhPos);
                return dot(normalize(rayDir), radialOut);
            }
            
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
						hitInfo.geometricNormal = normalize(hitInfo.hitPoint - sphereCenter);
					}
				}
				return hitInfo;
            }

			HitInfo rayTriangle(Ray ray, Triangle tri)
			{
				//float3 ao = ray.position - Vertices[tri.vertexIndex1];
                //float3 edgeAB = v1 - v0;
                //float3 edgeAC = v2 - v0;
                float3 edgeAB = tri.edgeAB;
                float3 edgeAC = tri.edgeAC;
				float3 geometricNormal = tri.geometricNormal;

				float determinant = -dot(ray.direction, geometricNormal);
                if (abs(determinant) <= 1e-6) {
                    return (HitInfo)0;
                }
				float invDet = 1 / determinant;
				// Calculate dst to triangle & barycentric coordinates of intersection point
				float3 ao = ray.position - Vertices[tri.vertex1];
				float dst = dot(ao, geometricNormal) * invDet;
                if (dst < 1e-6) {
                    return (HitInfo)0;
                }
                float3 dao = cross(ao, ray.direction);
				float u = dot(edgeAC, dao) * invDet;
                if (u < 0) {
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
                
                hitInfo.geometricNormal = geometricNormal;
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
                                    h.triIndex = j;
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
                                    
                                    float3 nGeometricNormal = h.geometricNormal;

                                    bestWorldT = worldT;
                                    bestLocalT = localT;

                                    closest = h;
                                    closest.hitPoint = hitWorld;
                                    closest.geometricNormal   = nGeometricNormal;
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
                    HitInfo hit = raySphere(ray, blackHole.position, blackHole.SchwartzchildRadius * blackHole.blackHoleSOIMultiplier);
                    if (hit.didHit && hit.distance < closest.distance) {
                        closest = hit;
                        closest.objectType = 1;
                        closest.objectIndex = i;
                    }

                }

                return closest;
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

            // GGX / Trowbridge-Reitz half-vector sampling
            float3 sampleGGX_H(float2 xi, float roughness, float3 N)
            {
                // Clamp to avoid singular behavior at 0
                float a = max(0.001, roughness * roughness);
                float a2 = a * a;

                float phi = 2.0 * PI * xi.x;
                float cosTheta = sqrt((1.0 - xi.y) / (1.0 + (a2 - 1.0) * xi.y));
                float sinTheta = sqrt(max(0.0, 1.0 - cosTheta * cosTheta));

                float3 Hlocal = float3(
                    sinTheta * cos(phi),
                    sinTheta * sin(phi),
                    cosTheta
                );

                return toWorld(Hlocal, N);
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
                return F0 + (1.0 - F0) * pow(1.0 - cosTheta, 5.0);
            }

            float luminance(float3 c)
            {
                return dot(c, float3(0.2126, 0.7152, 0.0722));
            }

            Ray handleReflection(Ray ray, inout uint rngState, HitInfo hitInfo)
            {
                ray.numBounces++;

                RayTracingMaterial material = Meshes[hitInfo.objectIndex].material;
                int triIndex = hitInfo.triIndex;
                float3 N = normalize(
                    Normals[Triangles[triIndex].vertex1] * (1 - (hitInfo.u + hitInfo.v)) +
                    Normals[Triangles[triIndex].vertex2] * hitInfo.u +
                    Normals[Triangles[triIndex].vertex3] * hitInfo.v
                );
                Mesh mesh = Meshes[hitInfo.objectIndex];
                float3x3 nMat = transpose((float3x3)mesh.worldToLocalMatrix);   
                N = safeNormalize(mul(nMat, N));
                float3 geomNormalLocal = normalize(hitInfo.geometricNormal);
                float3 Ng = safeNormalize(mul(nMat, geomNormalLocal));
                if (dot(Ng, ray.direction) > 0.0)
                    Ng = -Ng;
                if (dot(N, Ng) < 0.0)
                {
                    N = -N;
                }

                float3 emittedLight = material.emissiveColor * material.emissionStrength;
                ray.incomingLight += emittedLight * ray.rayColor;

                if (material.emissionStrength > 0.0)
                {
                    ray.rayEarlyKill = true;
                    return ray;
                }

                // Standard metallic workflow
                float3 baseColor = material.color;
                float metallic = saturate(material.metallicity);
                float roughness = saturate(material.roughness);

                float3 dielectricF0 = float3(0.04, 0.04, 0.04);
                float3 F0 = lerp(dielectricF0, baseColor, metallic);

                float3 diffuseColor = baseColor * (1.0 - metallic);

                // Branch probability: use energy estimate, not raw max(F)
                float specularWeight = saturate(luminance(F0));
                float diffuseWeight  = luminance(diffuseColor);

                float totalWeight = specularWeight + diffuseWeight;
                if (totalWeight < 1e-6)
                {
                    ray.rayEarlyKill = true;
                    return ray;
                }

                float specularChance = specularWeight / totalWeight;
                specularChance = clamp(specularChance, 0.001, 0.999);

                float choose = randomValue(rngState);

                if (choose < specularChance)
                {
                    float3 V = safeNormalize(-ray.direction);
                    // ----- SPECULAR BRANCH: GGX -----
                    float2 xi = float2(randomValue(rngState), randomValue(rngState));
                    float3 H = sampleGGX_H(xi, roughness, N);
                    float3 L = reflect(-V, H);
                    L = safeNormalize(L);

                    float NdotL = saturate(dot(N, L));
                    float NdotV = saturate(dot(N, V));
                    float NdotH = saturate(dot(N, H));
                    float VdotH = saturate(dot(V, H));

                    if (NdotL <= 1e-6 || NdotV <= 1e-6 || VdotH <= 1e-6)
                    {
                        ray.rayEarlyKill = true;
                        return ray;
                    }
                    if (dot(L, Ng) <= 1e-6)
                    {
                        ray.rayEarlyKill = true;
                        return ray;
                    }

                    float3 F = FresnelSchlick(VdotH, F0);
                    float D = D_GGX(NdotH, roughness);
                    float G = G_SmithGGX(NdotV, NdotL, roughness);

                    // Cook-Torrance BRDF
                    float3 specBRDF = (F * D * G) / max(4.0 * NdotV * NdotL, 1e-8);

                    // PDF for half-vector GGX reflection sampling
                    float pdf_H = D * NdotH;
                    float pdf_L = pdf_H / max(4.0 * VdotH, 1e-8);

                    float branchPdf = specularChance * max(pdf_L, 1e-8);

                    ray.direction = L;
                    ray.rayColor *= specBRDF * NdotL / branchPdf;
                }
                else
                {
                    // ----- DIFFUSE BRANCH: cosine-weighted hemisphere -----
                    float2 xi = float2(randomValue(rngState), randomValue(rngState));
                    float3 L = toWorld(sampleCosineHemisphere(xi), Ng);

                    float NdotL = saturate(dot(Ng, L));
                    if (NdotL <= 1e-6)
                    {
                        ray.rayEarlyKill = true;
                        return ray;
                    }

                    float3 diffuseBRDF = diffuseColor / PI;
                    float pdf_L = NdotL / PI;
                    float branchPdf = (1.0 - specularChance) * max(pdf_L, 1e-8);

                    ray.direction = L;
                    ray.rayColor *= diffuseBRDF * NdotL / branchPdf;
                }

                ray.direction = safeNormalize(ray.direction);
                ray.inverseDirection = 1.0 / ray.direction;
                
                ray.position = hitInfo.hitPoint + Ng * (1e-4);

                // Russian roulette
                float p = max(ray.rayColor.r, max(ray.rayColor.g, ray.rayColor.b));
                p = clamp(p, 0.001, 0.95);

                if (randomValue(rngState) >= p)
                {
                    ray.rayEarlyKill = true;
                    return ray;
                }

                ray.rayColor *= 1.0 / p;
                return ray;
            }
            
            int blackHoleOverrideCheck(Ray ray) {
                for (int i = 0; i < numBlackHoles; i++) {
                    BlackHole blackHole = BlackHoles[i];
                    float3 d = ray.position - blackHole.position;
                    float rr = blackHole.SchwartzchildRadius * blackHole.blackHoleSOIMultiplier;
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
                return blackHole.SchwartzchildRadius / r2;
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

            float3 schwarzschildGeodesicAccel(float3 worldPos, float3 worldVel, BlackHole blackHole, float h2)
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
            
            void ApplyBlackHoleBendLUT(
                inout Ray ray,
                BlackHole blackHole,
                float stepLen,
                out float3 moveDir)
            {
                float3 rel = ray.position - blackHole.position;
                float r = length(rel);
                moveDir = ray.direction;

                if (r <= 1e-6)
                    return;

                float3 rayDir    = normalize(ray.direction);
                float3 radialOut = rel / r;
                float3 radialIn  = -radialOut;

                float mu = dot(rayDir, radialOut);

                float u = RadiusToLUT_U(r, blackHole.SchwartzchildRadius,
                                        _BHLUT_RMinOverRs, _BHLUT_RMaxOverRs,
                                        _BHLUT_LogEpsilonOverRs);
                float v = MuToLUT_V(mu, _BHLUT_MuResolution);

                
                float dPhiDs = tex2Dlod(_BlackHoleBendLUT, float4(u, v, 0, 0)).g * bendStrength;
                
                float dTheta = dPhiDs * stepLen;

                float3 bendDir = radialIn - dot(radialIn, rayDir) * rayDir;
                float  bendLen = length(bendDir);

                if (bendLen > 1e-6)
                {
                    bendDir /= bendLen;

                    float3 midDir = normalize(rayDir + bendDir * (0.5 * dTheta));
                    float3 endDir = normalize(rayDir + bendDir * dTheta);

                    moveDir           = midDir;
                    ray.direction     = endDir;
                    ray.inverseDirection = 1.0 / max(abs(endDir), 1e-8) * sign(endDir);
                }
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
                float3 dv1 = schwarzschildGeodesicAccel(pos0, vel0, blackHole, h2);

                // k2
                float3 pos1 = pos0 + dp1 * (0.5 * k);
                float3 vel1 = vel0 + dv1 * (0.5 * k);
                float3 dp2 = vel1;
                float3 dv2 = schwarzschildGeodesicAccel(pos1, vel1, blackHole, h2);

                // k3
                float3 pos2 = pos0 + dp2 * (0.5 * k);
                float3 vel2 = vel0 + dv2 * (0.5 * k);
                float3 dp3 = vel2;
                float3 dv3 = schwarzschildGeodesicAccel(pos2, vel2, blackHole, h2);

                // k4
                float3 pos3 = pos0 + dp3 * k;
                float3 vel3 = vel0 + dv3 * k;
                float3 dp4 = vel3;
                float3 dv4 = schwarzschildGeodesicAccel(pos3, vel3, blackHole, h2);

                float3 posEnd = pos0 + (k / 6.0) * (dp1 + 2.0 * dp2 + 2.0 * dp3 + dp4);
                float3 velEnd = vel0 + (k / 6.0) * (dv1 + 2.0 * dv2 + 2.0 * dv3 + dv4);
                

                float3 chord = posEnd - pos0;
                float chordLen = length(chord);
                float3 chordDir = (chord/chordLen);

                ray.position = pos0;
                ray.direction = chordDir;
                ray.inverseDirection = 1.0 / chordDir;
                return ray;
            }
            bool IsCapturedFromOutside(float3 origin, float3 dir, float3 bhPos, float rs)
            {
                float M = rs * 0.5;
                float bCrit = 3.0 * sqrt(3.0) * M;

                float3 rel = origin - bhPos;
                float b = length(cross(rel, normalize(dir)));

                return b < bCrit;
            }

            Ray marchInsideBlackHoleSOI(Ray ray, BlackHole blackHole, inout uint rngState) {
                //ray.marchingMode = true;
                int emergencyBreak = 0;
                float3 rayToBlackHole = blackHole.position - ray.position;
                float distanceToBlackHole = length(rayToBlackHole);
                while (distanceToBlackHole <= blackHole.SchwartzchildRadius * blackHole.blackHoleSOIMultiplier) {
                    if (distanceToBlackHole < blackHole.SchwartzchildRadius) {
                        ray.hitBlackHole = true;
                        return ray;
                    }
                    float chordLen = 1;
                    float3 dirEnd = 0;
                    // in marchInsideBlackHoleSOI
                    float distFromHorizon = nearestPointOnSphere(ray.position, blackHole.position, blackHole.SchwartzchildRadius);
                    float adaptiveStep = distFromHorizon * stepSize;
                    float minStep = 0.01;
                    float stepLen = sqrt(adaptiveStep * adaptiveStep + minStep * minStep);
                    float3 moveDir;
                    #ifdef ENABLE_LENSING
                    #ifdef USE_LUT 
                    ApplyBlackHoleBendLUT(ray, blackHole, stepLen, moveDir);
                    #endif
                    #ifndef USE_LUT
                    ray = blackHoleRK4Check(ray,blackHole,stepLen);
                    #endif
                    #endif  
                    HitInfo h = queryCollisions(ray, stepLen);
                    

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
                    if (!h.didHit)
                    {
                        ray.position += ray.direction * stepLen;
                        ray.inverseDirection = 1.0 / max(abs(ray.direction), 1e-8) * sign(ray.direction);
                    }
                    
                    rayToBlackHole = blackHole.position - ray.position;
                    distanceToBlackHole = length(rayToBlackHole);

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
            float3 DebugDirection(float3 dir)
{
    dir = normalize(dir);
    return 0.5 + 0.5 * dir;
}

            float3 starColor(float seed)
{
    if (seed > 0.9) return float3(1.0, 0.4, 0.2);   // red
    if (seed > 0.8) return float3(1.0, 0.6, 0.3);   // orange
    if (seed > 0.7) return float3(1.0, 0.9, 0.7);   // yellow
    if (seed > 0.6) return float3(0.9, 0.95, 1.0);  // white
    if (seed > 0.5) return float3(0.7, 0.8, 1.0);   // blue-white
    if (seed > 0.4) return float3(0.5, 0.7, 1.0);   // blue

    // slight white variation
    float r = 0.9 + 0.2 * hash(seed * 13.1);
    float g = 0.9 + 0.2 * hash(seed * 37.2);
    float b = 0.9 + 0.2 * hash(seed * 71.7);

    return float3(r,g,b);
}

    float starLayer(float3 dir, float scale, float threshold, float brightness)
    {
        float h = hash(floor(dir * scale));
        return smoothstep(threshold, 1.0, h) * brightness;
    }

    float3 getStarField(float3 rayDirection)
    {
        float3 dir = normalize(rayDirection);

        // --- layer parameters (easy to tweak) ---
        float3 scales      = float3(100.0, 200.0, 500.0);
        float3 thresholds  = float3(0.999, 0.997, 0.995);
        float3 brightness  = float3(1.0, 0.8, 0.6);

        // --- star density layers ---
        float stars = 0.0;

        stars += starLayer(dir, scales.x, thresholds.x, brightness.x);
        stars += starLayer(dir, scales.y, thresholds.y, brightness.y);
        stars += starLayer(dir, scales.z, thresholds.z, brightness.z);

        if (stars <= 0.0)
            return (0,0,0);

        // --- star color ---
        float seed = hash(dir * 123.456);
        float3 color = starColor(seed);

        return color * stars;
    }
            
            
            float3 trace(float3 viewPoint, inout uint rngState)
            {
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
                            //ray.incomingLight += getAngularGrid(ray.direction);
                            //incomingLight += float3(0,0,1);
                            //ray.incomingLight += float3(0.02,0.02,0.02);
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
                        //return float3(10000,0,10000);
                        return ray.incomingLight;
                    }
                    
                    iterations++;

                    
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
