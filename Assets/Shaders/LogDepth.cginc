#ifndef LOGDEPTH_INCLUDED
#define LOGDEPTH_INCLUDED


float TransformVertexLog(inout float4 vert) {
	float far = _ProjectionParams.z;
	float near = _ProjectionParams.y;
	float C = 1;

	vert.z = log(C*vert.w + 1) / log(C*far + 1) * vert.w;
	return 0; // flogz
}

float GetFragmentDepthLog(float flogz) {
	return flogz;
}


/*float TransformVertexLog(inout float4 vert) {
	vert.z = log2(max(1e-6, 1.0 + vert.w)) * (2.0 / log2(_ProjectionParams.z + 1.0)) - 1.0;
	vert.z *= vert.w;
 	return 1.0 + vert.w;
}

float GetFragmentDepthLog(float flogz) {
	return log2(flogz) * (0.5 * (2.0 / log2(_ProjectionParams.z + 1.0)));
}*/

#endif
