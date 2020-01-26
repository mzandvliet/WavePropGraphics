using UnityEngine;
using Unity.Burst;
using Unity.Jobs;
using Unity.Collections;
using Unity.Mathematics;
using Rng = Unity.Mathematics.Random;
using System.Runtime.CompilerServices;
using Unity.Collections.LowLevel.Unsafe;

namespace WavesBurstF32 {
    public struct Octave : System.IDisposable {
        public PingPongBuffer<float> buffer;

        public Octave(int resolution, int ticksPerFrame) {
            buffer = new PingPongBuffer<float>(resolution * resolution, Allocator.Persistent);
        }

        public void Dispose() {
            buffer.Dispose();
        }
    }

    /*
        Job-system-friendly way to swap buffers, where we swap indices
        instead of a traditional pointer swap.

        See: struct SwapJob<T> : IJob further below

        The only downside here is that we can no longer tell burst
        about [ReadOnly] and [WriteOnly] attributes, but let's not
        worry about that right now.
    */
    public struct PingPongBuffer<T> : System.IDisposable where T : struct {
        private NativeArray<T> bufferA;
        private NativeArray<T> bufferB;

        public int Length { get => bufferA.Length; }

        public PingPongBuffer(int resolution, Allocator allocator) {
            bufferA = new NativeArray<T>(resolution, allocator);
            bufferB = new NativeArray<T>(resolution, allocator);
        }

        public NativeArray<T> GetBuffer(int idx) {
            return idx == 0 ? bufferA : bufferB;
        }

        public void Dispose() {
            bufferA.Dispose();
            bufferB.Dispose();
        }
    }

    public class WavePropagationFloat32 : MonoBehaviour {
        private Octave[] _octaves;

        private const int RES = 128; // current limiting factor: if we set this too big we create cache misses
        private const int TILE_RES = 16;
        private const int TILE_NUM = RES / TILE_RES;

        const int TICKSPERFRAME = 1;
        const int NUMOCTAVES = 1;

        private Texture2D _screenTex;

        private uint _tick = 0;
        private JobHandle _renderHandle;

        private void Awake() {
            Application.targetFrameRate = 60;

            _octaves = new Octave[NUMOCTAVES];
            for (int i = 0; i < NUMOCTAVES; i++) {
                _octaves[i] = new Octave(RES, TICKSPERFRAME);
            }

            _screenTex = new Texture2D(RES, RES, TextureFormat.RG16, false, true);
            _screenTex.filterMode = FilterMode.Point;
        }

        private void OnDestroy() {
            _renderHandle.Complete();

            for (int i = 0; i < _octaves.Length; i++) {
                _octaves[i].Dispose();
            }
        }

        private int buffIdx0 = 0;
        private int buffIdx1 = 1;

        private void Update() {
           
            var octave = _octaves[0];

            var simHandle = new JobHandle();
            for (int i = 0; i < TICKSPERFRAME; i++) {
                buffIdx0 = (buffIdx0 + 1) % 2;
                buffIdx1 = (buffIdx0 + 1) % 2;

                var impulseJob = new AddImpulseJob
                {
                    tick = _tick,
                    curr = octave.buffer.GetBuffer(buffIdx0),
                };
                simHandle = impulseJob.Schedule(simHandle);

                var simulateJob = new PropagateJobParallelBatch
                {
                    tick = _tick,
                    curr = octave.buffer.GetBuffer(buffIdx0),
                    next = octave.buffer.GetBuffer(buffIdx1),
                };
                simHandle = simulateJob.ScheduleBatch((RES-2) * (RES-2), RES-2, simHandle);

                _tick++;
            }
            simHandle.Complete();

            var texture = _screenTex.GetRawTextureData<byte2>();
            var waveData = octave.buffer.GetBuffer(buffIdx1);
            var renderJob = new RenderJobParallel
            {
                buf = waveData,
                texture = texture
            };
            _renderHandle = renderJob.Schedule(RES * RES, 32, _renderHandle);
        }

        private void LateUpdate() {
            _renderHandle.Complete();
            _screenTex.Apply(false);
        }

        private void OnGUI() {
            float size = math.min(Screen.width, Screen.height);
            GUI.DrawTexture(new Rect(0f, 0f, size, size), _screenTex, ScaleMode.ScaleToFit);
        }

        [BurstCompile]
        public struct AddImpulseJob : IJob {
            public NativeArray<float> curr;
            public uint tick;

