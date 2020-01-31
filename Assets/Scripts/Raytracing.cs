using Unity.Mathematics;

public struct Ray {
    public float3 pos;
    public float3 dir;

    public Ray(float3 origin, float3 direction) {
        this.pos = origin;
        this.dir = direction;
    }
    
    public Ray(Ray ray) {
        this.pos = ray.pos;
        this.dir = ray.dir;
    }
}

public struct BoundsF32 : System.IEquatable<BoundsF32> {
    // These are in METERS
    public float3 position;
    public float3 size;

    public float3 Min {
        get => position;
    }

    public float3 Max {
        get => position + size;
    }

    public float3 Center {
        get => position + size * 0.5f;
    }

    public BoundsF32(float3 position, float3 size) {
        this.position = position;
        this.size = size;
    }

    public override bool Equals(System.Object obj) {
        return obj is BoundsF32 && this == (BoundsF32)obj;
    }

    public bool Equals(BoundsF32 other) {
        return this == other;
    }

    public override int GetHashCode() {
        // return position.GetHashCode() ^ size.x.GetHashCode();
        unchecked {
            return (position.xz.GetHashCode() * 397) ^ size.x.GetHashCode();
        }
    }

    public static bool operator ==(BoundsF32 a, BoundsF32 b) {
        return
            a.position.x == b.position.x &&
            a.position.z == b.position.z &&
            a.size.x == b.size.x;
    }
    public static bool operator !=(BoundsF32 a, BoundsF32 b) {
        return !(a == b);
    }

    public override string ToString() {
        return string.Format("[Pos: {0}, Size: {1}]", position, size);
    }
}

public static class RayUtil {
    public static bool IntersectAABB3D(BoundsF32 b, Ray r, out float tmin) {
        // Based on: https://tavianator.com/fast-branchless-raybounding-box-intersections/
        // But using the naive version first, to verify it works

        tmin = float.NegativeInfinity;
        float tmax = float.PositiveInfinity;

        if (r.dir.x != 0.0) {
            float tx1 = (b.Min.x - r.pos.x) / r.dir.x;
            float tx2 = (b.Max.x - r.pos.x) / r.dir.x;

            tmin = math.max(tmin, math.min(tx1, tx2));
            tmax = math.min(tmax, math.max(tx1, tx2));
        }

        if (r.dir.y != 0.0) {
            float ty1 = (b.Min.y - r.pos.y) / r.dir.y;
            float ty2 = (b.Max.y - r.pos.y) / r.dir.y;

            tmin = math.max(tmin, math.min(ty1, ty2));
            tmax = math.min(tmax, math.max(ty1, ty2));
        }

        if (r.dir.z != 0.0) {
            float ty1 = (b.Min.z - r.pos.z) / r.dir.z;
            float ty2 = (b.Max.z - r.pos.z) / r.dir.z;

            tmin = math.max(tmin, math.min(ty1, ty2));
            tmax = math.min(tmax, math.max(ty1, ty2));
        }

        return tmax >= tmin && tmax >= 0;
    }
}