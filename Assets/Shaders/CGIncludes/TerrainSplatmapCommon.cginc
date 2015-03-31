#ifndef TERRAIN_SPLATMAP_COMMON_CGINC_INCLUDED
#define TERRAIN_SPLATMAP_COMMON_CGINC_INCLUDED

struct Input
{
	float2 uv_Splat0 : TEXCOORD0;
	float2 uv_Splat1 : TEXCOORD1;
	float2 uv_Splat2 : TEXCOORD2;
	float2 uv_Splat3 : TEXCOORD3;
	float2 tc_Control : TEXCOORD4;	// Not prefixing '_Contorl' with 'uv' allows a tighter packing of interpolators, which is necessary to support directional lightmap.
	float fresnel;
	float camDist;
	float3 worldPos;
	float3 worldNormal;
	INTERNAL_DATA
	UNITY_FOG_COORDS(5)
};

sampler2D _Control;
float4 _Control_ST;
sampler2D _Splat0,_Splat1,_Splat2,_Splat3;
sampler2D _Height0,_Height1,_Height2,_Height3;
sampler2D _GlobalColorTex;
sampler2D _GlobalNormalTex;

#ifdef _TERRAIN_NORMAL_MAP
	sampler2D _Normal0, _Normal1, _Normal2, _Normal3;
#endif

float _FresnelBias;
float _FresnelScale;
float _FresnelPower;

void SplatmapVert(inout appdata_full v, out Input data)
{
	UNITY_INITIALIZE_OUTPUT(Input, data);
	data.tc_Control = TRANSFORM_TEX(v.texcoord, _Control);	// Need to manually transform uv here, as we choose not to use 'uv' prefix for this texcoord.
	float4 pos = mul (UNITY_MATRIX_MVP, v.vertex);
	UNITY_TRANSFER_FOG(data, pos);

	float3 worldPos = mul(_Object2World, v.vertex).xyz;
	float3 worldNormal = normalize(mul((float3x3)_Object2World, v.normal));
	float3 camDelta = worldPos - _WorldSpaceCameraPos.xyz;
	float3 i = normalize(camDelta);
	data.camDist = length(camDelta);
 	data.fresnel = _FresnelBias + _FresnelScale * pow(1.0 + dot(i, worldNormal), _FresnelPower);

#ifdef _TERRAIN_NORMAL_MAP
	v.tangent.xyz = cross(v.normal, float3(0,0,1));
	v.tangent.w = -1;
#endif
}

fixed4 Tex2DTriplanar(sampler2D tex, float3 pos, float3 blend) {
	return
		tex2D(tex, pos.zy) * blend.x +
		tex2D(tex, pos.xz) * blend.y +
		tex2D(tex, pos.xy) * blend.z;
}

float4 HeightBlend(float4 c1, float h1, float a1, float4 c2, float h2, float a2) {
	float depth = 0.2;
	float ma = max(h1 + a1, h2 + a2) - depth;
	float b1 = max(h1+a1 - ma, 0);
	float b2 = max(h2+a2 - ma, 0);
	return (c1 * b1 + c2 * b2) / (b1+b2);
}

