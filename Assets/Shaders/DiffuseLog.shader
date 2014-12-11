Shader "Custom/DiffuseLog" {
	Properties {
		_MainTex ("Base (RGB)", 2D) = "white" {}
		_Tint ("Tint (RGB)", Color) = (1,1,1,1)
	}
	SubShader {
		Tags { "RenderType"="Opaque" }
				
		Pass {
			Tags { "LightMode"="ForwardBase" }
		
			CGPROGRAM
			
			#pragma target 3.0
			#pragma fragmentoption ARB_precision_hint_fastest
			
			#pragma vertex vert
			#pragma fragment frag
			#pragma multi_compile_fwdbase
			
			#include "UnityCG.cginc"
			#include "AutoLight.cginc"
			//#include "LogDepth.cginc"

			sampler2D _MainTex;
			float4 _Tint;
			float4 _LightColor0;
			
			struct v2f {
				float4 pos : POSITION;
				float3 lightDir : TEXCOORD0;
				float3 normal : TEXCOORD1;
				float2 uv : TEXCOORD2;
				float flogz : TEXCOORD3;
				LIGHTING_COORDS(4, 5)
			};
			
			v2f vert (appdata_base v) {
				v2f o;
				
				const float far = 1.0e5;
				const float near = 0.05;
				const float offset = 1.0;
				
				o.pos = mul(UNITY_MATRIX_MVP, v.vertex);
				
				// from here: http://forum.unity3d.com/threads/custom-z-buffer-problem-on-mac-and-windows.146710/
				//o.pos.z = (2.0 * log(C*v.vertex.z+offset) / log (C * far + offset) - 0) * v.vertex.w;
				
				// Original C method from outerra blog
				//o.pos.z = 2.0 * log(o.pos.w * C + offset)  / log(far * C + offset) - offset;
				//o.pos.z *= o.pos.w;
				
				// optimized from here: http://outerra.blogspot.nl/2013/07/logarithmic-depth-buffer-optimizations.html
				const float Fcoef = 2.0 / log2(far + near);
				o.pos.z = log2(max(1e-6, 1.0 + o.pos.w)) * Fcoef - near;
				o.flogz = 1.0 + o.pos.w;
				
				o.uv = v.texcoord.xy;
				o.lightDir = normalize(ObjSpaceLightDir(v.vertex));
				o.normal = normalize(v.normal).xyz;
				
				TRANSFER_VERTEX_TO_FRAGMENT(o);
				
				return o;
			}
			
			half4 frag(v2f i, out float depth:DEPTH) : COLOR {
				const float far = 1.0e5;
				const float near = 0.05;
				const float FcoefHalf = 2.0 / log2(far + near) * 0.5;
				depth = log2(i.flogz) * FcoefHalf;
				
				float3 L = normalize(i.lightDir);
				float3 N = normalize(i.normal);
				
				float attenuation = LIGHT_ATTENUATION(i) * 2;
				float4 ambient = UNITY_LIGHTMODEL_AMBIENT * 2;
				
				float NDotL = saturate(dot(N, L));
				float4 diffuseTerm = NDotL * _LightColor0 * _Tint * attenuation;
				
				float4 diffuse = tex2D(_MainTex, i.uv);
				float4 finalColor = (ambient + diffuseTerm) * diffuse;
				
				return finalColor;
			}
			
			ENDCG
		}
	} 
	FallBack "Diffuse"
}
