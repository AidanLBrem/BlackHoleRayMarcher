// SUMMARY
//Final pass of pipeline - provides TAA
//If weight is set to 0, full TAA, image converges to average of all simulations
//Otherwise, just merge previous and current frame, using w to decide which one is weighted more
//TODO: Look into velocity buffers to improve TAA

Shader "Custom/Ditherer"
{
	Properties
	{
		[MainColor] _BaseColor("Base Color", Color) = (1, 1, 1, 1)
		_MainTex ("Texture", 2D) = "white" {}
	}

	SubShader
	{
		Tags { "RenderType" = "Opaque"  }

		Pass
		{
			HLSLPROGRAM

			#pragma vertex vert
			#pragma fragment frag

			#include "UnityCG.cginc"
			#include "Wavefront/Math.hlsl"

			struct appdata
			{
				float4 vertex : POSITION;
				float2 uv : TEXCOORD0;
			};
			
			sampler2D _MainTex;
			int matrixSize;
			v2f vert (appdata v)
			{
				v2f o;
				o.vertex = UnityObjectToClipPos(v.vertex.xyz);
				o.uv = v.uv;
				return o;
			}

			float4 frag (v2f i) : SV_Target
			{
				float4 color = tex2D(_MainTex, i.uv);
				float luma = dot(color, float3(0.2126, 0.7152, 0.0722));
				float maxLuma = 10.0;

				if (luma > maxLuma)
					color *= maxLuma / luma;
				return  float4(orderedDither(i.uv, color, luma, matrixSize),1);
			}
			ENDHLSL
		}
	}
}