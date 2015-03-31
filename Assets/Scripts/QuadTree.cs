using UnityEngine;
using System.Collections.Generic;

/*
 * Should we handle streaming and culling separately? If we temporarilly look up at the sky (no terrain drawn)
 * we don't want to *render* terrain, but we probably do want to have it *loaded* and ready to go. Stream based
 * on where we *are*, not so much based on what we're *looking at*. Only exception is zooming through a scope, really.
 *
 * Performance/Scaling thought: if a (purely functional) procedural source is used, resolution is arbitrary. Let players set range and resolution to arbitrary levels.
 * 
 * Todo:
 * - Implement partial child expansion
 * - Implement frustum culling
 *      - Render camera
 *      - Light sources
 */

public static class QuadTree {
   public static float[] GetLodDistances(int numLods, float lodZeroRange) {
        // Todo: this would be a lot easier to read if lod level indices were in reversed order
        float[] distances = new float[numLods];

        distances[numLods - 1] = lodZeroRange;
        for (int i = numLods - 2; i >= 0; i--) {
            distances[i] = distances[i + 1] * 2f;
        }

        return distances;
    }

    // Todo: partial child expansion (needs parent mesh partial vertex enable/disable)
    // Todo: optimize
    public static void ExpandNodeRecursively(
        int currentLod,
        QTNode node,
        CameraInfo cam,
        float[] lodDistances,
        IList<IList<QTNode>> selectedNodes,
        IHeightSampler sampler) {

        // If we're at the deepest lod level, no need to expand further
        if (currentLod == lodDistances.Length-1) {
            selectedNodes[currentLod].Add(node);
            return;
        }

        // If not, we should create children if we're in LOD range
        if (Intersect(node, cam, lodDistances[currentLod])) {
            node.Expand(sampler);

            for (int i = 0; i < node.Children.Length; i++) {
                ExpandNodeRecursively(currentLod + 1, node.Children[i], cam, lodDistances, selectedNodes, sampler);
            }
            return;
        }

        // If we don't need to expand, just add to the list (todo: can roll this into top if statement)
        selectedNodes[currentLod].Add(node);
    }

    private static bool Intersect(QTNode node, CameraInfo camInfo, float range) {
        return Intersect(node.Position, node.Position + node.Size, camInfo.Position, range);
    }

    private static bool Intersect(Vector3 bMin, Vector3 bMax, Vector3 sPos, float sRadius) {
        float sqrRadius = Sqr(sRadius);
        float minDist = 0f;
        for (int i = 0; i < 3; i++) {
            if      (sPos[i] < bMin[i]) minDist += Sqr(sPos[i] - bMin[i]);
            else if (sPos[i] > bMax[i]) minDist += Sqr(sPos[i] - bMax[i]);
        }
        return minDist <= sqrRadius;
    }

    private static float Sqr(float val) {
        return val*val;
    }

    /* Todo: use this to cull tiles during tree expansion */
    private static bool IntersectFrustum(CameraInfo info, QTNode node) {
        return true;
    }

    public static void Diff(IList<IList<QTNode>> a, IList<IList<QTNode>> b, IList<IList<QTNode>> result) {
        for (int i = 0; i < b.Count; i++) {
            for (int j = 0; j < b[i].Count; j++) {
                if (!FastContains(a[i], b[i][j])) {
                    result[i].Add(b[i][j]);
                }
            }
        }
    }

    /* Fast Contains function that avoids boxing in QTNode type */
    private static bool FastContains(IList<QTNode> list, QTNode node) {
        for (int i = 0; i < list.Count; i++) {
            if (list[i].FastEquals(node)) {
                return true;
            }
        }
        return false;
    }

