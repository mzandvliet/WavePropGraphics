using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;

/*
Make a pool of X^2 resolution vert meshes
Walk quadtree tree each frame to find the payloads that should be streamed/generated
    LOD level is based on 3D distance to the camera
Get a mesh from the pool and stream data into it

Todo:

Improve normal map popping

Octaved height texture update from interactive wave simulation

Burstify all the things
Rewrite Quadtree logic. Flat, data-driven, burst-friendly

Maybe use compute buffers to send heigh data to GPU? We're not using
hardware-interpolators for reading the height textures at the moment,
since we're using text2dlod, and bilinearly sampling in software

Single mesh prototype, use material property blocks to assign textures and transforms
Shadow pass

Increase distance, draw and detail scales

Track accurate tile bounding box information for LOD selection

Figure out how to disable the 4 quadrants of a tile without cpu overhead

Separate culling passes to find visual and shadow caster tiles, use pass tags to render them differently
    - https://gist.github.com/pigeon6/4237385


Use TextureStreaming API? Texture2D.requestedMipmapLevel?
https://docs.unity3d.com/Manual/TextureStreaming-API.html

New Texture2D api features to try:
- LoadRawTextureData(NativeArra<T> data), https://docs.unity3d.com/ScriptReference/Texture2D.LoadRawTextureData.html
- GetRawTextureData(), returns a reference which is even better, https://docs.unity3d.com/ScriptReference/Texture2D.GetRawTextureData.html
- Compress(), compresses into DXT1, or DXT5 if alpha channel

*/

public struct byte2 {
    public byte x;
    public byte y;

    public byte2(byte x, byte y) {
        this.x = x;
        this.y = y;
    }
}

public class TerrainSystem : MonoBehaviour {
    [SerializeField] private Material _material;
    [SerializeField] private Camera _camera;
    [SerializeField] private float _lodZeroScale = 4096f;
    [SerializeField] private int _tileResolution = 16;
    [SerializeField] private int _numLods = 10;
    [SerializeField] private float _lodZeroRange = 32f;
    [SerializeField] private float _heightScale = 512f;

    private NativeArray<float> _lodDistances;

    private Stack<TerrainTile> _meshPool;
    private IDictionary<Bounds, TerrainTile> _activeMeshes;

    private Tree _visibleNodes;
    private NativeList<int> _toLoad; // indexes into _visibleNodes

    private Tree _loadedNodes;
    private NativeList<int> _toUnload; // indexes into _loadedNodes

    private HeightSampler _heightSampler;

    void Awake() {
        _lodDistances = new NativeArray<float>(_numLods, Allocator.Persistent);
        QuadTree.GenerateLodDistances(_lodDistances, _lodZeroRange);

        for (int i = 0; i < _lodDistances.Length; i++) {
            Debug.LogFormat("LOD_{0} Dist: {1}", i, _lodDistances[i]);
        }

        _heightSampler = new HeightSampler(_heightScale);

        int maxNodes = mathi.SumPowersOfFour(_numLods);

        var bMin = new float3(-_lodZeroScale * 0.5f, 0f, -_lodZeroScale * 0.5f);
        var lodZeroScale = new float3(_lodZeroScale, _heightScale, _lodZeroScale);

        _visibleNodes = new Tree(new Bounds(bMin, lodZeroScale), _lodDistances.Length, _heightSampler, Allocator.Persistent);
        _loadedNodes =  new Tree(new Bounds(bMin, lodZeroScale), _lodDistances.Length, _heightSampler, Allocator.Persistent);
        _toLoad = new NativeList<int>(maxNodes, Allocator.Persistent);
        _toUnload = new NativeList<int>(maxNodes, Allocator.Persistent);

        CreatePooledTiles();
        _activeMeshes = new Dictionary<Bounds, TerrainTile>();
    }

