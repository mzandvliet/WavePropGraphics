using UnityEngine;
using Unity.Burst;
using Unity.Jobs;
using Unity.Collections;
using Unity.Mathematics;
using Rng = Unity.Mathematics.Random;
using System;

/*
    Todo:

    - Use Roslyn code generation for most of this code!
    - Proper support for regular -> rational curves

    Perhaps take results from SymPy?

    ----

    Optimizations:

    Aggressive inlining, common term elimination

    Provide functions that yield multiple results without
    recalculating common terms. Eliminate common subexresions

    Example: position, normal and tangent queries all perform:
    float omt = 1f - t;
    float omt2 = omt * omt;
    float t2 = t * t;
    omt2 * omt
    ...
    
    And so on.

    -

    Again, code generation util like SymPy can be used to perform
    the elimination automatically. We'd get functions like:

    GetPosition
    GetTangent
    GetNormal
    GetPositionTangent
    GetPositionTangentNormal

    I mean, why skimp if you're going to autogenerate them?

    -

    Implement surface interpolation using bilinear method, should
    save on some operations

    --

    Code Generation

    Generate functions using Berstein Polynomial expansion

    A collection of blending function lambdas would be fun, but Burst doesn't like them
    That's important to note. Seems burst puts limits on first-order use of functions?
    Yep: https://lucasmeijer.com/posts/cpp_unity/

    private static readonly Func<float,float>[] funcs = new Func<float, float>[] {
        (t) => (1f - t) * (1f - t),
        (t) => 3f * ((1f - t)*(1f - t)) * t,
        (t) => 3f * (1f - t) * (t*t),
        (t) => t*t*t
    };

    Using the static Berstein class for now. Again, code generation solves it.
 */

public static class BDCMath {
    public static int TriangularNumber(int n) {
        return (n*(n-1))/2;
    }

    public static int IntPow(int x, uint pow) {
        int ret = 1;
        while (pow != 0) {
            if ((pow & 1) == 1)
                ret *= x;
            x *= x;
            pow >>= 1;
        }
        return ret;
    }
}

public static class Bernstein {
    public static float Linear0(float t) {
        return 1f - t;
    }

    public static float Linear1(float t) {
        return t;
    }

    public static float2 Linear(float t) {
        return new float2(
            Linear0(t),
            Linear1(t)
        );
    }


    public static float Quadratic0(float t) {
        float tInv = 1f - t;
        return tInv * tInv;
    }

    public static float Quadratic1(float t) {
        float tInv = 1f - t;
        return 2f * tInv * t;
    }

    public static float Quadratic2(float t) {
        return t * t;
    }

    public static float3 Quadratic(float t) {
        return new float3(
            Quadratic0(t),
            Quadratic1(t),
            Quadratic2(t)
        );
    }


    public static float Cubic0(float t) {
        float tInv = 1f - t;
        return tInv * tInv * tInv;
    }

    public static float Cubic1(float t) {
        float tInv = 1f - t;
        return 3f * tInv * tInv * t;
    }

    public static float Cubic2(float t) {
        return 3f * (1f - t) * t * t;
    }

    public static float Cubic3(float t) {
        return t * t * t;
    }

    public static float4 Cubic(float t) {
        return new float4(
            Cubic0(t),
            Cubic1(t),
            Cubic2(t),
            Cubic3(t)
        );
    }
}

public static class BDCQuartic1d {
    public static float Get(NativeSlice<float> c, float t) {
        float t2 = t * t;
        float t3 = t2 * t;
        float t4 = t3 * t;
        float tInv = 1 - t;
        float tInv2 = tInv * tInv;
        float tInv3 = tInv2 * tInv;
        float tInv4 = tInv3 * tInv;

        return
            tInv4 * c[0] +
            4 * tInv3 * c[1] * t +
            6 * tInv2 * c[2] * t2 +
            4 * tInv * c[3] * t3 +
            c[4] * t4;
    }
}

public static class BDCQuartic2d {
    public static float2 Get(NativeSlice<float2> c, float t) {
        float t2 = t * t;
        float t3 = t2 * t;
        float t4 = t3 * t;
        float tInv = 1 - t;
        float tInv2 = tInv * tInv;
        float tInv3 = tInv2 * tInv;
        float tInv4 = tInv3 * tInv;

        return
            tInv4 * c[0] +
            4 * tInv3 * c[1] * t +
            6 * tInv2 * c[2] * t2 +
            4 * tInv * c[3] * t3 +
            c[4] * t4;
    }
}

