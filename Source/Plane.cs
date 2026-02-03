using System.Numerics;

internal struct Plane {

    public Vector3 Normal;
    public float Distance;

    public Plane(Vector3 p1, Vector3 p2, Vector3 p3) {

        var v1 = p2 - p1;
        var v2 = p3 - p1;
        Normal = Vector3.Normalize(Vector3.Cross(v1, v2));
        Distance = -Vector3.Dot(Normal, p1);
    }

    public float DistanceToPoint(Vector3 point) {

        // Dot(N, P) + d
        return Vector3.Dot(Normal, point) + Distance;
    }
}