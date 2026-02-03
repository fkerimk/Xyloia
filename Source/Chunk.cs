using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using Raylib_cs;
using static Raylib_cs.Raylib;

// ReSharper disable InconsistentNaming
internal class Chunk(int x, int y, int z) : IDisposable {

    public const int Width = 16;
    public const int Height = 256;
    public const int Depth = 16;

    private const int Volume = Width * Height * Depth;

    public readonly int X = x, Y = y, Z = z;

    public double SpawnTime;
    public double MeshBuildTime;
    public double UnloadTime;

    public readonly List<Mesh> Meshes = [];
    public readonly List<Mesh> TransparentMeshes = [];
    public readonly List<int> LightEmitters = [];

    private ushort[]? _light = RentAndClear<ushort>(Volume);
    private ushort[]? _renderLight = RentAndClear<ushort>(Volume);
    private List<List<float>>? _vLists, _nLists, _tLists;
    private List<List<byte>>? _cLists;
    private List<ushort[]>? _iLists;

    private List<List<float>>? _vListsTrans, _nListsTrans, _tListsTrans;
    private List<List<byte>>? _cListsTrans;
    private List<ushort[]>? _iListsTrans;

    private readonly Block[]? _blocks = RentAndClear<Block>(Volume);

    private static readonly ThreadLocal<MeshBuilder> Builder = new(() => new MeshBuilder());

    private readonly Lock _lock = new();

    private class MeshBuilder {

        public readonly SubBuilder Opaque = new();
        public readonly SubBuilder Transparent = new();

        public readonly Queue<(int x, int y, int z)> BfsQueue = new();
        public readonly HashSet<(int x, int y, int z)> BfsVisited = [];

        public void Clear() {

            Opaque.Clear();
            Transparent.Clear();
        }

        public class SubBuilder {

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

