using System.Collections.Generic;
using Unity.Burst;
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
    [SerializeField] private int _lodZeroScale = 4096;
    [SerializeField] private int _tileResolution = 16;
    [SerializeField] private int _numLods = 10;
    [SerializeField] private int _lodZeroRange = 32;
    [SerializeField] private int _heightScale = 512;

    private NativeArray<float> _lodDistances;

    private List<TerrainTile> _tiles;
    private NativeStack<int> _tileIndexPool;
    private NativeHashMap<TreeNode, int> _tileMap;

    private Tree _visibleTree;
    private NativeList<TreeNode> _visibleSet;
    private NativeList<TreeNode> _toLoad;
    private NativeList<TreeNode> _toUnload;

    private HeightSampler _heightSampler;

    void Awake() {
        _lodDistances = new NativeArray<float>(_numLods, Allocator.Persistent);
        QuadTree.GenerateLodDistances(_lodDistances, _lodZeroRange);

        _heightSampler = new HeightSampler(_heightScale);

        int maxNodes = mathi.SumPowersOfFour(_numLods);

        var bMin = new int3(-_lodZeroScale / 2, 0, -_lodZeroScale / 2);
        var lodZeroScale = new int3(_lodZeroScale, _heightScale, _lodZeroScale);

        _visibleTree = new Tree(new Bounds(bMin, lodZeroScale), _lodDistances.Length, _heightSampler, Allocator.Persistent);
        _visibleSet = new NativeList<TreeNode>(maxNodes, Allocator.Persistent);
        _toLoad = new NativeList<TreeNode>(maxNodes, Allocator.Persistent);
        _toUnload = new NativeList<TreeNode>(maxNodes, Allocator.Persistent);

        _tiles = new List<TerrainTile>();
        _tileIndexPool = new NativeStack<int>(maxNodes, Allocator.Persistent);
        _tileMap = new NativeHashMap<TreeNode, int>(maxNodes, Allocator.Persistent);

        const int numTiles = 256; // Todo: how many do we really need at max?
        PreallocateTiles(numTiles);
    }

    private void OnDestroy() {
        _lodDistances.Dispose();

        _visibleTree.Dispose();
        _visibleSet.Dispose();
        _toLoad.Dispose();
        _toUnload.Dispose();

        _tileMap.Dispose();
        _tileIndexPool.Dispose();
    }

    private void PreallocateTiles(int count) {
        for (int i = 0; i < count; i++) {
            AllocateNewTileInPool();
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
    
    We currently use three collections to manage node and mesh instances. It's a bit unwieldy.
    
    Decouple visrep and simrep streaming. Allow multiple perspectives. Allow simrep streaming without a perspective.
    
    -- Performance --
    
    We could use a single mesh instance on the GPU, but we still need separate mesh instances on the cpu for tiles
    we want colliders on, which is every lod > x
    
    */

    private JobHandle _lodJobHandle;
    private CameraInfo _camInfo;

    private void Update() {
        /*
        Todo:
        Only update if camera moved more than a minimum from last update position
        */

        _camInfo = CameraInfo.Create(_camera, Allocator.Persistent);

        var bMin = new int3(-_lodZeroScale / 2, 0, -_lodZeroScale / 2);
        var lodZeroScale = new int3(_lodZeroScale, _heightScale, _lodZeroScale);

        _visibleTree.Clear(new Bounds(bMin, lodZeroScale));
        _visibleSet.Clear();
        
        var expandTreeJob = new ExpandQuadTreeQueueLoadsJob() {
            camInfo = _camInfo,
            lodDistances = _lodDistances,
            tree = _visibleTree,
            visibleSet = _visibleSet
        };
        _lodJobHandle = expandTreeJob.Schedule();
    }

    private void LateUpdate() {
        _lodJobHandle.Complete();

        /*
        This bit has been occupying me for waaaay too long.
        I kept getting uncoordinated loads and unloads.

        A given tile will either:
        - Not need to change right now
        - Refine to next LOD depth
        - Simplify to previous LOD depth

        A refinement would constitute:
        - Load 4 children
        - Unload 1 parent

        These should act as one atomic operation
        */

        _toLoad.Clear();
        _toUnload.Clear();

        /*
        Todo: These two loops take bloody ages
        
        Probably way better to determine load/unload instructions
        while we're traversing the quadtree
        */

        var loadedKeys = _tileMap.GetKeyArray(Allocator.Temp);
        for (int i = 0; i < loadedKeys.Length; i++) {
            if (!_visibleSet.Contains(loadedKeys[i])) {
                _toUnload.Add(loadedKeys[i]);
            }
        }

        for (int i = 0; i < _visibleSet.Length; i++) {
            if (!_tileMap.ContainsKey(_visibleSet[i])) {
                _toLoad.Add(_visibleSet[i]);
            }
        }

        Profiler.BeginSample("Unload");
        Unload(_toUnload);
        Profiler.EndSample();

        Profiler.BeginSample("Load");
        Load(_toLoad);
        Profiler.EndSample();

        _camInfo.Dispose();
    }

    private void OnDrawGizmos() {
        if (!Application.isPlaying) {
            return;
        }

        DrawTree(_visibleTree);
    }

    private void DrawTree(Tree tree) {
        for (int i = 0; i < tree.Nodes.Length; i++) {
            var node = tree.Nodes[i];

            if (!node.IsLeaf) {
                continue;
            }

            Color gizmoColor = Color.red;
            if (_tileMap.ContainsKey(node)) {
                gizmoColor = Color.HSVToRGB(node.depth / (float)tree.MaxDepth, 0.9f, 1f);
            }

            Gizmos.color = gizmoColor;
            Gizmos.DrawWireCube((float3)(node.bounds.position + node.bounds.size / 2), (float3)node.bounds.size);
        }
    }

    private void Unload(NativeList<TreeNode> toUnload) {
        for (int i = 0; i < toUnload.Length; i++) {
            TreeNode node = toUnload[i];

            if (!_tileMap.ContainsKey(node)) {
                Debug.LogWarningFormat("Attempting to unload node without active mesh assigned to it: {0}", toUnload[i].bounds);
                continue;
            }

            // Debug.Log(string.Format("Unloading: Terrain_D{0}_[{1},{2}]", node.depth, node.bounds.position.x, node.bounds.position.z));

            int idx = _tileMap[node];
            var mesh = _tiles[idx];
            mesh.gameObject.SetActive(false);

            _tileMap.Remove(node);
            _tileIndexPool.Push(idx);
        }
    }

    /* Todo: Optimize
     * - Run as Burst Job
     */
    private void Load(NativeList<TreeNode> toLoad) {
        int numVerts = _tileResolution + 1;

        var streamHandles = new NativeArray<JobHandle>(toLoad.Length, Allocator.Temp, NativeArrayOptions.ClearMemory);

        for (int i = 0; i < toLoad.Length; i++) {
            var node = toLoad[i];

            // Debug.Log(string.Format("Loading: Terrain_D{0}_[{1},{2}]", node.depth, node.bounds.position.x, node.bounds.position.z));

            const float lMin = 2.33f, lMax = 2.66f;
            var lerpRanges = new Vector4(_lodDistances[node.depth] * lMin, _lodDistances[node.depth] * lMax);

            if (_tileMap.ContainsKey(node)) {
                Debug.LogWarningFormat("Attempting to load tile that's already in use! {0}", node);
                continue;
            }

            if (_tileIndexPool.Count < 1) {
                Debug.LogWarningFormat("Dynamically allocating new tile to accomodate demand. Total tiles: {0}", _tiles.Count);
                AllocateNewTileInPool();
            }

            int idx = _tileIndexPool.Pop();
            _tileMap[node] = idx;
            var mesh = _tiles[idx];

            float3 position = new float3(node.bounds.position.x, 0f, node.bounds.position.z);
            mesh.Transform.position = position;
            mesh.Transform.localScale = new float3(node.bounds.size.x, 1f, node.bounds.size.z);
            mesh.MeshRenderer.material.SetFloat("_Scale", node.bounds.size.x);
            mesh.MeshRenderer.material.SetFloat("_HeightScale", _heightScale);
            mesh.MeshRenderer.material.SetVector("_LerpRanges", lerpRanges);

            var heights = mesh.HeightMap.GetRawTextureData<byte2>();
            var normals = mesh.NormalMap.GetRawTextureData<float2>();

            var generateJob = new StreamHeightDataJob() {
                heights=heights,
                normals=normals,
                numVerts=numVerts,
                sampler=_heightSampler,
                position=position,
                scale=node.bounds.size.x
            };
            streamHandles[i] = generateJob.Schedule(numVerts*numVerts, 32);
        }

        JobHandle.CompleteAll(streamHandles);

        for (int i = 0; i < toLoad.Length; i++) {
            var node = toLoad[i];
            var mesh = _tiles[_tileMap[node]];

            mesh.HeightMap.Apply(false);
            mesh.NormalMap.Apply(true);

            mesh.MeshRenderer.material.SetTexture("_HeightTex", mesh.HeightMap);
            mesh.MeshRenderer.material.SetTexture("_NormalTex", mesh.NormalMap);

            mesh.Mesh.bounds = new UnityEngine.Bounds(Vector3.zero, (float3)node.bounds.size);

            mesh.gameObject.name = string.Format("Terrain_D{0}_[{1},{2}]", node.depth, node.bounds.position.x, node.bounds.position.z);
            mesh.gameObject.SetActive(true);
        }
    }

    private void AllocateNewTileInPool() {
        int idx = _tiles.Count;
        var tile = CreateTileObject("tile_" + idx, _tileResolution, _material);
        
        tile.Transform.parent = transform;
        tile.gameObject.SetActive(false);

        _tileIndexPool.Push(idx);
        _tiles.Add(tile);
    }

    private static TerrainTile CreateTileObject(string name, int resolution, Material material) {
        var tileObject = new GameObject(name);
        var tile = tileObject.AddComponent<TerrainTile>();
        tile.Create(resolution);
	    tile.MeshRenderer.material = material;
        tile.MeshRenderer.material.SetFloat("_Scale", 16f);
        tile.MeshRenderer.material.SetVector("_LerpRanges", new Vector4(1f, 16f));

	    return tile;
	}

    [BurstCompile]
    public struct StreamHeightDataJob : IJobParallelFor {
        public NativeSlice<byte2> heights;
        public NativeSlice<float2> normals;
        public int numVerts;
        public HeightSampler sampler;
        public Vector3 position;
        public float scale;
        
        public void Execute(int idx) {
            float stepSize = scale / (float)(numVerts - 1);
            float stepSizeNormals = scale / (float)(numVerts - 1);
            float delta = 0.001f * scale;

            int x = idx % numVerts;
            int z = idx / numVerts;

            float xPos = position.x + x * stepSize;
            float zPos = position.z + z * stepSize;

            float height = sampler.Sample(xPos, zPos);

            heights[idx] = new byte2(
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
            normals[idx] = new float2(
                0.5f + normal.x * 0.5f,
                0.5f + normal.y * 0.5f);
        }
    }

    private static void GenerateTileHeights(NativeSlice<byte2> heights, NativeSlice<float2> normals, int numVerts, HeightSampler sampler, Vector3 position, float scale) {
        /*
         Todo: These sampling step sizes are off somehow, at least for normals
         I'm guessing it's my silly use of non-power-of-two textures, so let's
         fix that off-by-one thing everywhere.
         */
        float stepSize = scale / (float)(numVerts-1);
        float stepSizeNormals = scale / (float)(numVerts-1);

        /* Todo: can optimize normal generation by first sampling all heights, then using those to generate normals.
         * Only need procedural samples at edges. */

        float delta = 0.001f * scale;

        for (int z = 0; z < numVerts; z++) {
            for (int x = 0; x < numVerts; x++) {
                int idx = z * numVerts + x;

                float xPos = position.x + x * stepSize;
                float zPos = position.z + z * stepSize;

                float height = sampler.Sample(xPos, zPos);
                
                heights[idx] = new byte2(
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
                normals[idx] = new float2(
                    0.5f + normal.x * 0.5f,
                    0.5f + normal.y * 0.5f);
            }
        }
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