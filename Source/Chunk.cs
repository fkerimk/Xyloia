using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Raylib_cs;
using static Raylib_cs.Raylib;

internal class Chunk(int x, int y, int z) : IDisposable {

    public readonly int X = x, Y = y, Z = z;

    public readonly List<Mesh> Meshes = [];
    private Block[]? _blocks = System.Buffers.ArrayPool<Block>.Shared.Rent(4096);

    private readonly Lock _lock = new();

    private List<List<float>>? _vLists, _nLists, _tLists;
    private List<ushort[]>? _iLists;

    private static readonly ThreadLocal<MeshBuilder> Builder = new(() => new MeshBuilder());

    private class MeshBuilder {

        public List<float> Verts = new(4096);
        public List<float> Norms = new(4096);
        public List<float> Uvs = new(2048);
        public readonly List<ushort> Tris = new(2048);

        public ushort VIdx;

        public void Clear() {

            Verts.Clear();
            Norms.Clear();
            Uvs.Clear();
            Tris.Clear();
            VIdx = 0;
        }
    }

    public Block GetBlock(int x, int y, int z) {

        if (_blocks == null || x < 0 || x >= 16 || y < 0 || y >= 16 || z < 0 || z >= 16) return new Block();

        return _blocks[(x * 16 + z) * 16 + y];
    }

    public void SetBlock(int x, int y, int z, Block block) {

        if (_blocks == null || x < 0 || x >= 16 || y < 0 || y >= 16 || z < 0 || z >= 16) return;

        _blocks[(x * 16 + z) * 16 + y] = block;
        IsDirty = true;
    }

    private volatile bool _disposed;

    private static class ListPool<T> {

        private static readonly ConcurrentQueue<List<T>> Queue = new();

        public static List<T> Rent() {

            if (!Queue.TryDequeue(out var list)) return new List<T>(4096);

            list.Clear();

            return list;
        }

        public static void Return(List<T> list) => Queue.Enqueue(list);
    }

    public void Generate() {

        if (_disposed || _blocks == null) return;

        var blocks = _blocks;

        var grassId = Registry.GetId("grass");
        var dirtId = Registry.GetId("dirt");

        for (var lx = 0; lx < 16; lx++)
        for (var lz = 0; lz < 16; lz++) {

            float worldX = X * 16 + lx;
            float worldZ = Z * 16 + lz;

            var height = (Noise.Perlin3D(worldX * 0.015f, 0, worldZ * 0.015f) + 1) * 32 + 20;
            height += (Noise.Perlin3D(worldX * 0.05f, 0, worldZ * 0.05f)) * 8;

            for (var ly = 0; ly < 16; ly++) {

                if (_disposed) return;

                float worldY = Y * 16 + ly;

                byte blockId = 0;

                if (worldY < height) {

                    blockId = (worldY > height - 1) ? grassId : dirtId;

                    var cave = Noise.Perlin3D(worldX * 0.08f, worldY * 0.08f, worldZ * 0.08f);
                    if (cave > 0.45f) blockId = 0;
                }

                blocks[(lx * 16 + lz) * 16 + ly] = new Block(blockId);
            }
        }
    }

    public volatile bool IsDirty;