public static class BDCQuartic3d {
    public static float3 Get(NativeSlice<float3> c, float t) {
        float t2 = t * t;
        float t3 = t2 * t;
        float t4 = t3 * t;
        float tInv = 1 - t;
        float tInv2 = tInv * tInv;
        float tInv3 = tInv2 * tInv;
        float tInv4 = tInv3 * tInv;

        return 
            tInv4 * c[0] +
            4 * tInv3 * c[1] * t +
            6 * tInv2 * c[2] * t2 +
            4 * tInv * c[3] * t3 +
            c[4] * t4;
    }
}

public static class BDCQuartic4d {
    public static float4 Get(NativeSlice<float4> c, float t) {
        float t2 = t * t;
        float t3 = t2 * t;
        float t4 = t3 * t;
        float tInv = 1 - t;
        float tInv2 = tInv * tInv;
        float tInv3 = tInv2 * tInv;
        float tInv4 = tInv3 * tInv;

        return
            tInv4 * c[0] +
            4 * tInv3 * c[1] * t +
            6 * tInv2 * c[2] * t2 +
            4 * tInv * c[3] * t3 +
            c[4] * t4;
    }

    public static float4 GetTriangle(NativeSlice<float4> p, in float2 uv) {
        float u = uv[0];
        float v = uv[1];
        float w = 1f - u - v;

        /*----------------terms-------------------*/

        float a0 = 1.0f * math.pow(w, 4);
        float a1 = 1.0f * math.pow(v, 4);
        float a2 = 1.0f * math.pow(u, 4);
        float a3 = 4.0f * math.pow(w, 3);
        float a4 = a3 * v;
        float a5 = 4.0f * math.pow(v, 3);
        float a6 = a5 * w;
        float a7 = a3 * u;
        float a8 = a5 * u;
        float a9 = 4.0f * math.pow(u, 3);
        float a10 = a9 * w;
        float a11 = a9 * v;
        float a12 = math.pow(w, 2);
        float a13 = 12.0f * u;
        float a14 = a12 * a13 * v;
        float a15 = math.pow(v, 2);
        float a16 = a13 * a15 * w;
        float a17 = math.pow(u, 2);
        float a18 = 12.0f * a17 * v * w;
        float a19 = 6.0f * a12;
        float a20 = a15 * a19;
        float a21 = a17 * a19;
        float a22 = 6.0f * a15 * a17;

        /*--------------solutions------------------*/

        return a0 * p[0] + a1 * p[4] + a10 * p[12] + a11 * p[13] + a14 * p[6] + a16 * p[7] + a18 * p[10] + a2 * p[14] + a20 * p[2] + a21 * p[9] + a22 * p[11] + a4 * p[1] + a6 * p[3] + a7 * p[5] + a8 * p[8];
    }
}

public static class BDCCubic4d {
    public static float4 GetCasteljau(NativeArray<float4> c, in float t) {
        float4 bc = math.lerp(c[1], c[2], t);
        return math.lerp(math.lerp(math.lerp(c[0], c[1], t), bc, t), math.lerp(bc, math.lerp(c[2], c[3], t), t), t);
    }

    public static void Split(NativeArray<float4> o, in float t, NativeArray<float4> left, NativeArray<float4> right) {
        float4 ab = math.lerp(o[0], o[1], t);
        float4 bc = math.lerp(o[1], o[2], t);
        float4 cd = math.lerp(o[2], o[3], t);

        float4 abbc = math.lerp(ab, bc, t);
        float4 bccd = math.lerp(bc, cd, t);

        float4 p = math.lerp(abbc, bccd, t);

        left[0] = o[0];
        left[1] = ab;
        left[2] = abbc;
        left[3] = p;

        right[0] = p;
        right[1] = bccd;
        right[2] = cd;
        right[3] = o[3];
    }

    public static float4 Get(NativeSlice<float4> c, in float t) {
        float omt = 1f - t;
        float omt2 = omt * omt;
        float t2 = t * t;
        return
            c[0] * (omt2 * omt) +
            c[1] * (3f * omt2 * t) +
            c[2] * (3f * omt * t2) +
            c[3] * (t2 * t);
    }

