using System.Collections.Generic;
using UnityEngine;

/*
 * Make a pool of X^2 resolution vert meshes
 * Walk quadtree tree each frame to find the payloads that should be streamed/generated
 *      LOD level is based on 3D distance to the camera
 * Get a mesh from the pool and stream data into it
 * Shift odd-numbered vertices in vertex shader based on cam distance (approximation) for smooth lod transition
 */
public class TerrainSystem : MonoBehaviour {
    [SerializeField] private Material _material;
    private List<TerrainMesh> _meshPool;

    private Texture2D _heightmap;

	// Use this for initialization
	void Start () {
	    const int resolution = 16;

        _heightmap = GenerateHeightmap(resolution);

	    var tile = new TerrainMesh(resolution);
	    tile.MeshRenderer.material = _material;

        tile.MeshRenderer.material.SetTexture("_HeightTex", _heightmap);
	}

    private static Texture2D GenerateHeightmap(int resolution) {
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

    private void OnGUI() {
        GUI.DrawTexture(new Rect(16, 16, 128, 128), _heightmap);
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
        _mesh.bounds = new Bounds(new Vector3(0.5f, 0.5f, 0.5f), new Vector3(1f, 1f, 1f));
        _mesh.RecalculateNormals();

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
