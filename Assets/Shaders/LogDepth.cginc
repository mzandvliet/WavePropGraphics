#ifndef LOGDEPTH_INCLUDED
#define LOGDEPTH_INCLUDED

float TransformVertexLog(inout float4 vert) {
	const float far = 1.0e5;
	const float near = 0.005;
	const float offset = 1.0;
	const float Fcoef = 2.0 / log2(far + near);
	
	vert.z = log2(max(1e-6, 1.0 + vert.w)) * Fcoef - near;
	return 1.0 + vert.w; // flogz
}

float GetFragmentDepthLog(float flogz) {
	const float far = 1.0e5;
	const float near = 0.005;

	const float FcoefHalf = 2.0 / log2(far + near) * 0.5;

	return log2(flogz) * FcoefHalf;
}

#endif