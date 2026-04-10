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
            #include "GGX.hlsl"
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

            float4 frag(v2f i) : SV_Target
            {
                //return float4(_DebugInstancePosX * 0.1, 0, 0, 1);
                uint pixelIndex = getPixelIndex(i);

                if (!HasFlag(controls[pixelIndex].flags, FLAG_NEEDS_LINEAR_MARCH))
                    return float4(0, 0, 0, 0);
                control pixelControl = controls[pixelIndex];
                HitInfo HitInfo = hit_infos[pixelIndex];
                ray ray = main_rays[pixelIndex];
                pixelControl.num_bounces++;
                float3 preBounceColor = ray.rayColor;
                RayTracingMaterial material = Instances[HitInfo.objectIndex].material;

                // =======================
                // EMISSION
                // =======================
                if (material.emissionStrength > 0.0)
                {
                    float g = 1 / ray.energy;
                    #ifndef USE_REDSHIFTING
                    g = 1;
                    #endif
                    //float3 c = ApplyFakeRelativisticToneShift(material.emissiveColor.rgb, g, 1.0);
                    //float3 emittedLight = c * material.emissionStrength * (g * g * g);
                    float3 emittedLight = material.emissionStrength * (g * g * g);
                    ray.incomingLight += emittedLight * ray.rayColor;
                    ray.rayEarlyKill = true;
                    return ray;
                }

                // =======================
                // NORMAL SETUP
                // =======================
                uint triIndex = HitInfo.triIndex;
                Triangle tri = Triangles[triIndex];

                uint index1 = TriangleIndices[tri.baseIndex];
                uint index2 = TriangleIndices[tri.baseIndex + 1];
                uint index3 = TriangleIndices[tri.baseIndex + 2];

                float3 n1 = Normals[index1];
                float3 n2 = Normals[index2];
                float3 n3 = Normals[index3];

                float3 N = normalize(
                    n1 * (1 - (HitInfo.u + HitInfo.v)) +
                    n2 * HitInfo.u +
                    n3 * HitInfo.v
                );

                Mesh mesh = Instances[HitInfo.objectIndex];
                float3x3 nMat = transpose((float3x3)mesh.worldToLocalMatrix);
                N = safeNormalize(mul(nMat, N));

                float3 geomNormalLocal = normalize(cross(tri.edgeAB, tri.edgeAC));
                float3 Ng = safeNormalize(mul(nMat, geomNormalLocal));

                if (dot(Ng, ray.direction) > 0) Ng = -Ng;
                if (dot(N, Ng) < 0) N = -N;

                if (bad3(ray.direction) || bad3(ray.rayColor) || bad3(N) || bad3(Ng))
                {
                    ray.incomingLight = float3(0, 0, 0);
                    ray.rayEarlyKill = true;
                    return ray;
                }

                // =======================
                // MATERIAL
                // =======================
                float3 baseColor = material.color;
                float metallic = saturate(material.metallicity);
                float roughness = saturate(material.roughness);

                float3 dielectricF0 = float3(0.04, 0.04, 0.04);
                float3 F0 = lerp(dielectricF0, baseColor, metallic);

                float3 V = -ray.direction;
                float NdotV = max(dot(N, V), 1e-4);

                // Diffuse (energy-conserving, metallic-aware)
                float3 kd = (1.0 - metallic);
                float3 diffuseBRDF = kd * baseColor / PI;

                // =======================
                // DIRECT LIGHTING
                // =======================
                /*#ifdef APPLY_SUN_LIGHTING TODO: Impliment evaluateDirectSunAtHit
                ray.incomingLight += evaluateDirectSunAtHit(
                    hitInfo.hitPoint, N, Ng, V,
                    material.color, material.metallicity, material.roughness, F0
                );
                #endif

                #ifdef APPLY_SCATTERING
                /*if (ray.numBounces == 1) TODO: Impliment Scattering
                {
                    ray.incomingLight += calculateLight(
                        ray.ray.position,
                        ray.ray.direction,
                        hitInfo.distance,
                        rngState,
                        1
                    );
                }
                #endif*/

                // =======================
                // MIS SAMPLING
                // =======================
                float specWeight = luminance(F0);
                float diffWeight = luminance(baseColor) * (1.0 - metallic);

                float sum = specWeight + diffWeight;
                float specProb = (sum > 0.0) ? specWeight / sum : 0.5;

                // clamp to avoid degeneracy
                specProb = clamp(specProb, 0.01, 0.99);
                float choose = randomValue(rngState);
                bool sampleSpecular = (choose < specProb);

                float3 L;
                float pdf_spec = 0.0;
                float pdf_diff = 0.0;
                float3 specBRDF = float3(0,0,0);

                float NdotL = 0.0;
                if (sampleSpecular)
                {
                    float2 xi = float2(0,0);
                    float3 H = float3(0,0,0);
                    float NdotH;
                    float VdotH;
                    int attempts = 0;
                    const int maxAttempts = 10;
                    do
                    {
                        xi = float2(randomValue(rngState), randomValue(rngState));
                        H = sampleGGX_VNDF(xi, roughness, N, V);
                        L = reflect(-V, H);
                        NdotL = dot(N,L);
                        attempts++;
                    } while (NdotL <= 0 && attempts < maxAttempts);
                    if (NdotL <= 0)
                    {
                        ray.rayEarlyKill = true;
                        ray.incomingLight = float3(0,0,0);
                        return float4(1,0,0,0);
                    }
                    NdotH = saturate(dot(N, H));
                    VdotH = max(dot(V, H), 1e-4);
                    float3 F = FresnelSchlick(VdotH, F0);
                    float D = D_GGX(NdotH, roughness);
                    float G = G_SmithGGX(NdotV, NdotL, roughness);

                    specBRDF = (F * D * G) / max(4.0 * NdotV * NdotL, 1e-8);

                    float G1V = G1_SmithGGX(NdotV, roughness);
                    float pdf_H = D * G1V * VdotH / max(NdotV, 1e-8);
                    pdf_spec = pdf_H / max(4.0 * VdotH, 1e-8);

                    pdf_diff = NdotL / PI;
                }
                else
                {
                    float2 xi = float2(0,0);
                    int attempts = 0;
                    int maxAttempts = 10;
                    do
                    {
                        xi = float2(randomValue(rngState), randomValue((rngState)));
                        L = toWorld(sampleCosineHemisphere(xi), N);
                        attempts++;
                        NdotL = dot(N,L);
                    } while (NdotL <= 0 && attempts < maxAttempts);
                    if (NdotL <= 0)
                    {
                        ray.rayEarlyKill = true;
                        ray.incomingLight = float3(0,0,0);
                        return float4(1,0,0,0);
                    }

                    pdf_diff = NdotL / PI;

                    float3 H = normalize(V + L);
                    float NdotH = saturate(dot(N, H));
                    float VdotH = max(dot(V, H), 1e-4);

                    float D = D_GGX(NdotH, roughness);
                    float G1V = G1_SmithGGX(NdotV, roughness);

                    float pdf_H = D * G1V * VdotH / max(NdotV, 1e-8);
                    pdf_spec = pdf_H / max(4.0 * VdotH, 1e-8);
                }

                // =======================
                // MIS WEIGHTS
                // =======================
                float pdf_spec_w = pdf_spec * specProb;
                float pdf_diff_w = pdf_diff * (1 - specProb);

                float w_spec = (pdf_spec_w * pdf_spec_w) /
                    max(pdf_spec_w * pdf_spec_w + pdf_diff_w * pdf_diff_w, 1e-8);

                float w_diff = (pdf_diff_w * pdf_diff_w) /
                    max(pdf_spec_w * pdf_spec_w + pdf_diff_w * pdf_diff_w, 1e-8);

                float3 f = diffuseBRDF + specBRDF;

                float pdf = sampleSpecular ? pdf_spec_w : pdf_diff_w;
                float weight = sampleSpecular ? w_spec : w_diff;

                ray.direction = L;
                ray.rayColor *= f * NdotL * weight / max(pdf, 1e-8);

                ray.position = HitInfo.hitPoint + (Ng * 1e-4);

                // =======================
                // NEE TODO: IMPLIMENT NEE
                // =======================
                /*#ifdef APPLY_NEE
                if (ray.numBounces == 1)
                {
                    ray.incomingLight += NEE(ray.ray.position, N, rngState) * diffuseBRDF * preBounceColor;
                }
                
                #endif*/
                
                // =======================
                // RUSSIAN ROULETTE
                // =======================
                float p = max(dot(ray.rayColor, float3(0.2126, 0.7152, 0.0722)), 1e-4);
                if (randomValue(rngState) > p)
                {
                    ray.rayEarlyKill = true;
                    return float4(0,0,0,0);
                }

                ray.rayColor /= p;
                return ray;
            }
            ENDHLSL
        }
    }
}