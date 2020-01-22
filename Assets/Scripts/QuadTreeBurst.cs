using UnityEngine;
using Unity.Mathematics;
using Unity.Collections;
using System.Runtime.CompilerServices;
using Unity.Jobs;
using System.Runtime.InteropServices;
using Unity.Burst;

[BurstCompile]
public struct ExpandQuadTreeJob : IJob {
    [ReadOnly] public int maxDepth;
    [ReadOnly] public CameraInfo camInfo;
    [ReadOnly] public NativeSlice<float> lodDistances;
    [ReadOnly] public HeightSampler heights;

    [ReadOnly] public Bounds root;
    public Tree tree;

    public void Execute() {
        var stack = new NativeStack<int>(mathi.SumPowersOfTwo(maxDepth), Allocator.TempJob);

        stack.Push(0);

        while (stack.Count > 0) {
            int depth = stack.Count - 1;
            int parentIdx = stack.Pop();
            var parent = tree[parentIdx];

            // If we're at the deepest lod level, no need to expand further
            if (parent.depth == maxDepth - 1) {
                parent.payload = 1;
                tree[parentIdx] = parent;
                continue;
            }

            // If not, we should create children if we're in LOD range
            if (TreeUtil.Intersect(parent.bounds, camInfo, lodDistances[depth])) {
                tree.Expand(parentIdx);

                for (int i = 0; i < 4; i++) {
                    stack.Push(parent[i]);
                }

                continue;
            }

            // If we don't need to expand, just add to the list
            parent.payload = 1;
            tree[parentIdx] = parent;
        }

        stack.Dispose();
    }
}

[BurstCompile]
public struct DiffQuadTreesJob : IJob {
    [ReadOnly] public NativeArray<int> a;
    [ReadOnly] public NativeArray<int> b;

    public NativeList<int> diff;

    public void Execute() {
        diff.Clear();

        if (a.Length != b.Length) {
            Debug.LogError("Cannot diff two quadtrees of different capacities");
            return;
        }

        for (int i = 0; i < a.Length; i++) {
            if (b[i] != a[i]) {
                diff.Add(i);
            }
        }
    }
}

public static class TreeUtil {
    public static bool Intersect(Bounds node, CameraInfo camInfo, float range) {
        return IntersectBoxSphere(node.position, node.position + node.size, camInfo.position, range);
    }

    public static bool IntersectBoxSphere(float3 bMin, float3 bMax, float3 spherePos, float sphereRadius) {
        float sqrRadius = Sqr(sphereRadius);
        float minDist = 0f;
        for (int i = 0; i < 3; i++) {
            if (spherePos[i] < bMin[i]) minDist += Sqr(spherePos[i] - bMin[i]);
            else if (spherePos[i] > bMax[i]) minDist += Sqr(spherePos[i] - bMax[i]);
        }
        return minDist <= sqrRadius;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sqr(float val) {
        return val * val;
    }
}

// Todo: z-order curve addressing structure will simplify all of this structure
public struct Tree : System.IDisposable {
    private NativeList<TreeNode> _nodes;
    private HeightSampler _heights;

    public Tree(Bounds bounds, int maxLevels, HeightSampler heights, Allocator allocator) {
        _nodes = new NativeList<TreeNode>(mathi.SumPowersOfFour(maxLevels), allocator);
        _heights = heights;
        NewNode(bounds, 0);
    }

    private int NewNode(Bounds bounds, int depth) {
        int idx = _nodes.Length;
        var node = new TreeNode(bounds, 0);
        _nodes.Add(node);
        return idx;
    }

    public void Expand(int idx) {
        var parent = _nodes[idx];
        var halfSize = parent.bounds.size * 0.5f;

        var bl = new Bounds(parent.bounds.position + new float3(0f, 0f, 0f), halfSize);
        var tl = new Bounds(parent.bounds.position + new float3(0f, 0f, halfSize.z), halfSize);
        var tr = new Bounds(parent.bounds.position + new float3(halfSize.x, 0f, halfSize.z), halfSize);
        var br = new Bounds(parent.bounds.position + new float3(halfSize.x, 0f, 0f), halfSize);

        bl = FitHeightSamples(bl, _heights);
        tl = FitHeightSamples(tl, _heights);
        tr = FitHeightSamples(tr, _heights);
        br = FitHeightSamples(br, _heights);

        parent.bl = NewNode(bl, parent.depth + 1);
        parent.tl = NewNode(tl, parent.depth + 1);
        parent.tr = NewNode(tr, parent.depth + 1);
        parent.br = NewNode(br, parent.depth + 1);
    }

    private static Bounds FitHeightSamples(Bounds bounds, HeightSampler sampler) {
        /* Todo: move this logic out of this class, too much business going on */

        const int samplingResolution = 8;

        float highest = float.MinValue;
        float lowest = float.MaxValue;

        float stepSize = bounds.size.x / (samplingResolution - 1);

        for (int x = 0; x < samplingResolution; x++) {
            for (int z = 0; z < samplingResolution; z++) {
                float posX = bounds.position.x + x * stepSize;
                float posZ = bounds.position.z + z * stepSize;
                float height = sampler.Sample(posX, posZ) * sampler.HeightScale;

                if (height > highest) {
                    highest = height;
                }
                if (height < lowest) {
                    lowest = height;
                }
            }
        }

        bounds.position.y = lowest;
        bounds.size.y = (highest - lowest) * 1.05f; // Add in a tiny margin for error caused by subsampling

        return bounds;
    }

    public TreeNode this[int i] {
        get => _nodes[i];
        set => _nodes[i] = value;
    }

    public void Dispose() {
        _nodes.Dispose();
    }
}

/*
Todo: 
if we use Morton indexing there is no need for much of this structure
*/
[StructLayout(LayoutKind.Sequential)]
public struct TreeNode {
    public int payload;

    public Bounds bounds;
    public int depth;
    
    public int bl;
    public int br;
    public int tl;
    public int tr;

    public TreeNode(Bounds bounds, int depth) {
        this.bounds = bounds;
        this.depth = depth;
        payload = -1;
        bl = br = tl = tr = -1;
    }

    public unsafe int this[int idx] {
        // Index the 4 child indices as if they are an int[4] array
        // Todo: try fixed-size buffers:
        //  https://docs.microsoft.com/en-us/dotnet/csharp/programming-guide/unsafe-code-pointers/fixed-size-buffers

        get {
            fixed (int* p = &bl) {
                int* p2 = p;
                p2 += idx;
                return *p2;
            }
        }
        set {
            fixed (int* p = &bl) {
                int* p2 = p;
                p2 += idx;
                *p2 = value;
            }
        }
    }
}

public struct Bounds {
    public float3 position;
    public float3 size;

    public Bounds(float3 position, float3 size) {
        this.position = position;
        this.size = size;
    }
}

public static class mathi {
    public static int SumPowersOfFour(int n) {
        int n2 = n * n;
        return n * (6 * n2 * n + 9 * n2 + n - 1);
    }

    public static int SumPowersOfTwo(int n) {
        int n2 = n * n;
        return (2 * n2 * n + 3 * n2 + n) / 6;
    }

    public static int IntPow(int n, int pow) {
        int v = 1;
        while (pow != 0) {
            if ((pow & 1) == 1) {
                v *= n;
            }
            n *= n;
            pow >>= 1;
        }
        return v;
    }
}