    public static float4 GetTangent(NativeSlice<float4> c, in float t) {
        float omt = 1f - t;
        float omt2 = omt * omt;
        float t2 = t * t;
        return math.normalize(
            c[0] * (-omt2) +
            c[1] * (3f * omt2 - 2f * omt) +
            c[2] * (-3f * t2 + 2f * t) +
            c[3] * (t2)
        );
    }

    public static float4 GetNonUnitTangent(NativeSlice<float4> c, in float t) {
        float omt = 1f - t;
        float omt2 = omt * omt;
        float t2 = t * t;
        return
            c[0] * (-omt2) +
            c[1] * (3f * omt2 - 2f * omt) +
            c[2] * (-3f * t2 + 2f * t) +
            c[3] * (t2);
    }
}

public static class BDCQuadratic4d {
    public static float4 Get(NativeSlice<float4> c, in float t) {
        float4 u = 1f - t;
        return u * u * c[0] + 2f * t * u * c[1] + t * t * c[2];
    }

    public static float4 GetTriangle(NativeSlice<float4> p, in float2 uv) {
        float u = uv[0];
        float v = uv[1];
        float w = 1f - u - v;

        /*----------------terms-------------------*/

        float a0 = 2.0f * w;
        float a1 = a0 * v;
        float a2 = a0 * u;
        float a3 = 2.0f * u * v;
        float a4 = 1.0f * math.pow(w, 2);
        float a5 = 1.0f * math.pow(v, 2);
        float a6 = 1.0f * math.pow(u, 2);

        /*--------------solutions------------------*/

        return a1 * p[1] + a2 * p[3] + a3 * p[4] + a4 * p[0] + a5 * p[2] + a6 * p[5];
    }

    public static void GetQuadDU(NativeSlice<float4> c, NativeSlice<float4> du) {
        for (int v = 0; v < 3; v++) {
            for (int u = 0; u < 2; u++) {
                du[v * 2 + u] = 2f * (c[v * 3 + u] - c[v * 3 + (u + 1)]);
            }
        }
    }

    public static void GetQuadDV(NativeSlice<float4> c, NativeSlice<float4> dv) {
        for (int v = 0; v < 2; v++) {
            for (int u = 0; u < 3; u++) {
                dv[v * 3 + u] = 2f * (c[v * 3 + u] - c[(v + 1) * 3 + u]);
            }
        }
    }

    public static float4 GetQuadTangentU(NativeSlice<float4> du, in float2 uv) {
        float2 blend_u = Bernstein.Linear(uv.x);
        float3 blend_v = Bernstein.Quadratic(uv.y);

        float4 tng = float4.zero;
        for (int v = 0; v < 3; v++) {
            for (int u = 0; u < 2; u++) {
                var blend = blend_u[u] * blend_v[v];
                tng += du[v * 2 + u] * blend;
            }
        }

        return tng;
    }

    public static float4 GetQuadTangentV(NativeSlice<float4> dv, in float2 uv) {
        float3 blend_u = Bernstein.Quadratic(uv.x);
        float2 blend_v = Bernstein.Linear(uv.y);

        float4 tng = float4.zero;
        for (int v = 0; v < 2; v++) {
            for (int u = 0; u < 3; u++) {
                var blend = blend_u[u] * blend_v[v];
                tng += dv[v * 3 + u] * blend;
            }
        }

        return tng;
    }
}

public static class BDCCubic3d {
    /* === Lines === */

    public const int NUM_POINTS = 4;

    public static float3 GetCasteljau(NativeSlice<float3> c, in float t) {
        float3 bc = math.lerp(c[1], c[2], t);
        return math.lerp(math.lerp(math.lerp(c[0], c[1], t), bc, t), math.lerp(bc, math.lerp(c[2], c[3], t), t), t);
    }

    public static void Split(NativeSlice<float3> o, in float t, NativeSlice<float3> left, NativeSlice<float3> right) {
        float3 ab = math.lerp(o[0], o[1], t);
        float3 bc = math.lerp(o[1], o[2], t);
        float3 cd = math.lerp(o[2], o[3], t);

        float3 abbc = math.lerp(ab, bc, t);
        float3 bccd = math.lerp(bc, cd, t);

        float3 p = math.lerp(abbc, bccd, t);

        left[0] = o[0];
        left[1] = ab;
        left[2] = abbc;
        left[3] = p;

        right[0] = p;
        right[1] = bccd;
        right[2] = cd;
        right[3] = o[3];
    }

