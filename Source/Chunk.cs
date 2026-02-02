using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using Raylib_cs;
using static Raylib_cs.Raylib;

internal class Chunk(int x, int y, int z) : IDisposable {

    public const int Width = 16;
    public const int Height = 256;
    public const int Depth = 16;

    private const int Volume = Width * Height * Depth;

    public readonly int X = x, Y = y, Z = z;

    private readonly Block[]? _blocks = System.Buffers.ArrayPool<Block>.Shared.Rent(Volume);
    private ushort[]? _light = System.Buffers.ArrayPool<ushort>.Shared.Rent(Volume);
    public readonly List<Mesh> Meshes = [];

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

    public ushort GetLight(int x, int y, int z) {

        if (_light == null || x < 0 || x >= Width || y < 0 || y >= Height || z < 0 || z >= Depth) return 0;

        return _light[(x * Depth + z) * Height + y];
    }

    public void SetLight(int x, int y, int z, ushort val) {

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

        var grassId = Registry.GetId("Grass");
        var dirtId = Registry.GetId("Dirt");

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

            var block = blocks[(x * Depth + z) * Height + y];

            if (block.Id == 0) continue;

            var model = Registry.GetModel(block.Id);

            if (model == null) continue;

            var faceLights = new ushort[4];

            var facing = Registry.GetFacing(block.Id);
            var data = block.Data;
            var step = Registry.GetYawStep(block.Id);

            var isRotated = facing != FacingMode.Fixed && data != 0;
            var isAxisAligned = true;
            var rot = Quaternion.Identity;

            if (isRotated) {

                switch (facing) {

                    case FacingMode.Yaw: {

                        var axis = Vector3.UnitY;
                        var angle = data * (step <= 0 ? 90f : step);

                        rot = Quaternion.CreateFromAxisAngle(axis, angle * (float)(Math.PI / 180.0));
                        isAxisAligned = (angle % 90) == 0;

                        break;
                    }

                    case FacingMode.Rotate: {

                        const float angle = 90f * (float)(Math.PI / 180.0);

                        rot = data switch {

                            1 => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -angle),
                            2 => Quaternion.CreateFromAxisAngle(Vector3.UnitX, angle),
                            _ => rot
                        };

                        break;
                    }

                    case FacingMode.Fixed:
                    default:
                        break;
                }
            }

            var worldDirs = new (int X, int Y, int Z)[6];

            for (var i = 0; i < 6; i++) {

                var vec = i switch {

                    0 => -Vector3.UnitZ,
                    1 => Vector3.UnitX,
                    2 => Vector3.UnitZ,
                    3 => -Vector3.UnitX,
                    4 => Vector3.UnitY,
                    5 => -Vector3.UnitY,
                    _ => Vector3.Zero
                };

                if (isRotated) vec = Vector3.Transform(vec, rot);
                worldDirs[i] = ((int)Math.Round(vec.X), (int)Math.Round(vec.Y), (int)Math.Round(vec.Z));
            }

