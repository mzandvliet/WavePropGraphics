

using Unity.Collections;
using Unity.Mathematics;
using Waves;

public struct WaveSampler {
    [ReadOnly] public NativeArray<float> buffer;
    [ReadOnly] public readonly float heightScale;

    public WaveSampler(NativeArray<float> buffer, float heightScale) {
        this.buffer = buffer;
        this.heightScale = heightScale;
    }

    /*
    Slow, naive, ad-hoc sampling function

    Generates height values in normalized float range, [-1,1]
    x,y are in world units, meters

    Todo: respect tileMap indirection, worldshifting, etc.
    */
    public float3 Sample(float x, float z) {
        const float horScale = 1f / 64f;
        const float offset = (float)((32768 / 2) * horScale);

        x *= horScale;
        z *= horScale;

        x += offset;
        z += offset;

        int xFloor = (int)math.floor(x);
        int zFloor = (int)math.floor(z);

        if (xFloor < 1 || xFloor >= WaveSimulator.RES - 2 || zFloor < 1 || zFloor >= WaveSimulator.RES - 2) {
            return 0.5f;
        }

        // float xFrac = x - (float)xFloor;
        // float zFrac = z - (float)zFloor;

        float xFrac = math.frac(x);
        float zFrac = math.frac(z);

        /*
        Nearest Neighbor, only for debugging, really
        */

        // float height = buffer[WaveSimulator.Idx(xFloor, zFloor)]; // simple nearest-neighbor test
        // float2 tangents = new float2(0,0);

        /* 
        Bilinear Interpolation
        */

        // var b = new float2(
        //      buffer[WaveSimulator.Idx(xFloor, zFloor)],
        //      buffer[WaveSimulator.Idx(xFloor + 1, zFloor)]
        // );
        // var t = new float2(
        //      buffer[WaveSimulator.Idx(xFloor, zFloor + 1)],
        //      buffer[WaveSimulator.Idx(xFloor + 1, zFloor + 1)]
        // );

        // float height = Erp.BiLerp(b, t, new float2(xFrac, zFrac));
        // float2 tangents = new float2(
        //     b.y - b.x,
        //     t.x - b.x);

        /* 
        Bicubic Interpolation

        Todo:

        - Figure out correct transforms to and from these spaces:

        World space domain:
        32768 meters wide
        1024 meters tall
        512px
        16px tiles
        4px kernels

        w/h = 32

        Local wave simulation domain:
        512 pixels wide, at abstract unit distances
        [-1,+1] tall

        w/h = 512

        ... aspect ratios differ by factor of 16, right?

        - figure out why my [u,v] indexing is flipped from expectations
        -generate this address sequence more efficiently
        - simpler function starting from single pointer (like NativeSlice)
        */

        var samples = new NativeArray<float4>(4, Allocator.Temp);
        for (int zk = -1; zk < 3; zk++) {
            samples[1 + zk] = new float4(
                buffer[WaveSimulator.Idx(xFloor - 1, zFloor + zk)],
                buffer[WaveSimulator.Idx(xFloor + 0, zFloor + zk)],
                buffer[WaveSimulator.Idx(xFloor + 1, zFloor + zk)],
                buffer[WaveSimulator.Idx(xFloor + 2, zFloor + zk)]
            );
        }

        // float height = Erp.BiLerp3(samples, xFrac, zFrac);
        // float2 tangents = Erp.BiLerp3_Grad(samples, xFrac, zFrac);

        float height = Erp.BiLerp3_sympy(samples, zFrac, xFrac);
        // float2 tangents = Erp.BiLerp3_Grad_sympy(samples, zFrac, xFrac);

        float2 tangents = float2.zero;

        return new float3(height, tangents);
    }
}

public static class Erp {
    public static float BiLerp(float2 b, float2 t, float2 frac) {
        return math.lerp(
            math.lerp(b.x, b.y, frac.x),
            math.lerp(t.x, t.y, frac.x),
            frac.y
        );
    }

    public static float Lerp3(float4 p, float x) {
        return p[1] + 0.5f * x * (p[2] - p[0] + x * (2.0f * p[0] - 5.0f * p[1] + 4.0f * p[2] - p[3] + x * (3.0f * (p[1] - p[2]) + p[3] - p[0])));
    }

    public static float BiLerp3(NativeSlice<float4> p, float x, float z) {
        float4 arr = new float4();
        arr[0] = Lerp3(p[0], x);
        arr[1] = Lerp3(p[1], x);
        arr[2] = Lerp3(p[2], x);
        arr[3] = Lerp3(p[3], x);
        return Lerp3(arr, z);
    }


    public static float Lerp3_Grad(float4 p, float x) {
        /*----------------terms-------------------*/

        float a0 = (-1.0f / 2.0f) * p[0];
        float a1 = (1.0f / 2.0f) * p[3];

        /*--------------solutions------------------*/

        float output_0 = a0 + (1.0f / 2.0f) * p[2] + 3f * x * x * (a0 + a1 + (3.0f / 2.0f) * p[1] - (3.0f / 2.0f) * p[2]) + 2f * x * (-a1 + p[0] - (5.0f / 2.0f) * p[1] + 2 * p[2]);
        return output_0;
    }

