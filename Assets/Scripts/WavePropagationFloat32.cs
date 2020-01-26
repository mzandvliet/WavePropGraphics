using UnityEngine;
using Unity.Burst;
using Unity.Jobs;
using Unity.Collections;
using Unity.Mathematics;
using Rng = Unity.Mathematics.Random;
using System.Runtime.CompilerServices;
using Unity.Collections.LowLevel.Unsafe;

namespace WavesBurstF32 {
    public struct Tile : System.IDisposable {
        public PingPongBuffer<float> buffer;

        public Tile(int resolution, int ticksPerFrame) {
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
        private Tile[] _tiles;

        private const int RES = 32; // current limiting factor: if we set this too big we create cache misses
        // private const int TILE_RES = 16;
        // private const int TILE_NUM = RES / TILE_RES;

        const int NUM_TILES = 2;

        const int TICKSPERFRAME = 1;

        private Texture2D[] _screenTex;

        private uint _tick = 0;

        private void Awake() {
            Application.targetFrameRate = 60;

            _tiles = new Tile[NUM_TILES];
            for (int i = 0; i < NUM_TILES; i++) {
                _tiles[i] = new Tile(RES, TICKSPERFRAME);
            }

            _screenTex = new Texture2D[NUM_TILES];
            for (int i = 0; i < NUM_TILES; i++) {
                var tileTex = new Texture2D(RES, RES, TextureFormat.RG16, false, true);
                tileTex.filterMode = FilterMode.Point;
                _screenTex[i] = tileTex;
            }
        }

        private void OnDestroy() {
            for (int i = 0; i < _tiles.Length; i++) {
                _tiles[i].Dispose();
            }
        }

        private int buffIdx0 = 0;
        private int buffIdx1 = 1;

        private void Update() {
            var handles = new NativeList<JobHandle>(2, Allocator.Temp);

            buffIdx0 = (buffIdx0 + 1) % 2;
            buffIdx1 = (buffIdx0 + 1) % 2;

            for (uint i = 0; i < 2; i++) {
                var octave = _tiles[i];

                var tileHandle = new JobHandle();

                var impulseJob = new AddImpulseJob
                {
                    tick = _tick,
                    tile = new uint2(i, 0),
                    curr = octave.buffer.GetBuffer(buffIdx0),
                };
                tileHandle = impulseJob.Schedule(tileHandle);

                var simulateJob = new PropagateJobParallelBatch
                {
                    tick = _tick,
                    curr = octave.buffer.GetBuffer(buffIdx0),
                    next = octave.buffer.GetBuffer(buffIdx1),
                };
                tileHandle = simulateJob.ScheduleBatch((RES - 2) * (RES - 2), RES - 2, tileHandle);

                handles.Add(tileHandle);
            }

            JobHandle.CompleteAll(handles);

            var edgeSimJob = new PropagateLeftEdgeJobParallelBatch() {
                tick = _tick,
                curr_this = _tiles[1].buffer.GetBuffer(buffIdx0),
                curr_that = _tiles[0].buffer.GetBuffer(buffIdx0),
                next_this = _tiles[1].buffer.GetBuffer(buffIdx1),
                next_that = _tiles[0].buffer.GetBuffer(buffIdx1),
            };
            var edgeHandle = edgeSimJob.ScheduleBatch(RES, RES);
            edgeHandle.Complete();

            _tick++;

            handles.Clear();

            for (int i = 0; i < NUM_TILES; i++) {
                var texture = _screenTex[i].GetRawTextureData<byte2>();
                var waveData = _tiles[i].buffer.GetBuffer(buffIdx1);
                var renderJob = new RenderJobParallel
                {
                    buf = waveData,
                    texture = texture
                };
                var renderHandle = renderJob.Schedule(RES * RES, 32);
                handles.Add(renderHandle);
            }

            JobHandle.CompleteAll(handles);

            handles.Dispose();
        }

        private void LateUpdate() {
            for (int i = 0; i < NUM_TILES; i++) {
                _screenTex[i].Apply(false);
            }
        }

        private void OnGUI() {
            const float scale = 8f;
            float size = RES * scale;

            for (int i = 0; i < NUM_TILES; i++) {
                GUI.DrawTexture(new Rect(i * size, 0f, size, size), _screenTex[i], ScaleMode.ScaleToFit);
            }
        }

        [BurstCompile]
        public struct AddImpulseJob : IJob {
            public NativeArray<float> curr;
            public uint tick;
            public uint2 tile;

            public void Execute() {
                /*
                 Todo: 
                 - Still shows square-ish artifacts
                 - Fixed point arithmetic instead of float?
                */

                Rng rng;
                unchecked {
                    rng = new Rng(0x816EFB5Du + tick * 0x7461CA0Du + tile.x * 0x66F38F0Bu + tile.y * 0x568DAAA9u);
                }

                int2 pos = new int2(rng.NextInt(RES), rng.NextInt(RES));

                int radius = 15 + rng.NextInt(6) * rng.NextInt(6); // odd-numbered radius
                float amplitude = rng.NextFloat(-0.5f, 0.5f);
                float radiusScale = math.PI * 2f / (float)radius;

                const int impulsePeriod = 72;
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
                }
            }
        }

