using System;
using System.Collections.Generic;
using CoherentNoise.Generation.Fractal;
using UnityEngine;

/*
 * Make a pool of X^2 resolution vert meshes
 * Walk quadtree tree each frame to find the payloads that should be streamed/generated
 *      LOD level is based on 3D distance to the camera
 * Get a mesh from the pool and stream data into it
 * 
 * Todo: 
 * 
 * Use a noise lib that actually produces proper values. Geez.
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
 * 
 * We might want to do smooth lod transition based on an event (such as load complete) instead of distance to camera.
 * We certainly want to use predictive streaming.
 * 
 * Per pixel normals (with global normal maps). This is how you get low-res geometry to look high res.
 */
public class TerrainSystem : MonoBehaviour {
    [SerializeField] private Material _material;
    [SerializeField] private Camera _camera;
    [SerializeField] private float _lodZeroScale = 4096f;
    [SerializeField] private int _tileResolution = 16;
    [SerializeField] private int _numLods = 10;
    [SerializeField] private float _lodZeroRange = 32f;
    [SerializeField] private float _heightScale = 512f;

    private float[] _lodDistances;

    private Stack<TerrainTile> _meshPool;
    private IDictionary<QTNode, TerrainTile> _activeMeshes;  
    private IList<IList<QTNode>> _loadedNodes;


    private IHeightSampler _heightSampler;

    void Awake() {
        _lodDistances = QuadTree.GetLodDistances(_numLods, _lodZeroRange);

        _loadedNodes = new List<IList<QTNode>>();
        for (int i = 0; i < _numLods; i++) {
            _loadedNodes.Add(new List<QTNode>());
        }

        CreatePooledTiles();

        _activeMeshes = new Dictionary<QTNode, TerrainTile>();

        _heightSampler = new FractalHeightSampler(_heightScale);

        //DrawTestTerrain();
    }

    private void CreatePooledTiles() {
        const int numTiles = 360;
        _meshPool = new Stack<TerrainTile>();
        for (int i = 0; i < numTiles; i++) {
            var tile = CreateTile(_tileResolution, _material);
            tile.Transform.parent = transform;
            tile.gameObject.SetActive(false);
            _meshPool.Push(tile);
        }
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
     * Decouple visrep and simrep streaming. Allow multiple perspectives. Allow simrep streaming without a perspective.
     * 
     * 
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
     * 
     * -- Bugs --
     * 
     * We need accurate bounding boxes that encapsulate height data for each node during quad-tree traversal, otherwise
     * the intersection test (and hence the lod selection) will be inaccurate.
     * 
     * Shadows are broken. Sometimes something shows up, but it's completely wrong.
     */

    private void Update() {
        var camInfo = CameraInfo.Create(_camera);

        var bMin = new Vector3(-_lodZeroScale * 0.5f, 0f, -_lodZeroScale * 0.5f);
        var lodZeroScale = new Vector3(_lodZeroScale, _heightScale, _lodZeroScale);
        var requiredNodes = QuadTree.ExpandNodesToList(bMin, lodZeroScale, _lodDistances, camInfo, _heightSampler);

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
                mesh.gameObject.SetActive(false);
            }
        }
    }

    private void Load(IList<IList<QTNode>> toLoad) {
        int numVerts = _tileResolution + 1;
        Color[] heights = new Color[numVerts * numVerts];
        Color[] normals = new Color[numVerts * numVerts];

        for (int i = 0; i < toLoad.Count; i++) {
            var lodNodes = toLoad[i];
            var lerpRanges = new Vector4(_lodDistances[i] * 2f, _lodDistances[i] * 3.0f);

            for (int j = 0; j < lodNodes.Count; j++) {
                var node = lodNodes[j];
                var mesh = _meshPool.Pop();
                _activeMeshes.Add(node, mesh);

                Vector3 position = new Vector3(node.Center.x - node.Size.x * 0.5f, 0f, node.Center.z - node.Size.z * 0.5f);

                mesh.Transform.position = position;
                mesh.Transform.localScale = node.Size;
                mesh.MeshRenderer.material.SetFloat("_Scale", node.Size.x);
                mesh.MeshRenderer.material.SetFloat("_HeightScale", _heightScale);
                mesh.MeshRenderer.material.SetVector("_LerpRanges", lerpRanges);

                GenerateTileFractal(heights, normals, numVerts, _heightSampler, position, node.Size.x);

                var heightmap = new Texture2D(numVerts, numVerts, TextureFormat.ARGB32, false);
                var normalmap = new Texture2D(numVerts, numVerts, TextureFormat.ARGB32, false);
                heightmap.wrapMode = TextureWrapMode.Clamp;
                normalmap.wrapMode = TextureWrapMode.Clamp;
                LoadHeightsToTexture(heights, heightmap);
                LoadHeightsToTexture(normals, normalmap);
                mesh.MeshRenderer.material.SetTexture("_HeightTex", heightmap);
                mesh.MeshRenderer.material.SetTexture("_NormalTex", normalmap);

                mesh.gameObject.name = "Terrain_LOD_" + i;
                mesh.gameObject.SetActive(true);

                _loadedNodes[i].Add(node);
            }
        }
    }

    private static TerrainTile CreateTile(int resolution, Material material) {
        var tileObject = new GameObject();
        var tile = tileObject.AddComponent<TerrainTile>();
        tile.Create(resolution);
	    tile.MeshRenderer.material = material;

        tile.MeshRenderer.material.SetFloat("_Scale", 16f);
        tile.MeshRenderer.material.SetVector("_LerpRanges", new Vector4(1f, 16f));

	    return tile;
	}

    private static void GenerateTileFractal(Color[] heights, Color[] normals, int numVerts, IHeightSampler sampler, Vector3 position, float scale) {
        

        float stepSize = scale / (numVerts-1);

        for (int x = 0; x < numVerts; x++) {
            for (int z = 0; z < numVerts; z++) {
                int index = x + z*numVerts;

                float height = sampler.Sample(position.x + x * stepSize, position.z + z * stepSize);
                heights[index] = new Color(height, height, height, height);

                float heightL = sampler.Sample(position.x + (x - 1) * stepSize, position.z + z * stepSize);
                float heightR = sampler.Sample(position.x + (x + 1) * stepSize, position.z + z * stepSize);
                float heightB = sampler.Sample(position.x + x * stepSize, position.z + (z - 1) * stepSize);
                float heightT = sampler.Sample(position.x + x * stepSize, position.z + (z + 1) * stepSize);

                // Todo: the below is in tile-local units, should be world units
                Vector3 lr = new Vector3(stepSize * 2f, (heightR - heightL) * sampler.HeightScale, 0f).normalized;
                Vector3 bt = new Vector3(0f, (heightT - heightB) * sampler.HeightScale, stepSize * 2f).normalized;
                Vector3 normal = Vector3.Cross(lr, bt).normalized;
                normals[index] = new Color(
                    0.5f + normal.x * 0.5f,
                    0.5f + normal.y * 0.5f,
                    0.5f + normal.z * 0.5f,
                    1f);
            }
        }
    }

    private static void LoadHeightsToTexture(Color[] heights, Texture2D texture) {
        texture.SetPixels(heights);
        texture.Apply(true);
    }

    private void OnDrawGizmos() {
        var camInfo = CameraInfo.Create(_camera);

        if (_heightSampler == null) {
            _heightSampler = new FractalHeightSampler(_heightScale);
        }

        var bMin = new Vector3(-_lodZeroScale*0.5f, 0f, -_lodZeroScale*0.5f);
        var lodZeroScale = new Vector3(_lodZeroScale, _heightScale, _lodZeroScale);
        var nodes = QuadTree.ExpandNodesToList(bMin, lodZeroScale, QuadTree.GetLodDistances(_numLods, _lodZeroRange), camInfo, _heightSampler);
        QuadTree.DrawSelectedNodes(nodes);

        Gizmos.color = Color.magenta;
        Gizmos.DrawRay(camInfo.Position, Vector3.up * 1000f);
        Gizmos.DrawSphere(camInfo.Position, 1f);
    }

    private TerrainTile _testTile;

