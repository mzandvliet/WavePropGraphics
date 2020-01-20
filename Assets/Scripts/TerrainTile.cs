﻿using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

/*
Todo: create mesh once, reuse for all tiles
*/

public struct Vertex {
    public float3 position;
    public float3 normal;
}

public class TerrainTile : MonoBehaviour {
    private Transform _transform;
    private Mesh _mesh;
    private MeshFilter _meshFilter;
    private MeshRenderer _meshRenderer;

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

    public void Create(int resolution) {
        _transform = gameObject.GetComponent<Transform>();

        if (!Mathf.IsPowerOfTwo(resolution)) {
            resolution = Mathf.ClosestPowerOfTwo(resolution);
            Debug.LogWarning("Got incompatible tile resolution, rounding resolution to nearest power of two...");
        }

        _resolution = resolution;
        _meshFilter = gameObject.AddComponent<MeshFilter>();
        _meshRenderer = gameObject.AddComponent<MeshRenderer>();
        
        CreateMesh(resolution);
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
        
        _mesh.SetIndexBufferParams(numIndices, IndexFormat.UInt32);
        _mesh.SetIndexBufferData(indices, 0, 0, numIndices);
        _mesh.SetVertexBufferData(vertices, 0, 0, numVerts);

        _mesh.SetSubMesh(0, new SubMeshDescriptor(0, numIndices));

        /* We can set these manually with the knowledge we have during content
         * streaming (todo: autocalc bounds fails here for some reason, why?)
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
