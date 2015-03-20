Shader "Custom/Triplanar" {
	Properties {
		_Color ("Color", Color) = (1,1,1,1)
		_MainTex ("Albedo (RGB)", 2D) = "white" {}
		_NormalTex ("Normal (RGB)", 2D) = "bump" {}
		_Glossiness ("Smoothness", Range(0,1)) = 0.5
		_Metallic ("Metallic", Range(0,1)) = 0.0
		_UVScale ("UV Scale", Range(0.01, 16)) = 1.0
	}
	SubShader {
		Tags { "RenderType"="Opaque" }
		LOD 200

		CGPROGRAM
		// Physically based Standard lighting model, and enable shadows on all light types
		#pragma surface surf Standard fullforwardshadows

		// Use shader model 3.0 target, to get nicer looking lighting
		#pragma target 3.0

		sampler2D _MainTex;
		sampler2D _NormalTex;

		struct Input {
			float2 uv_MainTex;
			float3 worldPos;
			float3 worldNormal;
			INTERNAL_DATA
		};

		half _Glossiness;
		half _Metallic;
		fixed4 _Color;
		float _UVScale;

		void surf (Input IN, inout SurfaceOutputStandard o) {
			float3 worldNormal = WorldNormalVector(IN, float3(0,0,1));
			float3 blend = normalize(abs(worldNormal));

			float3 worldUV = IN.worldPos;

			fixed4 c =
			(
				(tex2D (_MainTex, worldUV.yz * _UVScale) * blend.x) +
				(tex2D (_MainTex, worldUV.xz * _UVScale) * blend.y) +
				(tex2D (_MainTex, worldUV.xy * _UVScale) * blend.z)
			)	* _Color;

			float3 n =
			(
				(UnpackNormal(tex2D (_NormalTex, worldUV.yz * _UVScale)) * blend.x) +
				(UnpackNormal(tex2D (_NormalTex, worldUV.xz * _UVScale)) * blend.y) +
				(UnpackNormal(tex2D (_NormalTex, worldUV.xy * _UVScale)) * blend.z)
			);

			o.Albedo = c.rgb;
			// Metallic and smoothness come from slider variables
			o.Normal = n;
			o.Metallic = _Metallic;
			o.Smoothness = _Glossiness;
			o.Alpha = c.a;
		}
		ENDCG
	}
	FallBack "Diffuse"
}