            foreach (var el in model.Elements) {

                // Calculate element bounds
                float x0 = el.From[0] / 16f, y0 = el.From[1] / 16f, z0 = el.From[2] / 16f;
                float x1 = el.To[0] / 16f, y1 = el.To[1] / 16f, z1 = el.To[2] / 16f;

                if (isRotated && isAxisAligned) {

                    var c = new Vector3(0.5f);
                    var pMin = new Vector3(x0, y0, z0) - c;
                    var pMax = new Vector3(x1, y1, z1) - c;

                    var corners = new[] { pMin, new Vector3(pMax.X, pMin.Y, pMin.Z), new Vector3(pMin.X, pMax.Y, pMin.Z), new Vector3(pMin.X, pMin.Y, pMax.Z), new Vector3(pMax.X, pMax.Y, pMin.Z), new Vector3(pMax.X, pMin.Y, pMax.Z), new Vector3(pMin.X, pMax.Y, pMax.Z), pMax };

                    var min = new Vector3(float.MaxValue);
                    var max = new Vector3(float.MinValue);

                    for (var k = 0; k < 8; k++) {

                        var t = Vector3.Transform(corners[k], rot);
                        min = Vector3.Min(min, t);
                        max = Vector3.Max(max, t);
                    }

                    min += c;
                    max += c;
                    x0 = min.X;
                    y0 = min.Y;
                    z0 = min.Z;
                    x1 = max.X;
                    y1 = max.Y;
                    z1 = max.Z;
                }

                if (!isRotated || isAxisAligned) {

                    // Local function to resolve source face for axis-aligned rotation
                    string GetSourceFace(Vector3 dir) {

                        if (!isRotated) {

                            if (dir == Vector3.UnitZ) return "south";
                            if (dir == -Vector3.UnitZ) return "north";
                            if (dir == Vector3.UnitX) return "east";
                            if (dir == -Vector3.UnitX) return "west";
                            if (dir == Vector3.UnitY) return "up";
                            if (dir == -Vector3.UnitY) return "down";

                            return "";
                        }

                        var invRot = Quaternion.Inverse(rot);
                        var modelDir = Vector3.Transform(dir, invRot);

                        if (Math.Abs(modelDir.X) > 0.9f) return modelDir.X > 0 ? "east" : "west";
                        if (Math.Abs(modelDir.Y) > 0.9f) return modelDir.Y > 0 ? "up" : "down";
                        if (Math.Abs(modelDir.Z) > 0.9f) return modelDir.Z > 0 ? "south" : "north";

                        return "";
                    }

                    if (el.Faces.TryGetValue(GetSourceFace(Vector3.UnitZ), out var fS)) {

                        // South
                        var uv = Registry.ResolveFaceUv(model, fS);

                        if (!ShouldCull(uv.CullMask)) {

                            FillLights(x, y, z1 >= 0.999f ? z + 1 : z, 1, 0, 0, 0, 1, 0, faceLights);
                            Swap(faceLights);
                            AddFace(builder, x, y, z, x0, y1, z1, x1, y1, z1, x1, y0, z1, x0, y0, z1, 0, 0, 1, uv, faceLights);
                        }
                    }

                    if (el.Faces.TryGetValue(GetSourceFace(-Vector3.UnitZ), out var fN)) {

                        // North
                        var uv = Registry.ResolveFaceUv(model, fN);

                        if (!ShouldCull(uv.CullMask)) {

                            FillLights(x, y, z0 <= 0.001f ? z - 1 : z, -1, 0, 0, 0, 1, 0, faceLights);
                            Swap(faceLights);
                            AddFace(builder, x, y, z, x1, y1, z0, x0, y1, z0, x0, y0, z0, x1, y0, z0, 0, 0, -1, uv, faceLights);
                        }
                    }

                    if (el.Faces.TryGetValue(GetSourceFace(Vector3.UnitY), out var fU)) {

                        // Up
                        var uv = Registry.ResolveFaceUv(model, fU);

                        if (!ShouldCull(uv.CullMask)) {

                            FillLights(x, y1 >= 0.999f ? y + 1 : y, z, 1, 0, 0, 0, 0, -1, faceLights);
                            Swap(faceLights);
                            AddFace(builder, x, y, z, x0, y1, z0, x1, y1, z0, x1, y1, z1, x0, y1, z1, 0, 1, 0, uv, faceLights);
                        }
                    }

                    if (el.Faces.TryGetValue(GetSourceFace(-Vector3.UnitY), out var fD)) {

                        // Down
                        var uv = Registry.ResolveFaceUv(model, fD);

                        if (!ShouldCull(uv.CullMask)) {

                            FillLights(x, y0 <= 0.001f ? y - 1 : y, z, 1, 0, 0, 0, 0, 1, faceLights);
                            SwapPairs(faceLights);
                            AddFace(builder, x, y, z, x1, y0, z0, x0, y0, z0, x0, y0, z1, x1, y0, z1, 0, -1, 0, uv, faceLights);
                        }
                    }

                    if (el.Faces.TryGetValue(GetSourceFace(Vector3.UnitX), out var fE)) {

                        // East
                        var uv = Registry.ResolveFaceUv(model, fE);

                        if (!ShouldCull(uv.CullMask)) {

                            FillLights(x1 >= 0.999f ? x + 1 : x, y, z, 0, 0, -1, 0, 1, 0, faceLights);
                            Swap(faceLights);
                            AddFace(builder, x, y, z, x1, y1, z1, x1, y1, z0, x1, y0, z0, x1, y0, z1, 1, 0, 0, uv, faceLights);
                        }
                    }

                    if (el.Faces.TryGetValue(GetSourceFace(-Vector3.UnitX), out var fW)) {

                        // West
                        var uv = Registry.ResolveFaceUv(model, fW);

                        if (!ShouldCull(uv.CullMask)) {

                            FillLights(x0 <= 0.001f ? x - 1 : x, y, z, 0, 0, 1, 0, 1, 0, faceLights);
                            Swap(faceLights);
                            AddFace(builder, x, y, z, x0, y1, z0, x0, y1, z1, x0, y0, z1, x0, y0, z0, -1, 0, 0, uv, faceLights);
                        }
                    }
                } else {

                    // Off-axis rotation (Generic)
                    var c = new Vector3(0.5f);
                    var min = new Vector3(el.From[0], el.From[1], el.From[2]) / 16f;
                    var max = new Vector3(el.To[0], el.To[1], el.To[2]) / 16f;
                    var minRot = min - c;
                    var maxRot = max - c;

                    foreach (var kvp in el.Faces) {

                        var f = kvp.Value;
                        var faceUv = Registry.ResolveFaceUv(model, f);

                        if (ShouldCull(faceUv.CullMask)) continue;

                        Vector3 p1, p2, p3, p4;

                        switch (kvp.Key) {

                            case "north": // Standard: TR, TL, BL, BR
                                p1 = new Vector3(maxRot.X, maxRot.Y, minRot.Z);
                                p2 = new Vector3(minRot.X, maxRot.Y, minRot.Z);
                                p3 = new Vector3(minRot.X, minRot.Y, minRot.Z);
                                p4 = new Vector3(maxRot.X, minRot.Y, minRot.Z);

                                break;

                            case "south": // Standard: TL, TR, BR, BL
                                p1 = new Vector3(minRot.X, maxRot.Y, maxRot.Z);
                                p2 = new Vector3(maxRot.X, maxRot.Y, maxRot.Z);
                                p3 = new Vector3(maxRot.X, minRot.Y, maxRot.Z);
                                p4 = new Vector3(minRot.X, minRot.Y, maxRot.Z);

                                break;

                            case "east": // Standard: TL, TR, BR, BL 
                                p1 = new Vector3(maxRot.X, maxRot.Y, maxRot.Z);
                                p2 = new Vector3(maxRot.X, maxRot.Y, minRot.Z);
                                p3 = new Vector3(maxRot.X, minRot.Y, minRot.Z);
                                p4 = new Vector3(maxRot.X, minRot.Y, maxRot.Z);

                                break;

                            case "west": // Standard: TR, TL, BL, BR
                                p1 = new Vector3(minRot.X, maxRot.Y, minRot.Z);
                                p2 = new Vector3(minRot.X, maxRot.Y, maxRot.Z);
                                p3 = new Vector3(minRot.X, minRot.Y, maxRot.Z);
                                p4 = new Vector3(minRot.X, minRot.Y, minRot.Z);

                                break;

                            case "up": // Standard: TL, TR, BR, BL
                                p1 = new Vector3(minRot.X, maxRot.Y, minRot.Z);
                                p2 = new Vector3(maxRot.X, maxRot.Y, minRot.Z);
                                p3 = new Vector3(maxRot.X, maxRot.Y, maxRot.Z);
                                p4 = new Vector3(minRot.X, maxRot.Y, maxRot.Z);

                                break;

                            case "down": // Standard: TR, TL, BL, BR
                                p1 = new Vector3(maxRot.X, minRot.Y, minRot.Z);
                                p2 = new Vector3(minRot.X, minRot.Y, minRot.Z);
                                p3 = new Vector3(minRot.X, minRot.Y, maxRot.Z);
                                p4 = new Vector3(maxRot.X, minRot.Y, maxRot.Z);

                                break;

                            default: continue;
                        }

                        // Apply Rotation
                        p1 = Vector3.Transform(p1, rot) + c;
                        p2 = Vector3.Transform(p2, rot) + c;
                        p3 = Vector3.Transform(p3, rot) + c;
                        p4 = Vector3.Transform(p4, rot) + c;

                        // Calculate Lighting Dimensions for AO
                        var vR = Vector3.Normalize(p2 - p1);
                        var vU = Vector3.Normalize(p1 - p4);

                        FillLights(x, y, z, (int)Math.Round(vR.X), (int)Math.Round(vR.Y), (int)Math.Round(vR.Z), (int)Math.Round(vU.X), (int)Math.Round(vU.Y), (int)Math.Round(vU.Z), faceLights);

                        // Fix light-vertex mapping (BL->TL, etc.)
                        Swap(faceLights);

                        // Use AddFace to handle UV rotation and Indexing correctly
                        AddFace(builder, x, y, z, p1.X, p1.Y, p1.Z, p2.X, p2.Y, p2.Z, p3.X, p3.Y, p3.Z, p4.X, p4.Y, p4.Z, 0, 0, 0, faceUv, faceLights);
                    }
                }
            }