    public static void DrawNodeRecursively(QTNode node, int currentLod, int maxLod) {
        Gizmos.color = Color.Lerp(Color.red, Color.green, currentLod / (float)maxLod);
        Gizmos.DrawWireCube(node.Center, node.Size);
        if (node.Children != null) {
            for (int i = 0; i < node.Children.Length; i++) {
                DrawNodeRecursively(node.Children[i], currentLod + 1, maxLod);
            }
        }
    }

    public static void DrawSelectedNodes(IList<IList<QTNode>> nodes) {
        for (int i = 0; i < nodes.Count; i++) {
            Gizmos.color = Color.Lerp(Color.red, Color.green, i / (float)nodes.Count);
            for (int j = 0; j < nodes[i].Count; j++) {
                var node = nodes[i][j];
                Gizmos.DrawWireCube(node.Center, node.Size);
            }
        }
    }
}

public class QTNode {
    private Vector3 _position;
    private Vector3 _size;
    private QTNode[] _children;

    public Vector3 Position { get { return _position; } }
    public Vector3 Size { get { return _size; } }
    public QTNode[] Children { get { return _children; } }
    public Vector3 Center { get { return _position + _size * 0.5f; } }

    public QTNode(Vector3 position, Vector3 size) {
        _position = position;
        _size = size;
    }

    public void Expand(IHeightSampler sampler) {
        _children = new QTNode[4];
        Vector3 halfSize = Size * 0.5f;

        _children[0] = new QTNode(_position + new Vector3(0f, 0f, 0f), halfSize);
        _children[1] = new QTNode(_position + new Vector3(0f, 0f, halfSize.z), halfSize);
        _children[2] = new QTNode(_position + new Vector3(halfSize.x, 0f, halfSize.z), halfSize);
        _children[3] = new QTNode(_position + new Vector3(halfSize.x, 0f, 0f), halfSize);

        for (int i = 0; i < _children.Length; i++) {
            _children[i].FitHeightSamples(sampler);
        }
    }

    /// <summary>
    /// Estimates node bounding box by taking scattered heightfield samples.
    /// </summary>
    private void FitHeightSamples(IHeightSampler sampler) {
        /* Todo: move this logic out of this class, too much business going on */

        const int samplingResolution = 4;

        float highest = float.MinValue;
        float lowest = float.MaxValue;

        float stepSize = Size.x/(samplingResolution-1);

        for (int x = 0; x < samplingResolution; x++) {
            for (int z = 0; z < samplingResolution; z++) {
                float posX = _position.x + x * stepSize;
                float posZ = _position.z + z * stepSize;
                float height = sampler.Sample(posX, posZ) * sampler.HeightScale;
                
                if (height > highest) {
                    highest = height;
                }
                if(height < lowest) {
                    lowest = height;
                }
            }
        }

        _position.y = lowest;
        _size.y = (highest - lowest) * 1.05f; // Add in a tiny margin for error caused by subsampling
    }

    /*
     * Todo: decide whether QTNode is a reference or value type. This is just weird.
     * 
     * It will be a reference type, as payloads are becoming non-trivial, and we want to
     * minimize garbage.
     */

    public bool FastEquals(QTNode other) {
        return Position == other.Position && Size == other.Size;
    }

    protected bool Equals(QTNode other) {
        return Position == other.Position && Size == other.Size;
    }

    public override bool Equals(object obj) {
        if (ReferenceEquals(null, obj)) {
            return false;
        }
        if (ReferenceEquals(this, obj)) {
            return true;
        }
        if (obj.GetType() != this.GetType()) {
            return false;
        }
        return Equals((QTNode) obj);
    }

    public override int GetHashCode() {
        unchecked {
            return (_position.GetHashCode()*397) ^ _size.GetHashCode();
        }
    }
}

public struct CameraInfo {
    public Vector3 Position;
    public Plane[] FrustumPlanes;

    public static CameraInfo Create(Camera camera) {
        return new CameraInfo() {
            Position = camera.transform.position,
            FrustumPlanes = GeometryUtility.CalculateFrustumPlanes(camera)
        };
    }
}