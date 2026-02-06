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

			// Provide either accumWeight from C#, or numRenderedFrames (fallback)
			float accumWeight;          // expected in (0,1]; set from C#
			int   numRenderedFrames;    // fallback if accumWeight <= 0

			// Halton base-2/base-3 (returns [0,1) for each component)


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

				// If accumWeight not set (>0), fall back to 1/(N+1)
				float w = 1.0 / (numRenderedFrames + 1.0);

				// Standard exponential moving average
				float4 accumulated = saturate(oldRender * (1 - w) + newRender * w);
				return accumulated;
			}
			ENDHLSL
		}
	}
}