    private void OnDestroy() {
        _lodDistances.Dispose();

        _visibleNodes.Dispose();
        _loadedNodes.Dispose();
        _toLoad.Dispose();
        _toUnload.Dispose();
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
    -- Configuration --
    
    Make easier-to-use parameters. Like, at max lod I want 1px/m resolution.
    
    -- Architecture --
    
    We are receiving a new list of quadtree nodes each time, which we need to compare with the old list.
    
    Testing equality of nodes is iffy. Nodes are reference types in one sense, but then value types later.
    Also, if nodes have more data to them it makes more sense to keep them as reference type. Caching and
    reusing a single tree instead of generating an immutable tree each frame and diffing.
    
    We could mark nodes on a persistent tree structure dirty if some action is need. Update this dirty state
    when validating the tree each frame, and creating jobs frome there.
    
    We currently use three collections to manage node and mesh instances. It's unwieldy.
    
    Decouple visrep and simrep streaming. Allow multiple perspectives. Allow simrep streaming without a perspective.
    
    -- Performance --
    
    Creating a class QTNode-based tree each frame means heap-related garbage. Creating a recursive struct QTNode
    is impossible because a struct can not have members of its own type.
    
    We could pool QTNodes
    
    We could use a single mesh instance on the GPU, but we still need separate mesh instances on the cpu for tiles
    we want colliders on, which is every lod > x
    
    -- Bugs --
    
    We need accurate bounding boxes that encapsulate height data for each node during quad-tree traversal, otherwise
    the intersection test (and hence the lod selection) will be inaccurate.
    
    Shadows are broken. Sometimes something shows up, but it's completely wrong.
    */

    private JobHandle _lodJobHandle;
    private CameraInfo _camInfo; // Todo: make fully stack-based using fixed array struct

    private void Update() {
        _camInfo = CameraInfo.Create(_camera, Allocator.Persistent);

        var bMin = new Vector3(-_lodZeroScale * 0.5f, 0f, -_lodZeroScale * 0.5f);
        var lodZeroScale = new Vector3(_lodZeroScale, _heightScale, _lodZeroScale);

        _visibleNodes.Clear(new Bounds(bMin, lodZeroScale));
        _toLoad.Clear();
        _toUnload.Clear();

        var expandTreeJob = new ExpandQuadTreeJob() {
            camInfo = _camInfo,
            lodDistances = _lodDistances,
            tree = _visibleNodes
        };
        var expandHandle = expandTreeJob.Schedule();

        var unloadDiffJob = new DiffQuadTreesJob() {
            a = _visibleNodes,
            b = _loadedNodes,
            diff = _toUnload,
        };
        var loadDiffJob = new DiffQuadTreesJob()
        {
            a = _loadedNodes,
            b = _visibleNodes,
            diff = _toLoad
        };

        _lodJobHandle = JobHandle.CombineDependencies(
            unloadDiffJob.Schedule(expandHandle),
            loadDiffJob.Schedule(expandHandle)
        );
    }

    private void LateUpdate() {
        _lodJobHandle.Complete();

        Profiler.BeginSample("Unload");
        Unload(_loadedNodes, _toUnload);
        Profiler.EndSample();
        Profiler.BeginSample("Load");
        Load(_visibleNodes, _toLoad);
        Profiler.EndSample();

        // We blindly assume all loads and unloads succeed

        _camInfo.Dispose();
    }

    private void Unload(Tree tree, NativeList<int> toUnload) {
        for (int i = 0; i < toUnload.Length; i++) {
            var node = tree[toUnload[i]];
            var mesh = _activeMeshes[node.bounds];
            _meshPool.Push(mesh);
            _activeMeshes.Remove(node.bounds);
            mesh.gameObject.SetActive(false);
        }
    }

    /* Todo: Optimize
     * - Store textures with their tiles, allocate at startup
     * - Allocate height and color arrays at startup too?
     */
    private void Load(Tree tree, NativeList<int> toLoad) {
        int numVerts = _tileResolution + 1;

        for (int i = 0; i < toLoad.Length; i++) {
            var node = tree[toLoad[i]];
            const float lMin = 3f, lMax = 3.5f;
            var lerpRanges = new Vector4(_lodDistances[node.depth] * lMin, _lodDistances[node.depth] * lMax);

            var mesh = _meshPool.Pop(); // Should be a TilePool, where Tile = { Transform, MatPropBlock, Tex2d, Tex2d }
            _activeMeshes.Add(node.bounds, mesh);

            float3 position = new float3(node.bounds.position.x, 0f, node.bounds.position.z);

            mesh.Transform.position = position;
            mesh.Transform.localScale = node.bounds.size;
            mesh.MeshRenderer.material.SetFloat("_Scale", node.bounds.size.x);
            mesh.MeshRenderer.material.SetFloat("_HeightScale", _heightScale);
            mesh.MeshRenderer.material.SetVector("_LerpRanges", lerpRanges);

            var heightMap = new Texture2D(numVerts, numVerts, TextureFormat.RG16, false, true);
            var normalMap = new Texture2D(numVerts, numVerts, TextureFormat.RGFloat, true, true); // Todo: use RGHalf?

            var heights = heightMap.GetRawTextureData<byte2>();
            var normals = normalMap.GetRawTextureData<float2>();
            heightMap.wrapMode = TextureWrapMode.Clamp;
            normalMap.wrapMode = TextureWrapMode.Clamp;
            heightMap.filterMode = FilterMode.Point;
            normalMap.filterMode = FilterMode.Trilinear;
            normalMap.anisoLevel = 4;

            GenerateTileHeights(heights, normals, numVerts, _heightSampler, position, node.bounds.size.x);
            heightMap.Apply(false);
            normalMap.Apply(true);

            mesh.MeshRenderer.material.SetTexture("_HeightTex", heightMap);
            mesh.MeshRenderer.material.SetTexture("_NormalTex", normalMap);

            mesh.Mesh.bounds = new UnityEngine.Bounds(Vector3.zero, node.bounds.size);

            mesh.gameObject.name = "Terrain_LOD_" + i;
            mesh.gameObject.SetActive(true);
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

    private static void GenerateTileHeights(NativeSlice<byte2> heights, NativeSlice<float2> normals, int numVerts, IHeightSampler sampler, Vector3 position, float scale) {
        /*
         Todo: These sampling step sizes are off somehow, at least for normals
         I'm guessing it's my silly use of non-power-of-two textures, so let's
         fix that off-by-one thing everywhere.
         */
        float stepSize = scale / (float)(numVerts-1);
        float stepSizeNormals = scale / (float)(numVerts);

        /* Todo: can optimize normal generation by first sampling all heights, then using those to generate normals.
         * Only need procedural samples at edges. */

        float delta = 0.01f * scale;

        for (int z = 0; z < numVerts; z++) {
            for (int x = 0; x < numVerts; x++) {
                int index = z * numVerts + x;

                float xPos = position.x + x * stepSize;
                float zPos = position.z + z * stepSize;

                float height = sampler.Sample(xPos, zPos);
                
                heights[index] = new byte2(
                    (byte)(Mathf.RoundToInt(height * 65535f) >> 8),
                    (byte)(Mathf.RoundToInt(height * 65535f))
                );

                xPos = position.x + x * stepSizeNormals;
                zPos = position.z + z * stepSizeNormals;

                float heightL = sampler.Sample(xPos + delta, zPos);
                float heightR = sampler.Sample(xPos - delta, zPos);
                float heightB = sampler.Sample(xPos, zPos - delta);
                float heightT = sampler.Sample(xPos, zPos + delta);

                Vector3 lr = new Vector3(delta * 2f, (heightR - heightL) * sampler.HeightScale, 0f);
                Vector3 bt = new Vector3(0f, (heightT - heightB) * sampler.HeightScale, delta * 2f);
                Vector3 normal = Vector3.Cross(bt, lr).normalized;

                // Note: normal z-component is recalculated on the gpu, which saves transfer memory
                normals[index] = new float2(
                    0.5f + normal.x * 0.5f,
                    0.5f + normal.y * 0.5f);
            }
        }
    }

    private void OnDrawGizmos() {
        if (!Application.isPlaying) {
            return;
        }

        // var camInfo = CameraInfo.Create(_camera);

        // var bMin = new Vector3(-_lodZeroScale*0.5f, 0f, -_lodZeroScale*0.5f);
        // var lodZeroScale = new Vector3(_lodZeroScale, _heightScale, _lodZeroScale);
        // QTNode root = new QTNode(bMin, lodZeroScale);
        // QuadTree.ExpandNodeRecursively(0, root, camInfo, _lodDistances, _visibleNodes, _heightSampler);
        // QuadTree.DrawSelectedNodes(_visibleNodes);

        // Gizmos.color = Color.magenta;
        // Gizmos.DrawRay(camInfo.position, Vector3.up * 1000f);
        // Gizmos.DrawSphere(camInfo.position, 1f);

        // camInfo.Dispose();
    }
}

public interface IHeightSampler {
    float HeightScale { get; }
    float Sample(float x, float z);
}

public struct HeightSampler : IHeightSampler {
    private float _heightScale;

    public float HeightScale {
        get { return _heightScale; }
    }

    public HeightSampler(float heightScale) {
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