    public static float3 Get(NativeSlice<float3> c, in float t) {
        float tInv = 1f - t;
        float tInv2 = tInv * tInv;
        float t2 = t * t;
        return
            c[0] * (tInv2 * tInv) +
            c[1] * (3f * tInv2 * t) +
            c[2] * (3f * tInv * t2) +
            c[3] * (t2 * t);
    }

    public static float3 GetTangent(NativeSlice<float3> c, in float t) {
        float omt = 1f - t;
        float omt2 = omt * omt;
        float t2 = t * t;
        return math.normalize(
            c[0] * (-omt2) +
            c[1] * (3f * omt2 - 2f * omt) +
            c[2] * (-3f * t2 + 2f * t) +
            c[3] * (t2)
        );
    }

    public static float3 GetNonUnitTangent(NativeSlice<float3> c, in float t) {
        float omt = 1f - t;
        float omt2 = omt * omt;
        float t2 = t * t;
        return
            c[0] * (-omt2) +
            c[1] * (3f * omt2 - 2f * omt) +
            c[2] * (-3f * t2 + 2f * t) +
            c[3] * (t2);
    }

    // Assumes tangent and up are already normal vectors
    public static float3 GetNormal(NativeSlice<float3> c, in float t, in float3 up) {
        float3 tangent = GetTangent(c, t);
        float3 binorm = math.cross(up, tangent);
        return math.cross(tangent, binorm);
    }

    public static float3 GetNonUnitNormal(NativeSlice<float3> c, in float t, in float3 up) {
        float3 tangent = GetNonUnitTangent(c, t);
        float3 binorm = math.cross(up, tangent);
        return math.cross(tangent, binorm);
    }

    public static float Length(NativeSlice<float3> c, in int steps) {
        float dist = 0;

        float3 pPrev = c[0];
        for (int i = 1; i <= steps; i++) {
            float t = i / (float)steps;
            float3 p = Get(c, t);
            dist += math.length(p - pPrev);
            pPrev = p;
        }

        return dist;
    }

    public static float Length(NativeSlice<float3> c, in int steps, in float t) {
        float dist = 0;

        float3 pPrev = c[0];
        for (int i = 1; i <= steps; i++) {
            float tNow = t * (i / (float)steps);
            float3 p = Get(c, tNow);
            dist += math.length(p - pPrev);
            pPrev = p;
        }

        return dist;
    }

    public static float Length(NativeSlice<float> distances, float t) {
        t = t * (float)(distances.Length - 1);
        int ti = (int)math.floor(t);
        if (ti >= distances.Length - 1) {
            return distances[distances.Length - 1];
        }
        return math.lerp(distances[ti], distances[ti + 1], t - (float)ti);
    }

    public static void CacheDistances(NativeSlice<float3> c, NativeSlice<float> outDistances) {
        float dist = 0;
        outDistances[0] = 0f;
        float3 pPrev = c[0];
        for (int i = 1; i < outDistances.Length; i++) {
            float t = i / (float)(outDistances.Length - 1);
            float3 p = Get(c, t);
            dist += math.length(p - pPrev);
            outDistances[i] = dist;
            pPrev = p;
        }
    }

    /* === Surfaces === */

    /*
        Todo:
        This is wrong, du and dv partials should be separate
     */

    public static void GetQuadPointDeltas_Wrong(NativeSlice<float3> c, NativeSlice<ValueTuple<float3>> q) {
        for (int v = 0; v < 3; v++) {
            for (int u = 0; u < 3; u++) {
                q[v * 3 + u] = new ValueTuple<float3>(
                    c[v * 4 + u] - c[v * 4 + (u + 1)], // u derivative
                    c[v * 4 + u] - c[(v + 1) * 4 + u] // v derivative
                );
            }
        }
    }

