Shader "Custom/ATrous"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        ZWrite Off Cull Off Blend Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            int stepSize;
            float colorSigma;  // recommended: 0.6 - 1.0
            float depthSigma;  // not used in color-only, kept for future

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float colorWeight(float3 a, float3 b)
            {
                float3 diff = a - b;
                float dist2 = dot(diff, diff);
                return exp(-dist2 / (2.0 * colorSigma * colorSigma));
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 centerColor = tex2D(_MainTex, i.uv).rgb;

                // 5x5 sparse kernel offsets
                const int2 offsets[25] = {
                    int2(-2,-2), int2(-1,-2), int2(0,-2), int2(1,-2), int2(2,-2),
                    int2(-2,-1), int2(-1,-1), int2(0,-1), int2(1,-1), int2(2,-1),
                    int2(-2, 0), int2(-1, 0), int2(0, 0), int2(1, 0), int2(2, 0),
                    int2(-2, 1), int2(-1, 1), int2(0, 1), int2(1, 1), int2(2, 1),
                    int2(-2, 2), int2(-1, 2), int2(0, 2), int2(1, 2), int2(2, 2)
                };

                // B3 spline kernel weights
                const float kernel[25] = {
                    1.0/256.0,  4.0/256.0,  6.0/256.0,  4.0/256.0, 1.0/256.0,
                    4.0/256.0, 16.0/256.0, 24.0/256.0, 16.0/256.0, 4.0/256.0,
                    6.0/256.0, 24.0/256.0, 36.0/256.0, 24.0/256.0, 6.0/256.0,
                    4.0/256.0, 16.0/256.0, 24.0/256.0, 16.0/256.0, 4.0/256.0,
                    1.0/256.0,  4.0/256.0,  6.0/256.0,  4.0/256.0, 1.0/256.0
                };

                float3 colorSum = float3(0, 0, 0);
                float weightSum = 0.0;

                for (int k = 0; k < 25; k++)
                {
                    float2 offset = offsets[k] * stepSize * _MainTex_TexelSize.xy;
                    float2 sampleUV = i.uv + offset;

                    float3 sampleColor = tex2D(_MainTex, sampleUV).rgb;

                    float w = kernel[k] * colorWeight(centerColor, sampleColor);

                    colorSum += sampleColor * w;
                    weightSum += w;
                }

                return float4(colorSum / max(weightSum, 1e-6), 1.0);
            }

            ENDHLSL
        }
    }
}