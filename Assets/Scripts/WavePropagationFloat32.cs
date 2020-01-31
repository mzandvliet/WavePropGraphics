using UnityEngine;
using Unity.Burst;
using Unity.Jobs;
using Unity.Collections;
using Unity.Mathematics;
using Rng = Unity.Mathematics.Random;
using System.Runtime.CompilerServices;
using Unity.Collections.LowLevel.Unsafe;

/*
Should cubic interpolation be a major benefit to a lot of systems,
then we should consider supporting easy and efficient use of it.

Part of the work is operating on the data once you have it in a form
that plugs into your calculation. But the part before it is just as
important: how do you get your data?

It occured to me that storing memory in easily addressable 4x4 chunks
of height data means that you can feed this algorithm very fast. Certainly
as opposed to current naive implementation, which goes through an expensive
function to generate the address for it to read from. Instead we want to
just be like: here it is. Here is one pointer, and you'll find all the others
just where you expect them to be. The pointer becomes the key, the key to
the start of a sequence.

Asking once is polite.
Asking twice is ok if you really must.
Asking thrice is getting on the nerves a bit.
Asking four times, maybe go get your coat?
 */

namespace Waves {
    public struct Octave : System.IDisposable {
        public DoubleDuffer<float> buffer;

        public Octave(int resolution, int ticksPerFrame, Allocator allocator) {
            buffer = new DoubleDuffer<float>(resolution * resolution, allocator);
        }

        public void Dispose() {
            buffer.Dispose();
        }
    }

    public struct DoubleDuffer<T> : System.IDisposable where T : struct {
        private NativeArray<T> bufferA;
        private NativeArray<T> bufferB;

        public int Length { get => bufferA.Length; }

        public DoubleDuffer(int resolution, Allocator allocator) {
            bufferA = new NativeArray<T>(resolution, allocator, NativeArrayOptions.ClearMemory);
            bufferB = new NativeArray<T>(resolution, allocator, NativeArrayOptions.ClearMemory);
        }

        public NativeArray<T> GetBuffer(int idx) {
            return idx == 0 ? bufferA : bufferB;
        }

        public void Dispose() {
            bufferA.Dispose();
            bufferB.Dispose();
        }
    }

    public struct WaveSampler {
        [ReadOnly] public NativeArray<float> buffer;
        [ReadOnly] public NativeArray<int> tileMap;
        [ReadOnly] public readonly float heightScale;

        public WaveSampler(NativeArray<float> buffer, NativeArray<int> tileMap, float heightScale) {
            this.buffer = buffer;
            this.tileMap = tileMap;
            this.heightScale = heightScale;
        }

