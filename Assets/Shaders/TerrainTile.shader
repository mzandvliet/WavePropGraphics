
Shader "Custom/Terrain/TerrainTile" {
	Properties {
		_MainColor ("Main Color Tint", Color) = (1,1,1,1) 
		_MainTex ("Base (RGB)", 2D) = "white" {}
		_HeightTex ("Height Map", 2D) = "white" {}
		_NormalTex ("Normal Map", 2D) = "bump" {}
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
			#pragma multi_compile_fwdbase nolightmap nodirlightmap nodynlightmap novertexlight

			#include "UnityCG.cginc"
			#include "AutoLight.cginc"

			float4 _MainColor;
			sampler2D _MainTex;
			sampler2D _HeightTex;
			sampler2D _NormalTex;
			float _Scale;
			float _HeightScale;
			float2 _LerpRanges;
			float4 _LightColor0;

			struct v2f {
				float4 pos : SV_POSITION;
				float4 worldPos : POSITION1;
				
				float2 uv1 : TEXCOORD2; // diffuse
				float2 uv2 : TEXCOORD3; // normals

				SHADOW_COORDS(5) // put shadows data into TEXCOORD1
			};

			inline float invLerp(float start, float end, float val) {
				return saturate((val - start) / (end - start));
			}

			/*
			 * Get a bilinearly interpolated sample from a texture (used in vert shader texture fetch, which doesn't support filtering)
			 *
			 * Could optimize by sampling from pre-calculated texture specificalliy for filtering step (http://http.developer.nvidia.com/GPUGems2/gpugems2_chapter18.html)
			 * or by evaluating procedural height function after morphing vertex
			 */
			float4 tex2Dlod_bilinear(sampler2D tex, float4 uv) {
				const float g_resolution = 16.0; // Todo: set from script
				const float g_resolutionInv = 1/g_resolution;

				float2 pixelFrac = frac(uv.xy * g_resolution);

				float4 baseUV = uv - float4(pixelFrac * g_resolutionInv,0,0);
				float4 heightBL = tex2Dlod(tex, baseUV);
				float4 heightBR = tex2Dlod(tex, baseUV + float4(g_resolutionInv,0,0,0));
				float4 heightTL = tex2Dlod(tex, baseUV + float4(0,g_resolutionInv,0,0));
				float4 heightTR = tex2Dlod(tex, baseUV + float4(g_resolutionInv,g_resolutionInv,0,0));

				float4 tA = lerp(heightBL, heightBR, pixelFrac.x);
				float4 tB = lerp(heightTL, heightTR, pixelFrac.x);

				return lerp(tA, tB, pixelFrac.y);
			}

			float UnpackHeight(float4 c) {
				return (c.r * 256 + c.g) / 257.0;
			}

			half3 UnpackNormalCustom(float4 c) {
				half3 n = (c.xyz * 2.0) - float3(1,1,1);
				n.z = 1 - n.x - n.y;
				return normalize(n);
			}

			// Todo: right now this is in unit quad space, so gridpos == vertex. Simplify.
			float2 morphVertex(float2 gridPos, float2 vertex, float lerp) {
				const float g_resolution = 16.0; // Todo: supply from script

				float2 fracPart = frac(gridPos.xy * g_resolution * 0.5) * 2; // Create sawtooth pattern that peaks every other vertex
				return vertex - (fracPart  / g_resolution * lerp);
			}

			v2f vert (appdata_base v) {
				v2f o;

				/* shift odd-numbered vertices to even numbered vertices based on distance to camera */

				float2 localVertex = float2(v.vertex.x, v.vertex.z);

				float height = UnpackHeight(tex2Dlod_bilinear(_HeightTex, float4(localVertex, 0, 0)));

				float4 wsVertex = mul(unity_ObjectToWorld, v.vertex); // world space vert for distance
				wsVertex.y = height * _HeightScale;

				// Construct morph parameter based on distance to camera
				float distance = length(wsVertex.xyz - _WorldSpaceCameraPos);
				float morph = invLerp(_LerpRanges.x, _LerpRanges.y, distance);

				// Morph in local unit space
				float2 morphedVertex = morphVertex(localVertex, localVertex, morph);
				wsVertex.x = morphedVertex.x;
				wsVertex.z = morphedVertex.y;

				o.uv1 = morphedVertex * _Scale / 16.0; // Todo: set resolution and uv-scale from script
				o.uv2 = morphedVertex;

				wsVertex = mul(unity_ObjectToWorld, wsVertex); // Morphed vertex to world space

				// Sample height using morphed local unit space
				height = UnpackHeight(tex2Dlod_bilinear(_HeightTex, float4(morphedVertex,0,0)));
				wsVertex.y = height * _HeightScale;

				// To clip space
				o.worldPos = wsVertex;
				o.pos = mul(UNITY_MATRIX_VP, wsVertex);

				TRANSFER_SHADOW(o)

				return o;
			}

			half4 frag(v2f i) : COLOR {
				half3 L = normalize(_WorldSpaceLightPos0.xyz);
				half3 worldNormal = UnpackNormalCustom(tex2D(_NormalTex, i.uv2));

				half attenuation = LIGHT_ATTENUATION(i) * 2;
				
				// float4 ambient = 0;
				// float4 ambient = UNITY_LIGHTMODEL_AMBIENT * 2;
				float4 ambient = float4(ShadeSH9(half4(worldNormal,1)),1);

				half3 worldViewDir = normalize(UnityWorldSpaceViewDir(i.worldPos));
                half3 worldRefl = reflect(-worldViewDir, worldNormal);
                half4 skyData = UNITY_SAMPLE_TEXCUBE(unity_SpecCube0, worldRefl);
                half4 skyColor = half4(DecodeHDR (skyData, unity_SpecCube0_HDR), 1);

				half shadow = SHADOW_ATTENUATION(i);

				half NDotL = saturate(dot(worldNormal, L));
				half4 diffuseTerm = NDotL * _LightColor0 * attenuation;

				half4 diffuse = tex2D(_MainTex, i.uv1) * _MainColor;
				half4 finalColor = (ambient + diffuseTerm) * diffuse * shadow + skyColor * 0.5;

				return finalColor;
			}

			ENDCG
		}

		// shadow caster rendering pass, implemented manually
        // using macros from UnityCG.cginc
        // Pass
        // {
        //     Tags {"LightMode"="ShadowCaster"}

        //     CGPROGRAM
        //     #pragma vertex vert
        //     #pragma fragment frag
        //     #pragma multi_compile_shadowcaster
        //     #include "UnityCG.cginc"

        //     struct v2f { 
        //         V2F_SHADOW_CASTER;
        //     };

        //     v2f vert(appdata_base v)
        //     {
        //         v2f o;
        //         TRANSFER_SHADOW_CASTER_NORMALOFFSET(o)
        //         return o;
        //     }

        //     float4 frag(v2f i) : SV_Target
        //     {
        //         SHADOW_CASTER_FRAGMENT(i)
        //     }
        //     ENDCG
        // }

		// shadow casting support
        // UsePass "Legacy Shaders/VertexLit/SHADOWCASTER"
	}
	FallBack "Diffuse"
}
