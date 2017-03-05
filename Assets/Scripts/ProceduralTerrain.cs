using CoherentNoise.Generation.Fractal;
using UnityEngine;

/* Todo: 
 * 
 * For precipitation effects, try tapering intensity over time. (effectively ending sim without water/snow, etc)
 * Rock hardness
 * Try downsampling in the erosion stage for speed increase
 * 
 */

[ExecuteInEditMode]
public class ProceduralTerrain : MonoBehaviour {
    [SerializeField] private bool _init;
    [SerializeField] private bool _erode;
    
    [SerializeField] private ErosionConfig _erosionConfig;
    [SerializeField] private int _iterations = 1;

    private Terrain _terrain;
    private ErosionMap _map;

	void Update () {
        if (!_terrain) {
            _terrain = GetComponent<Terrain>();
        }

	    if (_init) {
            _map = new ErosionMap(_terrain.terrainData.heightmapResolution);
	        GenerateNoise(_map);
            Apply();
            _init = false;
	    }

        if (_erode) {
            Debug.Log("Erode");
            Erode(_map, _erosionConfig, _iterations);
            Apply();
            _erode = false;
        }
	}

    private void Apply() {
        _terrain.terrainData.SetHeights(0, 0, _map.HeightMap);

        Debug.Log("Done");
   }

    private static void GenerateNoise(ErosionMap map) {
        var noise = new RidgeNoise(1234);
        noise.Frequency = 0.001f;
        noise.Exponent = .66f;
        noise.Gain = 2f;

        float stepSize = 1000f/map.Size;

        for (int x = 0; x < map.Size; x++) {
            for (int y = 0; y < map.Size; y++) {
                map.HeightMap[x, y] = Mathf.Clamp01(noise.GetValue(x * stepSize, y * stepSize, 0) * 0.5f);
            }
        }
    }
    
    private static void Erode(ErosionMap map, ErosionConfig cfg, int iterations) {
        for (int i = 0; i < iterations; i++) {

            // Add some water, dissolve some sediment in the water

            for (int x = 0; x < map.Size; x++) {
                for (int y = 0; y < map.Size; y++) {
                    var center = map.Get(x, y);

                    //float slope = GetSlope(new Index2D(x, y), map);

                    center.Water += cfg.Rain;

                    float cleanWater = center.Water - center.Sediment / cfg.Solubility;
                    float sedimentTaken = Mathf.Min(cleanWater * cfg.Solubility, center.Height);
                    center.Sediment += sedimentTaken;
                    center.Height -= sedimentTaken;

                    map.Set(x, y, center);
                }
            }

            // Spread water

            //float[,] waterTemp = new float[map.Size, map.Size];
            //float[,] sedimentTemp = new float[map.Size, map.Size];

            for (int x = 0; x < map.Size; x++) {
                for (int y = 0; y < map.Size; y++) {
                    var centerIndex = new Index2D(x, y);
                    var center = map.Get(x, y);

                    
                    var neighborIndex = GetLowestNeighbour(centerIndex, map);
                    if (neighborIndex != centerIndex) {
                        var neighbor = map.Get(neighborIndex);

                        float slope = center.Height - neighbor.Height;

                        float waterTransfered = center.Water * slope;
                        center.Water -= waterTransfered;
                        neighbor.Water += waterTransfered;

                        float sedimentTransfered = center.Sediment * slope;
                        center.Sediment -= sedimentTransfered;
                        neighbor.Sediment += sedimentTransfered;

                        //if (neighbor.Height + neighbor.Water + center.Water < center.Height) {
                        //    neighbor.Water += center.Water;
                        //    neighbor.Sediment += center.Sediment;
                        //    center.Water = 0f;
                        //    center.Sediment = 0f;
                        //} else {
                        //    float halfWaterDif = ((center.Height + center.Water) - (neighbor.Height + neighbor.Water)) * 0.5f;
                        //    float sedimentDif = halfWaterDif / center.Water * center.Sediment;
                        //    center.Water -= halfWaterDif;
                        //    center.Sediment -= sedimentDif;
                        //    neighbor.Water += halfWaterDif;
                        //    neighbor.Sediment += sedimentDif;
                        //}

                        map.Set(neighborIndex, neighbor);
                    }

                    map.Set(x, y, center);
                }
            }

            // Evaporate water & dispose sediment

            for (int x = 0; x < map.Size; x++) {
                for (int y = 0; y < map.Size; y++) {
                    var center = map.Get(x, y);

                    
                    center.Water = Mathf.Max(center.Water - cfg.Evaporation, 0f);

                    float capacity = center.Water * cfg.Capacity;
                    if (center.Sediment > capacity) {
                        float sedimentDisposed = center.Sediment - capacity;
                        center.Sediment -= sedimentDisposed;
                        center.Height += sedimentDisposed;
                    }

                    map.Set(x, y, center);
                }
            }
        }
    }

