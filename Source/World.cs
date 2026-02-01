using System.Collections.Concurrent;
using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

internal readonly struct ChunkPos(int x, int y, int z) : IEquatable<ChunkPos> {

    public readonly int X = x, Y = y, Z = z;

    public override bool Equals(object? obj) => obj is ChunkPos p && p.X == X && p.Y == Y && p.Z == Z;

    public override int GetHashCode() => HashCode.Combine(X, Y, Z);

    public bool Equals(ChunkPos other) => X == other.X && Y == other.Y && Z == other.Z;
}

// ReSharper disable InconsistentlySynchronizedField
internal class World {

    private readonly ConcurrentDictionary<ChunkPos, Chunk> _chunks = new();
    private readonly ConcurrentQueue<Chunk> _buildQueue = new();

    private readonly HashSet<ChunkPos> _processingChunks = [];
    private volatile int _activeTaskCount;

    private const int ViewDistance = 16;
    private const int WorldHeightChunks = 16;

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
        var cy = y >> 4;
        var cz = z >> 4;

        if (!_chunks.TryGetValue(new ChunkPos(cx, cy, cz), out var chunk)) return new Block();

        var lx = x & 15;
        var ly = y & 15;
        var lz = z & 15;

        return chunk.GetBlock(lx, ly, lz);
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

    public void SetBlock(int x, int y, int z, byte blockId) {

        var cx = x >> 4;
        var cy = y >> 4;
        var cz = z >> 4;
        var lx = x & 15;
        var ly = y & 15;
        var lz = z & 15;

        if (_chunks.TryGetValue(new ChunkPos(cx, cy, cz), out var chunk)) {

            chunk.SetBlock(lx, ly, lz, new Block(blockId));

            RebuildChunk(chunk);

            if (lx == 0) RebuildChunkAt(cx - 1, cy, cz);
            if (lx == 15) RebuildChunkAt(cx + 1, cy, cz);
            if (ly == 0) RebuildChunkAt(cx, cy - 1, cz);
            if (ly == 15) RebuildChunkAt(cx, cy + 1, cz);
            if (lz == 0) RebuildChunkAt(cx, cy, cz - 1);
            if (lz == 15) RebuildChunkAt(cx, cy, cz + 1);
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

    public struct RaycastResult {
        public bool Hit;
        public int X, Y, Z;
        public int FaceX, FaceY, FaceZ;
        public byte BlockId;
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

    public void Update(Vector3 cameraPos) {

        var camCx = (int)Math.Floor(cameraPos.X / 16);
        var camCz = (int)Math.Floor(cameraPos.Z / 16);

        _realTimeCamX = camCx;
        _realTimeCamZ = camCz;

        var maxTasks = Math.Max(1, Environment.ProcessorCount - 2);

        if (_activeTaskCount < maxTasks) {

            var camCy = (int)Math.Floor(cameraPos.Y / 16);

            Span<int> yOffsets = stackalloc int[WorldHeightChunks];
            for (var i = 0; i < WorldHeightChunks; i++) yOffsets[i] = i;

            foreach (var offset in ScanOffsets) {

                if (_activeTaskCount >= maxTasks) break;

                var x = camCx + offset.X;
                var z = camCz + offset.Z;

                for (var i = 0; i < WorldHeightChunks; i++) {

                    var dy = (i % 2 == 0) ? (i / 2) : -(i / 2 + 1);
                    var y = camCy + dy;

                    if (y is < 0 or >= WorldHeightChunks) continue;

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

                                if (distSq > (ViewDistance + 4) * (ViewDistance + 4)) return;

                                var chunk = new Chunk(pos.X, pos.Y, pos.Z);
                                chunk.Generate();

                                distSq = (x - _realTimeCamX) * (x - _realTimeCamX) + (z - _realTimeCamZ) * (z - _realTimeCamZ);

                                if (distSq > (ViewDistance + 4) * (ViewDistance + 4)) {

                                    _chunks.TryRemove(pos, out _);
                                    chunk.Dispose();

                                    return;
                                }

                                if (_chunks.TryAdd(pos, chunk)) {

                                    var nx = GetChunk(pos.X - 1, pos.Y, pos.Z);
                                    var px = GetChunk(pos.X + 1, pos.Y, pos.Z);
                                    var ny = GetChunk(pos.X, pos.Y - 1, pos.Z);
                                    var py = GetChunk(pos.X, pos.Y + 1, pos.Z);
                                    var nz = GetChunk(pos.X, pos.Y, pos.Z - 1);
                                    var pz = GetChunk(pos.X, pos.Y, pos.Z + 1);

                                    chunk.BuildArrays(nx, px, ny, py, nz, pz);
                                    _buildQueue.Enqueue(chunk);

                                    UpdateNeighbors(pos);

                                } else {

                                    chunk.Dispose();
                                }

                            } catch (Exception) {

                                // ignore 

                            } finally {

                                lock (_processingChunks) _processingChunks.Remove(pos);
                                Interlocked.Decrement(ref _activeTaskCount);
                            }
                        }
                    );

                    break;
                }
            }
        }

        var timer = System.Diagnostics.Stopwatch.StartNew();

        while (_buildQueue.TryDequeue(out var readyChunk)) {

            var distSq = (readyChunk.X - camCx) * (readyChunk.X - camCx) + (readyChunk.Z - camCz) * (readyChunk.Z - camCz);

            if (distSq > (ViewDistance + 2) * (ViewDistance + 2)) {

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

            if (timer.Elapsed.TotalMilliseconds > 3.0) break;
        }

        var chunksToRemove = new List<ChunkPos>();
        var count = 0;

        foreach (var kvp in _chunks) {

            float dx = kvp.Key.X - camCx;
            float dz = kvp.Key.Z - camCz;

            if (!((dx * dx + dz * dz) > (ViewDistance + 4) * (ViewDistance + 4))) continue;

            chunksToRemove.Add(kvp.Key);
            count++;

            if (count >= 30) break;
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

        if (_frameCounter++ % 15 == 0) {

            lock (_renderLock) {

                _renderList.Sort((a, b) => {

                        var da = (a.X * 16 - camPos.X) * (a.X * 16 - camPos.X) + (a.Z * 16 - camPos.Z) * (a.Z * 16 - camPos.Z);
                        var db = (b.X * 16 - camPos.X) * (b.X * 16 - camPos.X) + (b.Z * 16 - camPos.Z) * (b.Z * 16 - camPos.Z);

                        return da.CompareTo(db);
                    }
                );
            }
        }

        for (var i = 0; i < count; i++) {

            var chunk = _renderList[i];

            var cx = chunk.X * 16 + 8;
            var cy = chunk.Y * 16 + 8;
            var cz = chunk.Z * 16 + 8;

            var dx = cx - camPos.X;
            var dy = cy - camPos.Y;
            var dz = cz - camPos.Z;

            var distSq = dx * dx + dy * dy + dz * dz;

            if (distSq > (ViewDistance * 16 + 32) * (ViewDistance * 16 + 32)) continue;

            var dist = Math.Sqrt(distSq);

            var dirX = dx / (float)dist;
            var dirY = dy / (float)dist;
            var dirZ = dz / (float)dist;

            var dot = camDir.X * dirX + camDir.Y * dirY + camDir.Z * dirZ;

            if (dot < 0.3f && dist > 32) continue;

            foreach (var mesh in chunk.Meshes) {

                DrawMesh(mesh, material, Raymath.MatrixTranslate(chunk.X * 16, chunk.Y * 16, chunk.Z * 16));
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