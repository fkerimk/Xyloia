using System.Collections.Concurrent;
using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

// ReSharper disable InconsistentlySynchronizedField
internal class World {

    private readonly ConcurrentDictionary<ChunkPos, Chunk> _chunks = new();
    private readonly ConcurrentQueue<Chunk> _buildQueue = new();
    private readonly ConcurrentDictionary<ChunkPos, byte> _pendingRebuilds = new();

    private readonly HashSet<ChunkPos> _processingChunks = [];
    private volatile int _activeTaskCount;

    private Vector3? _dynamicLightPos;

    private const int ViewDistance = 16;

    private int _realTimeCamX, _realTimeCamZ;

    private static readonly ChunkPos[] ScanOffsets;

    static World() {

        var offsets = new List<ChunkPos>();

        for (var x = -ViewDistance; x <= ViewDistance; x++)
        for (var z = -ViewDistance; z <= ViewDistance; z++)
            if (x * x + z * z <= ViewDistance * ViewDistance)
                offsets.Add(new ChunkPos(x, 0, z));

        ScanOffsets = offsets.OrderBy(p => p.X * p.X + p.Z * p.Z).ToArray();
    }

    private Chunk? GetChunk(int x, int y, int z) { return _chunks.GetValueOrDefault(new ChunkPos(x, y, z)); }

    private Block GetBlock(int x, int y, int z) {

        var cx = x >> 4;
        var cy = y >> 8;
        var cz = z >> 4;

        return !_chunks.TryGetValue(new ChunkPos(cx, cy, cz), out var chunk) ? new Block() : chunk.GetBlock(x & 15, y & 255, z & 15);
    }

    // Lighting
    private byte GetSkylight(int x, int y, int z) => (byte)((GetLight(x, y, z) >> 12) & 0xF);

    private void SetSkylight(int x, int y, int z, byte val) {

        var light = GetLight(x, y, z);
        light = (ushort)((light & 0x0FFF) | ((val & 0xF) << 12));
        SetLight(x, y, z, light);
    }

    private ushort GetLight(int x, int y, int z) {

        var cx = x >> 4;
        var cy = y >> 8;
        var cz = z >> 4;

        return !_chunks.TryGetValue(new ChunkPos(cx, cy, cz), out var chunk) ? (ushort)0 : chunk.GetLight(x & 15, y & 255, z & 15);
    }

    private void SetLight(int x, int y, int z, ushort value) {

        var cx = x >> 4;
        var cy = y >> 8;
        var cz = z >> 4;

        if (!_chunks.TryGetValue(new ChunkPos(cx, cy, cz), out var chunk)) return;

        chunk.SetLight(x & 15, y & 255, z & 15, value);
    }

    // Light Propagation
    private void PropagateLights(Queue<(int x, int y, int z)> blockQueue, Queue<(int x, int y, int z)> skyQueue, bool markDirty = true) {

        if (!QualitySettings.Lighting) return;

        // Block Light (RGB)
        while (blockQueue.TryDequeue(out var p)) {

            var light = GetLight(p.x, p.y, p.z);

            var r = (byte)(light & 0xF);
            var g = (byte)((light >> 4) & 0xF);
            var b = (byte)((light >> 8) & 0xF);

            if (r == 0 && g == 0 && b == 0) continue;

            Span<(int dx, int dy, int dz)> dirs = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];

            foreach (var d in dirs) {

                int nx = p.x + d.dx, ny = p.y + d.dy, nz = p.z + d.dz;

                if (ny is < 0 or >= Chunk.Height) continue;

                var nBlock = GetBlock(nx, ny, nz);

                if (Registry.IsOpaque(nBlock.Id)) continue;

                var nLight = GetLight(nx, ny, nz);
                var nr = (byte)(nLight & 0xF);
                var ng = (byte)((nLight >> 4) & 0xF);
                var nb = (byte)((nLight >> 8) & 0xF);

                var changed = false;

                if (r > 1 && nr < r - 1) {
                    nr = (byte)(r - 1);
                    changed = true;
                }

                if (g > 1 && ng < g - 1) {
                    ng = (byte)(g - 1);
                    changed = true;
                }

                if (b > 1 && nb < b - 1) {
                    nb = (byte)(b - 1);
                    changed = true;
                }

                if (!changed) continue;

                var newVal = (ushort)((nLight & 0xF000) | nr | (ng << 4) | (nb << 8));
                SetLight(nx, ny, nz, newVal);
                blockQueue.Enqueue((nx, ny, nz));
                if (markDirty) MarkChunkDirty(nx, ny, nz);
            }
        }

