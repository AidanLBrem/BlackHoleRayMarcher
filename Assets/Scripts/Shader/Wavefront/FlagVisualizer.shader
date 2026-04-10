Shader "Custom/FlagVisualizer"
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
            #include "PixelFlags.hlsl"
            #include "BasicStructures.hlsl"
            
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f     { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; };

            struct control { uint flags; uint num_bounces; };

            StructuredBuffer<control> controls;
            StructuredBuffer<HitInfo> hit_info_buffer;
            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                /*//return float4(0,0,0,0);
                uint2 numPixels  = _ScreenParams.xy;
                uint2 pixelCoord = i.uv * numPixels;
                uint  pixelIndex = pixelCoord.y * numPixels.x + pixelCoord.x;

                uint flags = controls[pixelIndex].flags;

                // Each flag gets a distinct color, mixed if multiple are set
                float3 col = float3(0, 0, 0);

                if (HasFlag(flags, FLAG_NEEDS_LINEAR_MARCH))     col += float3(0.0, 0.5, 1.0); // cyan-blue
                if (HasFlag(flags, FLAG_NEEDS_GEODESIC_MARCH))   col += float3(1.0, 0.5, 0.0); // orange
                if (HasFlag(flags, FLAG_NEEDS_NEE_LINEAR))       col += float3(0.5, 1.0, 0.0); // yellow-green
                if (HasFlag(flags, FLAG_NEEDS_NEE_GEODESIC))     col += float3(1.0, 0.0, 0.5); // pink
                if (HasFlag(flags, FLAG_NEEDS_SCATTER_LINEAR))   col += float3(0.0, 1.0, 0.0); // green  = hit geometry
                if (HasFlag(flags, FLAG_NEEDS_SCATTER_GEODESIC)) col += float3(1.0, 1.0, 0.0); // yellow
                if (HasFlag(flags, FLAG_NEEDS_SKYBOX))           col += float3(0.0, 0.0, 1.0); // blue   = miss
                if (HasFlag(flags, FLAG_DONE))                   col += float3(1.0, 1.0, 1.0); // white

                // No flags set at all = pixel was never initialized
                if (flags == FLAG_NONE)
                    col = float3(1.0, 0.0, 0.0); // red = problem, classify never ran

                return float4(col, 1);*/
                uint2 numPixels  = _ScreenParams.xy;
                uint2 pixelCoord = i.uv * numPixels;
                uint  pixelIndex = pixelCoord.y * numPixels.x + pixelCoord.x;
                if (hit_info_buffer[pixelIndex].didHit)
                {
                    float3 color = Instances[hit_info_buffer[pixelIndex].objectIndex].material.color;
                    return float4(color, 1);
                }
                return float4(0,0,0,0);
            }
            ENDHLSL
        }
    }
}