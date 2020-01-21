using UnityEngine;
using Unity.Mathematics;
using Unity.Collections;
using System.Runtime.CompilerServices;
using Unity.Jobs;
using System.Runtime.InteropServices;

// [BurstCompile] // Note: can't do this yet due to temp buffer creation inside job?
public struct ExpandQuadTreeJob : IJob {
    [ReadOnly] public int numLevels;

    [ReadOnly] public CameraInfo camInfo;
    [ReadOnly] public NativeSlice<float> lodDistances;

    public NativeArray<int> tree;

    public void Execute() {
        var stack = new NativeStack<int>(mathi.SumPowersOfTwo(numLevels), Allocator.TempJob);

        stack.Push(0);

        while (stack.Count > 0) {
            if (stack.Count < numLevels) {

                for (int i = 0; i < 4; i++) {
                    if (false) {
                        continue;
                    }
                }
                stack.Pop();
            } else {
                stack.Pop();
            }
        }

        stack.Dispose();
    }

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

    // Todo: z-order curve addressing structure
    public static class Tree {

        public static NativeArray<TreeNode> Create(int maxLevels, Allocator allocator) {
            return new NativeArray<TreeNode>(mathi.SumPowersOfTwo(maxLevels), allocator);
        }

        public static readonly TreeNode EmptyNode = new TreeNode() {
            payload = -1,
            bl = -1,
            br = -1,
            tl = -1,
            tr = -1
        };

        public static TreeBounds GetNodeBound(int idx) {
            return new TreeBounds {
                position = float3.zero,
                size = new float3(1f,1f,1f)
            };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TreeNode {
        public int payload;

        public int index;
        public int bl;
        public int br;
        public int tl;
        public int tr;

        public unsafe int this [int idx] {
            // Index the 4 child indices as if they are an int[4] array

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

    public struct TreeBounds {
        public float3 position;
        public float3 size;
    }
}

public static class mathi {
    public static int SumPowersOfTwo(int n) {
        return IntPow(2, n) - 1;
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
}