            continue;

            bool ShouldCull(byte mask) {

                if (mask == 0) return false;

                for (var i = 0; i < 6; i++) {

                    if ((mask & (1 << i)) == 0) continue;

                    var d = worldDirs[i];

                    if (!IsOpaque(x + d.X, y + d.Y, z + d.Z)) return false;
                }

                return true;
            }

            void Swap(ushort[] arr) {

                (arr[0], arr[3]) = (arr[3], arr[0]);
                (arr[1], arr[2]) = (arr[2], arr[1]);
            }

            void SwapPairs(ushort[] arr) {

                (arr[0], arr[1]) = (arr[1], arr[0]);
                (arr[2], arr[3]) = (arr[3], arr[2]);
            }

            void FillLights(int lx, int ly, int lz, int rX, int rY, int rZ, int uX, int uY, int uZ, ushort[] output) {

                if (!QualitySettings.SmoothLighting) {

                    var flat = GetLightSafe(lx, ly, lz);
                    output[0] = output[1] = output[2] = output[3] = flat;

                    return;
                }

                // output[0]=BL, output[1]=BR, output[2]=TR, output[3]=TL
                output[0] = GetVertexLight(lx, ly, lz, -rX - uX, -rY - uY, -rZ - uZ);
                output[1] = GetVertexLight(lx, ly, lz, rX - uX, rY - uY, rZ - uZ);
                output[2] = GetVertexLight(lx, ly, lz, rX + uX, rY + uY, rZ + uZ);
                output[3] = GetVertexLight(lx, ly, lz, -rX + uX, -rY + uY, -rZ + uZ);
            }