    public static float3 GetQuad(NativeSlice<float3> c, in float2 uv) {
        float3 p = float3.zero;

        float4 blend_u = Bernstein.Cubic(uv.x);
        float4 blend_v = Bernstein.Cubic(uv.y);

        for (int v = 0; v < 4; v++) {
            for (int u = 0; u < 4; u++) {
                p += c[v * 4 + u] * (blend_u[u] * blend_v[v]);
            }
        }

        return p;
    }
}

public static class BDCCubic2d {
    /* === Curves === */

    public static float2 GetCasteljau(NativeSlice<float2> c, in float t) {
        float2 bc = math.lerp(c[1], c[2], t);
        return math.lerp(math.lerp(math.lerp(c[0], c[1], t), bc, t), math.lerp(bc, math.lerp(c[2], c[3], t), t), t);
    }

    public static float2 Get(NativeSlice<float2> c, in float t) {
        float omt = 1f - t;
        float omt2 = omt * omt;
        float t2 = t * t;
        return
            c[0] * (omt2 * omt) +
            c[1] * (3f * omt2 * t) +
            c[2] * (3f * omt * t2) +
            c[3] * (t2 * t);
    }

    public static float2 Get(NativeSlice<float2> c, NativeSlice<float> w, in float t) {
        float omt = 1f - t;
        float omt2 = omt * omt;
        float t2 = t * t;
        return
            c[0] * w[0] * (omt2 * omt) +
            c[1] * w[1] * (3f * omt2 * t) +
            c[2] * w[2] * (3f * omt * t2) +
            c[3] * w[3] * (t2 * t);
    }

    public static float2 GetTangent(NativeSlice<float2> c, in float t) {
        float omt = 1f - t;
        float omt2 = omt * omt;
        float t2 = t * t;
        return math.normalize(
            c[0] * (-omt2) +
            c[1] * (3f * omt2 - 2f * omt) +
            c[2] * (-3f * t2 + 2f * t) +
            c[3] * (t2)
        );
    }

    public static float2 GetNonUnitTangent(NativeSlice<float2> c, in float t) {
        float omt = 1f - t;
        float omt2 = omt * omt;
        float t2 = t * t;
        return
            c[0] * (-omt2) +
            c[1] * (3f * omt2 - 2f * omt) +
            c[2] * (-3f * t2 + 2f * t) +
            c[3] * (t2);
    }

    public static float2 GetNormal(NativeSlice<float2> c, in float t) {
        float2 tangent = math.normalize(GetTangent(c, t));
        return new float2(-tangent.y, tangent.x);
    }

    public static float GetLength(NativeSlice<float2> c, in int steps) {
        float dist = 0;

        float2 pPrev = c[0];
        for (int i = 1; i <= steps; i++) {
            float t = i / (float)steps;
            float2 p = Get(c, t);
            dist += math.length(p - pPrev);
            pPrev = p;
        }

        return dist;
    }

    public static float GetLength(NativeSlice<float2> c, in int steps, in float t) {
        float dist = 0;

        float2 pPrev = c[0];
        for (int i = 1; i <= steps; i++) {
            float tNow = t * (i / (float)steps);
            float2 p = Get(c, tNow);
            dist += math.length(p - pPrev);
            pPrev = p;
        }

        return dist;
    }

    public static float GetLength(NativeSlice<float> distances, float t) {
        t = t * (float)(distances.Length - 1);
        int ti = (int)math.floor(t);
        if (ti >= distances.Length - 1) {
            return distances[distances.Length - 1];
        }
        return math.lerp(distances[ti], distances[ti + 1], t - (float)ti);
    }

    // Instead of storing at linear t spacing, why not store with non-linear t-spacing and lerp between them
    public static void CacheDistances(NativeSlice<float2> c, NativeSlice<float> outDistances) {
        float dist = 0;
        outDistances[0] = 0f;
        float2 pPrev = c[0];
        for (int i = 1; i < outDistances.Length; i++) {
            float t = i / (float)(outDistances.Length - 1);
            float2 p = Get(c, t);
            dist += math.length(p - pPrev);
            outDistances[i] = dist;
            pPrev = p;
        }
    }
}

public static class BDCQuadratic3d {
    /* === Curves === */

    public static void Split(NativeSlice<float3> o, in float t, NativeSlice<float3> left, NativeSlice<float3> right) {
        float3 ab = math.lerp(o[0], o[1], t);
        float3 bc = math.lerp(o[1], o[2], t);
        float3 abbc = math.lerp(ab, bc, t);

        left[0] = o[0];
        left[1] = ab;
        left[2] = abbc;

        right[0] = abbc;
        right[1] = bc;
        right[2] = o[2];
    }

