using System.Collections.Generic;
using UnityEngine;

/*
 * Make a pool of X^2 resolution vert meshes
 * Walk quadtree tree each frame to find the payloads that should be streamed/generated
 *      LOD level is based on 3D distance to the camera
 * Get a mesh from the pool and stream data into it
 * 
 * Todo: 
 * 
 * Quadtree rendering algorithm
 * - Expand quaddree nodes based on distance rule and apply frustum culling
 * - Gather leaves into a list (this list contains all nodes that should be visible)
 * - Diff list with list from last frame
 * - Load/Unload tile data based on the diff
 * - Render the currently loaded tile set
 * 
 * - Figure out how to disable the 4 quadrants of a tile without cpu overhead
 * 
 * - Separate culling passes to find visual and shadow caster tiles, use pass tags to render them differently
 *      - https://gist.github.com/pigeon6/4237385
 * 
 *
 * The higher above the terrain you are, the fewer high-res patches are loaded. These could then be used so show even farther away terrain.
 */
public class TerrainSystem : MonoBehaviour {
    [SerializeField] private Material _material;
    [SerializeField] private Camera _camera;
    [SerializeField] private int _tileResolution = 16;
    [SerializeField] private int _numLods = 10;
    [SerializeField] private float _lodZeroRange = 32f;

    private Texture2D _heightmap;
    private float[] _lodDistances;

    private Stack<TerrainMesh> _meshPool;
    private IDictionary<QTNode, TerrainMesh> _activeMeshes;  
    private IList<IList<QTNode>> _loadedNodes;

    void Awake() {
        _heightmap = GenerateHeightmapFlat(_tileResolution);
        _lodDistances = QuadTree.GetLodDistances(_numLods, _lodZeroRange);

        _loadedNodes = new List<IList<QTNode>>();
        for (int i = 0; i < _numLods; i++) {
            _loadedNodes.Add(new List<QTNode>());
        }

        const int numTiles = 392;
        _meshPool = new Stack<TerrainMesh>();
        for (int i = 0; i < numTiles; i++) {
            var mesh = CreateMesh(_tileResolution, _material);
            mesh.Transform.parent = transform;
            mesh.GameObject.SetActive(false);
            _meshPool.Push(mesh);
        }

        _activeMeshes = new Dictionary<QTNode, TerrainMesh>();
    }

    /*
     * -- Configuration --
     * 
     * Make easier-to-use parameters. Like, at max lod I want 1px/m resolution.
     * 
     * -- Architecture --
     * 
     * We are receiving a new list of quadtree nodes each time, which we need to compare with the old list.
     * 
     * Testing equality of nodes is iffy. Nodes are reference types in one sense, but then value types later.
     * Also, if nodes have more data to them it makes more sense to keep them as reference type. Caching and
     * reusing a single tree instead of generating an immutable tree each frame and diffing.
     * 
     * We could mark nodes on a persistent tree structure dirty if some action is need. Update this dirty state
     * when validating the tree each frame, and creating jobs frome there.
     * 
     * We currently use three collections to manage node and mesh instances. It's unwieldy.
     * 
     * -- Performance --
     * 
     * Creating a class QTNode-based tree each frame means heap-related garbage. Creating a recursive struct QTNode
     * is impossible because a struct can not have members of its own type.
     * 
     * We could pool QTNodes
     * 
     * We could use a single mesh instance on the GPU, but we still need separate mesh instances on the cpu for tiles
     * we want colliders on, which is every lod > x
     */

    private void Update() {
        var requiredNodes = QuadTree.ExpandNodesToList(16384f, _lodDistances, CameraInfo.Create(_camera));

        var toUnload = QuadTree.Diff(requiredNodes, _loadedNodes);
        var toLoad = QuadTree.Diff(_loadedNodes, requiredNodes);

        Unload(toUnload);
        Load(toLoad);
    }

    private void Unload(IList<IList<QTNode>> toUnload) {
        for (int i = 0; i < toUnload.Count; i++) {
            for (int j = 0; j < toUnload[i].Count; j++) {
                var node = toUnload[i][j];
                var mesh = _activeMeshes[node];
                _meshPool.Push(mesh);
                _loadedNodes[i].Remove(node);
                _activeMeshes.Remove(node);
                mesh.GameObject.SetActive(false);
            }
        }
    }

    private void Load(IList<IList<QTNode>> toLoad) {
        for (int i = 0; i < toLoad.Count; i++) {
            var lodNodes = toLoad[i];
            var lerpRanges = new Vector4(_lodDistances[i]*1.66f, _lodDistances[i]*1.9f);

            for (int j = 0; j < lodNodes.Count; j++) {
                var node = lodNodes[j];
                var mesh = _meshPool.Pop();
                _activeMeshes.Add(node, mesh);

                Vector3 scale = Vector3.one*node.Size;

                mesh.GameObject.name = "Terrain_LOD_" + i;
                mesh.Transform.position = node.Center - (new Vector3(node.Size*0.5f, 0f, node.Size*0.5f));
                mesh.Transform.localScale = scale;
                mesh.MeshRenderer.material.SetFloat("_Scale", scale.x);
                mesh.MeshRenderer.material.SetVector("_LerpRanges", lerpRanges);
                mesh.SetHeightmap(_heightmap);
                mesh.GameObject.SetActive(true);

                _loadedNodes[i].Add(node);
            }
        }
    }

    private static TerrainMesh CreateMesh (int resolution, Material material) {
	    var tile = new TerrainMesh(resolution);
	    tile.MeshRenderer.material = material;

        tile.MeshRenderer.material.SetFloat("_Scale", 16f);
        tile.MeshRenderer.material.SetVector("_LerpRanges", new Vector4(1f, 16f));

	    return tile;
	}

