using System;
using UnityEngine;
using System.Collections.Generic;

/*
 * Just occurred to me: for quadtree traversal and culling the nodes need height information.
 * Else, if you're at a mountaintop at 4km, you'll still be 4km from the node, and thus at low lod.
 * Thus, nodes need some min/max information (bounding box)
 *
 * Should we handle streaming and culling separately? If we temporarilly look up at the sky (no terrain drawn)
 * we don't want to *render* terrain, but we probably do want to have it *loaded* and ready to go. Stream based
 * on where we *are*, not so much based on what we're *looking at*. Only exception is zooming through a scope, really.
 *
 * Performance/Scaling thought: if procedural source is used, resolution is arbitrary. Let players set range and resolution to arbitrary levels.
 * 
 * Todo:
 * - Produce a list of all required tiles, their positions and lod levels that should be rendered
 * - Implement partial child expansion
 * - Implement frustum culling
 *      - Render camera
 *      - Light sources
 *
 * Could use Bounds and GeometryUtility from UnityEngine
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

    /* This does two things: expand the quadtree based on distance rule and culling, and parses the results
     * to a handy to use list for streaming. Could be two separate functions, but that would be less speedy
     */
    public static IList<IList<QTNode>> ExpandNodesToList(
        Vector3 rootPosition,
        Vector3 lodZeroSize,
        float[] lodDistances,
        CameraInfo cam,
        IHeightSampler sampler) {
        var root = new QTNode(rootPosition, lodZeroSize);

        IList<IList<QTNode>> selectedNodes = new List<IList<QTNode>>(lodDistances.Length);
        for (int i = 0; i < lodDistances.Length; i++) {
            selectedNodes.Add(new List<QTNode>());
        }

        ExpandNodeRecursively(0, root, cam, lodDistances, selectedNodes, sampler);

        return selectedNodes;
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
            if (node.Children == null) {
                node.CreateChildren(sampler);
            }

            for (int i = 0; i < node.Children.Length; i++) {
                ExpandNodeRecursively(currentLod + 1, node.Children[i], cam, lodDistances, selectedNodes, sampler);
            }
            return;
        }

        // If we don't need to expand, just add to the list (todo: can roll into top if statement)
        selectedNodes[currentLod].Add(node);
    }

    // Todo: this check should be 3D, using QTNode min/max height, but is only on the horizontal plane right now
    // Todo: optimize
    private static bool Intersect(QTNode node, CameraInfo camInfo, float range) {
        return Intersect(node.Position, node.Position + node.Size, camInfo.Position, range);
    }

    private static bool Intersect(Vector3 bMin, Vector3 bMax, Vector3 c, float r) {
        float sqrDist = r*r;
        float minDist = 0f;
        for (int i = 0; i < 3; i++) {
            if (c[i] < bMin[i]) minDist += Square(c[i] - bMax[i]);
            else if (c[i] > bMax[i]) minDist += Square(c[i] - bMax[i]);
        }
        return minDist <= sqrDist;
    }

    private static float Square(float val) {
        return val*val;
    }

    private static bool IntersectFrustum(CameraInfo info, QTNode node) {
        return true;
    }

    public static IList<IList<QTNode>> Diff(IList<IList<QTNode>> a, IList<IList<QTNode>> b) {
        IList<IList<QTNode>> result = new List<IList<QTNode>>();

        for (int i = 0; i < b.Count; i++) {
            result.Add(new List<QTNode>());
            for (int j = 0; j < b[i].Count; j++) {
                if (!a[i].Contains(b[i][j])) {
                    result[i].Add(b[i][j]);
                }
            }
        }

        return result;
    } 

    public static void DrawNodeRecursively(QTNode node, int currentLod, int maxLod) {
        Gizmos.color = Color.Lerp(Color.red, Color.green, currentLod / (float)maxLod);
        DrawQuad(node.Center, node.Size);
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
                DrawQuad(node.Center, node.Size);
            }
        }
    }

    private static void DrawQuad(Vector3 center, Vector3 size) {
        Gizmos.DrawWireCube(center, size);
    }
}

public class QTNode {
    private Vector3 _position;
    private Vector3 _size;

    public Vector3 Position { get { return _position; } set { _position = value; } }
    public Vector3 Size { get { return _size; } set { _size = value; } }

    public Vector3 Center { get { return Position + Size * 0.5f; } }
    
    public float Left { get { return Center.x - Size.x * 0.5f; } }
    public float Right { get { return Center.x + Size.x * 0.5f; } }
    public float Bottom { get { return Center.y - Size.y * 0.5f; } }
    public float Top { get { return Center.y + Size.y * 0.5f; } }
    public float Back { get { return Center.z - Size.z * 0.5f; } }
    public float Front { get { return Center.z + Size.z * 0.5f; } }

    public QTNode[] Children { get; private set; }

    public QTNode(Vector3 position, Vector3 size) {
        Position = position;
        Size = size;
    }

    public void CreateChildren(IHeightSampler sampler) {
        Children = new QTNode[4];
        float halfSize = Size.x*0.5f;

        Children[0] = new QTNode(Position + new Vector3(0f, 0f, 0f), new Vector3(halfSize, 0f, halfSize));
        Children[1] = new QTNode(Position + new Vector3(0f, 0f, halfSize), new Vector3(halfSize, 0f, halfSize));
        Children[2] = new QTNode(Position + new Vector3(halfSize, 0f, halfSize), new Vector3(halfSize, 0f, halfSize));
        Children[3] = new QTNode(Position + new Vector3(halfSize, 0f, 0f), new Vector3(halfSize, 0f, halfSize));

        for (int i = 0; i < 4; i++) {
            Children[i].FitHeightSamples(sampler);
        }
    }

    /// <summary>
    /// Estimates node bounding box by taking scattered heightfield samples.
    /// </summary>
    private void FitHeightSamples(IHeightSampler sampler) {
        const int samplingResolution = 4;

        float highest = float.MinValue;
        float lowest = float.MaxValue;

        for (int x = 0; x < samplingResolution; x++) {
            for (int z = 0; z < samplingResolution; z++) {
                float posX = _position.x + (x / (float) samplingResolution) * Size.x;
                float posZ = _position.y + (z / (float) samplingResolution) * Size.z;
                float height = sampler.Sample(posX, posZ) * 512f; // Todo: get height scale from config, obv.
                
                if (height > highest) {
                    highest = height;
                } else if(height < lowest) {
                    lowest = height;
                }
            }
        }

        _position.y = lowest;
        _size.y = highest - lowest;
    }

    /*
     * Todo: decide whether QTNode is a reference or value type. This is just weird.
     * 
     * It will be a reference type, as payloads are becoming non-trivial, and we want to
     * minimize garbage.
     */

    protected bool Equals(QTNode other) {
        return _position.Equals(other._position) && _size.Equals(other._size);
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