    public ushort GetOldLight(int x, int y, int z) {

        if (_renderLight == null || x < 0 || x >= Width || y < 0 || y >= Height || z < 0 || z >= Depth) return 0;

        return _renderLight[(x * Depth + z) * Height + y];
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

    private static T[] RentAndClear<T>(int size) {

        var arr = System.Buffers.ArrayPool<T>.Shared.Rent(size);
        Array.Clear(arr, 0, arr.Length);

        return arr;
    }

    public unsafe void Generate() {

        if (_disposed || _blocks == null) return;

        var config = WorldGenConfig.Data;
        var t = config.Terrain;
        var c = config.Caves;
        var layers = config.Layers;
        var biomes = config.Biomes.List.OrderByDescending(b => b.Threshold).ToList();
        var waterId = Registry.GetId(config.General.WaterBlock);

        LightEmitters.Clear();

        fixed (Block* pBlocks = _blocks) {

            var ptr = pBlocks;
            var idx = 0;

            for (var locX = 0; locX < Width; locX++) {

                var wx = X * Width + locX;

                for (var locZ = 0; locZ < Depth; locZ++) {

                    var wz = Z * Depth + locZ;

                    // Calculate biome blending
                    var (b1, b2, tBlend) = GetBiomeBlend(wx, wz, config, biomes);

                    // Calculate terrain height
                    var height = GetTerrainHeight(wx, wz, t, b1, b2, tBlend);
                    var bedrockH = GetBedrockHeight(wx, wz, config.Bedrock);

                    // Determine block types for this column
                    var (surfId, subId) = GetBiomeBlocks(wx, wz, b1, b2, tBlend);

                    for (var ly = 0; ly < Height; ly++) {

                        if (_disposed) return;

                        byte blockId = 0;

                        if (ly <= bedrockH) {

                            blockId = config.Bedrock.BlockId;

                        } else if (ly <= height) {

                            // Determine block based on depth
                            if (ly == height)
                                blockId = surfId;
                            else if (ly >= height - 3)
                                blockId = subId;
                            else
                                blockId = layers.Count > 0 ? layers[^1].BlockId : (byte)1;

                            // Carve caves
                            if (c.Enabled && blockId != 0 && ly < height - 2) {

                                if (GetCaveNoise(wx, Y * Height + ly, wz, c) > c.Threshold) blockId = 0;
                            }
                        } else if (ly <= config.General.WaterLevel) {

                            blockId = waterId;
                        }

                        *ptr++ = new Block(blockId);

                        if (blockId > 0) {

                            var lum = Registry.GetLuminance(blockId);
                            if (lum.R > 0 || lum.G > 0 || lum.B > 0) LightEmitters.Add(idx);
                        }

                        idx++;
                    }
                }
            }
        }

        if (_light != null) Array.Clear(_light, 0, _light.Length);
    }

    private (WorldGenConfig.Biome b1, WorldGenConfig.Biome b2, float t) GetBiomeBlend(float x, float z, WorldGenConfig.Config config, List<WorldGenConfig.Biome> biomes) {

        var val = Noise.Perlin2D(x * (float)config.Biomes.Scale, z * (float)config.Biomes.Scale);
        const float blendRadius = 0.05f;

        for (var i = 0; i < biomes.Count; i++) {

            var b = biomes[i];

            if (i == biomes.Count - 1) return (b, b, 0);

            if (val > b.Threshold + blendRadius) return (b, b, 1.0f);

            if (val > b.Threshold - blendRadius) {
                var prev = biomes[i + 1];
                var t = (float)((val - (b.Threshold - blendRadius)) / (2 * blendRadius));

                return (prev, b, t);
            }
        }

        var last = biomes[^1];

        return (last, last, 0);
    }

    private int GetTerrainHeight(float x, float z, WorldGenConfig.TerrainSettings t, WorldGenConfig.Biome b1, WorldGenConfig.Biome b2, float blend) {

        var baseH = float.Lerp(b1.BaseHeight, b2.BaseHeight, blend);
        var amp = float.Lerp(b1.HeightAmplitude, b2.HeightAmplitude, blend);

        float nH = 0, nAmp = 1, freq = 1, totAmp = 0;

        for (var i = 0; i < t.Octaves; i++) {
            nH += Noise.Perlin2D(x * (float)t.Scale * freq, z * (float)t.Scale * freq) * nAmp;
            totAmp += nAmp;
            nAmp *= (float)t.Persistence;
            freq *= (float)t.Lacunarity;
        }

        return (int)(baseH + (nH / totAmp) * amp);
    }

    private int GetBedrockHeight(float x, float z, WorldGenConfig.BedrockSettings b) { return (int)(b.MinHeight + (Noise.Perlin2D(x * (float)b.Scale, z * (float)b.Scale) + 1f) * 0.5f * (b.MaxHeight - b.MinHeight)); }

    private (byte surf, byte sub) GetBiomeBlocks(float x, float z, WorldGenConfig.Biome b1, WorldGenConfig.Biome b2, float blend) {

        var noise = (Noise.Perlin2D(x * 0.1f, z * 0.1f) + 1f) * 0.5f;
        var modT = blend + (noise - 0.5f) * 0.4f;

        if (b2.TransitionBlockId != 0 && blend is > 0.05f and < 0.95f) {

            const float width = 0.35f;

            if (modT is > 0.5f - width / 2 and < 0.5f + width / 2) return (b2.TransitionBlockId, b2.SubSurfaceBlockId);
            if (modT >= 0.5f + width / 2) return (b2.SurfaceBlockId, b2.SubSurfaceBlockId);

            return (b1.SurfaceBlockId, b1.SubSurfaceBlockId);
        }

        return modT > 0.5f ? (b2.SurfaceBlockId, b2.SubSurfaceBlockId) : (b1.SurfaceBlockId, b1.SubSurfaceBlockId);
    }

    private float GetCaveNoise(float x, float y, float z, WorldGenConfig.CaveSettings c) {

        float val = 0, amp = 1, freq = 1;

        for (var i = 0; i < c.Octaves; i++) {
            val += Noise.Perlin3D(x * (float)c.ScaleX * freq, y * (float)c.ScaleY * freq, z * (float)c.ScaleZ * freq) * amp;
            amp *= 0.5f;
            freq *= 2.0f;
        }

        return val;
    }

    public volatile bool IsDirty;

    public unsafe void BuildArrays(Chunk? nx, Chunk? px, Chunk? ny, Chunk? py, Chunk? nz, Chunk? pz, Chunk? nxNz, Chunk? nxPz, Chunk? pxNz, Chunk? pxPz) {

        if (_disposed || _blocks == null || _light == null) return;

        var builder = Builder.Value!;
        builder.Clear();

        var newVLists = new List<List<float>>();
        var newNLists = new List<List<float>>();
        var newTLists = new List<List<float>>();
        var newCLists = new List<List<byte>>();
        var newILists = new List<ushort[]>();

        var newVListsTrans = new List<List<float>>();
        var newNListsTrans = new List<List<float>>();
        var newTListsTrans = new List<List<float>>();
        var newCListsTrans = new List<List<byte>>();
        var newIListsTrans = new List<ushort[]>();

        fixed (Block* pBlocks = _blocks)
        fixed (ushort* pLight = _light)
        fixed (ushort* pOldLight = _renderLight) {

            for (var x = 0; x < Width; x++)
            for (var z = 0; z < Depth; z++)
            for (var y = 0; y < Height; y++) {

                if (_disposed) return;

                if (builder.Opaque.VIdx > 60000 || builder.Transparent.VIdx > 60000) Flush(builder, newVLists, newNLists, newTLists, newCLists, newILists, newVListsTrans, newNListsTrans, newTListsTrans, newCListsTrans, newIListsTrans);

                var idx = (x * Depth + z) * Height + y;

                //var block = blocks[idx];
                var block = pBlocks[idx];

                if (block.Id == 0) continue;

                // Optimization: If a block (Simple or Complex) is fully surrounded by Opaque blocks, don't draw it.
                if (IsHidden(x, y, z, pBlocks)) continue;

                var isTrans = !block.Opaque && !block.Solid;
                var targetMesh = isTrans ? builder.Transparent : builder.Opaque;

                if (Registry.IsSimple(block.Id) && block.Data == 0) {

                    // dx, dy, dz, fIdx, rx, ry, rz, ux, uy, uz, x1, y1, z1, x2, y2, z2, x3, y3, z3, x4, y4, z4, nx, ny, nz
                    ReadOnlySpan<sbyte> faceData = [
                        0, 0, -1, 0, -1, 0, 0, 0, 1, 0, 1, 1, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, -1, // North
                        0, 0, 1, 2, 1, 0, 0, 0, 1, 0, 0, 1, 1, 1, 1, 1, 1, 0, 1, 0, 0, 1, 0, 0, 1,    // South
                        1, 0, 0, 1, 0, 0, -1, 0, 1, 0, 1, 1, 1, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 0,   // East
                        -1, 0, 0, 3, 0, 0, 1, 0, 1, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 0, 0, -1, 0, 0,  // West
                        0, 1, 0, 4, 1, 0, 0, 0, 0, -1, 0, 1, 0, 1, 1, 0, 1, 1, 1, 0, 1, 1, 0, 1, 0,   // Up
                        0, -1, 0, 5, 1, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 1, 0, -1, 0   // Down
                    ];

                    var faceLights = new ushort[4];
                    var oldFaceLights = new ushort[4];
                    const int stride = 25;

                    for (var i = 0; i < 6; i++) {

                        var offset = i * stride;
                        var dx = faceData[offset];
                        var dy = faceData[offset + 1];
                        var dz = faceData[offset + 2];
                        var fIdx = faceData[offset + 3];

                        if (IsOpaque(x + dx, y + dy, z + dz, pBlocks)) continue;

                        var uv = Registry.GetFaceUv(block.Id, fIdx);
                        var idxOffset = (dx * Depth + dz) * Height + dy;
                        var isSafe = z is > 1 and < 14 && x is > 1 and < 14 && y is > 1 and < 254;

                        var rx = faceData[offset + 4];
                        var ry = faceData[offset + 5];
                        var rz = faceData[offset + 6];
                        var ux = faceData[offset + 7];
                        var uy = faceData[offset + 8];
                        var uz = faceData[offset + 9];

                        if (isSafe)
                            FillLightsSimple(idx + idxOffset, rx, ry, rz, ux, uy, uz, faceLights, pLight, pBlocks);
                        else
                            FillLights(x + dx, y + dy, z + dz, rx, ry, rz, ux, uy, uz, faceLights, pLight, pBlocks);

                        if (fIdx == 5)
                            SwapPairs(faceLights);
                        else
                            Swap(faceLights);

                        if (isSafe)
                            FillLightsSimple(idx + idxOffset, rx, ry, rz, ux, uy, uz, oldFaceLights, pOldLight, pBlocks);
                        else
                            FillLights(x + dx, y + dy, z + dz, rx, ry, rz, ux, uy, uz, oldFaceLights, pOldLight, pBlocks, true);

                        if (fIdx == 5)
                            SwapPairs(oldFaceLights);
                        else
                            Swap(oldFaceLights);

                        AddFace(targetMesh, x, y, z, faceData[offset + 10], faceData[offset + 11], faceData[offset + 12], faceData[offset + 13], faceData[offset + 14], faceData[offset + 15], faceData[offset + 16], faceData[offset + 17], faceData[offset + 18], faceData[offset + 19], faceData[offset + 20], faceData[offset + 21], faceData[offset + 22], faceData[offset + 23], faceData[offset + 24], uv, faceLights, oldFaceLights);
                    }

                    continue;
                }

                var model = Registry.GetModel(block.Id);

                if (model == null) continue;

                var faceLights2 = new ushort[4];

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

                    if (isAxisAligned) {

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

                    if ((!isRotated || isAxisAligned) && el.Rotation == null) {

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

                        // dirX, dirY, dirZ, rx, ry, rz, ux, uy, uz, swapType(0=Swap,1=Pair), bIdx, bType(0=Min,1=Max), vIdx0..11, nx, ny, nz
                        ReadOnlySpan<sbyte> modelData = [

                            // S-Face(Z+1)
                            0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 5, 1, 0, 3, 5, 1, 3, 5, 1, 2, 5, 0, 2, 5, 0, 0, 1,

                            // N-Face(Z-1)
                            0, 0, -1, -1, 0, 0, 0, 1, 0, 0, 4, 0, 1, 3, 4, 0, 3, 4, 0, 2, 4, 1, 2, 4, 0, 0, -1,

                            // U-Face(Y+1)
                            0, 1, 0, 1, 0, 0, 0, 0, -1, 0, 3, 1, 0, 3, 4, 1, 3, 4, 1, 3, 5, 0, 3, 5, 0, 1, 0,

                            // D-Face(Y-1)
                            0, -1, 0, 1, 0, 0, 0, 0, 1, 1, 2, 0, 1, 2, 4, 0, 2, 4, 0, 2, 5, 1, 2, 5, 0, -1, 0,

                            // E-Face(X+1)
                            1, 0, 0, 0, 0, -1, 0, 1, 0, 0, 1, 1, 1, 3, 5, 1, 3, 4, 1, 2, 4, 1, 2, 5, 1, 0, 0,

                            // W-Face(X-1)
                            -1, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0, 0, 3, 4, 0, 3, 5, 0, 2, 5, 0, 2, 4, -1, 0, 0
                        ];

                        Span<float> bounds = [x0, x1, y0, y1, z0, z1];
                        const int mStride = 27;

                        for (var i = 0; i < 6; i++) {

                            var off = i * mStride;
                            var dir = new Vector3(modelData[off], modelData[off + 1], modelData[off + 2]);

                            if (!el.Faces.TryGetValue(GetSourceFace(dir), out var face)) continue;

                            var uv = Registry.ResolveFaceUv(model, face);

                            if (ShouldCull(uv.CullMask, pBlocks)) continue;

                            var bIdx = modelData[off + 10];
                            var bType = modelData[off + 11]; // 0: <= 0.001, 1: >= 0.999
                            var isOnBoundary = bType == 1 ? bounds[bIdx] >= 0.999f : bounds[bIdx] <= 0.001f;

                            var rx = modelData[off + 3];
                            var ry = modelData[off + 4];
                            var rz = modelData[off + 5];
                            var ux = modelData[off + 6];
                            var uy = modelData[off + 7];
                            var uz = modelData[off + 8];

                            var lx = x + (int)dir.X;
                            var ly = y + (int)dir.Y;
                            var lz = z + (int)dir.Z;

                            if (!isOnBoundary) {

                                lx = x;
                                ly = y;
                                lz = z;
                            }

                            FillLights(lx, ly, lz, rx, ry, rz, ux, uy, uz, faceLights2, pLight, pBlocks);

                            if (modelData[off + 9] == 1)
                                SwapPairs(faceLights2);
                            else
                                Swap(faceLights2);

                            var oldFaceLights2 = new ushort[4];

                            FillLights(lx, ly, lz, rx, ry, rz, ux, uy, uz, oldFaceLights2, pOldLight, pBlocks, true);

                            if (modelData[off + 9] == 1)
                                SwapPairs(oldFaceLights2);
                            else
                                Swap(oldFaceLights2);

                            // AddFace
                            var vOff = off + 12;
                            AddFace(targetMesh, x, y, z, bounds[modelData[vOff]], bounds[modelData[vOff + 1]], bounds[modelData[vOff + 2]], bounds[modelData[vOff + 3]], bounds[modelData[vOff + 4]], bounds[modelData[vOff + 5]], bounds[modelData[vOff + 6]], bounds[modelData[vOff + 7]], bounds[modelData[vOff + 8]], bounds[modelData[vOff + 9]], bounds[modelData[vOff + 10]], bounds[modelData[vOff + 11]], modelData[vOff + 12], modelData[vOff + 13], modelData[vOff + 14], uv, faceLights2, oldFaceLights2);
                        }
                    } else {

                        // Off-axis rotation (Generic)
                        var c = new Vector3(0.5f);
                        var min = new Vector3(el.From[0], el.From[1], el.From[2]) / 16f;
                        var max = new Vector3(el.To[0], el.To[1], el.To[2]) / 16f;
                        var minRot = min - c;
                        var maxRot = max - c;

                        Matrix4x4? elRotMat = null;

                        if (el.Rotation != null) {

                            var origin = new Vector3(el.Rotation.Origin[0], el.Rotation.Origin[1], el.Rotation.Origin[2]) / 16f - new Vector3(0.5f);

                            var axis = el.Rotation.Axis switch {

                                "x" => Vector3.UnitX,
                                "y" => Vector3.UnitY,
                                _   => Vector3.UnitZ
                            };

                            var angle = el.Rotation.Angle * (float)(Math.PI / 180.0);

                            elRotMat = Matrix4x4.CreateTranslation(-origin) * Matrix4x4.CreateFromAxisAngle(axis, angle) * Matrix4x4.CreateTranslation(origin);
                        }

                        foreach (var (key, value) in el.Faces) {

                            var faceUv = Registry.ResolveFaceUv(model, value);

                            if (ShouldCull(faceUv.CullMask, pBlocks)) continue;

                            Vector3 p1, p2, p3, p4;

                            switch (key) {

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

                            // Apply Element Rotation
                            if (elRotMat.HasValue) {

                                p1 = Vector3.Transform(p1, elRotMat.Value);
                                p2 = Vector3.Transform(p2, elRotMat.Value);
                                p3 = Vector3.Transform(p3, elRotMat.Value);
                                p4 = Vector3.Transform(p4, elRotMat.Value);
                            }

                            // Apply Rotation
                            p1 = Vector3.Transform(p1, rot) + c;
                            p2 = Vector3.Transform(p2, rot) + c;
                            p3 = Vector3.Transform(p3, rot) + c;
                            p4 = Vector3.Transform(p4, rot) + c;

                            // Calculate Lighting Dimensions for AO
                            var vR = Vector3.Normalize(p2 - p1);
                            var vU = Vector3.Normalize(p1 - p4);

                            FillLights(x, y, z, (int)Math.Round(vR.X), (int)Math.Round(vR.Y), (int)Math.Round(vR.Z), (int)Math.Round(vU.X), (int)Math.Round(vU.Y), (int)Math.Round(vU.Z), faceLights2, pLight, pBlocks);

                            // Fix light-vertex mapping (BL->TL, etc.)
                            Swap(faceLights2);

                            var oldFaceLights2 = new ushort[4];
                            FillLights(x, y, z, (int)Math.Round(vR.X), (int)Math.Round(vR.Y), (int)Math.Round(vR.Z), (int)Math.Round(vU.X), (int)Math.Round(vU.Y), (int)Math.Round(vU.Z), oldFaceLights2, pOldLight, pBlocks, true);
                            Swap(oldFaceLights2);

                            // Use AddFace to handle UV rotation and Indexing correctly
                            AddFace(targetMesh, x, y, z, p1.X, p1.Y, p1.Z, p2.X, p2.Y, p2.Z, p3.X, p3.Y, p3.Z, p4.X, p4.Y, p4.Z, 0, 0, 0, faceUv, faceLights2, oldFaceLights2);
                        }
                    }
                }

                continue;

                bool ShouldCull(byte mask, Block* pB) {

                    if (mask == 0) return false;

                    for (var i = 0; i < 6; i++) {

                        if ((mask & (1 << i)) == 0) continue;

                        var d = worldDirs[i];

                        if (!IsOpaque(x + d.X, y + d.Y, z + d.Z, pB)) return false;
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

                // Fast unsafe version
                void FillLightsSimple(int idX, int rX, int rY, int rZ, int uX, int uY, int uZ, ushort[] output, ushort* pL, Block* pB) {

                    if (!QualitySettings.SmoothLighting) {
                        var flat = pL[idX];
                        output[0] = output[1] = output[2] = output[3] = flat;

                        return;
                    }

                    // Assuming internal check is done by caller
                    output[0] = GetVertexLightPtr(idX, -rX - uX, -rY - uY, -rZ - uZ, pL, pB);
                    output[1] = GetVertexLightPtr(idX, rX - uX, rY - uY, rZ - uZ, pL, pB);
                    output[2] = GetVertexLightPtr(idX, rX + uX, rY + uY, rZ + uZ, pL, pB);
                    output[3] = GetVertexLightPtr(idX, -rX + uX, -rY + uY, -rZ + uZ, pL, pB);
                }

                ushort GetVertexLightPtr(int centerIdx, int dx, int dy, int dz, ushort* pL, Block* pB) {

                    // Center
                    var l1 = pL[centerIdx];

                    // A, B, C Logic
                    int idxA = centerIdx, idxB = centerIdx, idxC = centerIdx;

                    const int xStep = Depth * Height;

                    // Optimized offset calculation
                    if (dx == 0) {

                        // Axis A: Y+dy (idx + dy*yStep), Axis B: Z+dz (idx + dz*zStep) A = Y+dy, B = Z+dz, Y+dy + Z+dz
                        var offZ = dz * Height;

                        idxA += dy;
                        idxB += offZ;
                        idxC += dy + offZ;

                    } else if (dy == 0) {

                        // A = X+dx, B = Z+dz
                        var offX = dx * xStep;
                        var offZ = dz * Height;

                        idxA += offX;
                        idxB += offZ;
                        idxC += offX + offZ;

                    } else {

                        // A = X+dx, B = Y+dy
                        var offX = dx * xStep;

                        idxA += offX;
                        idxB += dy;
                        idxC += offX + dy;
                    }

                    var lA = pL[idxA];
                    var lB = pL[idxB];

                    if (pB[idxA].Solid && pB[idxB].Solid) return AverageLight(l1, lA, lB, l1);

                    var lC = pL[idxC];

                    return AverageLight(l1, lA, lB, lC);
                }

                void FillLights(int lx, int ly, int lz, int rX, int rY, int rZ, int uX, int uY, int uZ, ushort[] output, ushort* pL, Block* pB, bool isOld = false) {

                    if (!QualitySettings.SmoothLighting) {

                        var flat = GetLightSafe(lx, ly, lz, pL, isOld);
                        output[0] = output[1] = output[2] = output[3] = flat;

                        return;
                    }

                    // output[0]=BL, output[1]=BR, output[2]=TR, output[3]=TL
                    output[0] = GetVertexLight(lx, ly, lz, -rX - uX, -rY - uY, -rZ - uZ, pL, pB, isOld);
                    output[1] = GetVertexLight(lx, ly, lz, rX - uX, rY - uY, rZ - uZ, pL, pB, isOld);
                    output[2] = GetVertexLight(lx, ly, lz, rX + uX, rY + uY, rZ + uZ, pL, pB, isOld);
                    output[3] = GetVertexLight(lx, ly, lz, -rX + uX, -rY + uY, -rZ + uZ, pL, pB, isOld);
                }

                ushort GetVertexLight(int vx, int vy, int vz, int dx, int dy, int dz, ushort* pL, Block* pB, bool isOld = false) {

                    var l1 = GetLightSafe(vx, vy, vz, pL, isOld);

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

                    var lA = GetLightSafe(ax, ay, az, pL, isOld);
                    var lB = GetLightSafe(bx, by, bz, pL, isOld);

                    // Occlusion
                    if (IsSolid(ax, ay, az, pB) && IsSolid(bx, by, bz, pB)) {

                        return AverageLight(l1, lA, lB, l1);
                    }

                    var lC = GetLightSafe(cx, cy, cz, pL, isOld);

                    return AverageLight(l1, lA, lB, lC);
                }

                ushort AverageLight(ushort a, ushort b, ushort c, ushort d) {

                    var r = (a & 0xF) + (b & 0xF) + (c & 0xF) + (d & 0xF);
                    var g = ((a >> 4) & 0xF) + ((b >> 4) & 0xF) + ((c >> 4) & 0xF) + ((d >> 4) & 0xF);
                    var bl = ((a >> 8) & 0xF) + ((b >> 8) & 0xF) + ((c >> 8) & 0xF) + ((d >> 8) & 0xF);
                    var s = ((a >> 12) & 0xF) + ((b >> 12) & 0xF) + ((c >> 12) & 0xF) + ((d >> 12) & 0xF);

                    return (ushort)(((r + 2) >> 2) | (((g + 2) >> 2) << 4) | (((bl + 2) >> 2) << 8) | (((s + 2) >> 2) << 12));
                }

                ushort GetLightSafe(int cx, int cy, int cz, ushort* pL, bool isOld = false) {

                    if (cy is < 0 or >= Height) return 0; // Vertical limits (Sky/Void)

                    return cx switch {

                        // Optimize local access
                        >= 0 and < 16 when cz is >= 0 and < 16 => pL[(cx * 16 + cz) * 256 + cy],

                        // Diagonal & Neighbor Checks
                        < 0 when cz < 0   => (isOld ? nxNz?.GetOldLight(cx + 16, cy, cz + 16) : nxNz?.GetLight(cx + 16, cy, cz + 16)) ?? 0,
                        < 0 when cz >= 16 => (isOld ? nxPz?.GetOldLight(cx + 16, cy, cz - 16) : nxPz?.GetLight(cx + 16, cy, cz - 16)) ?? 0,
                        < 0               => (isOld ? nx?.GetOldLight(cx + 16, cy, cz) : nx?.GetLight(cx + 16, cy, cz)) ?? 0,

                        >= 16 when cz < 0   => (isOld ? pxNz?.GetOldLight(cx - 16, cy, cz + 16) : pxNz?.GetLight(cx - 16, cy, cz + 16)) ?? 0,
                        >= 16 when cz >= 16 => (isOld ? pxPz?.GetOldLight(cx - 16, cy, cz - 16) : pxPz?.GetLight(cx - 16, cy, cz - 16)) ?? 0,
                        >= 16               => (isOld ? px?.GetOldLight(cx - 16, cy, cz) : px?.GetLight(cx - 16, cy, cz)) ?? 0,

                        _ when cz < 0 => (isOld ? nz?.GetOldLight(cx, cy, cz + 16) : nz?.GetLight(cx, cy, cz + 16)) ?? 0,
                        _ when true   => (isOld ? pz?.GetOldLight(cx, cy, cz - 16) : pz?.GetLight(cx, cy, cz - 16)) ?? 0,
                    };
                }

                Block GetBlockSafe(int cx, int cy, int cz, Block* pB) {

                    if (_disposed || cy is < 0 or >= Height) return new Block();

                    return cx switch {

                        // Optimize local access
                        >= 0 and < 16 when cz is >= 0 and < 16 => pB[(cx * 16 + cz) * 256 + cy],

                        // Diagonal & Neighbor Checks
                        < 0 when cz < 0   => nxNz?.GetBlock(cx + 16, cy, cz + 16) ?? new Block(),
                        < 0 when cz >= 16 => nxPz?.GetBlock(cx + 16, cy, cz - 16) ?? new Block(),
                        < 0               => nx?.GetBlock(cx + 16, cy, cz) ?? new Block(),

                        >= 16 when cz < 0   => pxNz?.GetBlock(cx - 16, cy, cz + 16) ?? new Block(),
                        >= 16 when cz >= 16 => pxPz?.GetBlock(cx - 16, cy, cz - 16) ?? new Block(),
                        >= 16               => px?.GetBlock(cx - 16, cy, cz) ?? new Block(),

                        _ => cz < 0 ? nz?.GetBlock(cx, cy, cz + 16) ?? new Block() : pz?.GetBlock(cx, cy, cz - 16) ?? new Block()
                    };
                }

                // BFS check to see if a block is encased in opaque blocks. Uses reused collections from MeshBuilder to avoid zero-alloc.
                bool IsHidden(int startX, int startY, int startZ, Block* pB) {

                    // Reset collections
                    builder.BfsQueue.Clear();
                    builder.BfsVisited.Clear();

                    var start = (startX, startY, startZ);
                    builder.BfsQueue.Enqueue(start);
                    builder.BfsVisited.Add(start);

                    var count = 0;
                    const int SearchLimit = 64; // Limit to prevent freezing on large open areas

                    while (builder.BfsQueue.Count > 0) {

                        // Failing logic: If volume is too big, it's likely visible / open
                        if (count++ > SearchLimit) return false;

                        var (cx, cy, cz) = builder.BfsQueue.Dequeue();

                        // Check all 6 neighbors
                        if (!CheckNb(cx + 1, cy, cz, pB)) return false;
                        if (!CheckNb(cx - 1, cy, cz, pB)) return false;
                        if (!CheckNb(cx, cy, cz + 1, pB)) return false;
                        if (!CheckNb(cx, cy, cz - 1, pB)) return false;
                        if (!CheckNb(cx, cy + 1, cz, pB)) return false;
                        if (!CheckNb(cx, cy - 1, cz, pB)) return false;
                    }

                    return true;

                    bool CheckNb(int nX, int nY, int nZ, Block* blockPtr) {

                        if (builder.BfsVisited.Contains((nX, nY, nZ))) return true; // Already processed

                        var nb = GetBlockSafe(nX, nY, nZ, blockPtr);

                        if (nb.Opaque) return true;   // Wall -> Closed
                        if (nb.Id == 0) return false; // Air/Leak -> Visible!

                        // Transparent/Complex -> Add to search
                        builder.BfsVisited.Add((nX, nY, nZ));
                        builder.BfsQueue.Enqueue((nX, nY, nZ));

                        return true;
                    }
                }

                bool IsOpaque(int cx, int cy, int cz, Block* pB) {

                    var b = GetBlockSafe(cx, cy, cz, pB);

                    if (b.Opaque) return true;
                    if (b.Id == 0) return false;

                    if (b.Id == block.Id) return true;

                    // Connection-based culling
                    if (Registry.CanConnect(block.Id, b.Id)) return true;

                    // Only allow if the neighbor contains a model element that covers the full volume.
                    if (!Registry.IsFullBlock(b.Id)) return false;

                    return IsOpaqueNb(1, 0, 0) && IsOpaqueNb(-1, 0, 0) && IsOpaqueNb(0, 1, 0) && IsOpaqueNb(0, -1, 0) && IsOpaqueNb(0, 0, 1) && IsOpaqueNb(0, 0, -1);

                    // Clump-aware surrounded check.
                    bool IsOpaqueNb(int xOffset, int yOffset, int zOffset) {

                        var nb = GetBlockSafe(cx + xOffset, cy + yOffset, cz + zOffset, pB);

                        return nb.Opaque;
                    }
                }

                bool IsSolid(int cx, int cy, int cz, Block* pB) => GetBlockSafe(cx, cy, cz, pB).Solid;
            }

            Flush(builder, newVLists, newNLists, newTLists, newCLists, newILists, newVListsTrans, newNListsTrans, newTListsTrans, newCListsTrans, newIListsTrans);

            if (newVLists.Count <= 0 && newVListsTrans.Count <= 0) return;

            lock (_lock) {

                if (_disposed) return;

                if (_vLists != null) {

                    foreach (var l in _vLists) ListPool<float>.Return(l);
                    foreach (var l in _nLists!) ListPool<float>.Return(l);
                    foreach (var l in _tLists!) ListPool<float>.Return(l);
                    foreach (var l in _cLists!) ListPool<byte>.Return(l);
                }

                if (_vListsTrans != null) {

                    foreach (var l in _vListsTrans) ListPool<float>.Return(l);
                    foreach (var l in _nListsTrans!) ListPool<float>.Return(l);
                    foreach (var l in _tListsTrans!) ListPool<float>.Return(l);
                    foreach (var l in _cListsTrans!) ListPool<byte>.Return(l);
                }

                _vLists = newVLists;
                _nLists = newNLists;
                _tLists = newTLists;
                _cLists = newCLists;
                _iLists = newILists;

                _vListsTrans = newVListsTrans;
                _nListsTrans = newNListsTrans;
                _tListsTrans = newTListsTrans;
                _cListsTrans = newCListsTrans;
                _iListsTrans = newIListsTrans;
            }

            IsDirty = true;
        }
    }

    private static void Flush(MeshBuilder b, List<List<float>> v, List<List<float>> n, List<List<float>> t, List<List<byte>> c, List<ushort[]> i, List<List<float>> vT, List<List<float>> nT, List<List<float>> tT, List<List<byte>> cT, List<ushort[]> iT) {

        if (b.Opaque.Verts.Count > 0) {

            v.Add(b.Opaque.Verts);
            n.Add(b.Opaque.Norms);
            t.Add(b.Opaque.Uvs);
            c.Add(b.Opaque.Colors);
            i.Add(b.Opaque.Tris.ToArray());
            b.Opaque.Verts = ListPool<float>.Rent();
            b.Opaque.Norms = ListPool<float>.Rent();
            b.Opaque.Uvs = ListPool<float>.Rent();
            b.Opaque.Colors = ListPool<byte>.Rent();
            b.Opaque.Tris.Clear();
            b.Opaque.VIdx = 0;
        }

        if (b.Transparent.Verts.Count > 0) {

            vT.Add(b.Transparent.Verts);
            nT.Add(b.Transparent.Norms);
            tT.Add(b.Transparent.Uvs);
            cT.Add(b.Transparent.Colors);
            iT.Add(b.Transparent.Tris.ToArray());
            b.Transparent.Verts = ListPool<float>.Rent();
            b.Transparent.Norms = ListPool<float>.Rent();
            b.Transparent.Uvs = ListPool<float>.Rent();
            b.Transparent.Colors = ListPool<byte>.Rent();
            b.Transparent.Tris.Clear();
            b.Transparent.VIdx = 0;
        }
    }

    private static void AddFace(MeshBuilder.SubBuilder mesh, float ox, float oy, float oz, float x1, float y1, float z1, float x2, float y2, float z2, float x3, float y3, float z3, float x4, float y4, float z4, float nx, float ny, float nz, UvInfo info, ushort[] lights, ushort[] oldLights) {

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
            var oldLight = oldLights[k];
            mesh.Colors.Add((byte)(light & 0xFF));
            mesh.Colors.Add((byte)((light >> 8) & 0xFF));
            mesh.Colors.Add((byte)(oldLight & 0xFF));
            mesh.Colors.Add((byte)((oldLight >> 8) & 0xFF));
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

    public unsafe void Upload(double time) {

        List<List<float>>? vLists, nLists, tLists;
        List<List<byte>>? cLists;
        List<ushort[]>? iLists;

        List<List<float>>? vListsT, nListsT, tListsT;
        List<List<byte>>? cListsT;
        List<ushort[]>? iListsT;

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

            vListsT = _vListsTrans;
            nListsT = _nListsTrans;
            tListsT = _tListsTrans;
            cListsT = _cListsTrans;
            iListsT = _iListsTrans;

            _vListsTrans = null;
            _nListsTrans = null;
            _tListsTrans = null;
            _cListsTrans = null;
            _iListsTrans = null;
        }

        if (vLists == null && vListsT == null) return;

        UnloadMeshGraphics();

        if (vLists != null) UploadList(vLists, nLists!, tLists!, cLists!, iLists!, Meshes);
        if (vListsT != null) UploadList(vListsT, nListsT!, tListsT!, cListsT!, iListsT!, TransparentMeshes);

        MeshBuildTime = time;

        // Update render light history
        if (_renderLight != null && _light != null) {

            Array.Copy(_light, _renderLight, Volume);
        }

        IsDirty = false;

        return;

        void UploadList(List<List<float>> vs, List<List<float>> ns, List<List<float>> ts, List<List<byte>> cs, List<ushort[]> isIndices, List<Mesh> targetList) {

            for (var i = 0; i < vs.Count; i++) {

                var vList = vs[i];
                var nList = ns[i];
                var tList = ts[i];
                var cList = cs[i];
                var iArr = isIndices[i];

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
                targetList.Add(mesh);

                ListPool<float>.Return(vList);
                ListPool<float>.Return(nList);
                ListPool<float>.Return(tList);
                ListPool<byte>.Return(cList);
            }
        }
    }

    public void Unload() {

        lock (_lock) {

            if (_vLists != null) {
                foreach (var l in _vLists) ListPool<float>.Return(l);
                foreach (var l in _nLists!) ListPool<float>.Return(l);
                foreach (var l in _tLists!) ListPool<float>.Return(l);
                foreach (var l in _cLists!) ListPool<byte>.Return(l);
            }

            if (_vListsTrans != null) {
                foreach (var l in _vListsTrans) ListPool<float>.Return(l);
                foreach (var l in _nListsTrans!) ListPool<float>.Return(l);
                foreach (var l in _tListsTrans!) ListPool<float>.Return(l);
                foreach (var l in _cListsTrans!) ListPool<byte>.Return(l);
            }
        }

        UnloadMeshGraphics();
        Dispose();
    }

    private unsafe void UnloadMeshGraphics() {

        foreach (var mesh in Meshes) {
            FreeMesh(mesh);
        }

        Meshes.Clear();

        foreach (var mesh in TransparentMeshes) {
            FreeMesh(mesh);
        }

        TransparentMeshes.Clear();

        return;

        void FreeMesh(Mesh mesh) {

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
    }

    public void Dispose() {

        if (_disposed) return;

        _disposed = true;

        if (_blocks == null) return;

        System.Buffers.ArrayPool<Block>.Shared.Return(_blocks);
        System.Buffers.ArrayPool<ushort>.Shared.Return(_light!);
        System.Buffers.ArrayPool<ushort>.Shared.Return(_renderLight!);

        _light = null;
        _renderLight = null;
    }
}