    public static float2 BiLerp3_Grad(NativeSlice<float4> p, float x, float z) {
        float4 arr = new float4();
        arr[0] = Lerp3_Grad(p[0], x);
        arr[1] = Lerp3_Grad(p[1], x);
        arr[2] = Lerp3_Grad(p[2], x);
        arr[3] = Lerp3_Grad(p[3], x);

        float dx = Lerp3(arr, z);

        arr[0] = Lerp3_Grad(new float4(p[0][0], p[1][0], p[2][0], p[3][0]), z);
        arr[1] = Lerp3_Grad(new float4(p[0][1], p[1][1], p[2][1], p[3][1]), z);
        arr[2] = Lerp3_Grad(new float4(p[0][2], p[1][2], p[2][2], p[3][2]), z);
        arr[3] = Lerp3_Grad(new float4(p[0][3], p[1][3], p[2][3], p[3][3]), z);

        float dz = Lerp3(arr, x);

        return new float2(dx, dz);
    }

    public static float BiLerp3_sympy(NativeSlice<float4> p, float u, float v) {
        /*----------------terms-------------------*/

        float a0 = -1.0f / 2.0f * p[0][1];
        float a1 = u * (a0 + (1.0f / 2.0f) * p[2][1]);
        float a2 = math.pow(u, 2);
        float a3 = -5.0f / 2.0f * p[1][1];
        float a4 = (1.0f / 2.0f) * p[3][1];
        float a5 = a2 * (a3 - a4 + p[0][1] + 2 * p[2][1]);
        float a6 = math.pow(u, 3);
        float a7 = (3.0f / 2.0f) * p[1][1];
        float a8 = a6 * (a0 + a4 + a7 - 3.0f / 2.0f * p[2][1]);
        float a9 = -1.0f / 2.0f * p[0][2];
        float a10 = u * (a9 + (1.0f / 2.0f) * p[2][2]);
        float a11 = (1.0f / 2.0f) * p[3][2];
        float a12 = a2 * (-a11 + p[0][2] - 5.0f / 2.0f * p[1][2] + 2 * p[2][2]);
        float a13 = (3.0f / 2.0f) * p[1][2];
        float a14 = a6 * (a11 + a13 + a9 - 3.0f / 2.0f * p[2][2]);
        float a15 = -1.0f / 2.0f * p[0][0];
        float a16 = u * (a15 + (1.0f / 2.0f) * p[2][0]);
        float a17 = (1.0f / 2.0f) * p[3][0];
        float a18 = a2 * (-a17 + p[0][0] - 5.0f / 2.0f * p[1][0] + 2 * p[2][0]);
        float a19 = a6 * (a15 + a17 + (3.0f / 2.0f) * p[1][0] - 3.0f / 2.0f * p[2][0]);
        float a20 = -1.0f / 2.0f * a16 - 1.0f / 2.0f * a18 - 1.0f / 2.0f * a19 - 1.0f / 2.0f * p[1][0];
        float a21 = (1.0f / 2.0f) * p[1][3];
        float a22 = -1.0f / 2.0f * p[0][3];
        float a23 = (1.0f / 2.0f) * u * (a22 + (1.0f / 2.0f) * p[2][3]);
        float a24 = (1.0f / 2.0f) * p[3][3];
        float a25 = (1.0f / 2.0f) * a2 * (-a24 + p[0][3] - 5.0f / 2.0f * p[1][3] + 2 * p[2][3]);
        float a26 = (1.0f / 2.0f) * a6 * (a22 + a24 + (3.0f / 2.0f) * p[1][3] - 3.0f / 2.0f * p[2][3]);

        /*--------------solutions------------------*/

        float output_0 = a1 + a5 + a8 + p[1][1] + math.pow(v, 3) * ((3.0f / 2.0f) * a1 - 3.0f / 2.0f * a10 - 3.0f / 2.0f * a12 - a13 - 3.0f / 2.0f * a14 + a20 + a21 + a23 + a25
        + a26 + (3.0f / 2.0f) * a5 + a7 + (3.0f / 2.0f) * a8) + math.pow(v, 2) * (-5.0f / 2.0f * a1 + 2 * a10 + 2 * a12 + 2 * a14 + a16 + a18 + a19 - a21 - a23 - a25 - a26 + a3 -
        5.0f / 2.0f * a5 - 5.0f / 2.0f * a8 + p[1][0] + 2 * p[1][2]) + v * ((1.0f / 2.0f) * a10 + (1.0f / 2.0f) * a12 + (1.0f / 2.0f) * a14 + a20 + (1.0f / 2.0f) * p[1][2]);

        return output_0;
    }