    private static Index2D GetLowestNeighbour(Index2D center, ErosionMap map) {
        float lowestNeighborHeight = 1f;
        Index2D indexOfLowest = center;

        Index2D neighbor = new Index2D();

        //// Left
        //neighbor.X = center.X - 1;
        //neighbor.Y = center.Y;
        //lowestNeighborHeight = SampleNeighbour(map, neighbor, lowestNeighborHeight, ref indexOfLowest);

        //// Right
        //neighbor.X = center.X + 1;
        //neighbor.Y = center.Y;
        //lowestNeighborHeight = SampleNeighbour(map, neighbor, lowestNeighborHeight, ref indexOfLowest);

        //// Down
        //neighbor.X = center.X;
        //neighbor.Y = center.Y - 1;
        //lowestNeighborHeight = SampleNeighbour(map, neighbor, lowestNeighborHeight, ref indexOfLowest);

        //// Up
        //neighbor.X = center.X;
        //neighbor.Y = center.Y + 1;
        //lowestNeighborHeight = SampleNeighbour(map, neighbor, lowestNeighborHeight, ref indexOfLowest);

        // BottomLeft
        neighbor.X = center.X - 1;
        neighbor.Y = center.Y - 1;
        lowestNeighborHeight = CompareNeighbourLow(map, neighbor, lowestNeighborHeight, ref indexOfLowest);

        // BottomRight
        neighbor.X = center.X + 1;
        neighbor.Y = center.Y - 1;
        lowestNeighborHeight = CompareNeighbourLow(map, neighbor, lowestNeighborHeight, ref indexOfLowest);

        // TopLeft
        neighbor.X = center.X - 1;
        neighbor.Y = center.Y + 1;
        lowestNeighborHeight = CompareNeighbourLow(map, neighbor, lowestNeighborHeight, ref indexOfLowest);

        // TopRight
        neighbor.X = center.X + 1;
        neighbor.Y = center.Y + 1;
        lowestNeighborHeight = CompareNeighbourLow(map, neighbor, lowestNeighborHeight, ref indexOfLowest);

        return indexOfLowest;
    }

    private static float CompareNeighbourLow(ErosionMap map, Index2D neighbor, float lowestNeighborHeight, ref Index2D indexOfLowest) {
        if (neighbor.X >= 0 && neighbor.X < map.Size && neighbor.Y >= 0 && neighbor.Y < map.Size) {
            float neighbourHeight = map.HeightMap[neighbor.X, neighbor.Y];
            if (neighbourHeight < lowestNeighborHeight) {
                lowestNeighborHeight = neighbourHeight;
                indexOfLowest.X = neighbor.X;
                indexOfLowest.Y = neighbor.Y;
            }
        }

        return lowestNeighborHeight;
    }

    //private static Index2D GetHighestNeighbour(Index2D center, ErosionMap map) {
    //    float highestNeighbour = 0f;
    //    Index2D indexOfHighest = center;

    //    Index2D neighbor = new Index2D();

    //    // BottomLeft
    //    neighbor.X = center.X - 1;
    //    neighbor.Y = center.Y - 1;
    //    highestNeighbour = CompareNeighbourHigh(map, neighbor, highestNeighbour, ref indexOfHighest);