            public void Execute() {
                /*
                 Todo: 
                 - Still shows square-ish artifacts
                 - Fixed point arithmetic instead of float?
                */

                Rng rng;
                unchecked {
                    rng = new Rng(0x816EFB5Du + tick * 0x7461CA0Du);
                }

                int2 pos = new int2(rng.NextInt(RES), rng.NextInt(RES));

                int radius = 15 + rng.NextInt(6) * rng.NextInt(6); // odd-numbered radius
                float amplitude = rng.NextFloat(-0.5f, 0.5f);
                float radiusScale = math.PI * 2f / (float)radius;

                const int impulsePeriod = 16;
                if (tick == 0 || rng.NextInt(impulsePeriod) == 0) {
                    for (int y = -radius; y <= radius; y++) {
                        for (int x = -radius; x <= radius; x++) {
                            int xIdx = pos.x + x;
                            int yIdx = pos.y + y;

                            if (xIdx < 0 || xIdx >= RES || yIdx < 0 || yIdx >= RES) {
                                continue;
                            }

                            float r = math.sqrt(x * x + y * y);

                            curr[Idx(xIdx, yIdx)] += (
                                math.smoothstep(0f, 1f, 1f - r * radiusScale) *
                                math.cos(r * (math.PI * 0.1f)) *
                                amplitude
                            );
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
        public struct PropagateJobParallelBatch : IJobParallelForBatch {
            [ReadOnly] public uint tick;
            [ReadOnly] public NativeArray<float> curr;
            [NativeDisableParallelForRestriction] public NativeArray<float> next;
            

            public void Execute(int startIndex, int count) {
                // Todo: supply these constants in a struct, as part of Octave
                const float scaleFactor = 1f; // double or tripple per octave
                const float dcd = 0.15f * scaleFactor;
                const float dt = 0.0175f;
                const float C = 0.9f;
                const float R = C * dt / dcd;
                const float rSqr = R * R; // optimization

                const float rMinusOne = R - 1f;
                const float rPlusOne = R + 1f;
                const float rSqrx2 = rSqr * 2f;

                // Todo: hoist the inner-loop if-statements into separate loops or jobs

                // for (int i = 0; i < count; i++) {
                //     var coord = Coord(startIndex + i);
                //     var x = coord.x;
                //     var y = coord.y;
                //     int edgeIdx = Idx(x, y);

                //     int neighborIdx = -1;
                //     if (x == 0) {
                //         neighborIdx = Morton.Right(edgeIdx);
                //     }
                //     if (x >= RES - 1) {
                //         neighborIdx = Morton.Left(edgeIdx);
                //     }
                //     if (y == 0) {
                //         neighborIdx = Morton.Up(edgeIdx);
                //     }
                //     if (y >= RES - 1) {
                //         neighborIdx = Morton.Down(edgeIdx);
                //     }

                //     if (neighborIdx != -1) {
                //         float v = (
                //             2f * curr[edgeIdx] + rMinusOne * prev_next[edgeIdx] +
                //             rSqrx2 * (curr[neighborIdx] - curr[edgeIdx])
                //         ) / rPlusOne;
                //         prev_next[edgeIdx] = v;
                //     }
                // }

                for (int i = startIndex; i < startIndex + count; i++) {
                    int2 c = new int2(1,1) + Coord(i, RES-2);
                    int idx = Idx(c.x, c.y);

                    float spatial = rSqr * (
                        curr[Idx(c.x - 1, c.y + 0)] +
                        curr[Idx(c.x + 1, c.y + 0)] +
                        curr[Idx(c.x + 0, c.y - 1)] +
                        curr[Idx(c.x + 0, c.y + 1)] -
                        curr[idx] * 4
                    );

                    float temporal = 2 * curr[idx] - next[idx];

                    float v = spatial + temporal;

                    // Symmetric clamping as a safeguard
                    const float ceiling = 1f;
                    v = math.clamp(v, -ceiling, ceiling);

                    next[idx] = v;

                    // prev_next[i] = 1f;
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
                float sample = buf[i];

                var scaled = (sample * 256f);
                var pos = math.clamp(scaled, 0, 256f);
                var neg = math.clamp(-scaled, 0, 256f);

                var pressureColor = new byte2(
                    (byte)neg,
                    (byte)pos
                );
                texture[i] = pressureColor;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Idx(int x, int y) {
            return y * RES + x;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int2 Coord(int i) {
            return new int2(i % RES, i / RES);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int2 Coord(int i, int res) {
            return new int2(i % res, i / res);
        }
    }
}