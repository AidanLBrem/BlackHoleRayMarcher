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
            #include "ShaderBoilerplate.hlsl"
            #include "BasicStructures.hlsl"
            
            StructuredBuffer<HitInfo> hit_info_buffer;
            StructuredBuffer<control> controls;
            float4 frag(v2f i) : SV_Target
            {
                //return float4(0,0,0,0);
                uint2 numPixels  = _ScreenParams.xy;
                uint2 pixelCoord = i.uv * numPixels;
                uint  pixelIndex = pixelCoord.y * numPixels.x + pixelCoord.x;

                uint flags = controls[pixelIndex].flags;

                // Each flag gets a distinct color, mixed if multiple are set
                float3 col = float3(0, 0, 0);

                if (HasFlag(flags, FLAG_NEEDS_LINEAR_MARCH))     col += float3(0.0, 0.5, 1.0); // cyan-blue
                if (HasFlag(flags, FLAG_NEEDS_GEODESIC_MARCH))   col += float3(1.0, 0.5, 0.0); // orange
                if (HasFlag(flags, FLAG_NEEDS_REFLECTION))       col += float3(0.5, 1.0, 0.0); // yellow-green
                if (HasFlag(flags, FLAG_NEEDS_SKYBOX))           col += float3(0.0, 0.0, 1.0); // blue   = miss
                if (HasFlag(flags, FLAG_DONE))                   col += float3(1.0, 1.0, 1.0); // white

                // No flags set at all = pixel was never initialized
                if (flags == FLAG_NONE)
                    col = float3(1.0, 0.0, 0.0); // red = problem, classify never ran
                return float4(col,1);
                
            }
            ENDHLSL
        }
    }
}