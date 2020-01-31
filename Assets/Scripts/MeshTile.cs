using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Mathematics;

/*
Todo: 
- create mesh once, reuse for all tiles
- use material property blocks to manage per-tile data
*/

public struct Vertex {
    public float3 position;
    public float3 normal;
}

public class MeshTile : MonoBehaviour {
    private Transform _transform;
    private Mesh _mesh;
    private MeshFilter _meshFilter;
    private MeshRenderer _renderer;
    private Texture2D _heightMap;
    private Texture2D _normalMap;

    private int _resolution;
    private int _indexEndTl;
    private int _indexEndTr;
    private int _indexEndBl;
    private int _indexEndBr;

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
        get { return _renderer; }
    }

    public Texture2D HeightMap {
        get => _heightMap;
    }

    public Texture2D NormalMap {
        get => _normalMap;
    }

    public void Create(int resolution) {
        _transform = gameObject.GetComponent<Transform>();

        if (!Mathf.IsPowerOfTwo(resolution)) {
            resolution = Mathf.ClosestPowerOfTwo(resolution);
            Debug.LogWarning("Got incompatible tile resolution, rounding resolution to nearest power of two...");
        }

        _resolution = resolution;
        _meshFilter = gameObject.AddComponent<MeshFilter>();
        _renderer = gameObject.AddComponent<MeshRenderer>();
        
        CreateMesh(resolution);

        _heightMap = new Texture2D(resolution + 1, resolution + 1, TextureFormat.R16, false, true);
        _normalMap = new Texture2D(resolution + 1, resolution + 1, TextureFormat.RGFloat, true, true); // Todo: use RGHalf?
        _heightMap.wrapMode = TextureWrapMode.Clamp;
        _normalMap.wrapMode = TextureWrapMode.Clamp;
        _heightMap.filterMode = FilterMode.Point;
        _normalMap.filterMode = FilterMode.Bilinear;
        _normalMap.anisoLevel = 4;

        _renderer.material.SetTexture("_HeightTex", _heightMap);
        _renderer.material.SetTexture("_NormalTex", _normalMap);
}

    private void CreateMesh(int resolution) {
        int vertsPerDim = (resolution + 1);
        int numVerts = vertsPerDim * vertsPerDim;
        int numIndices = resolution * resolution * 2 * 3;

        var vertices = new NativeArray<Vertex>(numVerts, Allocator.Temp);
        var indices = new NativeArray<uint>(numIndices, Allocator.Temp); // (ushort)?

        for (int y = 0; y < vertsPerDim; y++) {
            for (int x = 0; x < vertsPerDim; x++) {
                vertices[vertsPerDim * y + x] = new Vertex {
                    position = new float3(x/(float)resolution, 0f, y/(float)resolution),
                    normal = new float3(0,1,0)
                };
            }
        }

        // CreateIndices(indices, resolution);

        int index = 0;
        int halfRes = resolution/2;

        CreateIndicesForQuadrant(indices, vertsPerDim, ref index, 0, halfRes, 0, halfRes);
        _indexEndTl = index;

        CreateIndicesForQuadrant(indices, vertsPerDim, ref index, 0, halfRes, halfRes, resolution);
        _indexEndTr = index;

        CreateIndicesForQuadrant(indices, vertsPerDim, ref index, halfRes, resolution, 0, halfRes);
        _indexEndBl = index;

        CreateIndicesForQuadrant(indices, vertsPerDim, ref index, halfRes, resolution, halfRes, resolution);
        _indexEndBr = index;

        _mesh = new Mesh();
        _mesh.hideFlags = HideFlags.DontSave;

        _mesh.SetVertexBufferParams(
            numVerts,
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3)
        );

        // var updateFlags = MeshUpdateFlags.Default;
        var updateFlags = MeshUpdateFlags.DontValidateIndices; // Saves on compute
        
        _mesh.SetIndexBufferParams(numIndices, IndexFormat.UInt32);
        _mesh.SetIndexBufferData(indices, 0, 0, numIndices, updateFlags);
        _mesh.SetVertexBufferData(vertices, 0, 0, numVerts, 0, updateFlags);

        _mesh.SetSubMesh(0, new SubMeshDescriptor(0, numIndices), updateFlags);

        /*
        Todo: Since we know the data we're rendering, don't leave Unity to
        calculate the bounds, just supply them.
        */
        // _mesh.bounds = new Bounds(new Vector3(8f, 8f, 8f), new Vector3(16f, 16f, 16f));
        
        _meshFilter.mesh = _mesh;
    }

    // We create the tile as 4 quadrants, for easy deactivation later.
    private static void CreateIndicesForQuadrant(NativeArray<uint> triangles, int vertsPerDim, ref int index, int yStart, int yEnd, int xStart, int xEnd) {
        for (int y = yStart; y < yEnd; y++) {
            for (int x = xStart; x < xEnd; x++) {
                triangles[index++] = (uint)((x + 0) + vertsPerDim * (y + 1));
                triangles[index++] = (uint)((x + 1) + vertsPerDim * (y + 0));
                triangles[index++] = (uint)((x + 0) + vertsPerDim * (y + 0));

                triangles[index++] = (uint)((x + 0) + vertsPerDim * (y + 1));
                triangles[index++] = (uint)((x + 1) + vertsPerDim * (y + 1));
                triangles[index++] = (uint)((x + 1) + vertsPerDim * (y + 0));
            }
        }
    }

    // private static void CreateIndices(NativeArray<uint> triangles, int resolution) {
    //     int vertsPerDim = resolution + 1;
    //     int index = 0;
    //     for (int y = 0; y < resolution; y++) {
    //         for (int x = 0; x < resolution; x++) {
    //             triangles[index++] = (uint)((x + 0) + vertsPerDim * (y + 1));
    //             triangles[index++] = (uint)((x + 1) + vertsPerDim * (y + 0));
    //             triangles[index++] = (uint)((x + 0) + vertsPerDim * (y + 0));

    //             triangles[index++] = (uint)((x + 0) + vertsPerDim * (y + 1));
    //             triangles[index++] = (uint)((x + 1) + vertsPerDim * (y + 1));
    //             triangles[index++] = (uint)((x + 1) + vertsPerDim * (y + 0));
    //         }
    //     }
    // }
}
