using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Raylib_cs;
using static Raylib_cs.Raylib;

internal class Chunk(int x, int y, int z) : IDisposable {

    public const int Width = 16;
    public const int Height = 256;
    public const int Depth = 16;
    
    private const int Volume = Width * Height * Depth;

    public readonly int X = x, Y = y, Z = z;

    public readonly List<Mesh> Meshes = [];
    private Block[]? _blocks = System.Buffers.ArrayPool<Block>.Shared.Rent(Volume);
    private byte[]? _light = System.Buffers.ArrayPool<byte>.Shared.Rent(Volume);

    private readonly Lock _lock = new();

    private List<List<float>>? _vLists, _nLists, _tLists;
    private List<List<byte>>? _cLists;
    private List<ushort[]>? _iLists;

    private static readonly ThreadLocal<MeshBuilder> Builder = new(() => new MeshBuilder());

    private class MeshBuilder {

        public List<float> Verts = new(4096);
        public List<float> Norms = new(4096);
        public List<float> Uvs = new(2048);
        public List<byte> Colors = new(4096);
        public readonly List<ushort> Tris = new(2048);

        public ushort VIdx;

        public void Clear() {

            Verts.Clear();
            Norms.Clear();
            Uvs.Clear();
            Colors.Clear();
            Tris.Clear();
            VIdx = 0;
        }
    }

    public Block GetBlock(int x, int y, int z) {

        if (_blocks == null || x < 0 || x >= Width || y < 0 || y >= Height || z < 0 || z >= Depth) return new Block();

        return _blocks[(x * Depth + z) * Height + y];
    }

    public void SetBlock(int x, int y, int z, Block block) {

        if (_blocks == null || x < 0 || x >= Width || y < 0 || y >= Height || z < 0 || z >= Depth) return;

        _blocks[(x * Depth + z) * Height + y] = block;
        IsDirty = true;
    }

    public byte GetLight(int x, int y, int z) {

        if (_light == null || x < 0 || x >= Width || y < 0 || y >= Height || z < 0 || z >= Depth) return 0;

        return _light[(x * Depth + z) * Height + y];
    }

    public void SetLight(int x, int y, int z, byte val) {

        if (_light == null || x < 0 || x >= Width || y < 0 || y >= Height || z < 0 || z >= Depth) return;

        _light[(x * Depth + z) * Height + y] = val;
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

        for (var lx = 0; lx < Width; lx++)
        for (var lz = 0; lz < Depth; lz++) {

            float worldX = X * Width + lx;
            float worldZ = Z * Depth + lz;

            var height = (Noise.Perlin3D(worldX * 0.015f, 0, worldZ * 0.015f) + 1) * 32 + 20;
            height += (Noise.Perlin3D(worldX * 0.05f, 0, worldZ * 0.05f)) * 8;

            for (var ly = 0; ly < Height; ly++) {

                if (_disposed) return;

                float worldY = Y * Height + ly;

                byte blockId = 0;

                if (worldY < height) {

                    blockId = (worldY > height - 1) ? grassId : dirtId;

                    var cave = Noise.Perlin3D(worldX * 0.08f, worldY * 0.08f, worldZ * 0.08f);
                    if (cave > 0.45f) blockId = 0;
                }

                blocks[(lx * Depth + lz) * Height + ly] = new Block(blockId);
            }
        }

        if (_light != null) Array.Clear(_light, 0, _light.Length);
    }

    public volatile bool IsDirty;

