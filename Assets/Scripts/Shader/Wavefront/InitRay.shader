Shader "Custom/InitRay"
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
            
            uint numRenderedFrames;
            uint currentRayNum;
            uint num_black_holes;
            float3 ViewParams;
            float4x4 CameraLocalToWorldMatrix;
            float3 CameraWorldPos;
            RWStructuredBuffer<control> controls  : register(u1);
            RWStructuredBuffer<ray>     main_rays : register(u2);
            
            float4 frag(v2f i) : SV_Target
            {
                int pixelIndex = getPixelIndex(i);
                controls[pixelIndex].flags = 0;
                uint sampleIndex = (uint)numRenderedFrames * currentRayNum + 1u;
                float2 h = halton2(sampleIndex); //do some jittering to get actual ray position and direction

                uint hash = pixelIndex * 1664525u + 1013904223u;
                float2 rot = float2((hash & 0xFFFFu) / 65536.0, (hash >> 16) / 65536.0);
                h = frac(h + rot);

                float2 jitterUV = (h - 0.5) / (float2)_ScreenParams.xy;
                float3 vpLocal = float3((i.uv + jitterUV) - 0.5, 1) * ViewParams;
                float3 vp = mul(CameraLocalToWorldMatrix, float4(vpLocal, 1)).xyz;
                float3 position = vp;
                float3 direction = normalize(position - CameraWorldPos);
                main_rays[pixelIndex].direction = direction;
                main_rays[pixelIndex].position = position;
                return float4(direction * 0.5 + 0.5, 1); // visualize direction as color
            }
            ENDHLSL
        }
    }
}