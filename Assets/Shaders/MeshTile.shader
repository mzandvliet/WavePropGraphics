
/*

--

Based on the paper Continuous Distance-Dependent Level of Detail
For Rendering Heightmaps (CDLOD), by Filip Strugar, 2010

https://github.com/fstrugar/CDLOD/blob/master/cdlod_paper_latest.pdf

*/

Shader "Custom/Waves/MeshTile" {
	Properties {
		_MainColor ("Main Color Tint", Color) = (1,1,1,1) 
		_MainTex ("Base (RGB)", 2D) = "white" {}
		_WaveTex ("Wave Height (R)", 2D) = "white" {}
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
			#pragma multi_compile_fwdbase multi_compile_fog nolightmap nodirlightmap nodynlightmap novertexlight

			#include "UnityCG.cginc"
			#include "AutoLight.cginc"

			float4 _MainColor;
			sampler2D _MainTex;
			sampler2D _WaveTex;
			sampler2D _HeightTex;
			sampler2D _NormalTex;
			float _Scale;
			float _HeightScale;
			float2 _LerpRanges;
			float4 _LightColor0;

			struct v2f {
				float4 pos : SV_POSITION;
				float4 worldPos : POSITION1;

				float2 uv1 : TEXCOORD0; // diffuse
				float2 uv2 : TEXCOORD1; // normals

				UNITY_FOG_COORDS(2)
				SHADOW_COORDS(3)
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

				// return tex2Dlod(tex, float4(uv.xy, 0,0));
			}

			float UnpackHeight(float4 c) {
				return (c.r * 256 + c.g) / 257.0;
			}

			// half3 UnpackNormalCustom(half3 c) {
			// 	half3 n;
			// 	n.xy = c.xy * 2.0 - 1.0;
			// 	n.z = sqrt(1.0 - saturate(dot(n.xy, n.xy)));
			// 	return normalize(n);
			// }

			half3 UnpackNormalCustom(half3 c) {
				return normalize(c * 2.0 - 1.0);
			}

			float BiLerp3_sympy(float4x4 p, float2 uv) {
				float u = uv.x;
				float v = uv.y;

				/*----------------terms-------------------*/

				float a0 = -1.0 / 2.0 * p[0][1];
				float a1 = u * (a0 + (1.0 / 2.0) * p[2][1]);
				float a2 = pow(u, 2);
				float a3 = -5.0 / 2.0 * p[1][1];
				float a4 = (1.0 / 2.0) * p[3][1];
				float a5 = a2 * (a3 - a4 + p[0][1] + 2 * p[2][1]);
				float a6 = pow(u, 3);
				float a7 = (3.0 / 2.0) * p[1][1];
				float a8 = a6 * (a0 + a4 + a7 - 3.0 / 2.0 * p[2][1]);
				float a9 = -1.0 / 2.0 * p[0][2];
				float a10 = u * (a9 + (1.0 / 2.0) * p[2][2]);
				float a11 = (1.0 / 2.0) * p[3][2];
				float a12 = a2 * (-a11 + p[0][2] - 5.0 / 2.0 * p[1][2] + 2 * p[2][2]);
				float a13 = (3.0 / 2.0) * p[1][2];
				float a14 = a6 * (a11 + a13 + a9 - 3.0 / 2.0 * p[2][2]);
				float a15 = -1.0 / 2.0 * p[0][0];
				float a16 = u * (a15 + (1.0 / 2.0) * p[2][0]);
				float a17 = (1.0 / 2.0) * p[3][0];
				float a18 = a2 * (-a17 + p[0][0] - 5.0 / 2.0 * p[1][0] + 2 * p[2][0]);
				float a19 = a6 * (a15 + a17 + (3.0 / 2.0) * p[1][0] - 3.0 / 2.0 * p[2][0]);
				float a20 = -1.0 / 2.0 * a16 - 1.0 / 2.0 * a18 - 1.0 / 2.0 * a19 - 1.0 / 2.0 * p[1][0];
				float a21 = (1.0 / 2.0) * p[1][3];
				float a22 = -1.0 / 2.0 * p[0][3];
				float a23 = (1.0 / 2.0) * u * (a22 + (1.0 / 2.0) * p[2][3]);
				float a24 = (1.0 / 2.0) * p[3][3];
				float a25 = (1.0 / 2.0) * a2 * (-a24 + p[0][3] - 5.0 / 2.0 * p[1][3] + 2 * p[2][3]);
				float a26 = (1.0 / 2.0) * a6 * (a22 + a24 + (3.0 / 2.0) * p[1][3] - 3.0 / 2.0 * p[2][3]);

				/*--------------solutions------------------*/

				float output_0 = a1 + a5 + a8 + p[1][1] + pow(v, 3) * ((3.0 / 2.0) * a1 - 3.0 / 2.0 * a10 - 3.0 / 2.0 * a12 - a13 - 3.0 / 2.0 * a14 + a20 + a21 + a23 + a25
				+ a26 + (3.0 / 2.0) * a5 + a7 + (3.0 / 2.0) * a8) + pow(v, 2) * (-5.0 / 2.0 * a1 + 2 * a10 + 2 * a12 + 2 * a14 + a16 + a18 + a19 - a21 - a23 - a25 - a26 + a3 -
				5.0 / 2.0 * a5 - 5.0 / 2.0 * a8 + p[1][0] + 2 * p[1][2]) + v * ((1.0 / 2.0) * a10 + (1.0 / 2.0) * a12 + (1.0 / 2.0) * a14 + a20 + (1.0 / 2.0) * p[1][2]);

				return output_0;
			}

			float2 BiLerp3_Grad_sympy(float4x4 p, float2 uv) {
				float u = uv.x;
				float v = uv.y;

				/*----------------terms-------------------*/

				float a0 = (1.0 / 2.0) * p[3][2];
				float a1 = -a0 + p[0][2] - 5.0 / 2.0 * p[1][2] + 2 * p[2][2];
				float a2 = a1 * u;
				float a3 = -1.0 / 2.0 * p[0][2];
				float a4 = (3.0 / 2.0) * p[1][2];
				float a5 = a0 + a3 + a4 - 3.0 / 2.0 * p[2][2];
				float a6 = pow(u, 2);
				float a7 = (3.0 / 2.0) * a6;
				float a8 = (1.0 / 2.0) * p[3][0];
				float a9 = -a8 + p[0][0] - 5.0 / 2.0 * p[1][0] + 2 * p[2][0];
				float a10 = a9 * u;
				float a11 = -1.0 / 2.0 * p[0][0];
				float a12 = a11 + a8 + (3.0 / 2.0) * p[1][0] - 3.0 / 2.0 * p[2][0];
				float a13 = -a10 - a12 * a7 + (1.0 / 4.0) * p[0][0] - 1.0 / 4.0 * p[2][0];
				float a14 = pow(v, 2);
				float a15 = (1.0 / 4.0) * p[2][3];
				float a16 = (1.0 / 4.0) * p[0][3];
				float a17 = (1.0 / 2.0) * p[3][3];
				float a18 = -a17 + p[0][3] - 5.0 / 2.0 * p[1][3] + 2 * p[2][3];
				float a19 = a18 * u;
				float a20 = -5.0 / 2.0 * p[1][1];
				float a21 = (1.0 / 2.0) * p[3][1];
				float a22 = a20 - a21 + p[0][1] + 2 * p[2][1];
				float a23 = a22 * u;
				float a24 = 3 * a6;
				float a25 = a5 * a6;
				float a26 = -1.0 / 2.0 * p[0][1];
				float a27 = (3.0 / 2.0) * p[1][1];
				float a28 = a21 + a26 + a27 - 3.0 / 2.0 * p[2][1];
				float a29 = a28 * a6;
				float a30 = -1.0 / 2.0 * p[0][3];
				float a31 = a17 + a30 + (3.0 / 2.0) * p[1][3] - 3.0 / 2.0 * p[2][3];
				float a32 = a31 * a7;
				float a33 = a11 + (1.0 / 2.0) * p[2][0];
				float a34 = a26 + (1.0 / 2.0) * p[2][1];
				float a35 = u * (a3 + (1.0 / 2.0) * p[2][2]);
				float a36 = a1 * a6;
				float a37 = pow(u, 3);
				float a38 = a37 * a5;
				float a39 = (1.0 / 2.0) * p[1][3];
				float a40 = a33 * u;
				float a41 = a34 * u;
				float a42 = (1.0 / 2.0) * u * (a30 + (1.0 / 2.0) * p[2][3]);
				float a43 = a6 * a9;
				float a44 = (1.0 / 2.0) * a18 * a6;
				float a45 = a12 * a37;
				float a46 = a28 * a37;
				float a47 = (1.0 / 2.0) * a31 * a37;
				float a48 = -1.0 / 2.0 * a40 - 1.0 / 2.0 * a43 - 1.0 / 2.0 * a45 - 1.0 / 2.0 * p[1][0];

				/*--------------solutions------------------*/

				float output_0 = a14 * (2 * a10 + a12 * a24 - a15 + a16 - a19 + 4 * a2 - 5 * a23 + 6 * a25 - 15.0 / 2.0 * a29 - a32 + a33 + (5.0 / 4.0) * p[0][1] - p[0][2] - 5.0 / 4.0 * p[2][1] + p[2][2]) + 2 * a23 + a24 * a28 + a34 + pow(v, 3) * (a13 + a15 - a16 + a19 - 3 * a2 + 3 * a23 - 9.0 / 2.0 * a25 + (9.0 / 2.0) * a29 + a32 - 3.0 / 4.0 * p[0][1] + (3.0 / 4.0) * p[0][2] + (3.0 / 4.0) * p[2][1] - 3.0 / 4.0 * p[2][2]) + v * (a13 + a2 + a5 * a7 - 1.0 / 4.0 * p[0][2] + (1.0 / 4.0) * p[2][2]);
				float output_1 = 3 * a14 * (-a1 * a7 + a22 * a7 + a27 - 3.0 / 2.0 * a35 - 3.0 / 2.0 * a38 + a39 - a4 + (3.0 / 2.0) * a41 + a42 + a44 + (3.0 / 2.0) * a46 + a47 + a48) + (1.0 / 2.0) * a35 + (1.0 / 2.0) * a36 + (1.0 / 2.0) * a38 + a48 + (1.0 / 2.0) * p[1][2] + 2 * v * (a20 - 5.0 / 2.0 * a22 * a6 + 2 * a35 + 2 * a36 + 2 * a38 - a39 + a40 - 5.0 / 2.0 * a41 - a42 + a43 - a44 + a45 - 5.0 / 2.0 * a46 - a47 + p[1][0] + 2 * p[1][2]);

				return float2(output_0, output_1);
			}

			/* 
			Shifts odd-numbered vertices to even numbered vertices based on distance to camera
			Todo: right now this is in unit quad space, so gridpos == vertex. Simplify.
			*/
			float2 morphVertex(float2 gridPos, float2 vertex, float lerp) {
				const float g_resolution = 16.0; // Todo: supply from script

				// Create sawtooth pattern that peaks every other vertex
				float2 fracPart = frac(gridPos.xy * g_resolution * 0.5) * 2; 
				return vertex - (fracPart  / g_resolution * lerp);
			}

			v2f vert (appdata_base v) {
				v2f o;

				/*
				Todo: As with virtual texturing, read from an index structure to figure out which
				part of wave texture to sample, after uploading that directly to the gpu.
				*/

				float2 localVertex = float2(v.vertex.x, v.vertex.z);

				float height = UnpackHeight(tex2Dlod_bilinear(_HeightTex, float4(localVertex, 0, 0)));

				float4 wsVertex = mul(unity_ObjectToWorld, v.vertex); // world space vert for distance
				wsVertex.y = height * _HeightScale;

				// Construct morph parameter based on distance to camera
				float distance = length(wsVertex.xyz - _WorldSpaceCameraPos);
				float morph = smoothstep(0, 1, invLerp(_LerpRanges.x, _LerpRanges.y, distance));

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
				UNITY_TRANSFER_FOG(o,o.pos);

				return o;
			}

			half4 frag(v2f i) : COLOR {
				half3 L = normalize(_WorldSpaceLightPos0.xyz);

				/// ------

				const float horScale = 1.0 / 64.0;
				const float offset = (32768 / 2) * horScale;

				float x = i.worldPos.x;
				float z = i.worldPos.z;

				x *= horScale;
				z *= horScale;

				x += offset;
        		z += offset;

				int xFloor = floor(x);
        		int zFloor = floor(z);

				float xFrac = frac(x);
				float zFrac = frac(z);

				/*
				To try:

				Let texture system generate mipmaps for wave texture, see how it looks
				Could use that for something, including recursive interpolation.

				Maybe do this in the vertex shader, and try NORMAL interpolation?
				*/

				float scale = 1.0/512.0;
				float halfScale = scale / 2.0;
				float4x4 samples = float4x4(0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0);
				for (int zk = -1; zk < 3; zk++) {
					for (int xk = -1; xk < 3; xk++) {
						samples[1+xk][1+zk] = tex2Dlod(_WaveTex, float4(halfScale + (xFloor+xk)*scale, halfScale + (zFloor+zk)*scale, 0, 0));
					}
				}

				/*
				Ok, fun problem: We need to project our screen pixel coordinates into
				the wave system's intrinsic space.

				Wait, that's just the UV coordinates though, right? :)
				*/

				const float gradStep = 1.0;
				float2 grad = BiLerp3_Grad_sympy(samples, float2(xFrac, zFrac));
				float3 worldNormal = normalize(cross(
					float3(0, grad.y, gradStep),
					float3(gradStep, grad.x, 0)));

				/// ------

				// half3 worldNormal = float3(0,1,0);
				// half3 worldNormal = normalize(i.worldNormal);
				// half3 worldNormal = UnpackNormalCustom(tex2D(_NormalTex, i.uv2));

				half attenuation = LIGHT_ATTENUATION(i) * 2;
				
				// float4 ambient = 0;
				// float4 ambient = UNITY_LIGHTMODEL_AMBIENT * 2;
				float4 ambient = float4(ShadeSH9(half4(worldNormal,1)),1) * 0.5;

				half3 worldViewDir = normalize(UnityWorldSpaceViewDir(i.worldPos));
                half3 worldRefl = reflect(-worldViewDir, worldNormal);
                half4 skyData = UNITY_SAMPLE_TEXCUBE(unity_SpecCube0, worldRefl);
                half4 skyColor = half4(DecodeHDR (skyData, unity_SpecCube0_HDR), 1);

				half shadow = SHADOW_ATTENUATION(i);

				

				// Bicubic interp debugging
				// half samp = samples[1][1];
				half samp = BiLerp3_sympy(samples, float2(xFrac, zFrac));
				// half4 finalColor = half4(samp, samp, samp, 1);
				// half4 finalColor = half4(0.5 + 0.5 * worldNormal, 1);

				half NDotL = saturate(dot(worldNormal, L));
				half4 diffuseTerm = NDotL * _LightColor0 * attenuation * (0.5 + samp * 0.7);
				half4 diffuse = tex2D(_MainTex, i.uv1) * _MainColor;
				half4 finalColor = (ambient + diffuseTerm) * diffuse * shadow + skyColor * 0.7;

				UNITY_APPLY_FOG(i.fogCoord, finalColor);
                UNITY_OPAQUE_ALPHA(finalColor.a);

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
