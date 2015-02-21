Shader "Custom/AtmosphericFog" {
Properties {
	_MainTex ("Base (RGB)", 2D) = "black" {}
}

CGINCLUDE

	#include "UnityCG.cginc"

	uniform sampler2D _MainTex;
	//uniform sampler2D _DepthTexture;
	uniform sampler2D _CameraDepthTexture;
	
	uniform float _GlobalDensity;
	uniform float _SeaLevel;
	uniform float _HeightScale;
	uniform float _AuraPower;
	uniform float4 _FogColor;
	uniform float4 _SunColor;
	uniform float4 _MainTex_TexelSize;
	
	uniform float4 _SunDir;

	// for fast world space reconstruction
	uniform float4x4 _FrustumCornersWS;
	uniform float4 _CameraWS;
	 
	struct v2f {
		float4 pos : POSITION;
		float2 uv : TEXCOORD0;
		float2 uv_depth : TEXCOORD1;
		float4 interpolatedRay : TEXCOORD2;
	};
	
	v2f vert( appdata_img v )
	{
		v2f o;
		float index = v.vertex.z;
		v.vertex.z = 0.1;
		o.pos = mul(UNITY_MATRIX_MVP, v.vertex);
		//o.pos.z = o.pos.z - 100.0; // Attempt to push precision outwards (set near clip to +100m) so we can use hardware 16-bit depth buffer
		o.uv = v.texcoord.xy;
		o.uv_depth = v.texcoord.xy;
		
		#if UNITY_UV_STARTS_AT_TOP
		if (_MainTex_TexelSize.y < 0)
			o.uv.y = 1-o.uv.y;
		#endif				
		
		o.interpolatedRay = _FrustumCornersWS[(int)index];
		o.interpolatedRay.w = index;
		
		return o;
	}

	half4 frag (v2f i) : COLOR
	{
		float depth = Linear01Depth(UNITY_SAMPLE_DEPTH(tex2D(_CameraDepthTexture, i.uv_depth)));
		
		float3 worldPos = (_CameraWS + depth * i.interpolatedRay);
		float3 ray = worldPos - _CameraWS;
		float3 rayDir = normalize(ray);

		float distance = length(ray);

		half camHeightSeaLevel = _CameraWS.y - _SeaLevel;
		half fogDensity = _GlobalDensity * exp(-camHeightSeaLevel*_HeightScale) * (1.0 - exp(-distance*rayDir.y*_HeightScale))/rayDir.y;
		fogDensity = saturate(fogDensity);

		/* Mask out skybox with depthbuffer hack. Skybox doesn't write to depthbuffer, so depth will be exactly 1.0 */
		if (depth == 1.0) {
			fogDensity = 0;
		}

		half sunAmount = max(dot(rayDir, _SunDir), 0.0);
		half4 fogColor = lerp(_FogColor, _SunColor, pow(sunAmount, _AuraPower));

		return lerp(tex2D(_MainTex, i.uv), fogColor, fogDensity);
	}

ENDCG

SubShader {
	Pass {
		ZTest Always Cull Off ZWrite Off
		Fog { Mode off }

		CGPROGRAM

		#pragma vertex vert
		#pragma fragment frag
		#pragma fragmentoption ARB_precision_hint_fastest 
		#pragma exclude_renderers flash
		
		ENDCG
	}
}

Fallback off

}