    public void BuildArrays(Chunk? nx, Chunk? px, Chunk? ny, Chunk? py, Chunk? nz, Chunk? pz) {

        if (_disposed || _blocks == null) return;

        var builder = Builder.Value!;
        builder.Clear();

        var newVLists = new List<List<float>>();
        var newNLists = new List<List<float>>();
        var newTLists = new List<List<float>>();
        var newCLists = new List<List<byte>>();
        var newILists = new List<ushort[]>();

        var blocks = _blocks;

        for (var x = 0; x < Width; x++)
        for (var z = 0; z < Depth; z++)
        for (var y = 0; y < Height; y++) {

            if (_disposed) return;

            if (builder.VIdx > 60000) Flush(builder, newVLists, newNLists, newTLists, newCLists, newILists);

            var b = blocks[(x * Depth + z) * Height + y];

            if (!b.Solid) continue;

            var uv = Registry.GetUv(b.Id);

            if (!IsSolid(x, y, z + 1)) AddFace(builder, x, y, z, 0, 0, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1, 0, 0, 1, uv, GetFaceLight(x, y, z + 1));
            if (!IsSolid(x, y, z - 1)) AddFace(builder, x, y, z, 0, 0, 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 0, 0, -1, uv, GetFaceLight(x, y, z - 1));
            if (!IsSolid(x, y + 1, z)) AddFace(builder, x, y, z, 0, 1, 0, 0, 1, 1, 1, 1, 1, 1, 1, 0, 0, 1, 0, uv, GetFaceLight(x, y + 1, z));
            if (!IsSolid(x, y - 1, z)) AddFace(builder, x, y, z, 0, 0, 0, 1, 0, 0, 1, 0, 1, 0, 0, 1, 0, -1, 0, uv, GetFaceLight(x, y - 1, z));
            if (!IsSolid(x + 1, y, z)) AddFace(builder, x, y, z, 1, 0, 0, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1, 0, 0, uv, GetFaceLight(x + 1, y, z));
            if (!IsSolid(x - 1, y, z)) AddFace(builder, x, y, z, 0, 0, 0, 0, 0, 1, 0, 1, 1, 0, 1, 0, -1, 0, 0, uv, GetFaceLight(x - 1, y, z));

            continue;

            byte GetFaceLight(int cx, int cy, int cz) {
                return cx switch {

                    >= 0 and < Width when cy is >= 0 and < Height && cz is >= 0 and < Depth => _light![(cx * Depth + cz) * Height + cy],
                    < 0                                                                     => nx?.GetLight(Width - 1, cy, cz) ?? 0,
                    >= Width                                                                => px?.GetLight(0, cy, cz) ?? 0,

                    _ => cy switch {

                        < 0      => ny?.GetLight(cx, Height - 1, cz) ?? 0,
                        >= Height => py?.GetLight(cx, 0, cz) ?? 0,

                        _ => cz switch {

                            < 0      => nz?.GetLight(cx, cy, Depth - 1) ?? 0,
                            >= Depth => pz?.GetLight(cx, cy, 0) ?? 0,
                            _        => 0
                        }
                    }
                };
            }

            bool IsSolid(int cx, int cy, int cz) {

                if (_disposed) return false;

                return cx switch {

                    >= 0 and < Width when cy is >= 0 and < Height && cz is >= 0 and < Depth => blocks[(cx * Depth + cz) * Height + cy].Solid,
                    < 0                                                                     => nx != null && nx.GetBlock(Width - 1, cy, cz).Solid,
                    >= Width                                                                => px != null && px.GetBlock(0, cy, cz).Solid,

                    _ => cy switch {

                        < 0      => ny != null && ny.GetBlock(cx, Height - 1, cz).Solid,
                        >= Height => py != null && py.GetBlock(cx, 0, cz).Solid,

                        _ => cz switch {

                            < 0      => nz != null && nz.GetBlock(cx, cy, Depth - 1).Solid,
                            >= Depth => pz != null && pz.GetBlock(cx, cy, 0).Solid,

                            _ => false
                        }
                    }
                };

            }
        }

        Flush(builder, newVLists, newNLists, newTLists, newCLists, newILists);

        if (newVLists.Count <= 0) return;

        lock (_lock) {

            if (_disposed) return;

            if (_vLists != null) {
                
                foreach (var l in _vLists) ListPool<float>.Return(l);
                foreach (var l in _nLists!) ListPool<float>.Return(l);
                foreach (var l in _tLists!) ListPool<float>.Return(l);
                foreach (var l in _cLists!) ListPool<byte>.Return(l);
            }

            _vLists = newVLists;
            _nLists = newNLists;
            _tLists = newTLists;
            _cLists = newCLists;
            _iLists = newILists;
        }

        IsDirty = true;
    }

    private static void Flush(MeshBuilder b, List<List<float>> v, List<List<float>> n, List<List<float>> t, List<List<byte>> c, List<ushort[]> i) {

        if (b.Verts.Count == 0) return;

        v.Add(b.Verts);
        n.Add(b.Norms);
        t.Add(b.Uvs);
        c.Add(b.Colors);
        i.Add(b.Tris.ToArray());

        b.Verts = ListPool<float>.Rent();
        b.Norms = ListPool<float>.Rent();
        b.Uvs = ListPool<float>.Rent();
        b.Colors = ListPool<byte>.Rent();
        b.Tris.Clear();
        b.VIdx = 0;
    }