    /*
        Todo: index buffer handling
     */
    public static void SplitPiecewisePatch(in NativeSlice<float3> o, in NativeSlice<int> oi, NativeSlice<float3> n, in NativeSlice<int> ni, in float2 uvSplit) {
        for (int v = 0; v < 3; v++) {
            // assign horizontal intermediates to quadrants

            int row = v * 3;
            float3 ab = math.lerp(o[oi[row + 0]], o[oi[row + 1]], uvSplit.x);
            float3 bc = math.lerp(o[oi[row + 1]], o[oi[row + 2]], uvSplit.x);
            float3 abbc = math.lerp(ab, bc, uvSplit.x);

            int h = v*2;
            // n[ni[h * 5 + 0]] = o[oi[v*3]];
            // n[ni[h * 5 + 1]] = ab;
            // n[ni[h * 5 + 2]] = abbc;
            // n[ni[h * 5 + 3]] = bc;
            // n[ni[h * 5 + 4]] = o[oi[(v+1)*3-1]];

            n[h * 5 + 0] = o[oi[v * 3]];
            n[h * 5 + 1] = ab;
            n[h * 5 + 2] = abbc;
            n[h * 5 + 3] = bc;
            n[h * 5 + 4] = o[oi[(v + 1) * 3 - 1]];
        }

        for (int h = 0; h < 5; h++) {
            // Interpolate the horizontal results vertically
            // assign intermediates to quadrants

            float3 ab = math.lerp(n[ni[(0 * 5) + h]], n[ni[(2 * 5) + h]], uvSplit.y);
            float3 bc = math.lerp(n[ni[(2 * 5) + h]], n[ni[(4 * 5) + h]], uvSplit.y);
            float3 abbc = math.lerp(ab, bc, uvSplit.y);

            // split[(0 * 5) + h] = o[0];
            n[(1 * 5) + h] = ab;
            n[(2 * 5) + h] = abbc;
            n[(3 * 5) + h] = bc;
            // split[(4 * 5) + h] = o[2];
        }
    }

    public static float3 Get(NativeSlice<float3> c, in float t) {
        float3 u = 1f - t;
        return u * u * c[0] + 2f * t * u * c[1] + t * t * c[2];
    }

    public static float3 GetTangent(NativeSlice<float3> c, in float t) {
        float3 a0 = 2f * c[1];
        return t * (-a0 + 2 * c[2]) + (1 - t) * (a0 - 2f * c[0]);
    }

    public static float LengthEuclidApprox(NativeSlice<float> distances, float t) {
        t = t * (float)(distances.Length - 1);
        int ti = (int)math.floor(t);
        if (ti >= distances.Length - 1) {
            return distances[distances.Length - 1];
        }
        return math.lerp(distances[ti], distances[ti + 1], t - (float)ti);
    }

    /* === Quad Surface === */

    public static float3 GetQuad(NativeSlice<float3> c, in float2 uv) {
        float3 p = float3.zero;

        float3 blend_u = Bernstein.Quadratic(uv.x);
        float3 blend_v = Bernstein.Quadratic(uv.y);

        for (int v = 0; v < 3; v++) {
            for (int u = 0; u < 3; u++) {
                p += c[v * 3 + u] * (blend_u[u] * blend_v[v]);
            }
        }

        return p;
    }

    public static void GetQuadDU(NativeSlice<float3> c, NativeSlice<float3> du) {
        for (int v = 0; v < 3; v++) {
            for (int u = 0; u < 2; u++) {
                du[v * 2 + u] = 2f * (c[v * 3 + u] - c[v * 3 + (u + 1)]);
            }
        }
    }

    public static void GetQuadDV(NativeSlice<float3> c, NativeSlice<float3> dv) {
        for (int v = 0; v < 2; v++) {
            for (int u = 0; u < 3; u++) {
                dv[v * 3 + u] = 2f * (c[v * 3 + u] - c[(v + 1) * 3 + u]);
            }
        }
    }

