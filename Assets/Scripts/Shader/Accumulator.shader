Shader "Custom/Accumulator"
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

			sampler2D _MainTexOld;
			sampler2D _MainTex;

			float accumWeight;       // recommended: 0.05 to 0.2
			int   numRenderedFrames; // no longer used for primary weighting

			v2f vert (appdata v)
			{
				v2f o;
				o.vertex = UnityObjectToClipPos(v.vertex.xyz);
				o.uv = v.uv;
				return o;
			}

			float4 frag (v2f i) : SV_Target
			{

				float4 oldRender = tex2D(_MainTexOld, i.uv);
				float4 newRender = tex2D(_MainTex, i.uv);
				if (numRenderedFrames == 0)
				{
					return newRender;
				}
				float w = accumWeight;
				float4 accumulated = oldRender;
				if (w <= 0.0)
				{
						// If accumWeight not set (>0), fall back to 1/(N+1)
					w = 1.0 / (numRenderedFrames + 1.0);

					// Standard exponential moving average
					accumulated = saturate(oldRender * (1 - w) + newRender * w);
				}
				else
				{
					accumulated = lerp(oldRender, newRender, w);
				}
				
				return accumulated;
					

				
			}
			ENDHLSL
		}
	}
}