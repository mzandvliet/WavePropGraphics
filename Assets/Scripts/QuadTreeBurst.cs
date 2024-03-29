using Unity.Mathematics;
using Unity.Collections;
using System.Runtime.CompilerServices;
using Unity.Jobs;
using System.Runtime.InteropServices;
using Unity.Burst;

/*
Todo: 

- Use height bound samples from wave sim, instead of resampling here

- Pure Morton addressing scheme

- culling using Quadtree traversal and TestPlanesAABB

- Post-fix expanded quadtree to ensure neighbors differ by no
more than one level of depth. Or mathematically guarantee that
expansion algorithm always yields such a balanced tree.
*/

[BurstCompile]
public struct ExpandQuadTreeJob : IJob {
    [ReadOnly] public CameraInfo camInfo;
    [ReadOnly] public NativeSlice<float> lodDistances;
    [ReadOnly] public WaveSampler waveSampler;

    public Tree tree;
    public NativeList<TreeNode> visibleSet;

    public void Execute() {
        visibleSet.Clear();

        var stack = new NativeStack<int2>(mathi32.SumPowersOfFour(tree.MaxDepth), Allocator.Temp);
        stack.Push(0);

        while (stack.Count > 0) {
            int2 mortonIdx = stack.Pop();
            var node = tree[mortonIdx];

            // If we're at the deepest lod level, no need to expand further
            if (node.depth == tree.MaxDepth - 1) {
                visibleSet.Add(node);
                continue;
            }

            // If not, we should create children if we're in LOD range
            if (TreeUtil.Intersect(node.bounds, camInfo, lodDistances[node.depth] * 1.15f)) {
                node = tree.Open(mortonIdx, waveSampler);

                int childBase = mortonIdx.x << 2;
                for (int i = 0; i < 4; i++) {
                    stack.Push(new int2(childBase | i, node.depth+1));
                }

                continue;
            }

            // Current node is visible, and at acceptable LOD
            visibleSet.Add(node);
        }

        stack.Dispose();
    }
}

[BurstCompile]
public struct DiffQuadTreesJob : IJob {
    [ReadOnly] public NativeArray<TreeNode> a;
    [ReadOnly] public NativeArray<TreeNode> b;

    public NativeList<TreeNode> diff;

