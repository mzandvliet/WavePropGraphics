using UnityEngine;
using Unity.Mathematics;
using Unity.Collections;
using System.Runtime.CompilerServices;
using Unity.Jobs;
using System.Runtime.InteropServices;
using Unity.Burst;

[BurstCompile]
public struct ExpandQuadTreeJob : IJob {
    [ReadOnly] public CameraInfo camInfo;
    [ReadOnly] public NativeSlice<float> lodDistances;

    public Tree tree;

    public void Execute() {
        var stack = new NativeStack<int>(mathi.SumPowersOfFour(tree.MaxLevels), Allocator.Temp);
        stack.Push(0);

        while (stack.Count > 0) {
            int nodeIdx = stack.Pop();
            var node = tree[nodeIdx];
            int depth = node.depth;

            // If we're at the deepest lod level, no need to expand further
            if (node.depth == tree.MaxLevels - 1) {
                node.payload = 1;
                tree[nodeIdx] = node;
                continue;
            }

            // If not, we should create children if we're in LOD range
            if (TreeUtil.Intersect(node.bounds, camInfo, lodDistances[depth])) {
                node = tree.Expand(nodeIdx);
                tree[nodeIdx] = node;

                for (int i = 0; i < 4; i++) {
                    stack.Push(node[i]);
                }

                continue;
            }

            // If we don't need to expand, just add to the list
            node.payload = 1;
            tree[nodeIdx] = node;
        }

        stack.Dispose();
    }
}

[BurstCompile]
public struct DiffQuadTreesJob : IJob {
    [ReadOnly] public Tree a;
    [ReadOnly] public Tree b;

    public NativeList<int> diff;

    public void Execute() {
        diff.Clear();

        for (int i = 0; i < a.Count; i++) {
            if (a[i].payload != 0 && !b.Contains(a[i].bounds)) {
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

    public static int GetMortonTreeIndex(int depth, int x, int y) {
        return mathi.SumPowersOfFour(depth-1) + Morton.Code2d(x, y);
    }
}

// Todo: z-order curve addressing structure will simplify all of this structure
public struct Tree : System.IDisposable {
    private NativeList<TreeNode> _nodes;
    private HeightSampler _heights;

    public int MaxLevels {
        get;
        private set;
    }

    public int Count {
        get => _nodes.Length;
    }

    public Tree(Bounds bounds, int maxLevels, HeightSampler heights, Allocator allocator) {
        _nodes = new NativeList<TreeNode>(mathi.SumPowersOfFour(maxLevels), allocator);
        MaxLevels = maxLevels;
        _heights = heights;
        NewNode(bounds, 0);
    }

    public void Clear(Bounds bounds) {
        _nodes.Clear();
        NewNode(bounds, 0);
    }

    public void Dispose() {
        _nodes.Dispose();
    }

    private int NewNode(Bounds bounds, int depth) {
        int idx = _nodes.Length;
        var node = new TreeNode(bounds, depth);
        _nodes.Add(node);
        return idx;
    }

    public TreeNode Expand(int idx) {
        var node = _nodes[idx];
        var halfSize = node.bounds.size / 2;

        var bl = new Bounds(node.bounds.position + new int3(0, 0, 0), halfSize);
        var tl = new Bounds(node.bounds.position + new int3(0, 0, halfSize.z), halfSize);
        var tr = new Bounds(node.bounds.position + new int3(halfSize.x, 0, halfSize.z), halfSize);
        var br = new Bounds(node.bounds.position + new int3(halfSize.x, 0, 0), halfSize);

        bl = FitHeightSamples(bl, _heights);
        tl = FitHeightSamples(tl, _heights);
        tr = FitHeightSamples(tr, _heights);
        br = FitHeightSamples(br, _heights);

        node[0] = NewNode(bl, node.depth + 1);
        node[1] = NewNode(tl, node.depth + 1);
        node[2] = NewNode(tr, node.depth + 1);
        node[3] = NewNode(br, node.depth + 1);

        _nodes[idx] = node;

        return node; // Convenience, since caller's old copy of Node data will be invalidated
    }

    private static Bounds FitHeightSamples(Bounds bounds, HeightSampler sampler) {
        /* Todo: move this logic out of this class, too much business going on */

        const int samplingResolution = 8;

        int highest = int.MinValue;
        int lowest = int.MaxValue;

        float stepSize = bounds.size.x / (samplingResolution - 1);

        for (int x = 0; x < samplingResolution; x++) {
            for (int z = 0; z < samplingResolution; z++) {
                float posX = bounds.position.x + x * stepSize;
                float posZ = bounds.position.z + z * stepSize;
                float height = sampler.Sample(posX, posZ) * sampler.HeightScale;

                if (height > highest) {
                    highest = (int)height;
                }
                if (height < lowest) {
                    lowest = (int)height;
                }
            }
        }

        bounds.position.y = lowest;
        bounds.size.y = highest - lowest;

        return bounds;
    }

    /* Important: this returns by copy, not by reference. Don't forget to
    explicitly write back new values if you change them! */
    public TreeNode this[int i] {
        get => _nodes[i];
        set => _nodes[i] = value;
    }

    public bool Contains(Bounds bounds) {
        for (int i = 0; i < _nodes.Length; i++) {
            if (_nodes[i].bounds == bounds) {
                return true;
            }
        }
        return false;
    }
}

/*
Todo: 
if we use Morton indexing there is no need for much of this structure
*/
[StructLayout(LayoutKind.Sequential)]
public unsafe struct TreeNode {
    public int payload;

    public Bounds bounds;
    public int depth;
    
    private fixed int children[4];

    public TreeNode(Bounds bounds, int depth) {
        this.bounds = bounds;
        this.depth = depth;
        payload = -1;

        fixed(int* p = children) {
            for (int i = 0; i < 4; i++) {
                p[i] = -1;
            }
        }
    }

    public unsafe int this[int idx] {
        // Index the 4 child indices as if they are an int[4] array

        get {
            fixed (int* p = children) {
                return p[idx];
            }
        }
        set {
            fixed (int* p = children) {
                p[idx] = value;
            }
        }
    }
}

public struct Bounds {
    // These are in METERS
    public int3 position;
    public int3 size;

    public Bounds(int3 position, int3 size) {
        this.position = position;
        this.size = size;
    }

    public override bool Equals(System.Object obj) {
        return obj is Bounds && this == (Bounds)obj;
    }
    public override int GetHashCode() {
        return position.GetHashCode() ^ size.GetHashCode();
    }
    public static bool operator ==(Bounds x, Bounds y) {
        return x.GetHashCode() == y.GetHashCode();
    }
    public static bool operator !=(Bounds x, Bounds y) {
        return !(x == y);
    }

    public override string ToString() {
        return string.Format("[Pos: {0}, Size: {1}]", position, size);
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

    public static ref int RefReturnTest(int[] values, int i) {
        return ref values[i];
    }
}
