// SUMMARY
//Final pass of pipeline - provides TAA
//If weight is set to 0, full TAA, image converges to average of all simulations
//Otherwise, just merge previous and current frame, using w to decide which one is weighted more
//TODO: Look into velocity buffers to improve TAA

Shader "Custom/ColorQuantization"
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
			struct v2f
			{
				float2 uv : TEXCOORD0;
				float4 vertex : SV_POSITION;
			};

			struct appdata
			{
				float4 vertex : POSITION;
				float2 uv : TEXCOORD0;
			};
			
			sampler2D _MainTex;
			int numColors;
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
			    color.rgb = floor(color.rgb * (numColors - 1.0) + 0.5) / (numColors - 1.0);
			    return color;
			}
			ENDHLSL
		}
	}
}