    //    // BottomRight
    //    neighbor.X = center.X + 1;
    //    neighbor.Y = center.Y - 1;
    //    highestNeighbour = CompareNeighbourHigh(map, neighbor, highestNeighbour, ref indexOfHighest);

    //    // TopLeft
    //    neighbor.X = center.X - 1;
    //    neighbor.Y = center.Y + 1;
    //    highestNeighbour = CompareNeighbourHigh(map, neighbor, highestNeighbour, ref indexOfHighest);

    //    // TopRight
    //    neighbor.X = center.X + 1;
    //    neighbor.Y = center.Y + 1;
    //    highestNeighbour = CompareNeighbourHigh(map, neighbor, highestNeighbour, ref indexOfHighest);

    //    return indexOfHighest;
    //}

    //private static float CompareNeighbourHigh(ErosionMap map, Index2D neighbor, float lowestNeighborHeight, ref Index2D indexOfLowest) {
    //    if (neighbor.X >= 0 && neighbor.X < map.Size && neighbor.Y >= 0 && neighbor.Y < map.Size) {
    //        float neighbourHeight = map.HeightMap[neighbor.X, neighbor.Y];
    //        if (neighbourHeight < lowestNeighborHeight) {
    //            lowestNeighborHeight = neighbourHeight;
    //            indexOfLowest.X = neighbor.X;
    //            indexOfLowest.Y = neighbor.Y;
    //        }
    //    }

    //    return lowestNeighborHeight;
    //}

    //private static float GetSlope(Index2D center, ErosionMap map) {
    //    var lowestNeighbour = GetLowestNeighbour(center, map);
    //    var highestNeighbour = GetHighestNeighbour(center, map);
    //    return map.HeightMap[highestNeighbour.X, highestNeighbour.Y] - map.HeightMap[lowestNeighbour.X, lowestNeighbour.Y];
    //}

    [System.Serializable]
    public class ErosionConfig {
        [SerializeField]
        public float Rain = 0.1f;
        [SerializeField]
        public float Solubility = 0.1f;
        [SerializeField]
        public float Capacity = 0.3f;
        [SerializeField]
        public float Evaporation = 0.05f;
    }

    public struct ErosionPixel {
        public float Height;
        public float Sediment;
        public float Water;

        public ErosionPixel(float height, float sediment, float water) {
            Height = height;
            Sediment = sediment;
            Water = water;
        }
    }

    public class ErosionMap {
        public int Size { get; private set; }
        public float[,] HeightMap { get; private set; }
        public float[,] SedimentMap { get; private set; }
        public float[,] WaterMap { get; private set; }
        
        public ErosionMap(int size) {
            HeightMap = new float[size,size];
            SedimentMap = new float[size, size];
            WaterMap = new float[size, size];
            Size = size;
        }

        public ErosionPixel Get(Index2D i) {
            return Get(i.X, i.Y);
        }

        public ErosionPixel Get(int x, int y) {
            return new ErosionPixel(HeightMap[x,y], SedimentMap[x,y], WaterMap[x,y]);
        }

        public void Set(Index2D i, ErosionPixel data) {
            Set(i.X, i.Y, data);
        }

        public void Set(int x, int y, ErosionPixel data) {
            HeightMap[x, y] = data.Height;
            SedimentMap[x, y] = data.Sediment;
            WaterMap[x, y] = data.Water;
        }
    }
}

public struct Index2D {
    public int X;
    public int Y;

    public Index2D(int x, int y) {
        X = x;
        Y = y;
    }

    public bool Equals(Index2D other) {
        return X == other.X && Y == other.Y;
    }

    public override bool Equals(object obj) {
        if (ReferenceEquals(null, obj)) {
            return false;
        }
        return obj is Index2D && Equals((Index2D)obj);
    }

    public override int GetHashCode() {
        unchecked {
            return (X * 397) ^ Y;
        }
    }

    public static bool operator ==(Index2D left, Index2D right) {
        return left.Equals(right);
    }

    public static bool operator !=(Index2D left, Index2D right) {
        return !left.Equals(right);
    }
}