    private static Texture2D GenerateHeightmapFlat(int resolution) {
        var heightmap = new Texture2D(resolution, resolution, TextureFormat.ARGB32, false, true);
        for (int x = 0; x < resolution; x++) {
            for (int y = 0; y < resolution; y++) {
                float height = 0f;
                heightmap.SetPixel(x, y, new Color(height, height, height, 1f));
            }
        }
        heightmap.Apply(false);
        return heightmap;
    }

    private static Texture2D GenerateHeightmapSine(int resolution) {
        var heightmap = new Texture2D(resolution, resolution, TextureFormat.ARGB32, false, true);
        for (int x = 0; x < resolution; x++) {
            for (int y = 0; y < resolution; y++) {
                float height = Mathf.Sin(x/(float) resolution*Mathf.PI)*Mathf.Sin(y/(float) resolution*Mathf.PI);
                heightmap.SetPixel(x, y, new Color(height, height, height, 1f));
            }
        }
        heightmap.Apply(false);
        return heightmap;
    }

    private void OnDrawGizmos() {
        var nodes = QuadTree.ExpandNodesToList(16384f, QuadTree.GetLodDistances(8, 64f), CameraInfo.Create(_camera));
        QuadTree.DrawSelectedNodes(nodes);
    }
}

public class TerrainMesh {
    private GameObject _gameObject;
    private Transform _transform;
    private Mesh _mesh;
    private MeshFilter _meshFilter;
    private MeshRenderer _meshRenderer;

    private int _resolution;
    private int _indexEndTl;
    private int _indexEndTr;
    private int _indexEndBl;
    private int _indexEndBr;

    public GameObject GameObject {
        get { return _gameObject; }
    }

    public Transform Transform {
        get { return _transform; }
    }

    public Mesh Mesh {
        get { return _mesh; }
    }

    public MeshFilter MeshFilter {
        get { return _meshFilter; }
    }

    public MeshRenderer MeshRenderer {
        get { return _meshRenderer; }
    }

    public int Resolution {
        get { return _resolution; }
    }

    public int IndexEndTl {
        get { return _indexEndTl; }
    }

    public int IndexEndTr {
        get { return _indexEndTr; }
    }

    public int IndexEndBl {
        get { return _indexEndBl; }
    }

    public int IndexEndBr {
        get { return _indexEndBr; }
    }

    public TerrainMesh(int resolution) {
        _gameObject = new GameObject("TerrainMesh");
        _transform = _gameObject.transform;

        if (!Mathf.IsPowerOfTwo(resolution)) {
            resolution = Mathf.ClosestPowerOfTwo(resolution);
        }

        _resolution = resolution;
        _meshFilter = _gameObject.AddComponent<MeshFilter>();
        _meshRenderer = _gameObject.AddComponent<MeshRenderer>();
        
        CreateMesh(resolution);
    }

    public void SetHeightmap(Texture2D heightmap) {
        MeshRenderer.material.SetTexture("_HeightTex", heightmap);
    }

    private void CreateMesh(int resolution) {
        int vertCount = (resolution + 1);
        _mesh = new Mesh();

        var vertices = new Vector3[vertCount*vertCount];
        var triangles = new int[resolution * resolution * 2 * 3];
        var uv = new Vector2[vertCount * vertCount];
        var normals = new Vector3[vertCount * vertCount];

        /* Create vertices */

        for (int x = 0; x < vertCount; x++) {
            for (int y = 0; y < vertCount; y++) {
                vertices[x + vertCount*y] = new Vector3(x/(float)resolution, 0f, y/(float)resolution);
                uv[x + vertCount * y] = new Vector2(x/(float)resolution, y/(float)resolution);
                normals[x + vertCount * y] = Vector3.up;
            }
        }

        /* Create triangle indices */

        int index = 0;
        int halfRes = resolution/2;

        CreateIndicesForQuadrant(triangles, vertCount, ref index, 0, halfRes, 0, halfRes);
        _indexEndTl = index;

        CreateIndicesForQuadrant(triangles, vertCount, ref index, 0, halfRes, halfRes, resolution);
        _indexEndTr = index;

        CreateIndicesForQuadrant(triangles, vertCount, ref index, halfRes, resolution, 0, halfRes);
        _indexEndBl = index;

        CreateIndicesForQuadrant(triangles, vertCount, ref index, halfRes, resolution, halfRes, resolution);
        _indexEndBr = index;

        _mesh.vertices = vertices;
        _mesh.triangles = triangles;
        _mesh.normals = normals;
        _mesh.uv = uv;

        /* We can set these manually with the knowledge we have during content
         * streaming (todo: autocalc bounds fails here for some reason, why?)
         */
        _mesh.bounds = new Bounds(new Vector3(8f, 8f, 8f), new Vector3(16f, 16f, 16f));
        
        _meshFilter.mesh = _mesh;
    }

    private static void CreateIndicesForQuadrant(int[] triangles, int vertCount, ref int index, int yStart, int yEnd, int xStart, int xEnd) {
        for (int y = xStart; y < xEnd; y++) {
            for (int x = yStart; x < yEnd; x++) {
                triangles[index++] = x + vertCount * (y + 1);
                triangles[index++] = (x + 1) + vertCount * y;
                triangles[index++] = x + vertCount * y;

                triangles[index++] = x + vertCount * (y + 1);
                triangles[index++] = (x + 1) + vertCount * (y + 1);
                triangles[index++] = (x + 1) + vertCount * y;
            }
        }
    }
}