            ushort GetVertexLight(int vx, int vy, int vz, int dx, int dy, int dz) {

                var l1 = GetLightSafe(vx, vy, vz);

                int ax, ay, az, bx, by, bz, cx, cy, cz;

                if (dx == 0) {
                    ax = vx;
                    ay = vy + dy;
                    az = vz;
                    bx = vx;
                    by = vy;
                    bz = vz + dz;
                    cx = vx;
                    cy = vy + dy;
                    cz = vz + dz;
                } else if (dy == 0) {
                    ax = vx + dx;
                    ay = vy;
                    az = vz;
                    bx = vx;
                    by = vy;
                    bz = vz + dz;
                    cx = vx + dx;
                    cy = vy;
                    cz = vz + dz;
                } else {
                    ax = vx + dx;
                    ay = vy;
                    az = vz;
                    bx = vx;
                    by = vy + dy;
                    bz = vz;
                    cx = vx + dx;
                    cy = vy + dy;
                    cz = vz;
                }

                var lA = GetLightSafe(ax, ay, az);
                var lB = GetLightSafe(bx, by, bz);

                // Occlusion
                if (IsSolid(ax, ay, az) && IsSolid(bx, by, bz)) {

                    return AverageLight(l1, lA, lB, l1);
                }

                var lC = GetLightSafe(cx, cy, cz);

                return AverageLight(l1, lA, lB, lC);
            }