    public static float3 EvaluateQuadDU(NativeSlice<float3> du, float2 uv) {
        float2 blend_u = Bernstein.Linear(uv.x);
        float3 blend_v = Bernstein.Quadratic(uv.y);

        float3 result = float3.zero;
        for (int v = 0; v < 3; v++) {
            for (int u = 0; u < 2; u++) {
                result += du[v * 2 + u] * (blend_u[u] * blend_v[v]);
            }
        }
        return result;
    }

    public static float3 EvaluateQuadDV(NativeSlice<float3> dv, float2 uv) {
        float3 blend_u = Bernstein.Quadratic(uv.x);
        float2 blend_v = Bernstein.Linear(uv.y);

        float3 result = float3.zero;
        for (int v = 0; v < 2; v++) {
            for (int u = 0; u < 3; u++) {
                result += dv[v * 3 + u] * (blend_u[u] * blend_v[v]);
            }
        }
        return result;
    }

    public static float3 GetQuadNormal(NativeSlice<float3> p, in float2 uv) {
        float u = uv[0];
        float v = uv[1];

        float a0 = 2 * u;
        float a1 = math.pow(v, 2);
        float a2 = a1 * p[7].y;
        float a3 = a0 * a1;
        float a4 = a0 - 2;
        float a5 = a1 * a4;
        float a6 = 4 * u * v;
        float a7 = 1 - v;
        float a8 = a7 * p[4].y;
        float a9 = a6 * a7;
        float a10 = math.pow(a7, 2);
        float a11 = a10 * p[1].y;
        float a12 = a0 * a10;
        float a13 = 1 - u;
        float a14 = 2 * a13;
        float a15 = a10 * a4;
        float a16 = 2 * v;
        float a17 = a16 * a4 * a7;
        float a18 = 4 * a13;
        float a19 = a18 * a8;
        float a20 = -a0 * a11 - a0 * a2 + a11 * a14 + a12 * p[2].y + a14 * a2 + a15 * p[0].y + a17 * p[3].y + a19 * v + a3 * p[8].y + a5 * p[6].y - a6 * a8 + a9 * p[5].y;
        float a21 = math.pow(u, 2);
        float a22 = a21 * p[5].z;
        float a23 = a16 * a21;
        float a24 = a16 - 2;
        float a25 = a21 * a24;
        float a26 = a13 * a6;
        float a27 = math.pow(a13, 2);
        float a28 = a27 * p[3].z;
        float a29 = 2 * a7;
        float a30 = a16 * a27;
        float a31 = a24 * a27;
        float a32 = a0 * a13 * a24;
        float a33 = a18 * a7;
        float a34 = a33 * p[4].z;
        float a35 = -a16 * a22 - a16 * a28 + a22 * a29 + a23 * p[8].z + a25 * p[2].z - a26 * p[4].z + a26 * p[7].z + a28 * a29 + a30 * p[6].z + a31 * p[0].z + a32 * p[1].z + a34 * u;
        float a36 = a21 * a29;
        float a37 = a27 * a29;
        float a38 = a19 * u - a23 * p[5].y + a23 * p[8].y + a25 * p[2].y - a26 * p[4].y + a26 * p[7].y - a30 * p[3].y + a30 * p[6].y + a31 * p[0].y + a32 * p[1].y + a36 * p[5].y + a37 * p[3].y;
        float a39 = a1 * a14;
        float a40 = a10 * a14;
        float a41 = -a12 * p[1].z + a12 * p[2].z + a15 * p[0].z + a17 * p[3].z - a3 * p[7].z + a3 * p[8].z + a34 * v + a39 * p[7].z + a40 * p[1].z + a5 * p[6].z - a9 * p[4].z + a9 * p[5].z;
        float a42 = a33 * p[4].x;
        float a43 = -a23 * p[5].x + a23 * p[8].x + a25 * p[2].x - a26 * p[4].x + a26 * p[7].x - a30 * p[3].x + a30 * p[6].x + a31 * p[0].x + a32 * p[1].x + a36 * p[5].x + a37 * p[3].x + a42 * u;
        float a44 = -a12 * p[1].x + a12 * p[2].x + a15 * p[0].x + a17 * p[3].x - a3 * p[7].x + a3 * p[8].x + a39 * p[7].x + a40 * p[1].x + a42 * v + a5 * p[6].x - a9 * p[4].x + a9 * p[5].x;

        /*--------------solutions------------------*/

        return new float3(
        a20 * a35 - a38 * a41,
        -a35 * a44 + a41 * a43,
        -a20 * a43 + a38 * a44);
    }

