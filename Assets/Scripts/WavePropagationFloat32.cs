using UnityEngine;
using Unity.Burst;
using Unity.Jobs;
using Unity.Collections;
using Unity.Mathematics;
using Rng = Unity.Mathematics.Random;
using System.Runtime.CompilerServices;
using Unity.Collections.LowLevel.Unsafe;
using System.Runtime.InteropServices;

/*
    Todo:
    
    Use this to get jobs to run while rest of frame waits for deltaTime?
    https://docs.unity3d.com/2018.1/Documentation/ScriptReference/Experimental.LowLevel.PlayerLoopSystem.html
*/

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

        private const int RES = 16;

        const int TILES_PER_DIM = 16;
        const int NUM_TILES = TILES_PER_DIM * TILES_PER_DIM;

        const int TICKSPERFRAME = 1;

        private Texture2D[] _screenTex;

        private uint _tick = 0;

        private FunctionPointer<Indexer> _indexH;
        private FunctionPointer<Indexer> _indexV;

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

            _indexH = new FunctionPointer<Indexer>(Marshal.GetFunctionPointerForDelegate((Indexer)PIdxH));
            _indexV = new FunctionPointer<Indexer>(Marshal.GetFunctionPointerForDelegate((Indexer)PIdxV));
        }

        private void OnDestroy() {
            for (int i = 0; i < _tiles.Length; i++) {
                _tiles[i].Dispose();
            }
        }

        private int buffIdx0 = 0;
        private int buffIdx1 = 1;
        private JobHandle _updateHandle;

        private void Update() {
            var handles = new NativeList<JobHandle>(NUM_TILES * 4, Allocator.Temp);

            var scaleFactor = 1f;
            var simConfig = new SimConfig(
                scaleFactor,            // double per octave
                0.15f * scaleFactor,    // dcd
                0.0175f,                // dt
                0.9f                    // C
            );

            buffIdx0 = (buffIdx0 + 1) % 2;
            buffIdx1 = (buffIdx0 + 1) % 2;

            for (uint x = 0; x < TILES_PER_DIM; x++) {
                for (uint y = 0; y < TILES_PER_DIM; y++) {
                    uint i = TIdxH(x,y);
                    var octave = _tiles[i];

                    var tileHandle = new JobHandle();

                    var impulseJob = new AddImpulseJob
                    {
                        tick = _tick,
                        tile = new uint2(x, y),
                        curr = octave.buffer.GetBuffer(buffIdx0),
                    };
                    tileHandle = impulseJob.Schedule(tileHandle);

                    var simulateJob = new PropagateTileJob
                    {
                        tick = _tick,
                        config = simConfig,
                        curr = octave.buffer.GetBuffer(buffIdx0),
                        next = octave.buffer.GetBuffer(buffIdx1),
                    };
                    tileHandle = simulateJob.ScheduleBatch((RES - 2) * (RES - 2), RES - 2, tileHandle);

                    handles.Add(tileHandle);
                }
            }

            var tileSimHandle = JobHandle.CombineDependencies(handles);
            handles.Clear();

            // Horizontal edge jobs
            for (int x = 0; x < TILES_PER_DIM-1; x++) {
                for (int y = 0; y < TILES_PER_DIM; y++) {
                    var edgeSimJob = new PropagateTileEdgeJob()
                    {
                        tick = _tick,
                        config = simConfig,
                        a_curr = _tiles[TIdxH(x + 0, y)].buffer.GetBuffer(buffIdx0),
                        a_next = _tiles[TIdxH(x + 0, y)].buffer.GetBuffer(buffIdx1),
                        b_curr = _tiles[TIdxH(x + 1, y)].buffer.GetBuffer(buffIdx0),
                        b_next = _tiles[TIdxH(x + 1, y)].buffer.GetBuffer(buffIdx1),
                        index = _indexH
                    };
                    var handle = edgeSimJob.ScheduleBatch(RES, RES, tileSimHandle);
                    handles.Add(handle);
                }
            }

            // Vertical edge jobs
            for (int x = 0; x < TILES_PER_DIM; x++) {
                for (int y = 0; y < TILES_PER_DIM-1; y++) {
                    var edgeSimJob = new PropagateTileEdgeVerticalJob()
                    {
                        tick = _tick,
                        config = simConfig,
                        a_curr = _tiles[TIdxH(x, y + 0)].buffer.GetBuffer(buffIdx0),
                        a_next = _tiles[TIdxH(x, y + 0)].buffer.GetBuffer(buffIdx1),
                        b_curr = _tiles[TIdxH(x, y + 1)].buffer.GetBuffer(buffIdx0),
                        b_next = _tiles[TIdxH(x, y + 1)].buffer.GetBuffer(buffIdx1),
                    };
                    var handle = edgeSimJob.ScheduleBatch(RES, RES, tileSimHandle);
                    handles.Add(handle);
                }
            }

            // Corner jobs
            for (int x = 0; x < TILES_PER_DIM-1; x++) {
                for (int y = 0; y < TILES_PER_DIM-1; y++) {
                    var cornerSimJob = new PropagateTileCornerJob()
                    {
                        tick = _tick,
                        config = simConfig,
                        bl_curr = _tiles[TIdxH(x + 0, y + 0)].buffer.GetBuffer(buffIdx0),
                        bl_next = _tiles[TIdxH(x + 0, y + 0)].buffer.GetBuffer(buffIdx1),
                        br_curr = _tiles[TIdxH(x + 1, y + 0)].buffer.GetBuffer(buffIdx0),
                        br_next = _tiles[TIdxH(x + 1, y + 0)].buffer.GetBuffer(buffIdx1),
                        tl_curr = _tiles[TIdxH(x + 0, y + 1)].buffer.GetBuffer(buffIdx0),
                        tl_next = _tiles[TIdxH(x + 0, y + 1)].buffer.GetBuffer(buffIdx1),
                        tr_curr = _tiles[TIdxH(x + 1, y + 1)].buffer.GetBuffer(buffIdx0),
                        tr_next = _tiles[TIdxH(x + 1, y + 1)].buffer.GetBuffer(buffIdx1),
                    };
                    var handle = cornerSimJob.ScheduleBatch(RES, RES, tileSimHandle);
                    handles.Add(handle);
                }
            }

            tileSimHandle = JobHandle.CombineDependencies(handles);
            handles.Clear();
            
            _tick++;

            var edgeSimHandle = JobHandle.CombineDependencies(handles);
            handles.Clear();

            for (int i = 0; i < NUM_TILES; i++) {
                var texture = _screenTex[i].GetRawTextureData<byte2>();
                var waveData = _tiles[i].buffer.GetBuffer(buffIdx1);
                var renderJob = new RenderJobParallel
                {
                    buf = waveData,
                    texture = texture
                };
                var renderHandle = renderJob.Schedule(RES * RES, 32, edgeSimHandle);
                handles.Add(renderHandle);
            }

            _updateHandle = JobHandle.CombineDependencies(handles);

            handles.Dispose();
        }

        private void LateUpdate() {
            _updateHandle.Complete();

            for (int i = 0; i < NUM_TILES; i++) {
                _screenTex[i].Apply(false);
            }
        }

        private void OnGUI() {
            float scale = 2f;
            float size = RES * scale;
            float sizeMinOne = (RES-1) * scale;

            for (int x = 0; x < TILES_PER_DIM; x++) {
                for (int y = 0; y < TILES_PER_DIM; y++) {
                    var rect = new Rect(
                        x * sizeMinOne,
                        Screen.height - (y+1) * sizeMinOne,
                        size,
                        size);

                    GUI.DrawTexture(rect, _screenTex[TIdxH(x,y)], ScaleMode.ScaleToFit);
                }
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
                    rng = new Rng(0x816EFB5Du + tick * 0x7461CA0Du + (uint)tile.GetHashCode());
                }

                int2 pos = new int2(rng.NextInt(RES), rng.NextInt(RES));

                int radius = 15 + rng.NextInt(6) * rng.NextInt(6); // odd-numbered radius
                float amplitude = rng.NextFloat(-0.5f, 0.5f);
                float radiusScale = math.PI * 2f / (float)radius;

                const int impulsePeriod = 128 * 128;
                if (rng.NextInt(impulsePeriod) == 0) {
                    for (int y = -radius; y <= radius; y++) {
                        for (int x = -radius; x <= radius; x++) {
                            int xIdx = pos.x + x;
                            int yIdx = pos.y + y;

                            if (xIdx < 0 || xIdx >= RES || yIdx < 0 || yIdx >= RES) {
                                continue;
                            }

                            float r = math.sqrt(x * x + y * y);

                            curr[PIdxH(xIdx, yIdx)] += (
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
        public struct PropagateTileJob : IJobParallelForBatch {
            [ReadOnly] public uint tick;
            [ReadOnly] public SimConfig config;

            [ReadOnly] public NativeArray<float> curr;
            [NativeDisableParallelForRestriction] public NativeArray<float> next;
            

            public void Execute(int startIndex, int count) {
                // Calculates inner pixels, skipping 1 pixel skirt

                for (int i = startIndex; i < startIndex + count; i++) {
                    int2 c = new int2(1,1) + Coord(i, RES-2);
                    int idx = PIdxH(c.x, c.y);

                    float spatial = config.rSqr * (
                        curr[PIdxH(c.x - 1, c.y + 0)] +
                        curr[PIdxH(c.x + 1, c.y + 0)] +
                        curr[PIdxH(c.x + 0, c.y - 1)] +
                        curr[PIdxH(c.x + 0, c.y + 1)] -
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

        /*
         Function pointer used to make the tile-edge job code work
        for both horizontal and vertical edges
        */
        public delegate int Indexer(int x, int y);

        [BurstCompile]
        public struct PropagateTileEdgeJob : IJobParallelForBatch {
            [ReadOnly] public uint tick;

            [ReadOnly] public SimConfig config;

            [ReadOnly] public NativeArray<float> a_curr;
            [NativeDisableParallelForRestriction] public NativeArray<float> a_next;

            [ReadOnly] public NativeArray<float> b_curr;
            [NativeDisableParallelForRestriction] public NativeArray<float> b_next;

            [ReadOnly] public FunctionPointer<Indexer> index;

            /*
            Implementation note:

            The idea here is that a line of pixels at the edge of a tile is the same
            data as the edge on the neighboring tile.

            Why? Joining two tiles with two unfilled edges of data is not possible,
            as both then see their neighbors edge data as uninitialized.

            This will work out well enough, I think, as the rendering system also
            works with odd-numbered resolutions for meshes and textures.

            Todo:
            - generalize from left-right to top-bottom setup
            - pay special attention to corner pixels...
            */

            public void Execute(int startIndex, int count) {
                for (int i = 1; i < RES - 1; i++) {
                    int a_idx = index.Invoke(RES - 1, i);
                    int b_idx = index.Invoke(1, i);

                    float spatial = config.rSqr * (
                        b_curr[index.Invoke(1, i + 0)] +
                        a_curr[index.Invoke(RES - 2, i + 0)] +
                        a_curr[index.Invoke(RES - 2, i - 1)] +
                        a_curr[index.Invoke(RES - 2, i + 1)] -
                        a_curr[a_idx] * 4
                    );

                    float temporal = 2 * a_curr[a_idx] - a_next[a_idx];

                    float v = spatial + temporal;

                    // Symmetric clamping as a safeguard
                    const float ceiling = 1f;
                    v = math.clamp(v, -ceiling, ceiling);

                    a_next[a_idx] = v;
                    b_next[index.Invoke(0, i)] = v;
                }
            }
        }

        [BurstCompile]
        public struct PropagateTileEdgeVerticalJob : IJobParallelForBatch {
            [ReadOnly] public uint tick;

            [ReadOnly] public SimConfig config;

            [ReadOnly] public NativeArray<float> a_curr;
            [NativeDisableParallelForRestriction] public NativeArray<float> a_next;

            [ReadOnly] public NativeArray<float> b_curr;
            [NativeDisableParallelForRestriction] public NativeArray<float> b_next;

            // [ReadOnly] public FunctionPointer<Indexer> index;

            /*
            Implementation note:

            The idea here is that a line of pixels at the edge of a tile is the same
            data as the edge on the neighboring tile.

            Why? Joining two tiles with two unfilled edges of data is not possible,
            as both then see their neighbors edge data as uninitialized.

            This will work out well enough, I think, as the rendering system also
            works with odd-numbered resolutions for meshes and textures.

            Todo:
            - generalize from left-right to top-bottom setup
            - pay special attention to corner pixels...
            */

            public void Execute(int startIndex, int count) {
                for (int i = 1; i < RES - 1; i++) {
                    int a_idx = PIdxH(i, RES - 1);
                    int b_idx = PIdxH(i, 1);

                    float spatial = config.rSqr * (
                        b_curr[PIdxH(i + 0, 1)] +
                        a_curr[PIdxH(i + 0, RES - 2)] +
                        a_curr[PIdxH(i - 1, RES - 2)] +
                        a_curr[PIdxH(i + 1, RES - 2)] -
                        a_curr[a_idx] * 4
                    );

                    float temporal = 2 * a_curr[a_idx] - a_next[a_idx];

                    float v = spatial + temporal;

                    // Symmetric clamping as a safeguard
                    const float ceiling = 1f;
                    v = math.clamp(v, -ceiling, ceiling);

                    a_next[a_idx] = v;
                    b_next[PIdxH(i, 0)] = v;
                }
            }
        }

        [BurstCompile]
        public struct PropagateTileCornerJob : IJobParallelForBatch {
            [ReadOnly] public uint tick;

            [ReadOnly] public SimConfig config;

            [ReadOnly] public NativeArray<float> bl_curr;
            [NativeDisableParallelForRestriction] public NativeArray<float> bl_next;

            [ReadOnly] public NativeArray<float> br_curr;
            [NativeDisableParallelForRestriction] public NativeArray<float> br_next;

            [ReadOnly] public NativeArray<float> tl_curr;
            [NativeDisableParallelForRestriction] public NativeArray<float> tl_next;

            [ReadOnly] public NativeArray<float> tr_curr;
            [NativeDisableParallelForRestriction] public NativeArray<float> tr_next;

            public void Execute(int startIndex, int count) {
                int centerIdx = PIdxH(RES - 1, RES - 1);

                float spatial = config.rSqr * (
                    bl_curr[PIdxH(RES - 2, RES - 1)] +
                    br_curr[PIdxH(1, RES - 1)] +
                    bl_curr[PIdxH(RES - 1, RES - 2)] +
                    br_curr[PIdxH(RES - 1, 1)] -
                    bl_curr[centerIdx] * 4
                );

                float temporal = 2 * bl_curr[centerIdx] - bl_next[centerIdx];

                float v = spatial + temporal;

                // Symmetric clamping as a safeguard
                const float ceiling = 1f;
                v = math.clamp(v, -ceiling, ceiling);

                bl_next[PIdxH(RES-1, RES-1)] = v;
                br_next[PIdxH(0, RES-1)] = v;
                tl_next[PIdxH(RES-1, 0)] = v;
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

        // Pixel indexing

        // Horizontal pixel indexing
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int PIdxH(int x, int y) {
            return y * RES + x;
        }

        // Vertical pixel indexing
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int PIdxV(int y, int x) {
            return y * RES + x;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int PIdxH(int2 v) {
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

        // Tile indexing

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int TIdxH(int x, int y) {
            return y * TILES_PER_DIM + x;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint TIdxH(uint x, uint y) {
            return y * TILES_PER_DIM + x;
        }
    }
}