//    private void DrawTestTerrain() {
//        int numVerts = _tileResolution + 1;
//        Color[] heights = new Color[numVerts * numVerts];
//        Color[] normals = new Color[numVerts * numVerts];
//
//        _heightTools = new HeightSamplingTools() {
//            Sampler = new FractalHeightSampler(),
//            Random = new System.Random()
//        };
//
//        if (_testTile == null) {
//            _testTile = CreateTile(16, _material);
//        }
//
//        Vector3 scale = Vector3.one * 256f;
//        Vector3 position = Vector3.zero;
//
//        _testTile.Transform.position = position;
//        _testTile.Transform.localScale = scale;
//        _testTile.MeshRenderer.material.SetFloat("_Scale", scale.x);
//        _testTile.MeshRenderer.material.SetFloat("_HeightScale", 256f);
//        _testTile.MeshRenderer.material.SetVector("_LerpRanges", new Vector2(128f, 256f));
//
//        GenerateTileFractal(heights, normals, numVerts, _heightTools, position, 256f, 256f);
//
//        var heightmap = new Texture2D(numVerts, numVerts, TextureFormat.ARGB32, false);
//        var normalmap = new Texture2D(numVerts, numVerts, TextureFormat.ARGB32, false);
//        heightmap.wrapMode = TextureWrapMode.Clamp;
//        normalmap.wrapMode = TextureWrapMode.Clamp;
//        LoadHeightsToTexture(heights, heightmap);
//        LoadHeightsToTexture(normals, normalmap);
//        _testTile.MeshRenderer.material.SetTexture("_HeightTex", heightmap);
//        _testTile.MeshRenderer.material.SetTexture("_NormalTex", normalmap);
//
//        _testTile.gameObject.name = "Terrain_NormalTest";
//        _testTile.gameObject.SetActive(true);
//    }
}

public interface IHeightSampler {
    float HeightScale { get; }
    float Sample(float x, float z);
}

public class FractalHeightSampler : IHeightSampler {
    private RidgeNoise _noise;
    private float _heightScale;

    public float HeightScale {
        get { return _heightScale; }
    }

    public FractalHeightSampler(float heightScale) {
        _heightScale = heightScale;

        _noise = new RidgeNoise(1234);
        _noise.Frequency = 0.001f;
        _noise.Exponent = 0.5f;
        _noise.Gain = 1f;
    }

    public float Sample(float x, float z) {
        return _noise.GetValue(x, z, 0f) * 0.5f;
    }
}