    /* === Triangular Surface === */

    /*
        Note: UV here is barycentric
    */
    public static float3 GetTriangle(NativeSlice<float3> p, in float2 uv) {
        float u = uv[0];
        float v = uv[1];
        float w = 1f - u - v;

        /*----------------terms-------------------*/

        float a0 = 2.0f * w;
        float a1 = a0 * v;
        float a2 = a0 * u;
        float a3 = 2.0f * u * v;
        float a4 = 1.0f * math.pow(w, 2);
        float a5 = 1.0f * math.pow(v, 2);
        float a6 = 1.0f * math.pow(u, 2);

        /*--------------solutions------------------*/

        return a1 * p[1] + a2 * p[3] + a3 * p[4] + a4 * p[0] + a5 * p[2] + a6 * p[5];
    }
}


public static class BDCQuadratic2d {
    public static float2 GetCasteljau(in float2 a, in float2 b, in float2 c, in float t) {
        return math.lerp(math.lerp(a, b, t), math.lerp(b, c, t), t);
    }

    public static float2 Get(NativeSlice<float2> c, in float t) {
        float u = 1f - t;
        float t2 = t * t;
        return
            c[0] * (u * u) +
            c[1] * (2f * t * u) +
            c[2] * t2;
    }

    public static float2 GetTangent(NativeSlice<float2> c, in float t) {
        float2 a0 = 2f * c[1];
        return t * (-a0 + 2 * c[2]) + (1 - t) * (a0 - 2f * c[0]);
    }

    public static float2 GetNormal(NativeSlice<float2> c, in float t) {
        float2 tng = GetTangent(c, t);
        return new float2(-tng.y, tng.x);
    }

    public static float2 Get(NativeSlice<float2> c, NativeSlice<float> w, in float t) {
        float u = 1f - t;
        float t2 = t * t;
        return
            c[0] * w[0] * (u * u) +
            c[1] * w[1] * (2f * t * u) +
            c[2] * w[2] * t2;
    }

    public static float GetLength(NativeSlice<float2> c, in int steps, in float t) {
        float dist = 0;

        float2 pPrev = c[0];
        for (int i = 1; i <= steps; i++) {
            float tNow = t * (i / (float)steps);
            float2 p = Get(c, tNow);
            dist += math.length(p - pPrev);
            pPrev = p;
        }

        return dist;
    }
}

public static class BDCLinear2d {
    public static ValueTuple<float2> GetSurface(NativeSlice<ValueTuple<float2>> c, in float2 uv) {
        float2 blend_u = Bernstein.Linear(uv.x);
        float2 blend_v = Bernstein.Linear(uv.y);

        ValueTuple<float2> result = new ValueTuple<float2>();

        for (int v = 0; v < 2; v++) {
            for (int u = 0; u < 2; u++) {
                var p = c[v * 2 + u];
                var blend = blend_u[u] * blend_v[v];
                result.a += p.a * blend;
                result.b += p.b * blend;
            }
        }

        return result;
    }
}

public static class BDCLinear3d {
    public static float3 GetSurface(NativeSlice<float3> c, in float2 uv) {
        float2 blend_u = Bernstein.Linear(uv.x);
        float2 blend_v = Bernstein.Linear(uv.y);

        float3 pos = float3.zero;

        for (int v = 0; v < 2; v++) {
            for (int u = 0; u < 2; u++) {
                var p = c[v * 2 + u];
                var blend = blend_u[u] * blend_v[v];
                pos += p * blend;
            }
        }

        return pos;
    }

    public static ValueTuple<float3> GetSurface(NativeSlice<ValueTuple<float3>> c, in float2 uv) {
        float2 blend_u = Bernstein.Linear(uv.x);
        float2 blend_v = Bernstein.Linear(uv.y);

        ValueTuple<float3> result = new ValueTuple<float3>();

        for (int v = 0; v < 2; v++) {
            for (int u = 0; u < 2; u++) {
                var p = c[v * 2 + u];
                var blend = blend_u[u] * blend_v[v];
                result.a += p.a * blend;
                result.b += p.b * blend;
            }
        }

        return result;
    }
}