        /*
        Slow, naive, ad-hoc sampling function

        Generates height values in normalized float range, [0,1]
        x,y are in world units, meters

        Todo: respect tileMap indirection, worldshifting, etc.
        */
        public float3 Sample(float x, float z) {
            const float offset = (float)-(32768 / 2);
            const float horScale = 1f / 64f;

            x -= offset;
            z -= offset;

            x *= horScale;
            z *= horScale;

            int xFloor = (int)math.floor(x);
            int zFloor = (int)math.floor(z);

            // Clamp?
            // xFloor = math.clamp(xFloor, 0, WaveSimulator.RES-2);
            // zFloor = math.clamp(zFloor, 0, WaveSimulator.RES-2);

            if (xFloor < 1 || xFloor >= WaveSimulator.RES-2 || zFloor < 1 || zFloor >= WaveSimulator.RES-2) {
                return 0.5f;
            }

            float xFrac = x - (float)xFloor;
            float zFrac = z - (float)zFloor;

            var samples = new NativeArray<float4>(4, Allocator.Temp);

            /* Todo:
            -generate this address sequence more efficiently

            - simpler function starting from single pointer (like NativeSlice)

            ...or perhaps even better, store in Morton Blocks of 4x4
            coefficients? In that case, no copy or function needed,
            bicubic can work directly on the stored data.

            Slice.Reinterpret<float4> could be useful

            NORMALS! Generate the here using the interpolated data, and
            the gradient of the Cubic Hermite function! Done. :)

            Then, the TerrainSystem no longer has to do its own
            bilinear filtering, and the whole thing is better.
            */
            for (int zk = -1; zk < 3; zk++) {
                samples[1+zk] = new float4(
                    buffer[WaveSimulator.Idx(xFloor - 1, zFloor + zk)],
                    buffer[WaveSimulator.Idx(xFloor + 0, zFloor + zk)],
                    buffer[WaveSimulator.Idx(xFloor + 1, zFloor + zk)],
                    buffer[WaveSimulator.Idx(xFloor + 2, zFloor + zk)]
                );
            }

            // float height = Erp.BiLerp3(samples, xFrac, zFrac);
            float height = Erp.BiLerp3_sympy(samples, zFrac, xFrac);
            float2 tangents = Erp.BiLerp3_deriv_sympy(samples, zFrac, xFrac);

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


        public static float Lerp3_deriv(float4 p, float x) {
            /*----------------terms-------------------*/

            float a0 = (-1.0f / 2.0f) * p[0];
            float a1 = (1.0f / 2.0f) * p[3];

            /*--------------solutions------------------*/

            float output_0 = a0 + (1.0f / 2.0f) * p[2] + 3f * x * x * (a0 + a1 + (3.0f / 2.0f) * p[1] - (3.0f / 2.0f) * p[2]) + 2f * x * (-a1 + p[0] - (5.0f / 2.0f) * p[1] + 2 * p[2]);
            return output_0;
        }

        public static float2 BiLerp3_deriv(NativeSlice<float4> p, float x, float z) {
            float4 arr = new float4();
            arr[0] = Lerp3(p[0], x);
            arr[1] = Lerp3(p[1], x);
            arr[2] = Lerp3(p[2], x);
            arr[3] = Lerp3(p[3], x);

            float dz = Lerp3_deriv(arr, z);

            arr[0] = Lerp3(new float4(p[0][0], p[1][0], p[2][0], p[3][0]), z);
            arr[1] = Lerp3(new float4(p[0][1], p[1][1], p[2][1], p[3][1]), z);
            arr[2] = Lerp3(new float4(p[0][2], p[1][2], p[2][2], p[3][2]), z);
            arr[3] = Lerp3(new float4(p[0][3], p[1][3], p[2][3], p[3][3]), z);

            float dx = Lerp3_deriv(arr, x);

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

        public static float2 BiLerp3_deriv_sympy(NativeSlice<float4> p, float u, float v) {

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

    public class WaveSimulator : System.IDisposable {
        private Octave _octave;

        public const int RES = 512;
        public const int TILE_RES = 16;
        public const int TILES_PER_DIM = RES / TILE_RES;
        private float _heightScale;

        private NativeArray<int> _tileMap; // Todo: use for storing tile addresses when implementing world shift

        const int TICKSPERFRAME = 1;
        const int NUMOCTAVES = 1;

        private Texture2D _screenTex;

        private uint _tick = 0;

        private JobHandle _simHandle;
        private JobHandle _renderHandle;

        public WaveSimulator(float heightScale) {
            _heightScale = heightScale;
            
            _octave = new Octave(RES, TICKSPERFRAME, Allocator.Persistent);
            _tileMap = new NativeArray<int>(TILES_PER_DIM * TILES_PER_DIM, Allocator.Persistent);

            _screenTex = new Texture2D(RES, RES, TextureFormat.RG16, false, true);
            _screenTex.filterMode = FilterMode.Point;
        }

        public void Dispose() {
            _simHandle.Complete();
            _renderHandle.Complete();

            _octave.Dispose();
            _tileMap.Dispose();
        }

        public WaveSampler GetSampler() {
            return new WaveSampler(
                _octave.buffer.GetBuffer(buffIdx1),
                _tileMap,
                _heightScale
            );
        }

        private int buffIdx0 = 0;
        private int buffIdx1 = 1;

        public void StartUpdate() {
            var scaleFactor = 1f;
            var simConfig = new SimConfig(
                scaleFactor,            // double per octave
                0.15f * scaleFactor,    // dcd
                0.0175f,                // dt
                0.9f                    // C
            );

            var simHandle = new JobHandle();
            for (int i = 0; i < TICKSPERFRAME; i++) {
                buffIdx0 = (buffIdx0 + 1) % 2;
                buffIdx1 = (buffIdx0 + 1) % 2;

                var impulseJob = new AddRandomImpulsesJob
                {
                    tick = _tick,
                    curr = _octave.buffer.GetBuffer(buffIdx0),
                    prev = _octave.buffer.GetBuffer(buffIdx1),
                };
                simHandle = impulseJob.Schedule(simHandle);

                var simTileJob = new PropagateJob
                {
                    config = simConfig,
                    tick = _tick,
                    curr = _octave.buffer.GetBuffer(buffIdx0),
                    next = _octave.buffer.GetBuffer(buffIdx1),
                };
                simHandle = simTileJob.ScheduleBatch((RES-2) * (RES-2), RES-2, simHandle);

                var simBoundsJob = new PropagateBoundariesJob {
                    config = simConfig,
                    tick = _tick,
                    curr = _octave.buffer.GetBuffer(buffIdx0),
                    next = _octave.buffer.GetBuffer(buffIdx1),
                };
                simHandle = simBoundsJob.Schedule(simHandle);

                _tick++;
            }
            simHandle.Complete();
        }

        public void CompleteUpdate() {
            _simHandle.Complete();
        }

        public void StartRender() {
            var texture = _screenTex.GetRawTextureData<byte2>();
            var waveData = _octave.buffer.GetBuffer(buffIdx1);
            var renderJob = new RenderJobParallel
            {
                buf = waveData,
                texture = texture
            };
            _renderHandle = renderJob.Schedule(RES * RES, 32, _renderHandle);
        }

        public void CompleteRender() {
            _renderHandle.Complete();
            _screenTex.Apply(false);
        }

        public void OnDrawGUI() {
            float size = math.min(Screen.width, Screen.height) * 0.5f;
            GUI.DrawTexture(new Rect(0f, 0f, size, size), _screenTex, ScaleMode.ScaleToFit);
        }

        public struct SimConfig {
            public readonly float scaleFactor; // double or tripple per octave
            public readonly float dcd;
            public readonly float dt;
            public readonly float C;
            public readonly float R;

            // Cached derived values, todo: calculate in constructor
            public readonly float rSqr;
            public readonly float rMinusOne;
            public readonly float rPlusOne;
            public readonly float rSqrx2;

            public SimConfig(float scaleFactor, float dcd, float dt, float C) {
                this.scaleFactor = scaleFactor;
                this.dcd = dcd;
                this.dt = dt;
                this.C = C;

                R = C * dt / dcd;
                rSqr = R * R;
                rMinusOne = R - 1f;
                rPlusOne = R + 1f;
                rSqrx2 = rSqr * 2f;
            }
        }

        [BurstCompile]
        public struct AddRandomImpulsesJob : IJob {
            public NativeArray<float> prev;
            public NativeArray<float> curr;
            public uint tick;

            public void Execute() {
                /*
                 Todo: 
                 - Ensure smoothness at given scale and resolution
                 - Still shows square-ish artifacts
                 - Fixed point arithmetic instead of float?
                */

                Rng rng;
                unchecked {
                    rng = new Rng(0x816EFB5Du + tick * 0x7461CA0Du);
                }

                int2 pos = new int2(rng.NextInt(RES), rng.NextInt(RES/3));

                float strength = rng.NextFloat(0f, 1f);

                int radius = 7 + 2*((int)math.round(strength * 16f)); // odd-numbered radius
                float amplitude = rng.NextFloat(-0.5f, 0.5f) * strength;
                float radiusInv = 1f / (float)(radius-1);
                float rippleFreq = (math.PI * 2f * 0.025f);

                const int impulsePeriod = 64;
                if (tick == 0 || rng.NextInt(impulsePeriod) == 0) {
                    for (int y = -radius; y <= radius; y++) {
                        for (int x = -radius; x <= radius; x++) {
                            int xIdx = pos.x + x;
                            int yIdx = pos.y + y;

                            if (xIdx < 0 || xIdx >= RES || yIdx < 0 || yIdx >= RES) {
                                continue;
                            }

                            float r = math.sqrt(x * x + y * y);
                            float interp = math.smoothstep(1f, 0f, r * radiusInv);
                            int idx = Idx(xIdx, yIdx);

                            float perturb = math.cos(r * rippleFreq) * interp * amplitude;

                            curr[idx] = curr[idx] + perturb * 0.66f;
                            prev[idx] = prev[idx] + perturb * 0.33f;
                        }
                    }
                }
            }
        }

        /*
        Per octave:

        dcd = 0.15f * scaleFactor; // delta cell distance, with consistent relative scale between octaves
        dt = 0.0175; // step time. arbitrary scale, but needs to respect tick frequency. Half freq = double dt.
        C = 0.98f; // Viscocity constant
        R = C * dt / dcd; // Viscocity per timestep, normalized by delta cell distance

        rSqr = R * R // optimization

        rMinusOne = R - 1f;
        rPlusOne = R + 1f;
        rSqrx2 = rSqr * 2f;

        Regular calculation pattern:
        
        next[edge] =
            (2f * curr[cell] - prev[cell]) +
            rSqr * (
                curr[cell+up] +
                curr[cell+down] +
                curr[cell+up] +
                curr[cell+down] -
                4f * curr[cell]);

        Edge/Boundary calculation pattern:

        next[edge] = (
            2f * curr[edge] + rMinusOne * prev[edge] +
            rSqrx2 * (curr[neighbor] - curr[edge])
        ) / rPlusOne;

        e.g.:

        next[edge] = (
            2f * curr[edge] + (0.98 * 0.0175 / 0.15 - 1) * prev[edge] +
            ((0.98 * 0.0175 / 0.15)^2 - 1) * (curr[neighbor] - curr[edge])
        ) / ((0.98 * 0.0175 / 0.15)^2 + 1);

        next[edge] = (
            2f * curr[edge] + 0.11433333 * prev[edge] +
            -1.013072111 * (curr[neighbor] - curr[edge])
        ) / 1.013072111;
        */

        [BurstCompile]
        public struct PropagateJob : IJobParallelForBatch {
            [ReadOnly] public SimConfig config;

            [ReadOnly] public uint tick;
            [ReadOnly] public NativeArray<float> curr;
            [NativeDisableParallelForRestriction] public NativeArray<float> next;

            public void Execute(int startIndex, int count) {
                // Inner part
                for (int i = startIndex; i < startIndex + count; i++) {
                    int2 c = new int2(1,1) + Coord(i, RES-2);
                    int idx = Idx(c.x, c.y);

                    float spatial = config.rSqr * (
                        curr[Idx(c.x - 1, c.y + 0)] +
                        curr[Idx(c.x + 1, c.y + 0)] +
                        curr[Idx(c.x + 0, c.y - 1)] +
                        curr[Idx(c.x + 0, c.y + 1)] -
                        curr[idx] * 4
                    );

                    float temporal = 2 * curr[idx] - next[idx];

                    float v = spatial + temporal;

                    // Symmetric clamping as a safeguard
                    const float ceiling = .995f;
                    v = math.clamp(v, -ceiling, ceiling);

                    next[idx] = v;
                }
            }
        }

        [BurstCompile]
        public struct PropagateBoundariesJob : IJob {
            [ReadOnly] public SimConfig config;

            [ReadOnly] public uint tick;
            [ReadOnly] public NativeArray<float> curr;
            [NativeDisableParallelForRestriction] public NativeArray<float> next;

            public void Execute() {
                // Open boundary condition

                // Bottom
                for (int i = 0; i < RES - 1; i++) {
                    int2 c = new int2(i, 0);
                    int idx = Idx(c.x, c.y);

                    float v = (
                        2f * curr[idx] + config.rMinusOne * next[idx] +
                        config.rSqrx2 * (curr[Idx(c.x, c.y + 1)] - curr[idx])
                    ) / config.rPlusOne;

                    // Symmetric clamping as a safeguard
                    const float ceiling = 1f;
                    v = math.clamp(v, -ceiling, ceiling);

                    next[idx] = v;
                }

                // Top
                for (int i = 1; i < RES; i++) {
                    int2 c = new int2(i, RES - 1);
                    int idx = Idx(c.x, c.y);

                    float v = (
                        2f * curr[idx] + config.rMinusOne * next[idx] +
                        config.rSqrx2 * (curr[Idx(c.x, c.y - 1)] - curr[idx])
                    ) / config.rPlusOne;

                    // Symmetric clamping as a safeguard
                    const float ceiling = 1f;
                    v = math.clamp(v, -ceiling, ceiling);

                    next[idx] = v;
                }

                // Left
                for (int i = 0; i < RES - 1; i++) {
                    int2 c = new int2(0, i);
                    int idx = Idx(c.x, c.y);

                    float v = (
                        2f * curr[idx] + config.rMinusOne * next[idx] +
                        config.rSqrx2 * (curr[Idx(c.x + 1, c.y)] - curr[idx])
                    ) / config.rPlusOne;

                    // Symmetric clamping as a safeguard
                    const float ceiling = 1f;
                    v = math.clamp(v, -ceiling, ceiling);

                    next[idx] = v;
                }

                // Right
                for (int i = 1; i < RES; i++) {
                    int2 c = new int2(RES - 1, i);
                    int idx = Idx(c.x, c.y);

                    float v = (
                        2f * curr[idx] + config.rMinusOne * next[idx] +
                        config.rSqrx2 * (curr[Idx(c.x - 1, c.y)] - curr[idx])
                    ) / config.rPlusOne;

                    // Symmetric clamping as a safeguard
                    const float ceiling = 0.95f;
                    v = math.clamp(v, -ceiling, ceiling);

                    next[idx] = v;
                }
            }
        }

        public struct byte2 {
            public byte r;
            public byte g;

            public byte2(byte r, byte g) {
                this.r = r;
                this.g = g;
            }
        }

        [BurstCompile]
        public struct RenderJobParallel : IJobParallelFor {
            [ReadOnly] public NativeArray<float> buf;
            [WriteOnly] public NativeArray<byte2> texture;

            public void Execute(int i) {
                int2 c = Coord(i);
                int idx = Idx(c.x, c.y);
                float sample = buf[idx];

                // var scaled = (sample * 256f);
                // var pos = math.clamp(scaled, 0, 256f);
                // var neg = math.clamp(-scaled, 0, 256f);
                // var color = new byte2(
                //     (byte)neg,
                //     (byte)pos
                // );

                var color = new byte2(
                    (byte) ((0.5f + 0.5f * sample) * 255f),
                    0
                );

                texture[i] = color;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int TileIdx(int x, int y) {
            int tileX = x / TILE_RES;
            int tileY = y / TILE_RES;
            int pixelX = x % TILE_RES;
            int pixelY = y % TILE_RES;

            int tileAddr = Morton.Code2d(tileX, tileY) << 8;
            // note: shift by 8 assumes TILE_RES = 16

            return tileAddr;
        }

        // Index TILE_RES*TILE_RES tiles, who's base addresses are in Morton order
        // Todo: precalculate tile's morton address once per job
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Idx(int x, int y) {
            int tileX = x >> 4;
            int tileY = y >> 4;
            int pixelX = x - (tileX << 4);
            int pixelY = y - (tileY << 4);

            int tileAddr = Morton.Code2d(tileX, tileY);
            int pixelAddr = (tileAddr << 8) | ((pixelY << 4) + pixelX);

            // Working-but-slow:

            // int tileX = x / TILE_RES;
            // int tileY = y / TILE_RES;
            // int pixelX = x % TILE_RES;
            // int pixelY = y % TILE_RES;

            // int tileAddress = Morton.Code2d(tileX, tileY);
            // int pixelAddress = (tileAddress << 8) | (pixelY * TILE_RES + pixelX);
            // note: shift by 8 assumes TILE_RES = 16

            // Identity transform for testing:

            // int tileAddress = (tileY * TILES_PER_DIM + tileX) * (TILE_RES*TILE_RES);
            // int pixelAddress = tileAddress + (pixelY * TILE_RES + pixelX);

            return pixelAddr;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int2 Coord(int i) {
            return new int2(i % RES, i / RES);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int2 Coord(int i, int res) {
            return new int2(i % res, i / res);
        }
    }
}