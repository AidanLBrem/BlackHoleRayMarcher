Shader "Custom/Classify"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        ZWrite Off Cull Off
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0

            #include "UnityCG.cginc"
            #include "Math.hlsl"
            #include "PixelFlags.hlsl"
            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }
            struct blackhole
            {
                float3 position;
                float schwarzchild_radius;
                float black_hole_soi_multiplier;
            };
            struct control
            {
                uint flags;
                uint num_bounces;
            };
            
            struct ray
            {
                float3 position;
                float3 direction;
            };
            uint num_black_holes;

            RWStructuredBuffer<control> controls  : register(u1);
            RWStructuredBuffer<ray>     main_rays : register(u2);
            StructuredBuffer<blackhole> blackholes;
            float GetBlackHoleMarchShellRadius(int i)
            {
                float rs = blackholes[i].schwarzchild_radius;
                return max(rs * blackholes[i].black_hole_soi_multiplier, 4.0 * rs);
            }
            //True if the point is inside any BH march shell.
            bool IsInsideAnyMarchShell(float3 pos)
            {
                for (int i = 0; i < num_black_holes; i++)
                {
                    float shell = GetBlackHoleMarchShellRadius(i);
                    if (PointInsideSphere(pos, blackholes[i].position, shell))
                        return true;
                }
                return false;
            }
            float4 frag(v2f i) : SV_Target
            {
               int pixelIndex = getPixelIndex(i);
                if (HasFlag(controls[pixelIndex].flags, FLAG_DONE)) //no need to reclassify if done
                    return float4(0,0,0,0);
                float3 position = main_rays[pixelIndex].position;
                if (IsInsideAnyMarchShell(position))
                {
                    controls[pixelIndex].flags = FLAG_NEEDS_GEODESIC_MARCH;
                    return float4(1,0,0,0);
                }
                
                else
                {
                    controls[pixelIndex].flags = FLAG_NEEDS_LINEAR_MARCH;
                    return float4(0,1,0,0);
                }
            }
            ENDHLSL
        }
    }
}