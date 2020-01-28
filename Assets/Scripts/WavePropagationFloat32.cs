using UnityEngine;
using Unity.Burst;
using Unity.Jobs;
using Unity.Collections;
using Unity.Mathematics;
using Rng = Unity.Mathematics.Random;
using System.Runtime.CompilerServices;
using Unity.Collections.LowLevel.Unsafe;

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
        public float Sample(float x, float z) {
            const float offset = (float)-(32768 / 2);
            const float horScale = 1f / 128f;

            x -= offset;
            z -= offset;

            x *= horScale;
            z *= horScale;

            int xFloor = (int)math.floor(x);
            int zFloor = (int)math.floor(z);

            // Clamp?
            // xFloor = math.clamp(xFloor, 0, WaveSimulator.RES-2);
            // zFloor = math.clamp(zFloor, 0, WaveSimulator.RES-2);

            if (xFloor < 0 || xFloor >= WaveSimulator.RES-1 || zFloor < 0 || zFloor >= WaveSimulator.RES-1) {
                return 0.5f;
            }

            float xFrac = x - (float)xFloor;
            float zFrac = z - (float)zFloor;
            
            int bl_idx = WaveSimulator.Idx(xFloor + 0, zFloor + 0);
            int br_idx = WaveSimulator.Idx(xFloor + 1, zFloor + 0);
            int tl_idx = WaveSimulator.Idx(xFloor + 0, zFloor + 1);
            int tr_idx = WaveSimulator.Idx(xFloor + 1, zFloor + 1);

            float bl_sample = buffer[bl_idx];
            float br_sample = buffer[br_idx];
            float tl_sample = buffer[tl_idx];
            float tr_sample = buffer[tr_idx];

            float height = math.lerp(
                math.lerp(bl_sample, br_sample, xFrac),
                math.lerp(tl_sample, tr_sample, xFrac),
                zFrac
            );

            return 0.5f + 0.5f * height;
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
            var scaleFactor = 02f;
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

                int2 pos = new int2(rng.NextInt(RES), rng.NextInt(RES));

                int radius = 31 + 2*(rng.NextInt(5) * rng.NextInt(5)); // odd-numbered radius
                float amplitude = rng.NextFloat(-0.5f, 0.5f);
                float radiusInv = 1f / (float)(radius-1);
                float rippleFreq = (math.PI * 2f * 0.02f);

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
                            float interp = math.smoothstep(1f, 0f, r * radiusInv);
                            int idx = Idx(xIdx, yIdx);

                            curr[idx] += 
                                math.cos(r * rippleFreq) * interp * amplitude
                            ;

                            prev[idx] = math.lerp(prev[idx], curr[idx], 0.75f);
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
                    const float ceiling = 1f;
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