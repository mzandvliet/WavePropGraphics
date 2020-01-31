using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using Waves;

/*

Uses a quadtree mechanism to manage a virtual texturing / virtual meshing scheme

See here for an overview and links to good talks by Barret08,Waveren09:

http://holger.dammertz.org/stuff/notes_VirtualTexturing.html

---------------------------------------

Todo:

- Use simulation's precalculated Min/Max wave height to help construct quadtree,
instead of resampling in QuadTree expansion job.

- Octaved height texture update from interactive wave simulation, straight to GPU

- Improve normal map popping (bicubic height sampling, on GPU)

- Single mesh prototype, use material property blocks to assign textures and transforms

- Implement Shadow pass

- Batched raycast system

- Separate culling passes to find visual and shadow caster tiles, use pass tags to render them differently
    - https://gist.github.com/pigeon6/4237385


Use TextureStreaming API? Texture2D.requestedMipmapLevel?
https://docs.unity3d.com/Manual/TextureStreaming-API.html
(Doesn't seem like it works with procedurally generated data...)

New Texture2D api features to try:
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

public class WaveSystem : MonoBehaviour {
    [SerializeField] private Material _material;
    [SerializeField] private Camera _camera;
    [SerializeField] private bool _showWaveDebugData = false;
    [SerializeField] private int _lodZeroScale = 4096;
    [SerializeField] private int _tileResolution = 16;
    [SerializeField] private int _numLods = 10;
    [SerializeField] private int _lodZeroRange = 32;

    [SerializeField] private bool _overrideDefaultBurstWorkerCount = true;
    [SerializeField] private int _burstWorkerCount = 4;

    private const int WaveHeightScale = 512; // Meters

    private NativeArray<float> _lodDistances;

    private List<MeshTile> _tiles; // Actual mesh/texture data
    private NativeStack<int> _tileIndexPool; // Index into list of tiles
    private NativeHashMap<TreeNode, int> _tileMap; // Page table that maps quadtree nodes to tiles

    private Tree _currVisTree; // Quad tree expanded to exactly the needed LODS for current frame
    private Tree _lastVisTree; // Same as above, but from last frame
    private NativeList<TreeNode> _visibleSet; // Flat list of quadtree nodes, right now containing only deepest visible level
    private NativeList<TreeNode> _loadQueue; // Tiles to stream into active set
    private NativeList<TreeNode> _unloadQueue; // Tiles to stream out of active set

    private WaveSimulator _waves;

    void Awake() {
        Application.targetFrameRate = 60;

        ConfigureBurst();

        _waves = new WaveSimulator(WaveHeightScale);

        _lodDistances = new NativeArray<float>(_numLods, Allocator.Persistent);
        QuadTree.GenerateLodDistances(_lodDistances, _lodZeroRange);

        int maxNodes = mathi.SumPowersOfFour(_numLods);

        var bMin = new int3(-_lodZeroScale / 2, 0, -_lodZeroScale / 2);
        var lodZeroScale = new int3(_lodZeroScale, WaveHeightScale, _lodZeroScale);

        _currVisTree = new Tree(new Bounds(bMin, lodZeroScale), _lodDistances.Length, Allocator.Persistent);
        _lastVisTree = new Tree(new Bounds(bMin, lodZeroScale), _lodDistances.Length, Allocator.Persistent);

        _visibleSet = new NativeList<TreeNode>(maxNodes, Allocator.Persistent);

        _loadQueue = new NativeList<TreeNode>(maxNodes, Allocator.Persistent);
        _unloadQueue = new NativeList<TreeNode>(maxNodes, Allocator.Persistent);

        _tiles = new List<MeshTile>();
        _tileIndexPool = new NativeStack<int>(maxNodes, Allocator.Persistent);
        _tileMap = new NativeHashMap<TreeNode, int>(maxNodes, Allocator.Persistent);

        const int numTiles = 256; // Todo: how many do we really need at max?
        PreallocateTiles(numTiles);
    }

    private void ConfigureBurst() {
        Debug.Log("--- BURST INFO ---");
        Debug.Log("Reported CacheLine Size: " + JobsUtility.CacheLineSize);
        Debug.LogFormat("Native Compilation: {0}, Job Debugger: {1}", JobsUtility.JobCompilerEnabled, JobsUtility.JobDebuggerEnabled);
        Debug.LogFormat("Default Worker Count: {0}", JobsUtility.JobWorkerCount);
        
        if (_overrideDefaultBurstWorkerCount) {
            JobsUtility.JobWorkerCount = _burstWorkerCount;
            Debug.LogFormat("Custom Worker Count set to: {0}", JobsUtility.JobWorkerCount);
        }
    }

    private void OnDestroy() {
        _lodJobHandle.Complete();

        _waves.Dispose();

        _lodDistances.Dispose();

        _currVisTree.Dispose();
        _lastVisTree.Dispose();
        _visibleSet.Dispose();

        _loadQueue.Dispose();
        _unloadQueue.Dispose();

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
    */

    private JobHandle _lodJobHandle;
    private CameraInfo _camInfo;

    private void Update() {
       UpdateInteractions();
       UpdateSimulation();        
    }

    private void UpdateInteractions() {
        /* 
       Handle interaction, interfacing with other game systems that
       need to affect the wave surface
       */

        if (Input.GetKeyDown(KeyCode.Space)) {
            var worldRay = new Ray(
                _camera.transform.position, _camera.transform.forward
            );
            _waves.PerturbWavesAtRayIntersection(worldRay);
        }
    }

    private void UpdateSimulation() {
        /*
        Trigger simulation update, and refresh the visual representation
        after that.

        Todo:
        - Only update if camera moved more than a minimum from last update position
        */

        _camInfo = CameraInfo.Create(_camera, Allocator.Persistent);

        _waves.StartUpdate();
        // Todo: arrange scheduling such that this is non-blocking
        _waves.CompleteUpdate();

        var waveSampler = _waves.GetSampler();

        var bMin = new int3(-_lodZeroScale / 2, 0, -_lodZeroScale / 2);
        var lodZeroScale = new int3(_lodZeroScale, WaveHeightScale, _lodZeroScale);

        _currVisTree.Clear(new Bounds(bMin, lodZeroScale));
        var loadedNodes = _tileMap.GetKeyArray(Allocator.TempJob);

        // Todo: let these job depend on simulation jobs
        var expandTreeJob = new ExpandQuadTreeJob()
        {
            camInfo = _camInfo,
            lodDistances = _lodDistances,
            waveSampler = waveSampler,
            tree = _currVisTree,
            visibleSet = _visibleSet
        };

        _lodJobHandle = expandTreeJob.Schedule();

        var deferredVisibleSet = _visibleSet.AsDeferredJobArray();
        var unloadDiffJob = new DiffQuadTreesJob()
        {
            a = loadedNodes,
            b = deferredVisibleSet,
            diff = _unloadQueue
        };
        var loadDiffJob = new DiffQuadTreesJob()
        {
            a = deferredVisibleSet,
            b = loadedNodes,
            diff = _loadQueue
        };

        _lodJobHandle = JobHandle.CombineDependencies(
            unloadDiffJob.Schedule(_lodJobHandle),
            loadDiffJob.Schedule(_lodJobHandle)
        );
        _lodJobHandle.Complete();

        loadedNodes.Dispose();
    }

    private void LateUpdate() {
        _lodJobHandle.Complete();

        Profiler.BeginSample("FreeTile");
        FreeTiles(_unloadQueue);
        Profiler.EndSample();

        Profiler.BeginSample("AssignTile");
        AssignTiles(_loadQueue);
        Profiler.EndSample();

        Profiler.BeginSample("StreamData");
        // Todo: either move to earlier in, or better yet, sample wave maps directly on gpu
        var sampler = _waves.GetSampler();
        StreamNodeData(_visibleSet, sampler);
        Profiler.EndSample();
        
        _camInfo.Dispose();
    }

    private void SwapTrees() {
        var temp = _currVisTree;
        _currVisTree = _lastVisTree;
        _lastVisTree = temp;
    }

    private void OnGUI() {
        if (!Application.isPlaying) {
            return;
        }
        
        if (_showWaveDebugData) {
            _waves.StartRender();
            _waves.CompleteRender();
            _waves.OnDrawGUI();
        }

        GUILayout.BeginVertical(GUI.skin.box);
        {
            GUILayout.Label(string.Format("Domain Extents: {0}", _lodZeroScale));
            GUILayout.Label(string.Format("Visible gpu tiles: {0}", _tileMap.Length));
        }
        GUILayout.EndVertical();
    }

    private void OnDrawGizmos() {
        if (!Application.isPlaying) {
            return;
        }

        var worldRay = new Ray(
            _camera.transform.position, _camera.transform.forward
        );
        _waves.DrawWavesRayIntersection(worldRay);

        DrawTree(_currVisTree);
    }

    private void DrawTree(Tree tree) {
        var nodeList = tree.Nodes.GetValueArray(Allocator.Temp);
        for (int i = 0; i < nodeList.Length; i++) {
            var node = nodeList[i];

            if (node.hasChildren) {
                continue;
            }

            Color gizmoColor = Color.red;
            if (_tileMap.ContainsKey(node)) {
                gizmoColor = Color.HSVToRGB(node.depth / (float)tree.MaxDepth, 0.9f, 1f);
            }

            Gizmos.color = gizmoColor;
            Gizmos.DrawWireCube((float3)(node.bounds.position + node.bounds.size / 2), (float3)node.bounds.size);
        }
        nodeList.Dispose();
    }

    private void FreeTiles(NativeArray<TreeNode> unloadQueue) {
        for (int i = 0; i < unloadQueue.Length; i++) {
            TreeNode node = unloadQueue[i];

            if (!_tileMap.ContainsKey(node)) {
                Debug.LogWarningFormat("Attempting to unload node without active mesh assigned to it: {0}", unloadQueue[i].bounds);
                continue;
            }

            // Debug.Log(string.Format("Unloading: MeshTile_D{0}_[{1},{2}]", node.depth, node.bounds.position.x, node.bounds.position.z));

            int idx = _tileMap[node];
            var mesh = _tiles[idx];
            mesh.gameObject.SetActive(false);

            _tileMap.Remove(node);
            _tileIndexPool.Push(idx);
        }
    }

    private void AssignTiles(NativeArray<TreeNode> loadQueue) {
        for (int i = 0; i < loadQueue.Length; i++) {
            var node = loadQueue[i];

            // Debug.Log(string.Format("Loading: MeshTile_D{0}_[{1},{2}]", node.depth, node.bounds.position.x, node.bounds.position.z));

            const float lMin = 2.1f, lMax = 2.9f;
            var lerpRanges = new Vector4(_lodDistances[node.depth] * lMin, _lodDistances[node.depth] * lMax);

            if (_tileMap.ContainsKey(node)) {
                Debug.LogWarningFormat("Attempting to assign tile that's already in use! {0}", node);
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
            mesh.MeshRenderer.material.SetFloat("_HeightScale", WaveHeightScale);
            mesh.MeshRenderer.material.SetVector("_LerpRanges", lerpRanges);

            mesh.MeshRenderer.material.SetTexture("_HeightTex", mesh.HeightMap);
            mesh.MeshRenderer.material.SetTexture("_NormalTex", mesh.NormalMap);
            mesh.Mesh.bounds = new UnityEngine.Bounds(Vector3.zero, (float3)node.bounds.size);
            mesh.gameObject.name = string.Format("MeshTile_D{0}_[{1},{2}]", node.depth, node.bounds.position.x, node.bounds.position.z);
            mesh.gameObject.SetActive(true);
        }
    }

    /* Todo:
    - Send wave data directly to the gpu, then sample it there directly, saves work here
    */

    private void StreamNodeData(NativeArray<TreeNode> nodes, WaveSampler sampler) {
        var streamHandles = new NativeArray<JobHandle>(nodes.Length, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
        int numVerts = _tileResolution + 1;

        for (int i = 0; i < nodes.Length; i++) {
            var node = nodes[i];
            var mesh = _tiles[_tileMap[node]];

            var heights = mesh.HeightMap.GetRawTextureData<byte2>();
            var normals = mesh.NormalMap.GetRawTextureData<float2>();

            var generateJob = new StreamWaveDataJob()
            {
                heights = heights,
                normals = normals,
                numVerts = numVerts,
                sampler = sampler,
                bounds = node.bounds
            };
            streamHandles[i] = generateJob.Schedule(numVerts * numVerts, 32);
        }

        JobHandle.CompleteAll(streamHandles);

        for (int i = 0; i < nodes.Length; i++) {
            var node = nodes[i];
            var mesh = _tiles[_tileMap[node]];

            mesh.HeightMap.Apply(false);
            mesh.NormalMap.Apply(true);
        }

        streamHandles.Dispose();
    }

    private void AllocateNewTileInPool() {
        int idx = _tiles.Count;
        var tile = CreateTileObject("tile_" + idx, _tileResolution, _material);
        
        tile.Transform.parent = transform;
        tile.gameObject.SetActive(false);

        _tileIndexPool.Push(idx);
        _tiles.Add(tile);
    }

    private static MeshTile CreateTileObject(string name, int resolution, Material material) {
        var tileObject = new GameObject(name);
        var tile = tileObject.AddComponent<MeshTile>();
        tile.Create(resolution);
	    tile.MeshRenderer.material = material;
        tile.MeshRenderer.material.SetFloat("_Scale", 16f);
        tile.MeshRenderer.material.SetVector("_LerpRanges", new Vector4(1f, 16f));

	    return tile;
	}

    [BurstCompile]
    public struct GetValuesJob : IJob {
        [ReadOnly] public NativeHashMap<int, TreeNode> map;
        [ReadOnly] public Allocator allocator;
        [WriteOnly] public NativeArray<TreeNode> values;

        public void Execute() {
            values = map.GetValueArray(allocator);
        }
    }

    [BurstCompile]
    public struct StreamWaveDataJob : IJobParallelFor {
        public NativeSlice<byte2> heights;
        public NativeSlice<float2> normals;
        public int numVerts;
        public WaveSampler sampler;
        public Bounds bounds;
        
        public void Execute(int idx) {
            // Todo: calculate job-constant data on main thread instead of per pixel

            float stepSize = bounds.size.x / (float)(numVerts - 1);

            int x = idx % numVerts;
            int z = idx / numVerts;

            float xPos = bounds.position.x + x * stepSize;
            float zPos = bounds.position.z + z * stepSize;

            float3 sample = sampler.Sample(xPos, zPos);
            float height = (0.5f + 0.5f * sample.x);

            heights[idx] = new byte2(
                (byte)(Mathf.RoundToInt(height * 65535f) >> 8),
                (byte)(Mathf.RoundToInt(height * 65535f))
            );

            // float3 normal = math.normalize(math.cross(
            //     new float3(stepSize, sample.y * WaveHeightScale, 0f),
            //     new float3(0f, sample.z * WaveHeightScale, stepSize)));

            float3 normal = math.normalize(math.cross(
                new float3(1f, sample.y, 0f),
                new float3(0f, sample.z, 1f)));

            // Note: normal z-component is recalculated on the gpu, which saves transfer memory
            normals[idx] = new float2(
                0.5f + normal.x * 0.5f,
                0.5f + normal.y * 0.5f);
        }
    }
}

public interface IHeightSampler {
    float HeightScale { get; }
    float Sample(float x, float z);
}