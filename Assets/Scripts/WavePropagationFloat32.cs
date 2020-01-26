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

            var scaleFactor = 1f;
            var simConfig = new SimConfig(
                scaleFactor,            // double or tripple per octave
                0.15f * scaleFactor,    // dcd
                0.0175f,                // dt
                0.9f                    // C
            );

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
                    config = simConfig,
                    curr = octave.buffer.GetBuffer(buffIdx0),
                    next = octave.buffer.GetBuffer(buffIdx1),
                };
                tileHandle = simulateJob.ScheduleBatch((RES - 2) * (RES - 2), RES - 2, tileHandle);

                handles.Add(tileHandle);
            }

            JobHandle.CompleteAll(handles);

            /* 
            Todo: iterate over dual grid between the tiles
            each of the verts can handle the same pattern?
            */

            var edgeSimJob = new PropagateEdgeHorizontalJob() {
                tick = _tick,
                config = simConfig,
                l_curr = _tiles[0].buffer.GetBuffer(buffIdx0),
                r_curr = _tiles[1].buffer.GetBuffer(buffIdx0),
                l_next = _tiles[0].buffer.GetBuffer(buffIdx1),
                r_next = _tiles[1].buffer.GetBuffer(buffIdx1),
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
            float sizeMinOne = (RES-1) * scale;

            for (int i = 0; i < NUM_TILES; i++) {
                GUI.DrawTexture(new Rect(i * sizeMinOne, 0f, size, size), _screenTex[i], ScaleMode.ScaleToFit);
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

                const int impulsePeriod = 72 * 4;
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
        public struct PropagateJobParallelBatch : IJobParallelForBatch {
            [ReadOnly] public uint tick;
            [ReadOnly] public SimConfig config;

            [ReadOnly] public NativeArray<float> curr;
            [NativeDisableParallelForRestriction] public NativeArray<float> next;
            

            public void Execute(int startIndex, int count) {

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
                    const float ceiling = 1f;
                    v = math.clamp(v, -ceiling, ceiling);

                    next[idx] = v;
                }
            }
        }

        /*

        Todo: Continuation or Transient Edge code pattern

        float v = (
            2f * curr[edgeIdx] + rMinusOne * prev_next[edgeIdx] +
            rSqrx2 * (curr[neighborIdx] - curr[edgeIdx])
        ) / rPlusOne;
        prev_next[edgeIdx] = v;

        */

        [BurstCompile]
        public struct PropagateEdgeHorizontalJob : IJobParallelForBatch {
            [ReadOnly] public uint tick;

            [ReadOnly] public SimConfig config;

            [ReadOnly] public NativeArray<float> l_curr;
            [NativeDisableParallelForRestriction] public NativeArray<float> l_next;

            [ReadOnly] public NativeArray<float> r_curr;
            [NativeDisableParallelForRestriction] public NativeArray<float> r_next;

            /*
            Implementation note:

            The idea here is that a line of pixels at the edge of a tile is the same
            data as the edge on the neighboring tile.

            Why? Joining two tiles with two unfilled edges of data is not possible,
            as both then see their neighbors edge data as uninitialized.

            This will work out well enough, I think, as the rendering system also
            works with odd-numbered resolutions for meshes and textures.

            Todo:
            - Also needs top and bottom for corner pixels...
            */

            public void Execute(int startIndex, int count) {
                // Left tile
                for (int i = 1; i < RES - 1; i++) {
                    int r_idx = Idx(1       , i);
                    int l_idx = Idx(RES - 1 , i);

                    float spatial = config.rSqr * (
                        r_curr[Idx(1      , i + 0)] +
                        l_curr[Idx(RES - 2, i + 0)] +
                        l_curr[Idx(RES - 2, i - 1)] +
                        l_curr[Idx(RES - 2, i + 1)] -
                        l_curr[l_idx] * 4
                    );

                    float temporal = 2 * l_curr[l_idx] - l_next[l_idx];

                    float v = spatial + temporal;

                    // Symmetric clamping as a safeguard
                    const float ceiling = 1f;
                    v = math.clamp(v, -ceiling, ceiling);

                    l_next[l_idx] = v;
                    r_next[Idx(0, i)] = v;
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
                var pos = math.clamp(scaled, 0, 255f);
                var neg = math.clamp(-scaled, 0, 255f);

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