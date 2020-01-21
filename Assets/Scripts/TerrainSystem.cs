using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;

/*
 * Make a pool of X^2 resolution vert meshes
 * Walk quadtree tree each frame to find the payloads that should be streamed/generated
 *      LOD level is based on 3D distance to the camera
 * Get a mesh from the pool and stream data into it
 * 
 * Todo:
 * 
 * Use TextureStreaming API? Texture2D.requestedMipmapLevel?
 https://docs.unity3d.com/Manual/TextureStreaming-API.html
 *
 * Improve normal map popping
 *
 * Octaved height texture update from interactive wave simulation
 * 
 * Burstify all the things
 * Rewrite Quadtree logic. Flat, data-driven, burst-friendly
 * 
 * Single mesh prototype, use material property blocks to assign textures and transforms
 * Shadow pass
 * 
 * Increase distance, draw and detail scales
 *
 * Track accurate tile bounding box information for LOD selection
 *
 * - Figure out how to disable the 4 quadrants of a tile without cpu overhead
 * 
 * - Separate culling passes to find visual and shadow caster tiles, use pass tags to render them differently
 *      - https://gist.github.com/pigeon6/4237385
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

    private IList<IList<QTNode>> _visibleNodes;
    private IList<IList<QTNode>> _toLoad;
    private IList<IList<QTNode>> _toUnload;
    private IList<IList<QTNode>> _loadedNodes;

    private IHeightSampler _heightSampler;

    void Awake() {
        _lodDistances = QuadTree.GetLodDistances(_numLods, _lodZeroRange);

        for (int i = 0; i < _lodDistances.Length; i++) {
            Debug.LogFormat("LOD_{0} Dist: {1}", i, _lodDistances[i]);
        }

        _visibleNodes = CreateList(_numLods);
        _loadedNodes = CreateList(_numLods);
        _toLoad = CreateList(_numLods);
        _toUnload = CreateList(_numLods);

        _heightSampler = new FractalHeightSampler(_heightScale);

        CreatePooledTiles();
        _activeMeshes = new Dictionary<QTNode, TerrainTile>();
    }

    private static IList<IList<QTNode>> CreateList(int length) {
        var list = new List<IList<QTNode>>(length);
        for (int i = 0; i < length; i++) {
            list.Add(new List<QTNode>());
        }
        return list;
    }

    private static void ClearList(IList<IList<QTNode>> list) {
        for (int i = 0; i < list.Count; i++) {
            list[i].Clear();
        }
    }

    private void CreatePooledTiles() {
        const int numTiles = 360; // Todo: how many do we really need at max?

        _meshPool = new Stack<TerrainTile>();
        for (int i = 0; i < numTiles; i++) {
            var tile = CreateTile("tile_"+i, _tileResolution, _material);
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

        Profiler.BeginSample("Clear");
        ClearList(_visibleNodes);
        ClearList(_toLoad);
        ClearList(_toUnload);
        Profiler.EndSample();

        Profiler.BeginSample("ExpandNodesToList");
        QTNode root = new QTNode(bMin, lodZeroScale);
        QuadTree.ExpandNodeRecursively(0, root, camInfo, _lodDistances, _visibleNodes, _heightSampler);
        Profiler.EndSample();

        Profiler.BeginSample("Diffs");
        QuadTree.Diff(_visibleNodes, _loadedNodes, _toUnload);
        QuadTree.Diff(_loadedNodes, _visibleNodes, _toLoad);
        Profiler.EndSample();

        Profiler.BeginSample("Unload");
        Unload(_toUnload);
        Profiler.EndSample();
        Profiler.BeginSample("Load");
        Load(_toLoad);
        Profiler.EndSample();
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

    /* Todo: Optimize
     * - Store textures with their tiles, allocate at startup
     * - Allocate height and color arrays at startup too?
     */
    private void Load(IList<IList<QTNode>> toLoad) {
        int numVerts = _tileResolution;
        Color32[] heights = new Color32[numVerts * numVerts];
        Color[] normals = new Color[numVerts * numVerts];

        for (int i = 0; i < toLoad.Count; i++) {
            var lodNodes = toLoad[i];
            const float lMin = 3f, lMax = 3.5f;
            var lerpRanges = new Vector4(_lodDistances[i] * lMin, _lodDistances[i] * lMax); // math.max(0,i-1)

            for (int j = 0; j < lodNodes.Count; j++) {
                var node = lodNodes[j];
                var mesh = _meshPool.Pop(); // Should be a TilePool, where Tile = { Transform, MatPropBlock, Tex2d, Tex2d }
                _activeMeshes.Add(node, mesh);

                Vector3 position = new Vector3(node.Center.x - node.Size.x * 0.5f, 0f, node.Center.z - node.Size.z * 0.5f);

                mesh.Transform.position = position;
                mesh.Transform.localScale = node.Size;
                mesh.MeshRenderer.material.SetFloat("_Scale", node.Size.x);
                mesh.MeshRenderer.material.SetFloat("_HeightScale", _heightScale);
                mesh.MeshRenderer.material.SetVector("_LerpRanges", lerpRanges);

                GenerateTileHeights(heights, normals, numVerts, _heightSampler, position, node.Size.x);

                var heightmap = new Texture2D(numVerts, numVerts, TextureFormat.RGBA32, false, true); // Note: BA channels go unused right now, get creative!
                var normalmap = new Texture2D(numVerts, numVerts, TextureFormat.RGFloat, true, true);
                heightmap.wrapMode = TextureWrapMode.Clamp;
                normalmap.wrapMode = TextureWrapMode.Clamp;
                heightmap.filterMode = FilterMode.Point;
                normalmap.filterMode = FilterMode.Trilinear;
                normalmap.anisoLevel = 4;

                ToTexture(heights, heightmap);
                ToTexture(normals, normalmap);
                mesh.MeshRenderer.material.SetTexture("_HeightTex", heightmap);
                mesh.MeshRenderer.material.SetTexture("_NormalTex", normalmap);

                mesh.Mesh.bounds = new Bounds(Vector3.zero, node.Size);

                mesh.gameObject.name = "Terrain_LOD_" + i;
                mesh.gameObject.SetActive(true);

                _loadedNodes[i].Add(node);
            }
        }
    }

    private static TerrainTile CreateTile(string name, int resolution, Material material) {
        var tileObject = new GameObject(name);
        var tile = tileObject.AddComponent<TerrainTile>();
        tile.Create(resolution);
	    tile.MeshRenderer.material = material;

        tile.MeshRenderer.material.SetFloat("_Scale", 16f);
        tile.MeshRenderer.material.SetVector("_LerpRanges", new Vector4(1f, 16f));

	    return tile;
	}

    private static void GenerateTileHeights(Color32[] heights, Color[] normals, int resolution, IHeightSampler sampler, Vector3 position, float scale) {
        /*
         Todo: These sampling step sizes are off somehow, at least for normals
         I'm guessing it's my silly use of non-power-of-two textures, so let's
         fix that off-by-one thing everywhere.
         */
        float stepSize = scale / (float)(resolution-1);
        float stepSizeNormals = scale / (float)(resolution-1);

        /* Todo: can optimize normal generation by first sampling all heights, then using those to generate normals.
         * Only need procedural samples at edges. */

        float delta = 0.01f * scale;

        for (int z = 0; z < resolution; z++) {
            for (int x = 0; x < resolution; x++) {
                int index = z * resolution + x;

                float xPos = position.x + x * stepSize;
                float zPos = position.z + z * stepSize;

                float height = sampler.Sample(xPos, zPos);
                
                heights[index] = new Color32(
                    (byte)(Mathf.RoundToInt(height * 65535f) >> 8),
                    (byte)(Mathf.RoundToInt(height * 65535f)),
                    0,0);

                xPos = position.x + x * stepSizeNormals;
                zPos = position.z + z * stepSizeNormals;

                float heightL = sampler.Sample(xPos + delta, zPos);
                float heightR = sampler.Sample(xPos - delta, zPos);
                float heightB = sampler.Sample(xPos, zPos - delta);
                float heightT = sampler.Sample(xPos, zPos + delta);

                Vector3 lr = new Vector3(delta * 2f, (heightR - heightL) * sampler.HeightScale, 0f);
                Vector3 bt = new Vector3(0f, (heightT - heightB) * sampler.HeightScale, delta * 2f);
                Vector3 normal = Vector3.Cross(bt, lr).normalized;
                
                normals[index] = new Color(
                    0.5f + normal.x * 0.5f,
                    0.5f + normal.y * 0.5f,
                    0.5f + normal.z * 0.5f,
                    1f);
            }
        }
    }

    private static void ToTexture(Color[] heights, Texture2D texture) {
        texture.SetPixels(heights);
        texture.Apply(true);
    }

    private static void ToTexture(Color32[] heights, Texture2D texture) {
        texture.SetPixels32(heights);
        texture.Apply(false);
    }

    private void OnDrawGizmos() {
        var camInfo = CameraInfo.Create(_camera);

        if (_heightSampler == null) {
            _lodDistances = QuadTree.GetLodDistances(_numLods, _lodZeroRange);
            _heightSampler = new FractalHeightSampler(_heightScale);
            _visibleNodes = CreateList(_numLods);
        }

        var bMin = new Vector3(-_lodZeroScale*0.5f, 0f, -_lodZeroScale*0.5f);
        var lodZeroScale = new Vector3(_lodZeroScale, _heightScale, _lodZeroScale);
        QTNode root = new QTNode(bMin, lodZeroScale);
        QuadTree.ExpandNodeRecursively(0, root, camInfo, _lodDistances, _visibleNodes, _heightSampler);
        QuadTree.DrawSelectedNodes(_visibleNodes);

        Gizmos.color = Color.magenta;
        Gizmos.DrawRay(camInfo.Position, Vector3.up * 1000f);
        Gizmos.DrawSphere(camInfo.Position, 1f);
    }
}

public interface IHeightSampler {
    float HeightScale { get; }
    float Sample(float x, float z);
}

public class FractalHeightSampler : IHeightSampler {
    private float _heightScale;

    public float HeightScale {
        get { return _heightScale; }
    }

    public FractalHeightSampler(float heightScale) {
        _heightScale = heightScale;
    }

    /// Generates height values in normalized float range, [0,1]
    public float Sample(float x, float z) {
        float h = 0f;

        for (int i = 0; i < 10; i++) {
            h += 
                (0.5f + Mathf.Sin(0.73197f * ((i+1)*11) + (x * Mathf.PI) * 0.001093f * (i+1)) * 0.5f) *
                (0.5f + Mathf.Cos(-1.1192f * ((i+1) *17) + (z * Mathf.PI) * 0.001317f * (i+1)) * 0.5f) *
                (0.5f / (float)(1+i*2));
        }
       

        return h;
           
    }
}