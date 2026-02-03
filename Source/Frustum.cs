using System.Numerics;
using Raylib_cs;

internal class Frustum {

    private readonly Plane[] _planes = new Plane[6];

    public Frustum(Camera3D cam, float aspect, float zNear, float zFar) {

        // Calculate Camera Basis
        var forward = Vector3.Normalize(cam.Target - cam.Position);
        var right = Vector3.Normalize(Vector3.Cross(forward, cam.Up)); // Raylib uses OpenGL, Y is up, -Z is forward usually, but here forward is explicitly Target-Pos
        var up = Vector3.Normalize(Vector3.Cross(right, forward));

        // Half dimensions at Near and Far planes - tan(fov/2)
        var tanHalfFov = (float)Math.Tan(cam.FovY * Math.PI / 360.0);

        var hNear = 2f * tanHalfFov * zNear;
        var wNear = hNear * aspect;

        var hFar = 2f * tanHalfFov * zFar;
        var wFar = hFar * aspect;

        // Centers
        var cNear = cam.Position + forward * zNear;
        var cFar = cam.Position + forward * zFar;

        // Corners (Half widths/heights)
        var wn2 = wNear / 2f;
        var hn2 = hNear / 2f;
        var wf2 = wFar / 2f;
        var hf2 = hFar / 2f;

        // Near Plane Corners (TL, TR, BR, BL) using right/up vectors
        var ntl = cNear + (up * hn2) - (right * wn2);
        var ntr = cNear + (up * hn2) + (right * wn2);
        var nbr = cNear - (up * hn2) + (right * wn2);
        var nbl = cNear - (up * hn2) - (right * wn2);

        // Far Plane Corners
        var ftl = cFar + (up * hf2) - (right * wf2);
        var ftr = cFar + (up * hf2) + (right * wf2);
        var fbr = cFar - (up * hf2) + (right * wf2);
        var fbl = cFar - (up * hf2) - (right * wf2);

        // Near Plane
        _planes[0] = new Plane(ntl, nbl, fbl); // Left
        _planes[1] = new Plane(ntr, nbr, fbr); // Right
        _planes[2] = new Plane(ntl, ntr, nbr); // Near
        _planes[3] = new Plane(ftl, ftr, fbr); // Far
        _planes[4] = new Plane(ntl, ftl, ftr); // Top
        _planes[5] = new Plane(nbl, fbr, fbl); // Bottom

        // Ensure all normals point INSIDE the frustum
        var center = cam.Position + forward * ((zNear + zFar) / 2f);

        for (var i = 0; i < 6; i++) {

            if (!(_planes[i].DistanceToPoint(center) < 0)) continue;

            _planes[i].Normal = -_planes[i].Normal;
            _planes[i].Distance = -_planes[i].Distance;
        }
    }

    public bool IntersectsBox(Vector3 min, Vector3 max) { return !(from plane in _planes let p = new Vector3(plane.Normal.X > 0 ? max.X : min.X, plane.Normal.Y > 0 ? max.Y : min.Y, plane.Normal.Z > 0 ? max.Z : min.Z) where plane.DistanceToPoint(p) < 0 select plane).Any(); }
}