    public void BuildArrays(Chunk? nx, Chunk? px, Chunk? ny, Chunk? py, Chunk? nz, Chunk? pz) {

        if (_disposed || _blocks == null) return;

        var builder = Builder.Value!;
        builder.Clear();

        var newVLists = new List<List<float>>();
        var newNLists = new List<List<float>>();
        var newTLists = new List<List<float>>();
        var newILists = new List<ushort[]>();

        var blocks = _blocks;

        for (var x = 0; x < 16; x++)
        for (var z = 0; z < 16; z++)
        for (var y = 0; y < 16; y++) {

            if (_disposed) return;

            if (builder.VIdx > 60000) Flush(builder, newVLists, newNLists, newTLists, newILists);

            var b = blocks[(x * 16 + z) * 16 + y];

            if (!b.Solid) continue;

            var uv = Registry.GetUv(b.Id);

            if (!IsSolid(x, y, z + 1)) AddFace(builder, x, y, z, 0, 0, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1, 0, 0, 1, uv);
            if (!IsSolid(x, y, z - 1)) AddFace(builder, x, y, z, 0, 0, 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 0, 0, -1, uv);
            if (!IsSolid(x, y + 1, z)) AddFace(builder, x, y, z, 0, 1, 0, 0, 1, 1, 1, 1, 1, 1, 1, 0, 0, 1, 0, uv);
            if (!IsSolid(x, y - 1, z)) AddFace(builder, x, y, z, 0, 0, 0, 1, 0, 0, 1, 0, 1, 0, 0, 1, 0, -1, 0, uv);
            if (!IsSolid(x + 1, y, z)) AddFace(builder, x, y, z, 1, 0, 0, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1, 0, 0, uv);
            if (!IsSolid(x - 1, y, z)) AddFace(builder, x, y, z, 0, 0, 0, 0, 0, 1, 0, 1, 1, 0, 1, 0, -1, 0, 0, uv);

            continue;

            bool IsSolid(int cx, int cy, int cz) {

                if (_disposed) return false;

                return cx switch {

                    >= 0 and < 16 when cy is >= 0 and < 16 && cz is >= 0 and < 16 => blocks[(cx * 16 + cz) * 16 + cy].Solid,
                    < 0                                                           => nx != null && nx.GetBlock(15, cy, cz).Solid,
                    >= 16                                                         => px != null && px.GetBlock(0, cy, cz).Solid,

                    _ => cy switch {

                        < 0   => ny != null && ny.GetBlock(cx, 15, cz).Solid,
                        >= 16 => py != null && py.GetBlock(cx, 0, cz).Solid,

                        _ => cz switch {

                            < 0   => nz != null && nz.GetBlock(cx, cy, 15).Solid,
                            >= 16 => pz != null && pz.GetBlock(cx, cy, 0).Solid,

                            _ => false
                        }
                    }
                };

            }
        }

        Flush(builder, newVLists, newNLists, newTLists, newILists);

        if (newVLists.Count <= 0) return;

        lock (_lock) {

            if (_disposed) return;

            if (_vLists != null) {

                foreach (var l in _vLists) ListPool<float>.Return(l);
                foreach (var l in _nLists!) ListPool<float>.Return(l);
                foreach (var l in _tLists!) ListPool<float>.Return(l);
            }

            _vLists = newVLists;
            _nLists = newNLists;
            _tLists = newTLists;
            _iLists = newILists;
        }

        IsDirty = true;
    }

    private static void Flush(MeshBuilder b, List<List<float>> v, List<List<float>> n, List<List<float>> t, List<ushort[]> i) {

        if (b.Verts.Count == 0) return;

        v.Add(b.Verts);
        n.Add(b.Norms);
        t.Add(b.Uvs);
        i.Add(b.Tris.ToArray());

        b.Verts = ListPool<float>.Rent();
        b.Norms = ListPool<float>.Rent();
        b.Uvs = ListPool<float>.Rent();
        b.Tris.Clear();
        b.VIdx = 0;
    }

    private static void AddFace(MeshBuilder b, float ox, float oy, float oz, float x1, float y1, float z1, float x2, float y2, float z2, float x3, float y3, float z3, float x4, float y4, float z4, float nx, float ny, float nz, UvInfo info) {

        b.Verts.Add(ox + x1);
        b.Verts.Add(oy + y1);
        b.Verts.Add(oz + z1);
        b.Verts.Add(ox + x2);
        b.Verts.Add(oy + y2);
        b.Verts.Add(oz + z2);
        b.Verts.Add(ox + x3);
        b.Verts.Add(oy + y3);
        b.Verts.Add(oz + z3);
        b.Verts.Add(ox + x4);
        b.Verts.Add(oy + y4);
        b.Verts.Add(oz + z4);

        b.Uvs.Add(info.X);
        b.Uvs.Add(info.Y);
        b.Uvs.Add(info.X + info.Width);
        b.Uvs.Add(info.Y);
        b.Uvs.Add(info.X + info.Width);
        b.Uvs.Add(info.Y + info.Height);
        b.Uvs.Add(info.X);
        b.Uvs.Add(info.Y + info.Height);

        for (var k = 0; k < 4; k++) {

            b.Norms.Add(nx);
            b.Norms.Add(ny);
            b.Norms.Add(nz);
        }

        b.Tris.Add(b.VIdx);
        b.Tris.Add((ushort)(b.VIdx + 1));
        b.Tris.Add((ushort)(b.VIdx + 2));
        b.Tris.Add(b.VIdx);
        b.Tris.Add((ushort)(b.VIdx + 2));
        b.Tris.Add((ushort)(b.VIdx + 3));

        b.VIdx += 4;
    }