    public static float2 BiLerp3_Grad_sympy(NativeSlice<float4> p, float u, float v) {

        /*----------------terms-------------------*/

        float a0 = (1.0f / 2.0f) * p[3][2];
        float a1 = -a0 + p[0][2] - 5.0f / 2.0f * p[1][2] + 2 * p[2][2];
        float a2 = a1 * u;
        float a3 = -1.0f / 2.0f * p[0][2];
        float a4 = (3.0f / 2.0f) * p[1][2];
        float a5 = a0 + a3 + a4 - 3.0f / 2.0f * p[2][2];
        float a6 = math.pow(u, 2);
        float a7 = (3.0f / 2.0f) * a6;
        float a8 = (1.0f / 2.0f) * p[3][0];
        float a9 = -a8 + p[0][0] - 5.0f / 2.0f * p[1][0] + 2 * p[2][0];
        float a10 = a9 * u;
        float a11 = -1.0f / 2.0f * p[0][0];
        float a12 = a11 + a8 + (3.0f / 2.0f) * p[1][0] - 3.0f / 2.0f * p[2][0];
        float a13 = -a10 - a12 * a7 + (1.0f / 4.0f) * p[0][0] - 1.0f / 4.0f * p[2][0];
        float a14 = math.pow(v, 2);
        float a15 = (1.0f / 4.0f) * p[2][3];
        float a16 = (1.0f / 4.0f) * p[0][3];
        float a17 = (1.0f / 2.0f) * p[3][3];
        float a18 = -a17 + p[0][3] - 5.0f / 2.0f * p[1][3] + 2 * p[2][3];
        float a19 = a18 * u;
        float a20 = -5.0f / 2.0f * p[1][1];
        float a21 = (1.0f / 2.0f) * p[3][1];
        float a22 = a20 - a21 + p[0][1] + 2 * p[2][1];
        float a23 = a22 * u;
        float a24 = 3 * a6;
        float a25 = a5 * a6;
        float a26 = -1.0f / 2.0f * p[0][1];
        float a27 = (3.0f / 2.0f) * p[1][1];
        float a28 = a21 + a26 + a27 - 3.0f / 2.0f * p[2][1];
        float a29 = a28 * a6;
        float a30 = -1.0f / 2.0f * p[0][3];
        float a31 = a17 + a30 + (3.0f / 2.0f) * p[1][3] - 3.0f / 2.0f * p[2][3];
        float a32 = a31 * a7;
        float a33 = a11 + (1.0f / 2.0f) * p[2][0];
        float a34 = a26 + (1.0f / 2.0f) * p[2][1];
        float a35 = u * (a3 + (1.0f / 2.0f) * p[2][2]);
        float a36 = a1 * a6;
        float a37 = math.pow(u, 3);
        float a38 = a37 * a5;
        float a39 = (1.0f / 2.0f) * p[1][3];
        float a40 = a33 * u;
        float a41 = a34 * u;
        float a42 = (1.0f / 2.0f) * u * (a30 + (1.0f / 2.0f) * p[2][3]);
        float a43 = a6 * a9;
        float a44 = (1.0f / 2.0f) * a18 * a6;
        float a45 = a12 * a37;
        float a46 = a28 * a37;
        float a47 = (1.0f / 2.0f) * a31 * a37;
        float a48 = -1.0f / 2.0f * a40 - 1.0f / 2.0f * a43 - 1.0f / 2.0f * a45 - 1.0f / 2.0f * p[1][0];

        /*--------------solutions------------------*/

        float output_0 = a14 * (2 * a10 + a12 * a24 - a15 + a16 - a19 + 4 * a2 - 5 * a23 + 6 * a25 - 15.0f / 2.0f * a29 - a32 + a33 + (5.0f / 4.0f) * p[0][1] - p[0][2] - 5.0f / 4.0f * p[2][1] + p[2][2]) + 2 * a23 + a24 * a28 + a34 + math.pow(v, 3) * (a13 + a15 - a16 + a19 - 3 * a2 + 3 * a23 - 9.0f / 2.0f * a25 + (9.0f / 2.0f) * a29 + a32 - 3.0f / 4.0f * p[0][1] + (3.0f / 4.0f) * p[0][2] + (3.0f / 4.0f) * p[2][1] - 3.0f / 4.0f * p[2][2]) + v * (a13 + a2 + a5 * a7 - 1.0f / 4.0f * p[0][2] + (1.0f / 4.0f) * p[2][2]);
        float output_1 = 3 * a14 * (-a1 * a7 + a22 * a7 + a27 - 3.0f / 2.0f * a35 - 3.0f / 2.0f * a38 + a39 - a4 + (3.0f / 2.0f) * a41 + a42 + a44 + (3.0f / 2.0f) * a46 + a47 + a48) + (1.0f / 2.0f) * a35 + (1.0f / 2.0f) * a36 + (1.0f / 2.0f) * a38 + a48 + (1.0f / 2.0f) * p[1][2] + 2 * v * (a20 - 5.0f / 2.0f * a22 * a6 + 2 * a35 + 2 * a36 + 2 * a38 - a39 + a40 - 5.0f / 2.0f * a41 - a42 + a43 - a44 + a45 - 5.0f / 2.0f * a46 - a47 + p[1][0] + 2 * p[1][2]);

        return new float2(output_0, output_1);
    }
}