            ushort AverageLight(ushort a, ushort b, ushort c, ushort d) {

                var r = (a & 0xF) + (b & 0xF) + (c & 0xF) + (d & 0xF);
                var g = ((a >> 4) & 0xF) + ((b >> 4) & 0xF) + ((c >> 4) & 0xF) + ((d >> 4) & 0xF);
                var bl = ((a >> 8) & 0xF) + ((b >> 8) & 0xF) + ((c >> 8) & 0xF) + ((d >> 8) & 0xF);
                var s = ((a >> 12) & 0xF) + ((b >> 12) & 0xF) + ((c >> 12) & 0xF) + ((d >> 12) & 0xF);

                return (ushort)(((r + 2) >> 2) | (((g + 2) >> 2) << 4) | (((bl + 2) >> 2) << 8) | (((s + 2) >> 2) << 12));
            }

            ushort GetLightSafe(int cx, int cy, int cz) {

                if (cx is >= 0 and < Width && cy is >= 0 and < Height && cz is >= 0 and < Depth) return _light![(cx * Depth + cz) * Height + cy];

                switch (cy) {

                    case >= Height: return 0xF000;
                    case < 0:       return 0;
                }

                var clx = cx < 0 ? 0 : (cx >= Width ? Width - 1 : cx);
                var clz = cz < 0 ? 0 : (cz >= Depth ? Depth - 1 : cz);
                var fallback = _light![(clx * Depth + clz) * Height + cy];

                return cx switch {

                    < 0 when cz is < 0 or >= Depth      => fallback,
                    < 0                                 => nx?.GetLight(Width - 1, cy, cz) ?? fallback,
                    >= Width when cz is < 0 or >= Depth => fallback,
                    >= Width                            => px?.GetLight(0, cy, cz) ?? fallback,

                    _ => cz switch {

                        < 0      => nz?.GetLight(cx, cy, Depth - 1) ?? fallback,
                        >= Depth => pz?.GetLight(cx, cy, 0) ?? fallback,
                        _        => 0
                    }
                };
            }

            Block GetBlockSafe(int cx, int cy, int cz) {

                if (_disposed) return new Block();

                return cx switch {

                    >= 0 and < Width when cy is >= 0 and < Height && cz is >= 0 and < Depth => blocks[(cx * Depth + cz) * Height + cy],
                    < 0                                                                     => nx?.GetBlock(Width - 1, cy, cz) ?? new Block(),
                    >= Width                                                                => px?.GetBlock(0, cy, cz) ?? new Block(),

                    _ => cy switch {

                        < 0       => ny?.GetBlock(cx, Height - 1, cz) ?? new Block(),
                        >= Height => py?.GetBlock(cx, 0, cz) ?? new Block(),

                        _ => cz switch {
                            < 0      => nz?.GetBlock(cx, cy, Depth - 1) ?? new Block(),
                            >= Depth => pz?.GetBlock(cx, cy, 0) ?? new Block(),
                            _        => new Block()
                        }
                    }
                };
            }

            bool IsOpaque(int cx, int cy, int cz) {

                var b = GetBlockSafe(cx, cy, cz);

                if (b.Opaque) return true;
                if (b.Id == 0) return false;

                // Connection-based culling
                if (Registry.CanConnect(block.Id, b.Id)) return true;

                // Smart culling: Transparent blocks act as opaque if fully enclosed by naturally opaque blocks
                return Registry.IsOpaque(GetBlockSafe(cx + 1, cy, cz).Id) && Registry.IsOpaque(GetBlockSafe(cx - 1, cy, cz).Id) && Registry.IsOpaque(GetBlockSafe(cx, cy + 1, cz).Id) && Registry.IsOpaque(GetBlockSafe(cx, cy - 1, cz).Id) && Registry.IsOpaque(GetBlockSafe(cx, cy, cz + 1).Id) && Registry.IsOpaque(GetBlockSafe(cx, cy, cz - 1).Id);
            }

            bool IsSolid(int cx, int cy, int cz) => GetBlockSafe(cx, cy, cz).Solid;
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

