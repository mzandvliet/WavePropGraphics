using System;
using UnityEngine;
using System.Collections.Generic;
using System.Runtime.Remoting.Messaging;

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
    public static IList<IList<QTNode>> ExpandNodesToList(Vector3 rootPosition, float lodZeroSize, float[] lodDistances, CameraInfo cam) {
        var root = new QTNode(rootPosition, lodZeroSize);

        IList<IList<QTNode>> selectedNodes = new List<IList<QTNode>>(lodDistances.Length);
        for (int i = 0; i < lodDistances.Length; i++) {
            selectedNodes.Add(new List<QTNode>());
        }

        ExpandNodeRecursively(0, root, cam, lodDistances, selectedNodes);

        return selectedNodes;
    }

    // Todo: partial child expansion (needs parent mesh partial vertex enable/disable)
    // Todo: optimize
    public static void ExpandNodeRecursively(int currentLod, QTNode node, CameraInfo cam, float[] lodDistances, IList<IList<QTNode>> selectedNodes) {
        // If we're at the deepest lod level, no need to expand further
        if (currentLod == lodDistances.Length-1) {
            selectedNodes[currentLod].Add(node);
            return;
        }

        // If not, we should create children if we're in LOD range
        if (Intersect(node, cam, lodDistances[currentLod])) {
            if (node.Children == null) {
                node.CreateChildren();
            }

            for (int i = 0; i < node.Children.Length; i++) {
                ExpandNodeRecursively(currentLod + 1, node.Children[i], cam, lodDistances, selectedNodes);
            }
            return;
        }

        // If we don't need to expand, just add to the list (todo: can roll into top if statement)
        selectedNodes[currentLod].Add(node);
    }

    // Todo: this check should be 3D, but is only on the horizontal plane right now
    // Todo: optimize
    private static bool Intersect(QTNode node, CameraInfo camInfo, float range) {
        float halfSize = node.Size*0.5f;
        return
            Vector3.Distance(node.Center + new Vector3(-halfSize, 256f, -halfSize), camInfo.Position) < range ||
            Vector3.Distance(node.Center + new Vector3(-halfSize, 256f, halfSize), camInfo.Position) < range ||
            Vector3.Distance(node.Center + new Vector3(halfSize, 256f, -halfSize), camInfo.Position) < range ||
            Vector3.Distance(node.Center + new Vector3(halfSize, 256f, halfSize), camInfo.Position) < range;
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

    private static void DrawQuad(Vector3 center, float size) {
        float halfSize = size * 0.5f;
        Gizmos.DrawRay(center - Vector3.right * halfSize - Vector3.forward * halfSize, Vector3.forward * size);
        Gizmos.DrawRay(center - Vector3.right * halfSize + Vector3.forward * halfSize, Vector3.right * size);
        Gizmos.DrawRay(center + Vector3.right * halfSize + Vector3.forward * halfSize, Vector3.back * size);
        Gizmos.DrawRay(center + Vector3.right * halfSize - Vector3.forward * halfSize, Vector3.left * size);
    }
}

public class QTNode {
    public QTNode[] Children { get; private set; }
    public Vector2 Center { get; private set; }
    public float Size { get; private set; }
    public float Bottom { get; private set; }
    public float Top { get; private set; }

    public QTNode(Vector2 center, float size) {
        Center = center;
        Size = size;
    }

    public void CreateChildren() {
        Children = new QTNode[4];
        float halfSize = Size*0.5f;
        float quarterSize = Size*0.25f;
        Children[0] = new QTNode(Center + new Vector2(-quarterSize, -quarterSize), halfSize);
        Children[1] = new QTNode(Center + new Vector2(-quarterSize,  quarterSize), halfSize);
        Children[2] = new QTNode(Center + new Vector2( quarterSize,  quarterSize), halfSize);
        Children[3] = new QTNode(Center + new Vector2( quarterSize, -quarterSize), halfSize);
    }

    /// <summary>
    /// Estimates node bounding box by taking scattered heightfield samples.
    /// </summary>
    /// <param name="heightFunc">The func definining the heightfield, which can be sampled at arbitrary worldspace points</param>
    /// <param name="random">Random object used for sampling</param>
    public void GenerateBoundingBox(Func<Vector2, float> heightFunc, System.Random random) {
        const int NumHeightSamples = 16;

        /* Todo: could sample four corners, midpoint, and several random samples. Maybe random isn't even needed?
         * Can also assume that sub-tile height range is always smaller than parent-tile's.
         */

        float highest = float.MinValue;
        float lowest = float.MaxValue;
        for (int i = 0; i < NumHeightSamples; i++) {
            float height = heightFunc(Center);
            if (height > highest) {
                highest = height;
            } else if(height < lowest) {
                lowest = height;
            }
        }

        Top = highest;
        Bottom = lowest;
    }

    /*
     * Todo: decide whether QTNode is a reference or value type. This is just weird.
     * 
     * It will be a reference type, as payloads are becoming non-trivial, and we want to
     * minimize garbage.
     */

    protected bool Equals(QTNode other) {
        return Center == other.Center && Math.Abs(Size - other.Size) < float.Epsilon;
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
            return (Center.GetHashCode()*397) ^ Size.GetHashCode();
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