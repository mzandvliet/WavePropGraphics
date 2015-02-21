Shader "Custom/Clouds" {
Properties {
	_MainTex ("Base (RGB)", 2D) = "black" {}
}

CGINCLUDE

	#pragma target 3.0
	#include "UnityCG.cginc"
	
	uniform sampler2D _MainTex;
	uniform sampler2D _CameraDepthTexture;

	uniform sampler2D _Gradient4D;
	uniform sampler2D _PermTable1D;
	uniform sampler2D _PermTable2D;
	uniform float _Frequency;
	uniform float _Lacunarity;
	uniform float _Gain;

	uniform float4 _MainTex_TexelSize;

	// for fast world space reconstruction
	uniform float4x4 _FrustumCornersWS;
	uniform float4 _CameraWS;

	struct v2f {
		float4 pos : POSITION;
		float2 uv : TEXCOORD0;
		float2 uv_depth : TEXCOORD1;
		float4 interpolatedRay : TEXCOORD2;
	};

	float4 fade(float4 t)
	{
		return t * t * t * (t * (t * 6 - 15) + 10); // new curve
													//return t * t * (3 - 2 * t); // old curve
	}

	float perm(float x)
	{
		return tex2D(_PermTable1D, float2(x, 0)).a;
	}

	float4 perm2d(float2 uv)
	{
		return tex2D(_PermTable2D, uv);
	}

	float grad(float x, float4 p)
	{
		float4 g = tex2D(_Gradient4D, float2(x, 0)) * 2.0 - 1.0;
			return dot(g, p);
	}

	float gradperm(float x, float4 p)
	{
		float4 g = tex2D(_Gradient4D, float2(x, 0)) * 2.0 - 1.0;
			return dot(g, p);
	}

	float inoise(float4 p)
	{
		float4 P = fmod(floor(p), 256.0);	// FIND UNIT HYPERCUBE THAT CONTAINS POINT
			p -= floor(p);                      // FIND RELATIVE X,Y,Z OF POINT IN CUBE.
		float4 f = fade(p);                 // COMPUTE FADE CURVES FOR EACH OF X,Y,Z, W
			P = P / 256.0;
		const float one = 1.0 / 256.0;

		// HASH COORDINATES OF THE 16 CORNERS OF THE HYPERCUBE

		float4 AA = perm2d(P.xy) + P.z;

		float AAA = perm(AA.x) + P.w, AAB = perm(AA.x + one) + P.w;
		float ABA = perm(AA.y) + P.w, ABB = perm(AA.y + one) + P.w;
		float BAA = perm(AA.z) + P.w, BAB = perm(AA.z + one) + P.w;
		float BBA = perm(AA.w) + P.w, BBB = perm(AA.w + one) + P.w;

		return lerp(
			lerp(lerp(lerp(gradperm(AAA, p),
			gradperm(BAA, p + float4(-1, 0, 0, 0)), f.x),
			lerp(gradperm(ABA, p + float4(0, -1, 0, 0)),
			gradperm(BBA, p + float4(-1, -1, 0, 0)), f.x), f.y),

			lerp(lerp(gradperm(AAB, p + float4(0, 0, -1, 0)),
			gradperm(BAB, p + float4(-1, 0, -1, 0)), f.x),
			lerp(gradperm(ABB, p + float4(0, -1, -1, 0)),
			gradperm(BBB, p + float4(-1, -1, -1, 0)), f.x), f.y), f.z),

			lerp(lerp(lerp(gradperm(AAA + one, p + float4(0, 0, 0, -1)),
			gradperm(BAA + one, p + float4(-1, 0, 0, -1)), f.x),
			lerp(gradperm(ABA + one, p + float4(0, -1, 0, -1)),
			gradperm(BBA + one, p + float4(-1, -1, 0, -1)), f.x), f.y),

			lerp(lerp(gradperm(AAB + one, p + float4(0, 0, -1, -1)),
			gradperm(BAB + one, p + float4(-1, 0, -1, -1)), f.x),
			lerp(gradperm(ABB + one, p + float4(0, -1, -1, -1)),
			gradperm(BBB + one, p + float4(-1, -1, -1, -1)), f.x), f.y), f.z), f.w);
	}

	// fractal sum, range -1.0 - 1.0
	float fBm(float4 p, int octaves)
	{
		float freq = _Frequency, amp = 0.5;
		float sum = 0;
		for (int i = 0; i < octaves; i++)
		{
			sum += inoise(p * freq) * amp;
			freq *= _Lacunarity;
			amp *= _Gain;
		}
		return sum;
	}

	// fractal abs sum, range 0.0 - 1.0
	float turbulence(float4 p, int octaves)
	{
		float sum = 0;
		float freq = _Frequency, amp = 1.0;
		for (int i = 0; i < octaves; i++)
		{
			sum += abs(inoise(p*freq))*amp;
			freq *= _Lacunarity;
			amp *= _Gain;
		}
		return sum;
	}

	// Ridged multifractal, range 0.0 - 1.0
	// See "Texturing & Modeling, A Procedural Approach", Chapter 12
	float ridge(float h, float offset)
	{
		h = abs(h);
		h = offset - h;
		h = h * h;
		return h;
	}

	float ridgedmf(float4 p, int octaves, float offset)
	{
		float sum = 0;
		float freq = _Frequency, amp = 0.5;
		float prev = 1.0;
		for (int i = 0; i < octaves; i++)
		{
			float n = ridge(inoise(p*freq), offset);
			sum += n*amp*prev;
			prev = n;
			freq *= _Lacunarity;
			amp *= _Gain;
		}
		return sum;
	}

	v2f vert( appdata_img v )
	{
		v2f o;
		float index = v.vertex.z;
		v.vertex.z = 0.1;
		o.pos = mul(UNITY_MATRIX_MVP, v.vertex);
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

	float sphereDensity(float3 pos, float time) {
		float r = 100.0 * sin(fmod(time, 1) * 3.1423);
		return 1.0 - saturate(length(pos) / r);
	}

	half4 frag (v2f i) : COLOR
	{
		float4 t = tex2D(_MainTex, i.uv);
		float4 f = float4(1, 1, 1, 1);
		
		float depth = Linear01Depth(UNITY_SAMPLE_DEPTH(tex2D(_CameraDepthTexture, i.uv_depth)));
		//float3 worldPos = (_CameraWS + depth * i.interpolatedRay);
		//float3 ray = normalize(worldPos - _CameraWS);
		float3 ray = normalize(i.interpolatedRay);
		
		float fogDensity = 0.0;
		
		float stepSize = 1000 / 16.0;

		float3 pos = _CameraWS;
		for (int i = 0; i < 32; i++)
		{
			//float fog = sphereDensity(pos, _Time[1] * 0.2)
			float fog = fBm(float4(pos, _Time[1]), 2);
			fogDensity += fog * stepSize * _Gain;
			pos += ray * stepSize;
		}
		f.r = 1 - fogDensity * 0.4;
		f.g = 1 - fogDensity * 0.4;
		f.b = 1 - fogDensity * 0.4;
		fogDensity = saturate(fogDensity);

		
		return lerp(t, f, fogDensity);
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
		#pragma target 4.0

		ENDCG
	}
}

Fallback off

}
