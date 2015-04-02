#ifndef TERRAIN_SPLATMAP_COMMON_CGINC_INCLUDED
#define TERRAIN_SPLATMAP_COMMON_CGINC_INCLUDED

struct Input
{
	float2 tc_Control : TEXCOORD4;	// Not prefixing '_Contorl' with 'uv' allows a tighter packing of interpolators, which is necessary to support directional lightmap.
	float camDist;
	float3 camAngle;
	float3 myWorldPos;
	float3 myWorldNormal;
	INTERNAL_DATA
	UNITY_FOG_COORDS(5)
};

sampler2D _Control;
float4 _Control_ST;
sampler2D _Splat0,_Splat1,_Splat2,_Splat3;
//sampler2D _Heights;
sampler2D _GlobalColorTex;
sampler2D _GlobalNormalTex;

sampler2D _Normal0, _Normal1, _Normal2, _Normal3;

float _FresnelBias;
float _FresnelScale;
float _FresnelPower;

void SplatmapVert(inout appdata_full v, out Input data)
{
	UNITY_INITIALIZE_OUTPUT(Input, data);
	data.tc_Control = TRANSFORM_TEX(v.texcoord, _Control);	// Need to manually transform uv here, as we choose not to use 'uv' prefix for this texcoord.
	float4 pos = mul (UNITY_MATRIX_MVP, v.vertex);
	UNITY_TRANSFER_FOG(data, pos);

	data.myWorldPos = mul(_Object2World, v.vertex).xyz;
	data.myWorldNormal = normalize(mul((float3x3)_Object2World, v.normal));
	float3 camDelta = data.myWorldPos - _WorldSpaceCameraPos.xyz;
	data.camAngle = normalize(camDelta);
	data.camDist = length(camDelta);

#ifdef _TERRAIN_NORMAL_MAP
	v.tangent.xyz = cross(v.normal, float3(0,0,1));
	v.tangent.w = -1;
#endif
}

fixed4 Tex2DTriplanar(sampler2D tex, float3 uv, float3 blend) {
	return
		tex2D(tex, uv.zy) * blend.x +
		tex2D(tex, uv.xz) * blend.y +
		tex2D(tex, uv.xy) * blend.z;
}

float3 UnpackNormals(float4 c) {
	const float3 minusOne = float3(-1,-1,-1);
	return minusOne + c.rgb * 2.0;
}

float4 HeightBlend(float4 c1, inout float h1, float4 c2, float h2) {
	float depth = 0.2;
	float ma = max(h1, h2) - depth;
	float b1 = max(h1 - ma, 0);
	float b2 = max(h2 - ma, 0);
	h1 = max(h1, h2);
	return (c1 * b1 + c2 * b2) / (b1+b2);
}

void SplatmapMix(Input IN, out half4 splat_control, out half weight, out fixed4 mixedDiffuse, inout fixed3 mixedNormal)
{
	splat_control = tex2D(_Control, IN.tc_Control);

	//Todo: do splatmap equalization offline. Snow needs a boost.
	splat_control.a *= 1.66;
	splat_control = normalize(splat_control);

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

	/* Calculate blending and lerping values */

	float3 tpBlend = abs(IN.myWorldNormal); // Triplanar blend
	float distLerp = saturate((IN.camDist - 50.0) / 100.0); // Distance based crossfade

	// Worldspace uvs (todo: make scale parameters)
	const float _UVScale = 0.1;
	const float _UVScaleLod = 0.01;
	const float _UVScaleUnit = 0.001;
	float3 worldUV = IN.myWorldPos * _UVScale;
	float3 worldUVLod = IN.myWorldPos * _UVScaleLod;
	float3 worldUVUnit = IN.myWorldPos * _UVScaleUnit;

	/* Sample Diffuse */
	float4 splat0 = 		tex2D(_Splat0, worldUV.xz);
	float4 splat1 = 		Tex2DTriplanar(_Splat1, worldUV, tpBlend);
	float4 splat2 = 		tex2D(_Splat2, worldUV.xz);
	float4 splat3 = 		tex2D(_Splat3, worldUV.xz);
	float4 splat0Lod = 	tex2D(_Splat0, worldUVLod.xz);
	float4 splat1Lod = 	Tex2DTriplanar(_Splat1, worldUVLod, tpBlend);
	float4 splat2Lod = 	tex2D(_Splat2, worldUVLod.xz);
	float4 splat3Lod = 	tex2D(_Splat3, worldUVLod.xz);

	splat0 = lerp(splat0, splat0Lod, distLerp);
	splat1 = lerp(splat1, splat1Lod, distLerp);
	splat2 = lerp(splat2, splat2Lod, distLerp);
	splat3 = lerp(splat3, splat3Lod, distLerp);

	/* Sample Normals */

	float4 norm0 = 		tex2D(_Normal0, worldUV.xz);
	float4 norm1 = 		Tex2DTriplanar(_Normal1, worldUV, tpBlend);
	float4 norm2 = 		tex2D(_Normal2, worldUV.xz);
	float4 norm3 = 		tex2D(_Normal3, worldUV.xz);
	float4 norm0Lod = tex2D(_Normal0, worldUVLod.xz);
	float4 norm1Lod = Tex2DTriplanar(_Normal1, worldUVLod, tpBlend);

	norm0 = lerp(norm0, norm0Lod, distLerp);
	norm1 = lerp(norm1, norm1Lod, distLerp);

	// float4 heights = tex2D(_Heights, worldUV.xz);
	// float4 heightsLod = tex2D(_Heights, worldUVLod.xz);
	// heights = lerp(heights, heightsLod, distLerp);

	/* Mix Diffuse */

	float4 diffuse = splat0;
	float lastHeight = norm0.a + splat_control.r;
	diffuse = HeightBlend(diffuse, lastHeight, splat1, norm1.a + splat_control.g);
	diffuse = HeightBlend(diffuse, lastHeight, splat2, norm2.a + splat_control.b);
	diffuse = HeightBlend(diffuse, lastHeight, splat3, norm3.a + splat_control.a);

	float4 globalDiffuse = tex2D(_GlobalColorTex, worldUVUnit.xz);
	globalDiffuse *= 1.2; // Todo: do this equalization pass offline
	diffuse *= lerp(float4(1,1,1,1), globalDiffuse, 0.75);

	/* Mix Normals */

	// Todo: heightblend
	float3 normal = 0.0;
	normal += splat_control.r * UnpackNormals(norm0);
	normal += splat_control.g * UnpackNormals(norm1);
	normal += splat_control.b * UnpackNormals(norm2);
	normal += splat_control.a * UnpackNormals(norm3);
	normal = normalize(normal);

	float4 globalNormal = tex2D(_GlobalNormalTex, worldUVUnit.xz);
	normal = lerp(normal, UnpackNormal(globalNormal), saturate(IN.camDist * 0.001));


	/* Fresnel. Todo: configurable fresnel color */

	//float fresnel = _FresnelBias + _FresnelScale * pow(1.0 + dot(IN.camAngle, normal), _FresnelPower);

	//diffuse.r = lerp(diffuse.r, fixed4(1,1,1,0), fresnel);
	//diffuse.b = lerp(diffuse.b, fixed4(1,1,1,0), fresnel);

	/* Apply outputs */

	mixedDiffuse = diffuse;
	mixedNormal = normal;
}

void SplatmapApplyWeight(inout fixed4 color, fixed weight)
{
	color.rgb *= weight;
	color.a = 1.0;
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