    private static void AddFace(MeshBuilder mesh, float ox, float oy, float oz, float x1, float y1, float z1, float x2, float y2, float z2, float x3, float y3, float z3, float x4, float y4, float z4, float nx, float ny, float nz, UvInfo info, ushort[] lights) {

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

        // Apply UV Rotation
        float u1 = info.X, v1 = info.Y;
        float u2 = info.X + info.Width, v2 = info.Y + info.Height;

        switch (info.Rotation) {

            case 90:
                mesh.Uvs.Add(u1);
                mesh.Uvs.Add(v2); // BL
                mesh.Uvs.Add(u1);
                mesh.Uvs.Add(v1); // TL
                mesh.Uvs.Add(u2);
                mesh.Uvs.Add(v1); // TR
                mesh.Uvs.Add(u2);
                mesh.Uvs.Add(v2); // BR

                break;

            case 180:
                mesh.Uvs.Add(u2);
                mesh.Uvs.Add(v2); // BR
                mesh.Uvs.Add(u1);
                mesh.Uvs.Add(v2); // BL
                mesh.Uvs.Add(u1);
                mesh.Uvs.Add(v1); // TL
                mesh.Uvs.Add(u2);
                mesh.Uvs.Add(v1); // TR

                break;

            case 270:
                mesh.Uvs.Add(u2);
                mesh.Uvs.Add(v1); // TR
                mesh.Uvs.Add(u2);
                mesh.Uvs.Add(v2); // BR
                mesh.Uvs.Add(u1);
                mesh.Uvs.Add(v2); // BL
                mesh.Uvs.Add(u1);
                mesh.Uvs.Add(v1); // TL

                break;

            default:
                mesh.Uvs.Add(u1);
                mesh.Uvs.Add(v1); // TL
                mesh.Uvs.Add(u2);
                mesh.Uvs.Add(v1); // TR
                mesh.Uvs.Add(u2);
                mesh.Uvs.Add(v2); // BR
                mesh.Uvs.Add(u1);
                mesh.Uvs.Add(v2); // BL

                break;

        }

        for (var k = 0; k < 4; k++) {

            var light = lights[k];
            mesh.Colors.Add((byte)(light & 0xFF));
            mesh.Colors.Add((byte)((light >> 8) & 0xFF));
            mesh.Colors.Add(0);
            mesh.Colors.Add(255);
        }

        for (var k = 0; k < 4; k++) {

            mesh.Norms.Add(nx);
            mesh.Norms.Add(ny);
            mesh.Norms.Add(nz);
        }

        var br0 = (lights[0] & 0xF) + ((lights[0] >> 4) & 0xF) + ((lights[0] >> 8) & 0xF) + ((lights[0] >> 12) & 0xF);
        var br1 = (lights[1] & 0xF) + ((lights[1] >> 4) & 0xF) + ((lights[1] >> 8) & 0xF) + ((lights[1] >> 12) & 0xF);
        var br2 = (lights[2] & 0xF) + ((lights[2] >> 4) & 0xF) + ((lights[2] >> 8) & 0xF) + ((lights[2] >> 12) & 0xF);
        var br3 = (lights[3] & 0xF) + ((lights[3] >> 4) & 0xF) + ((lights[3] >> 8) & 0xF) + ((lights[3] >> 12) & 0xF);

        // Reverse winding to fix "Inside Out" faces (CW instead of CCW)
        if (br0 + br2 > br1 + br3) {

            mesh.Tris.Add(mesh.VIdx);
            mesh.Tris.Add((ushort)(mesh.VIdx + 2));
            mesh.Tris.Add((ushort)(mesh.VIdx + 1));

            mesh.Tris.Add(mesh.VIdx);

        } else {

            mesh.Tris.Add((ushort)(mesh.VIdx + 0));
            mesh.Tris.Add((ushort)(mesh.VIdx + 3));
            mesh.Tris.Add((ushort)(mesh.VIdx + 1));

            mesh.Tris.Add((ushort)(mesh.VIdx + 1));
        }

        mesh.Tris.Add((ushort)(mesh.VIdx + 3));
        mesh.Tris.Add((ushort)(mesh.VIdx + 2));

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
        System.Buffers.ArrayPool<ushort>.Shared.Return(_light!);

        _light = null;
    }
}