    public unsafe void Upload() {

        List<List<float>>? vLists, nLists, tLists;
        List<ushort[]>? iLists;

        lock (_lock) {

            vLists = _vLists;
            nLists = _nLists;
            tLists = _tLists;
            iLists = _iLists;

            _vLists = null;
            _nLists = null;
            _tLists = null;
            _iLists = null;
        }

        if (vLists == null) return;

        UnloadMeshGraphics();

        for (var i = 0; i < vLists.Count; i++) {

            var vList = vLists[i];
            var nList = nLists![i];
            var tList = tLists![i];
            var iArr = iLists![i];

            var mesh = new Mesh {
                VertexCount = vList.Count / 3,
                TriangleCount = iArr.Length / 3,
                Vertices = (float*)NativeMemory.Alloc((UIntPtr)(vList.Count * sizeof(float))),
                Normals = (float*)NativeMemory.Alloc((UIntPtr)(nList.Count * sizeof(float))),
                TexCoords = (float*)NativeMemory.Alloc((UIntPtr)(tList.Count * sizeof(float))),
                Indices = (ushort*)NativeMemory.Alloc((UIntPtr)(iArr.Length * sizeof(ushort)))
            };

            var vSpan = CollectionsMarshal.AsSpan(vList);
            var nSpan = CollectionsMarshal.AsSpan(nList);
            var tSpan = CollectionsMarshal.AsSpan(tList);

            fixed (float* v = vSpan) Buffer.MemoryCopy(v, mesh.Vertices, vList.Count * 4, vList.Count * 4);
            fixed (float* n = nSpan) Buffer.MemoryCopy(n, mesh.Normals, nList.Count * 4, nList.Count * 4);
            fixed (float* t = tSpan) Buffer.MemoryCopy(t, mesh.TexCoords, tList.Count * 4, tList.Count * 4);
            fixed (ushort* idx = iArr) Buffer.MemoryCopy(idx, mesh.Indices, iArr.Length * 2, iArr.Length * 2);

            UploadMesh(ref mesh, false);
            Meshes.Add(mesh);

            ListPool<float>.Return(vList);
            ListPool<float>.Return(nList);
            ListPool<float>.Return(tList);
        }

        IsDirty = false;
    }

    public void Unload() {

        lock (_lock) {

            if (_vLists != null) {

                foreach (var l in _vLists) ListPool<float>.Return(l);
                foreach (var l in _nLists!) ListPool<float>.Return(l);
                foreach (var l in _tLists!) ListPool<float>.Return(l);
            }
        }

        UnloadMeshGraphics();
        Dispose();
    }

    private unsafe void UnloadMeshGraphics() {

        foreach (var mesh in Meshes) {

            var copy = mesh;
            copy.Vertices = null;
            copy.Normals = null;
            copy.TexCoords = null;
            copy.Indices = null;

            copy.Colors = null;
            copy.Tangents = null;
            copy.TexCoords2 = null;
            copy.AnimVertices = null;
            copy.AnimNormals = null;
            copy.BoneIds = null;
            copy.BoneWeights = null;

            UnloadMesh(copy);

            if (mesh.Vertices != null) NativeMemory.Free(mesh.Vertices);
            if (mesh.Normals != null) NativeMemory.Free(mesh.Normals);
            if (mesh.TexCoords != null) NativeMemory.Free(mesh.TexCoords);
            if (mesh.Indices != null) NativeMemory.Free(mesh.Indices);
        }

        Meshes.Clear();
    }

    public void Dispose() {

        if (_disposed) return;

        _disposed = true;

        if (_blocks == null) return;

        System.Buffers.ArrayPool<Block>.Shared.Return(_blocks);

        _blocks = null;
    }
}