        /*

        Edge code pattern:

        float v = (
            2f * curr[edgeIdx] + rMinusOne * prev_next[edgeIdx] +
            rSqrx2 * (curr[neighborIdx] - curr[edgeIdx])
        ) / rPlusOne;
        prev_next[edgeIdx] = v;

        */

        [BurstCompile]
        public struct PropagateLeftEdgeJobParallelBatch : IJobParallelForBatch {
            [ReadOnly] public uint tick;

            [ReadOnly] public NativeArray<float> curr_this;
            [NativeDisableParallelForRestriction] public NativeArray<float> next_this;

            [ReadOnly] public NativeArray<float> curr_that;
            [NativeDisableParallelForRestriction] public NativeArray<float> next_that;

            // Todo: also needs top and bottom for corner pixels...

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

                for (int i = 1; i < RES-1; i++) {
                    int2 cThis = new int2(0    , i);
                    int2 cThat = new int2(RES-1, i);
                    int thisIdx = Idx(cThis);
                    int thatIdx = Idx(cThat);

                    float spatial = rSqr * (
                        curr_that[Idx(RES - 1, i + 0)] +
                        curr_this[Idx(1, i + 0)] +
                        curr_this[Idx(0, i - 1)] +
                        curr_this[Idx(0, i + 1)] -
                        curr_this[thisIdx] * 4
                    );

                    float temporal = 2 * curr_this[thisIdx] - next_this[thisIdx];

                    float v = spatial + temporal;

                    // Symmetric clamping as a safeguard
                    const float ceiling = 1f;
                    v = math.clamp(v, -ceiling, ceiling);

                    next_this[thisIdx] = v;
                }

                for (int i = 1; i < RES - 1; i++) {
                    int2 cThis = new int2(0, i);
                    int2 cThat = new int2(RES - 1, i);
                    int thisIdx = Idx(cThis);
                    int thatIdx = Idx(cThat);

                    float spatial = rSqr * (
                        curr_this[Idx(0, i + 0)] +
                        curr_that[Idx(RES - 1, i + 0)] +
                        curr_that[Idx(RES - 1, i - 1)] +
                        curr_that[Idx(RES - 1, i + 1)] -
                        curr_that[thatIdx] * 4
                    );

                    float temporal = 2 * curr_that[thatIdx] - next_that[thatIdx];

                    float v = spatial + temporal;

                    // Symmetric clamping as a safeguard
                    const float ceiling = 1f;
                    v = math.clamp(v, -ceiling, ceiling);

                    next_that[thatIdx] = v;
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
        private static int Idx(int2 v) {
            return v.y * RES + v.x;
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