    /*
    Returns: all items in A that are not in B
    */
    public void Execute() {
        diff.Clear();

        for (int i = 0; i < a.Length; i++) {
            if (!a[i].hasChildren && !b.Contains(a[i])) {
                diff.Add(a[i]);
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
    public static float Sqr(float x) {
        return x * x;
    }
}

// Todo: z-order curve addressing structure will simplify all of this structure
public struct Tree : System.IDisposable {
    private NativeHashMap<int2, TreeNode> _nodes;
    

    public int MaxDepth {
        get;
        private set;
    }

    public int Count {
        get => _nodes.Length;
    }

    public NativeHashMap<int2, TreeNode> Nodes{
        get => _nodes;
    }

    public Tree(Bounds bounds, int maxLevels, Allocator allocator) {
        _nodes = new NativeHashMap<int2, TreeNode>(mathi32.SumPowersOfFour(maxLevels), allocator);
        MaxDepth = maxLevels;

        var root = new TreeNode(bounds, 0);
        _nodes.TryAdd(0, root);
    }

    public void Clear(Bounds bounds) {
        _nodes.Clear();
        var root = new TreeNode(bounds, 0);
        _nodes.TryAdd(0, root);
    }

    public void Dispose() {
        _nodes.Dispose();
    }

    public TreeNode Open(int2 mortonIdx, WaveSampler sampler) {
        var node = _nodes[mortonIdx];
        var halfSize = node.bounds.size / 2;

        var bl = new Bounds(node.bounds.position + new int3(0, 0, 0), halfSize);
        var tl = new Bounds(node.bounds.position + new int3(0, 0, halfSize.z), halfSize);
        var tr = new Bounds(node.bounds.position + new int3(halfSize.x, 0, halfSize.z), halfSize);
        var br = new Bounds(node.bounds.position + new int3(halfSize.x, 0, 0), halfSize);

        bl = FitHeightSamples(bl, sampler);
        tl = FitHeightSamples(tl, sampler);
        tr = FitHeightSamples(tr, sampler);
        br = FitHeightSamples(br, sampler);

        int childBase = mortonIdx.x << 2;
        int childDepth = node.depth + 1;

        int2 blIdx = new int2(childBase | 0b00, childDepth);
        int2 brIdx = new int2(childBase | 0b01, childDepth);
        int2 tlIdx = new int2(childBase | 0b10, childDepth);
        int2 trIdx = new int2(childBase | 0b11, childDepth);

        _nodes.TryAdd(blIdx, new TreeNode(bl, childDepth));
        _nodes.TryAdd(brIdx, new TreeNode(br, childDepth));
        _nodes.TryAdd(tlIdx, new TreeNode(tl, childDepth));
        _nodes.TryAdd(trIdx, new TreeNode(tr, childDepth));

        node.hasChildren = true;
        _nodes[mortonIdx] = node;

        return node; // Convenience, since caller's old copy of Node data will be invalidated
    }

    private static Bounds FitHeightSamples(Bounds bounds, WaveSampler sampler) {
        /* 
        Todo: determine min/max height for a given bound earlier in the process,
        possibly letting wave simulator keep track of it per LOD, as it passes
        over all the data anyway.
        */

        int lowest = 999;
        int highest = -1;

        const int samplingResolution = 4;

        float stepSize = bounds.size.x / (float)(samplingResolution - 1);

        for (int x = 0; x < samplingResolution; x++) {
            for (int z = 0; z < samplingResolution; z++) {
                float posX = bounds.position.x + x * stepSize;
                float posZ = bounds.position.z + z * stepSize;
                float3 sample = sampler.Sample(posX, posZ);
                float height = (0.5f + 0.5f * sample.x) * sampler.heightScale;

                if (height > highest) {
                    highest = (int)height;
                }
                if (height < lowest) {
                    lowest = (int)height;
                }
            }
        }

        bounds.position.y = lowest;
        bounds.size.y = highest - lowest + 1;

        return bounds;
    }

    /* Important: this returns by copy, not by reference. Don't forget to
    explicitly write back new values if you change them! */
    public TreeNode this[int2 i] {
        get => _nodes[i];
        set => _nodes[i] = value;
    }
}

/*
Todo: 
if we use Morton indexing there is no need for much of this structure
*/
[StructLayout(LayoutKind.Sequential)]
public unsafe struct TreeNode : System.IEquatable<TreeNode> {
    public Bounds bounds;
    public int depth;
    public bool hasChildren;
    
    public TreeNode(Bounds bounds, int depth) {
        this.bounds = bounds;
        this.depth = depth;
        this.hasChildren = false;
    }

    public override bool Equals(System.Object obj) {
        return obj is TreeNode && this == (TreeNode)obj;
    }

    public bool Equals(TreeNode other) {
        return this == other;
    }

    public override int GetHashCode() {
        return bounds.GetHashCode();
    }

    public static bool operator ==(TreeNode a, TreeNode b) {
        return
            a.bounds.position.x == b.bounds.position.x &&
            a.bounds.position.z == b.bounds.position.z &&
            a.depth == b.depth;
    }

    public static bool operator !=(TreeNode a, TreeNode b) {
        return !(a == b);
    }

    public override string ToString() {
        return string.Format("[D{0}: {1}]", depth, bounds);
    }
}

public struct Bounds : System.IEquatable<Bounds> {
    // These are in METERS
    public int3 position;
    public int3 size;

    public float3 Min {
        get => position;
    }

    public float3 Max {
        get => position + size;
    }

    public Bounds(int3 position, int3 size) {
        this.position = position;
        this.size = size;
    }

    public override bool Equals(System.Object obj) {
        return obj is Bounds && this == (Bounds)obj;
    }

    public bool Equals(Bounds other) {
        return this == other;
    }

    public override int GetHashCode() {
        // return position.GetHashCode() ^ size.x.GetHashCode();
        unchecked {
            return (position.xz.GetHashCode() * 397) ^ size.x.GetHashCode();
        }
    }
    
    public static bool operator ==(Bounds a, Bounds b) {
        return 
            a.position.x == b.position.x &&
            a.position.z == b.position.z &&
            a.size.x == b.size.x;
    }
    public static bool operator !=(Bounds a, Bounds b) {
        return !(a == b);
    }

    public override string ToString() {
        return string.Format("[Pos: {0}, Size: {1}]", position, size);
    }
}

public static class mathi32 {
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
