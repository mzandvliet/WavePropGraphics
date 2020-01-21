using UnityEngine;
using Unity.Burst;
using Unity.Jobs;
using Unity.Collections;
using Unity.Mathematics;

public struct CameraInfo : System.IDisposable {
    public float3 position;
    public NativeArray<Plane> frustumPlanes; // Todo: hardcode struct size to accomodate plane array?

    private static Plane[] PlaneCache = new Plane[6];

    public static CameraInfo Create(Camera camera) {
        GeometryUtility.CalculateFrustumPlanes(camera, PlaneCache);

        var planes = new NativeArray<Plane>(6, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
        planes.CopyFrom(PlaneCache);

        return new CameraInfo() {
            position = camera.transform.position,
            frustumPlanes = planes,
        };
    }

    public void Dispose() {
        frustumPlanes.Dispose();
    }
}