        // Skylight
        while (skyQueue.TryDequeue(out var p)) {

            var light = GetSkylight(p.x, p.y, p.z);

            if (light <= 0) continue;

            Span<(int dx, int dy, int dz)> dirs = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];

            foreach (var d in dirs) {

                int nx = p.x + d.dx, ny = p.y + d.dy, nz = p.z + d.dz;

                if (ny is < 0 or >= Chunk.Height) continue;

                var nBlock = GetBlock(nx, ny, nz);

                if (Registry.IsOpaque(nBlock.Id)) continue;

                var nLight = GetSkylight(nx, ny, nz);
                var decay = (d.dy == -1 && light == 15) ? 0 : 1;
                var newLight = light - decay;

                if (newLight <= nLight) continue;

                SetSkylight(nx, ny, nz, (byte)newLight);
                skyQueue.Enqueue((nx, ny, nz));
                if (markDirty) MarkChunkDirty(nx, ny, nz);
            }
        }
    }

    private void MarkChunkDirty(int x, int y, int z) {

        var cx = x >> 4;
        var cy = y >> 8;
        var cz = z >> 4;
        var pos = new ChunkPos(cx, cy, cz);

        if (_chunks.ContainsKey(pos)) _pendingRebuilds.TryAdd(pos, 0);
    }

    private void RemoveLight(int x, int y, int z, bool isBlockLight) {

        if (!QualitySettings.Lighting) return;

        var removeQ = new Queue<(int x, int y, int z, ushort val)>();
        var refillQ = new Queue<(int x, int y, int z)>();

        var startVal = GetLight(x, y, z);

        // Mask out the relevant channels we are removing
        var removeMask = isBlockLight ? (ushort)0x0FFF : (ushort)0xF000;

        var valToRemove = (ushort)(startVal & removeMask);

        if (valToRemove == 0) return;

        // Set the world value to 0 for these channels
        SetLight(x, y, z, (ushort)(startVal & ~removeMask));

        removeQ.Enqueue((x, y, z, valToRemove));
        MarkChunkDirty(x, y, z);

        while (removeQ.TryDequeue(out var p)) {

            Span<(int dx, int dy, int dz)> dirs = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];

            foreach (var d in dirs) {

                int nx = p.x + d.dx, ny = p.y + d.dy, nz = p.z + d.dz;

                if (ny is < 0 or >= Chunk.Height) continue;

                var nLight = GetLight(nx, ny, nz);
                var nRelevant = (ushort)(nLight & removeMask);

                if (nRelevant == 0) continue;

                ushort nextRemoveVal = 0;

                // Process each channel independently
                if (isBlockLight) {

                    // Red (Bits 0-3)
                    var r = p.val & 0xF;
                    var nr = nRelevant & 0xF;
                    if (r > 0 && nr == r - 1)
                        nextRemoveVal |= (ushort)nr;
                    else if (nr >= r) refillQ.Enqueue((nx, ny, nz));

                    // Green (Bits 4-7)
                    var g = (p.val >> 4) & 0xF;
                    var ng = (nRelevant >> 4) & 0xF;
                    if (g > 0 && ng == g - 1)
                        nextRemoveVal |= (ushort)(ng << 4);
                    else if (ng >= g) refillQ.Enqueue((nx, ny, nz));

                    // Blue (Bits 8-11)
                    var b = (p.val >> 8) & 0xF;
                    var nb = (nRelevant >> 8) & 0xF;
                    if (b > 0 && nb == b - 1)
                        nextRemoveVal |= (ushort)(nb << 8);
                    else if (nb >= b) refillQ.Enqueue((nx, ny, nz));

                } else {

                    // Sky (Bits 12-15)
                    var s = (p.val >> 12) & 0xF;
                    var ns = (nRelevant >> 12) & 0xF;
                    var decay = (d.dy == -1 && s == 15) ? 0 : 1;

                    if (s > 0 && ns == s - decay)
                        nextRemoveVal |= (ushort)(ns << 12);
                    else if (ns >= s) refillQ.Enqueue((nx, ny, nz));
                }

                if (nextRemoveVal > 0) {

                    // Remove these specific channel bits from the world
                    SetLight(nx, ny, nz, (ushort)(nLight & ~nextRemoveVal));
                    removeQ.Enqueue((nx, ny, nz, nextRemoveVal));
                    MarkChunkDirty(nx, ny, nz);
                }
            }
        }

        if (isBlockLight)
            PropagateLights(refillQ, new Queue<(int, int, int)>());
        else
            PropagateLights(new Queue<(int, int, int)>(), refillQ);
    }

    // Simple AABB vs World Collision Helper
    public bool GetAabbCollision(float minX, float minY, float minZ, float maxX, float maxY, float maxZ) {

        var x0 = (int)Math.Floor(minX);
        var x1 = (int)Math.Floor(maxX);
        var y0 = (int)Math.Floor(minY);
        var y1 = (int)Math.Floor(maxY);
        var z0 = (int)Math.Floor(minZ);
        var z1 = (int)Math.Floor(maxZ);

        for (var x = x0; x <= x1; x++)
        for (var y = y0; y <= y1; y++)
        for (var z = z0; z <= z1; z++) {

            if (GetBlock(x, y, z).Solid) return true;
        }

        return false;
    }

    public bool IsChunkLoaded(int cx, int cy, int cz) => _chunks.ContainsKey(new ChunkPos(cx, cy, cz));

    public int GetTopBlockHeight(int x, int z) {
        
        for (var y = Chunk.Height - 1; y >= 0; y--) {
            
            if (GetBlock(x, y, z).Solid) return y;
        }
        
        return 0;
    }

    public void SetBlock(int x, int y, int z, byte blockId) => SetBlock(x, y, z, new Block(blockId));

    public void SetBlock(int x, int y, int z, Block block) {

        var blockPos = new ChunkPos(x >> 4, y >> 8, z >> 4);

        if (!_chunks.ContainsKey(blockPos)) return;

        var blockId = block.Id;
        var oldBlock = GetBlock(x, y, z);
        var oldLum = Registry.GetLuminance(oldBlock.Id);
        var newLum = Registry.GetLuminance(blockId);
        var oldTranslucent = !Registry.IsOpaque(oldBlock.Id);
        var newTranslucent = !Registry.IsOpaque(blockId);

        if (oldBlock.Id == blockId && oldBlock.Data == block.Data) return;

        var cx = x >> 4;
        var cy = y >> 8;
        var cz = z >> 4;
        var lx = x & 15;
        var ly = y & 255;
        var lz = z & 15;

        if (!_chunks.TryGetValue(new ChunkPos(cx, cy, cz), out var chunk)) return;

        chunk.SetBlock(lx, ly, lz, block);

        // Block Light Update

        // Remove old light if it was an emitter
        if (oldLum.R > 0 || oldLum.G > 0 || oldLum.B > 0) {

            RemoveLight(x, y, z, true);
        }

        // Set new light if it is an emitter
        if (newLum.R > 0 || newLum.G > 0 || newLum.B > 0) {

            var r = (byte)(newLum.R / 16);
            var g = (byte)(newLum.G / 16);
            var bl = (byte)(newLum.B / 16);

            var light = GetLight(x, y, z);
            light = (ushort)((light & 0xF000) | (r & 0xF) | ((g & 0xF) << 4) | ((bl & 0xF) << 8));
            SetLight(x, y, z, light);

            var q = new Queue<(int, int, int)>();
            q.Enqueue((x, y, z));
            PropagateLights(q, new Queue<(int, int, int)>());

        } else if (newTranslucent != oldTranslucent) {

            // Opacity changed
            if (newTranslucent) {
                // Became translucent -> Light can flow in
                var q = new Queue<(int, int, int)>();
                Span<(int dx, int dy, int dz)> dirs = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];

                foreach (var d in dirs) {
                    if ((GetLight(x + d.dx, y + d.dy, z + d.dz) & 0x0FFF) > 0) q.Enqueue((x + d.dx, y + d.dy, z + d.dz));
                }

                PropagateLights(q, new Queue<(int, int, int)>());

            } else {
                // Became solid -> Blocks light
                if ((GetLight(x, y, z) & 0x0FFF) > 0) RemoveLight(x, y, z, true);
            }
        }

        // Skylight Update
        if (newTranslucent != oldTranslucent) {

            if (newTranslucent) {
                var q = new Queue<(int, int, int)>();
                Span<(int dx, int dy, int dz)> dirs = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];

                foreach (var d in dirs) {
                    if (GetSkylight(x + d.dx, y + d.dy, z + d.dz) > 0) q.Enqueue((x + d.dx, y + d.dy, z + d.dz));
                }

                PropagateLights(new Queue<(int, int, int)>(), q);

            } else {
                if (GetSkylight(x, y, z) > 0) RemoveLight(x, y, z, false);
            }
        }

        RebuildChunk(chunk);

        switch (lx) {

            case 0:  RebuildChunkAt(cx - 1, cy, cz); break;
            case 15: RebuildChunkAt(cx + 1, cy, cz); break;
        }

        switch (ly) {

            case 0:   RebuildChunkAt(cx, cy - 1, cz); break;
            case 255: RebuildChunkAt(cx, cy + 1, cz); break;
        }

        switch (lz) {

            case 0:  RebuildChunkAt(cx, cy, cz - 1); break;
            case 15: RebuildChunkAt(cx, cy, cz + 1); break;
        }
    }

    private void RebuildChunkAt(int cx, int cy, int cz) {

        if (_chunks.TryGetValue(new ChunkPos(cx, cy, cz), out var chunk)) RebuildChunk(chunk);
    }

    private void RebuildChunk(Chunk chunk) {
        Task.Run(() => {

                var nx = GetChunk(chunk.X - 1, chunk.Y, chunk.Z);
                var px = GetChunk(chunk.X + 1, chunk.Y, chunk.Z);
                var ny = GetChunk(chunk.X, chunk.Y - 1, chunk.Z);
                var py = GetChunk(chunk.X, chunk.Y + 1, chunk.Z);
                var nz = GetChunk(chunk.X, chunk.Y, chunk.Z - 1);
                var pz = GetChunk(chunk.X, chunk.Y, chunk.Z + 1);

                chunk.BuildArrays(nx, px, ny, py, nz, pz);
                _buildQueue.Enqueue(chunk);
            }
        );
    }

    public RaycastResult Raycast(Vector3 origin, Vector3 direction, float maxDistance) {

        var x = (int)Math.Floor(origin.X);
        var y = (int)Math.Floor(origin.Y);
        var z = (int)Math.Floor(origin.Z);

        var stepX = Math.Sign(direction.X);
        var stepY = Math.Sign(direction.Y);
        var stepZ = Math.Sign(direction.Z);

        var tDeltaX = stepX != 0 ? 1f / Math.Abs(direction.X) : float.MaxValue;
        var tDeltaY = stepY != 0 ? 1f / Math.Abs(direction.Y) : float.MaxValue;
        var tDeltaZ = stepZ != 0 ? 1f / Math.Abs(direction.Z) : float.MaxValue;

        var tMaxX = stepX > 0 ? (x + 1 - origin.X) * tDeltaX : (origin.X - x) * tDeltaX;
        var tMaxY = stepY > 0 ? (y + 1 - origin.Y) * tDeltaY : (origin.Y - y) * tDeltaY;
        var tMaxZ = stepZ > 0 ? (z + 1 - origin.Z) * tDeltaZ : (origin.Z - z) * tDeltaZ;

        if (float.IsNaN(tMaxX)) tMaxX = float.MaxValue;
        if (float.IsNaN(tMaxY)) tMaxY = float.MaxValue;
        if (float.IsNaN(tMaxZ)) tMaxZ = float.MaxValue;

        var faceX = 0;
        var faceY = 0;
        var faceZ = 0;

        float distance = 0;

        while (distance <= maxDistance) {

            var block = GetBlock(x, y, z);

            if (block.Solid) {

                return new RaycastResult {
                    Hit = true,
                    X = x,
                    Y = y,
                    Z = z,
                    FaceX = faceX,
                    FaceY = faceY,
                    FaceZ = faceZ,
                    BlockId = block.Id
                };
            }

            if (tMaxX < tMaxY) {

                if (tMaxX < tMaxZ) {

                    x += stepX;
                    distance = tMaxX;
                    tMaxX += tDeltaX;
                    faceX = -stepX;
                    faceY = 0;
                    faceZ = 0;

                } else {

                    z += stepZ;
                    distance = tMaxZ;
                    tMaxZ += tDeltaZ;
                    faceX = 0;
                    faceY = 0;
                    faceZ = -stepZ;
                }
            } else {

                if (tMaxY < tMaxZ) {

                    y += stepY;
                    distance = tMaxY;
                    tMaxY += tDeltaY;
                    faceX = 0;
                    faceY = -stepY;
                    faceZ = 0;

                } else {

                    z += stepZ;
                    distance = tMaxZ;
                    tMaxZ += tDeltaZ;
                    faceX = 0;
                    faceY = 0;
                    faceZ = -stepZ;
                }
            }
        }

        return new RaycastResult { Hit = false };
    }

    public void UpdateDynamicLight(Vector3 camPos, bool enabled) {

        var lx = (int)Math.Floor(camPos.X);
        var ly = (int)Math.Floor(camPos.Y);
        var lz = (int)Math.Floor(camPos.Z);

        if (_dynamicLightPos != null) {

            var ox = (int)Math.Floor(_dynamicLightPos.Value.X);
            var oy = (int)Math.Floor(_dynamicLightPos.Value.Y);
            var oz = (int)Math.Floor(_dynamicLightPos.Value.Z);

            if (ox == lx && oy == ly && oz == lz && enabled) return;

            // Remove old light
            var oldBlock = GetBlock(ox, oy, oz);
            var oldLum = Registry.GetLuminance(oldBlock.Id);

            if (oldLum is { R: 0, G: 0, B: 0 }) {

                RemoveLight(ox, oy, oz, true);
            }
        }

        if (enabled) {

            _dynamicLightPos = camPos;

            // Add new light
            var currentBlock = GetBlock(lx, ly, lz);

            if (currentBlock.Solid) return;

            // Torch Color: R=15, G=13, B=10
            const ushort torchLight = 0xF | (13 << 4) | (10 << 8);

            // Only overwrite if we are brighter
            var existing = GetLight(lx, ly, lz);

            // Merge
            var r = Math.Max(existing & 0xF, torchLight & 0xF);
            var g = Math.Max((existing >> 4) & 0xF, (torchLight >> 4) & 0xF);
            var b = Math.Max((existing >> 8) & 0xF, (torchLight >> 8) & 0xF);
            var s = (existing >> 12) & 0xF;

            var newVal = (ushort)(r | (g << 4) | (b << 8) | (s << 12));

            if (newVal == existing) return;

            SetLight(lx, ly, lz, newVal);
            var q = new Queue<(int, int, int)>();
            q.Enqueue((lx, ly, lz));
            PropagateLights(q, new Queue<(int, int, int)>());

        } else {

            _dynamicLightPos = null;
        }
    }

    public void Update(Vector3 cameraPos) {

        var camCx = (int)Math.Floor(cameraPos.X / Chunk.Width);
        var camCz = (int)Math.Floor(cameraPos.Z / Chunk.Depth);

        _realTimeCamX = camCx;
        _realTimeCamZ = camCz;

        var maxTasks = Math.Max(1, Environment.ProcessorCount - 2);

        if (_activeTaskCount < maxTasks) {

            foreach (var offset in ScanOffsets) {

                if (_activeTaskCount >= maxTasks) break;

                var x = camCx + offset.X;
                var z = camCz + offset.Z;
                const int y = 0; // World is now a single chunk high (256 blocks)

                var pos = new ChunkPos(x, y, z);

                if (_chunks.ContainsKey(pos)) continue;

                bool alreadyProcessing;

                lock (_processingChunks) {

                    alreadyProcessing = !_processingChunks.Add(pos);
                }

                if (alreadyProcessing) continue;

                Interlocked.Increment(ref _activeTaskCount);

                Task.Run(() => {

                        try {

                            var distSq = (x - _realTimeCamX) * (x - _realTimeCamX) + (z - _realTimeCamZ) * (z - _realTimeCamZ);

                            if (distSq > (ViewDistance + 2) * (ViewDistance + 2)) return;

                            var chunk = new Chunk(pos.X, pos.Y, pos.Z);
                            chunk.Generate();

                            if (_chunks.TryAdd(pos, chunk)) {

                                var bQ = new Queue<(int, int, int)>();
                                var sQ = new Queue<(int, int, int)>();

                                // Skylight Initiation: Fast vertical fill + horizontal seeding
                                for (var lx = 0; lx < Chunk.Width; lx++)
                                for (var lz = 0; lz < Chunk.Depth; lz++) {

                                    var wx = pos.X * Chunk.Width + lx;
                                    var wz = pos.Z * Chunk.Depth + lz;

                                    // Fast fill from top down until first solid block
                                    for (var ly = Chunk.Height - 1; ly >= 0; ly--) {

                                        var b = chunk.GetBlock(lx, ly, lz);

                                        if (b.Solid) {

                                            // Seed the block just above for horizontal propagation
                                            if (ly + 1 < Chunk.Height) sQ.Enqueue((wx, ly + 1, wz));

                                            break;
                                        }

                                        chunk.SetLight(lx, ly, lz, (ushort)(chunk.GetLight(lx, ly, lz) | 0xF000));

                                        if (ly == 0) sQ.Enqueue((wx, 0, wz)); // Reach bottom

                                        // Also seed horizontal if neighbors might be solid
                                        sQ.Enqueue((wx, ly, wz));
                                    }
                                }

                                // Block Light Initiation (Emitters)
                                for (var lx = 0; lx < Chunk.Width; lx++)
                                for (var ly = 0; ly < Chunk.Height; ly++)
                                for (var lz = 0; lz < Chunk.Depth; lz++) {

                                    var b = chunk.GetBlock(lx, ly, lz);
                                    var lum = Registry.GetLuminance(b.Id);

                                    if (lum is { R: 0, G: 0, B: 0 }) continue;

                                    var wx = pos.X * Chunk.Width + lx;
                                    var wy = ly;
                                    var wz = pos.Z * Chunk.Depth + lz;

                                    var r = (byte)(lum.R / 16);
                                    var g = (byte)(lum.G / 16);
                                    var bl = (byte)(lum.B / 16);

                                    var light = GetLight(wx, wy, wz);
                                    light = (ushort)((light & 0xF000) | (r & 0xF) | ((g & 0xF) << 4) | ((bl & 0xF) << 8));
                                    SetLight(wx, wy, wz, light);

                                    bQ.Enqueue((wx, wy, wz));
                                }

                                // Pull light from neighbors more intelligently
                                Span<(int dx, int dz)> neighbors = [(-1, 0), (1, 0), (0, -1), (0, 1)];

                                foreach (var n in neighbors) {

                                    if (!_chunks.TryGetValue(new ChunkPos(pos.X + n.dx, 0, pos.Z + n.dz), out _)) continue;

                                    var nxStart = n.dx < 0 ? -1 : (n.dx > 0 ? Chunk.Width : 0);
                                    var nzStart = n.dz < 0 ? -1 : (n.dz > 0 ? Chunk.Depth : 0);
                                    var nxEnd = n.dx < 0 ? -1 : (n.dx > 0 ? Chunk.Width : Chunk.Width - 1);
                                    var nzEnd = n.dz < 0 ? -1 : (n.dz > 0 ? Chunk.Depth : Chunk.Depth - 1);

                                    // Vertical sampling
                                    for (var nx = nxStart; nx <= nxEnd; nx++)
                                    for (var nz = nzStart; nz <= nzEnd; nz++)
                                    for (var ly = 0; ly < Chunk.Height; ly += 8) {

                                        var wx = pos.X * Chunk.Width + nx;
                                        var wz = pos.Z * Chunk.Depth + nz;
                                        if ((GetLight(wx, ly, wz) & 0x0FFF) > 0) bQ.Enqueue((wx, ly, wz));
                                        if (GetSkylight(wx, ly, wz) > 0) sQ.Enqueue((wx, ly, wz));
                                    }
                                }

                                // Propagate without triggering redundant rebuilds (markDirty = false)
                                PropagateLights(bQ, sQ, false);

                                var cnx = GetChunk(pos.X - 1, pos.Y, pos.Z);
                                var cpx = GetChunk(pos.X + 1, pos.Y, pos.Z);
                                var cnz = GetChunk(pos.X, pos.Y, pos.Z - 1);
                                var cpz = GetChunk(pos.X, pos.Y, pos.Z + 1);

                                chunk.BuildArrays(cnx, cpx, null, null, cnz, cpz);
                                _buildQueue.Enqueue(chunk);

                                UpdateNeighbors(pos);

                            } else {

                                chunk.Dispose();
                            }

                        } catch (Exception) {

                            /* ignore */
                        } finally {

                            lock (_processingChunks) _processingChunks.Remove(pos);
                            Interlocked.Decrement(ref _activeTaskCount);
                        }
                    }
                );

                break;
            }
        }

        // Process pending rebuilds
        if (!_pendingRebuilds.IsEmpty) {

            foreach (var pos in _pendingRebuilds.Keys) {

                if (_chunks.TryGetValue(pos, out var c)) RebuildChunk(c);
                _pendingRebuilds.TryRemove(pos, out _);
            }
        }

        var buildTimer = System.Diagnostics.Stopwatch.StartNew();

        while (_buildQueue.TryDequeue(out var readyChunk)) {

            var dx = readyChunk.X - camCx;
            var dz = readyChunk.Z - camCz;

            if ((dx * dx + dz * dz) > (ViewDistance + 2) * (ViewDistance + 2)) {

                if (_chunks.TryRemove(new ChunkPos(readyChunk.X, readyChunk.Y, readyChunk.Z), out var removed)) {

                    removed.Unload();
                }

                continue;
            }

            if (!readyChunk.IsDirty) continue;

            readyChunk.Upload();

            lock (_renderLock) {

                if (readyChunk.Meshes.Count > 0 && !_renderList.Contains(readyChunk)) _renderList.Add(readyChunk);
            }

            if (buildTimer.Elapsed.TotalMilliseconds > 4.0) break;
        }

        var chunksToRemove = new List<ChunkPos>();
        var removeCount = 0;

        foreach (var kvp in _chunks) {

            float dx = kvp.Key.X - camCx;
            float dz = kvp.Key.Z - camCz;

            if ((dx * dx + dz * dz) <= (ViewDistance + 4) * (ViewDistance + 4)) continue;

            chunksToRemove.Add(kvp.Key);

            if (++removeCount >= 10) break;
        }

        if (chunksToRemove.Count > 0) {

            lock (_renderLock) {

                foreach (var pos in chunksToRemove) {

                    if (!_chunks.TryRemove(pos, out var chunk)) continue;

                    _renderList.Remove(chunk);
                    chunk.Unload();
                }
            }
        }
    }

    private readonly List<Chunk> _renderList = [];
    private readonly Lock _renderLock = new();
    private int _frameCounter;

    public void Render(Camera3D camera, Material material) {

        var camDir = Vector3.Normalize(camera.Target - camera.Position);
        var camPos = camera.Position;

        var count = _renderList.Count;

        if (_frameCounter++ % 30 == 0) {

            lock (_renderLock) {

                _renderList.Sort((a, b) => {

                        var da = (a.X * Chunk.Width - camPos.X) * (a.X * Chunk.Width - camPos.X) + (a.Z * Chunk.Depth - camPos.Z) * (a.Z * Chunk.Depth - camPos.Z);
                        var db = (b.X * Chunk.Width - camPos.X) * (b.X * Chunk.Width - camPos.X) + (b.Z * Chunk.Depth - camPos.Z) * (b.Z * Chunk.Depth - camPos.Z);

                        return da.CompareTo(db);
                    }
                );
            }
        }

        for (var i = 0; i < count; i++) {

            var chunk = _renderList[i];

            var cx = chunk.X * Chunk.Width + Chunk.Width / 2f;
            var cz = chunk.Z * Chunk.Depth + Chunk.Depth / 2f;

            // Find the closest point vertically within the chunk's 256-block column
            var closestY = Math.Clamp(camPos.Y, 0, Chunk.Height);

            var dx = cx - camPos.X;
            var dy = closestY - camPos.Y;
            var dz = cz - camPos.Z;

            var distSq = dx * dx + dy * dy + dz * dz;

            switch (distSq) {

                // Broad distance culling (Total 3D distance)
                case > (ViewDistance * Chunk.Width + 64) * (ViewDistance * Chunk.Width + 64): continue;

                // Dot product culling (Frustum-lite)
                case > 16384: {

                    var dist = (float)Math.Sqrt(distSq);
                    var dot = (dx / dist) * camDir.X + (dy / dist) * camDir.Y + (dz / dist) * camDir.Z;

                    if (dot < -0.3f) continue;

                    break;
                }
            }

            foreach (var mesh in chunk.Meshes) {

                DrawMesh(mesh, material, Raymath.MatrixTranslate(chunk.X * Chunk.Width, 0, chunk.Z * Chunk.Depth));
            }
        }
    }

    public void Unload() {

        foreach (var chunk in _chunks.Values) chunk.Unload();
        _chunks.Clear();

        lock (_renderLock) {

            _renderList.Clear();
        }
    }

    private void UpdateNeighbors(ChunkPos pos) {

        Span<ChunkPos> neighbors = [new(pos.X + 1, pos.Y, pos.Z), new(pos.X - 1, pos.Y, pos.Z), new(pos.X, pos.Y + 1, pos.Z), new(pos.X, pos.Y - 1, pos.Z), new(pos.X, pos.Y, pos.Z + 1), new(pos.X, pos.Y, pos.Z - 1)];

        foreach (var nPos in neighbors) {

            if (!_chunks.TryGetValue(nPos, out var nChunk)) continue;

            var nx = GetChunk(nPos.X - 1, nPos.Y, nPos.Z);
            var px = GetChunk(nPos.X + 1, nPos.Y, nPos.Z);
            var ny = GetChunk(nPos.X, nPos.Y - 1, nPos.Z);
            var py = GetChunk(nPos.X, nPos.Y + 1, nPos.Z);
            var nz = GetChunk(nPos.X, nPos.Y, nPos.Z - 1);
            var pz = GetChunk(nPos.X, nPos.Y, nPos.Z + 1);

            nChunk.BuildArrays(nx, px, ny, py, nz, pz);
            _buildQueue.Enqueue(nChunk);
        }
    }
}