    private static void AddFace(MeshBuilder mesh, float ox, float oy, float oz, float x1, float y1, float z1, float x2, float y2, float z2, float x3, float y3, float z3, float x4, float y4, float z4, float nx, float ny, float nz, UvInfo info, byte light) {

        mesh.Verts.Add(ox + x1);
        mesh.Verts.Add(oy + y1);
        mesh.Verts.Add(oz + z1);
        mesh.Verts.Add(ox + x2);
        mesh.Verts.Add(oy + y2);
        mesh.Verts.Add(oz + z2);
        mesh.Verts.Add(ox + x3);
        mesh.Verts.Add(oy + y3);
        mesh.Verts.Add(oz + z3);
        mesh.Verts.Add(ox + x4);
        mesh.Verts.Add(oy + y4);
        mesh.Verts.Add(oz + z4);

        // Extract lighting
        var sl = (light >> 4) & 0xF;
        var bl = light & 0xF;

        // Curve light for better visual
        var slf = (float)Math.Pow(0.8, 15 - sl);
        var blf = (float)Math.Pow(0.8, 15 - bl);

        var combined = Math.Max(slf, blf);
        var val = (byte)(combined * 255);

        var r = slf;
        var g = slf;
        var b = slf;

        r = Math.Max(r, blf * 1.0f); // Yellowish
        g = Math.Max(g, blf * 0.9f);
        b = Math.Max(b, blf * 0.6f); // Less blue for warm light

        // Ensure standard ambient
        r = Math.Max(r, 0.05f);
        g = Math.Max(g, 0.05f);
        b = Math.Max(b, 0.05f);

        var finalR = (byte)Math.Min(255, r * 255);
        var finalG = (byte)Math.Min(255, g * 255);
        var finalB = (byte)Math.Min(255, b * 255);

        for (var k = 0; k < 4; k++) {
            
            mesh.Colors.Add(finalR);
            mesh.Colors.Add(finalG);
            mesh.Colors.Add(finalB);
            mesh.Colors.Add(255);
        }

        mesh.Uvs.Add(info.X);
        mesh.Uvs.Add(info.Y);
        mesh.Uvs.Add(info.X + info.Width);
        mesh.Uvs.Add(info.Y);
        mesh.Uvs.Add(info.X + info.Width);
        mesh.Uvs.Add(info.Y + info.Height);
        mesh.Uvs.Add(info.X);
        mesh.Uvs.Add(info.Y + info.Height);

        for (var k = 0; k < 4; k++) {

            mesh.Norms.Add(nx);
            mesh.Norms.Add(ny);
            mesh.Norms.Add(nz);
        }

        mesh.Tris.Add(mesh.VIdx);
        mesh.Tris.Add((ushort)(mesh.VIdx + 1));
        mesh.Tris.Add((ushort)(mesh.VIdx + 2));
        mesh.Tris.Add(mesh.VIdx);
        mesh.Tris.Add((ushort)(mesh.VIdx + 2));
        mesh.Tris.Add((ushort)(mesh.VIdx + 3));

        mesh.VIdx += 4;
    }

    public unsafe void Upload() {

        List<List<float>>? vLists, nLists, tLists;
        List<List<byte>>? cLists;
        List<ushort[]>? iLists;

        lock (_lock) {

            vLists = _vLists;
            nLists = _nLists;
            tLists = _tLists;
            cLists = _cLists;
            iLists = _iLists;

            _vLists = null;
            _nLists = null;
            _tLists = null;
            _cLists = null;
            _iLists = null;
        }

        if (vLists == null) return;

        UnloadMeshGraphics();

        for (var i = 0; i < vLists.Count; i++) {

            var vList = vLists[i];
            var nList = nLists![i];
            var tList = tLists![i];
            var cList = cLists![i];
            var iArr = iLists![i];

            var mesh = new Mesh {
                VertexCount = vList.Count / 3,
                TriangleCount = iArr.Length / 3,
                Vertices = (float*)NativeMemory.Alloc((UIntPtr)(vList.Count * sizeof(float))),
                Normals = (float*)NativeMemory.Alloc((UIntPtr)(nList.Count * sizeof(float))),
                TexCoords = (float*)NativeMemory.Alloc((UIntPtr)(tList.Count * sizeof(float))),
                Colors = (byte*)NativeMemory.Alloc((UIntPtr)(cList.Count * sizeof(byte))),
                Indices = (ushort*)NativeMemory.Alloc((UIntPtr)(iArr.Length * sizeof(ushort)))
            };

            var vSpan = CollectionsMarshal.AsSpan(vList);
            var nSpan = CollectionsMarshal.AsSpan(nList);
            var tSpan = CollectionsMarshal.AsSpan(tList);
            var cSpan = CollectionsMarshal.AsSpan(cList);

            fixed (float* v = vSpan) Buffer.MemoryCopy(v, mesh.Vertices, vList.Count * 4, vList.Count * 4);
            fixed (float* n = nSpan) Buffer.MemoryCopy(n, mesh.Normals, nList.Count * 4, nList.Count * 4);
            fixed (float* t = tSpan) Buffer.MemoryCopy(t, mesh.TexCoords, tList.Count * 4, tList.Count * 4);
            fixed (byte* c = cSpan) Buffer.MemoryCopy(c, mesh.Colors, cList.Count, cList.Count);
            fixed (ushort* idx = iArr) Buffer.MemoryCopy(idx, mesh.Indices, iArr.Length * 2, iArr.Length * 2);

            UploadMesh(ref mesh, false);
            Meshes.Add(mesh);

            ListPool<float>.Return(vList);
            ListPool<float>.Return(nList);
            ListPool<float>.Return(tList);
            ListPool<byte>.Return(cList);
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
            if (mesh.Colors != null) NativeMemory.Free(mesh.Colors);
            if (mesh.Indices != null) NativeMemory.Free(mesh.Indices);
        }

        Meshes.Clear();
    }

    public void Dispose() {

        if (_disposed) return;

        _disposed = true;

        if (_blocks == null) return;

        System.Buffers.ArrayPool<Block>.Shared.Return(_blocks);
        System.Buffers.ArrayPool<byte>.Shared.Return(_light!);

        _blocks = null;
        _light = null;
    }
}