void SplatmapMix(Input IN, out half4 splat_control, out half weight, out fixed4 mixedDiffuse, inout fixed3 mixedNormal)
{
	splat_control = tex2D(_Control, IN.tc_Control);
	weight = dot(splat_control, half4(1,1,1,1));

	#ifndef UNITY_PASS_DEFERRED
		// Normalize weights before lighting and restore weights in applyWeights function so that the overal
		// lighting result can be correctly weighted.
		// In G-Buffer pass we don't need to do it if Additive blending is enabled.
		// TODO: Normal blending in G-buffer pass...
		splat_control /= (weight + 1e-3f); // avoid NaNs in splat_control
	#endif

	#if !defined(SHADER_API_MOBILE) && defined(TERRAIN_SPLAT_ADDPASS)
		clip(weight - 0.0039 /*1/255*/);
	#endif

	float distLerp = min(1, max(0, (IN.camDist - 50.0) / 100.0));

	// The below loses the y component, making this effectively duoplanar mapping.
	// Very useful for cliff textures with clear horizontal lines, where the y component would look awful.
	//float3 worldNormal = normalize(float3(IN.worldNormal.x, 0, IN.worldNormal.z));
	float3 worldNormal = IN.worldNormal;
	float3 tpBlend = abs(worldNormal);

	const float _UVScale = 0.125;
	const float _UVScaleLod = 0.02;
	float3 worldUV = IN.worldPos * _UVScale;
	float3 worldUVLod = IN.worldPos * _UVScaleLod;

	float4 splat0 = tex2D(_Splat0, worldUV.xz);
	float4 splat1 = Tex2DTriplanar(_Splat1, worldUV, tpBlend);
	float4 splat2 = tex2D(_Splat2, worldUV.xz);
	float4 splat3 = tex2D(_Splat3, worldUV.xz);
	float4 splat0Lod = tex2D(_Splat0, worldUVLod.xz);
	float4 splat1Lod = Tex2DTriplanar(_Splat1, worldUVLod, tpBlend);

	// Todo: bake height value into alpha channel of diffuse textures (unless standard shader expects smoothness?)
	float4 height0 = tex2D(_Height0, worldUV.xz);
	float4 height1 = Tex2DTriplanar(_Height1, worldUV, tpBlend);
	float4 height0Lod = tex2D(_Height0, worldUVLod.xz);
	float4 height1Lod = Tex2DTriplanar(_Height1, worldUVLod, tpBlend);

	// Todo: also need to sample heights with these lod uvs
	splat0 = lerp(splat0, splat0Lod, distLerp);
	splat1 = float4(1,1,1,0);//lerp(splat1, splat1Lod, distLerp);

	height0 = lerp(height0, height0Lod, distLerp);
	height1 = lerp(height1, height1Lod, distLerp);

	splat0 = lerp(splat0, fixed4(1,1,1,0), IN.fresnel);

	// mixedDiffuse += splat_control.r * splat0;
	// mixedDiffuse += splat_control.g * splat1;
	mixedDiffuse = HeightBlend(splat0, height0, splat_control.r, splat1, height1, splat_control.g);
	mixedDiffuse += splat_control.b * splat2;
	mixedDiffuse += splat_control.a * splat3;

	#ifdef _TERRAIN_NORMAL_MAP
		float4 norm0 = tex2D(_Normal0, worldUV.xz);
		float4 norm1 = Tex2DTriplanar(_Normal1, worldUV, tpBlend);
		float4 norm2 = tex2D(_Normal2, worldUV.xz);
		float4 norm3 = tex2D(_Normal3, worldUV.xz);
		float4 norm0Lod = tex2D(_Normal0, worldUVLod.xz);
		float4 norm1Lod = Tex2DTriplanar(_Normal1, worldUVLod, tpBlend);

		norm0 = lerp(norm0, norm0Lod, distLerp);
		//norm1 = lerp(norm1, norm1Lod, distLerp);

		fixed4 nrm = 0.0f;
		nrm += splat_control.r * norm0;
		nrm += splat_control.g * norm1;
		nrm += splat_control.b * norm2;
		nrm += splat_control.a * norm3;
		mixedNormal = UnpackNormal(nrm);
	#endif
}

void SplatmapApplyWeight(inout fixed4 color, fixed weight)
{
	color.rgb *= weight;
	color.a = 1.0f;
}

void SplatmapApplyFog(inout fixed4 color, Input IN)
{
	#ifdef TERRAIN_SPLAT_ADDPASS
		UNITY_APPLY_FOG_COLOR(IN.fogCoord, color, fixed4(0,0,0,0));
	#else
		UNITY_APPLY_FOG(IN.fogCoord, color);
	#endif
}

#endif
