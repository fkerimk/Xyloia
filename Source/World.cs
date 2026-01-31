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
internal class World(Jitter2.World physicsWorld) {

    private readonly ConcurrentDictionary<ChunkPos, Chunk> _chunks = new();
    private readonly ConcurrentQueue<Chunk> _buildQueue = new();

    private readonly HashSet<ChunkPos> _processingChunks = [];
    private readonly HashSet<ChunkPos> _activePhysicsChunks = [];
    private volatile int _activeTaskCount;

    private const int ViewDistance = 16;
    private const int WorldHeightChunks = 16;

    private int _realTimeCamX, _realTimeCamZ;

    private static readonly ChunkPos[] ScanOffsets;
    private static readonly ChunkPos[] PhysicsOffsets;

    static World() {

        var offsets = new List<ChunkPos>();

        for (var x = -ViewDistance; x <= ViewDistance; x++)
        for (var z = -ViewDistance; z <= ViewDistance; z++)
            if (x * x + z * z <= ViewDistance * ViewDistance)
                offsets.Add(new ChunkPos(x, 0, z));

        ScanOffsets = offsets.OrderBy(p => p.X * p.X + p.Z * p.Z).ToArray();

        var physOffsets = new List<ChunkPos>();

        const int physicsRadius = 3;

        for (var x = -physicsRadius; x <= physicsRadius; x++)
        for (var y = -physicsRadius; y <= physicsRadius; y++)
        for (var z = -physicsRadius; z <= physicsRadius; z++)
            if (x * x + y * y + z * z <= physicsRadius * physicsRadius)
                physOffsets.Add(new ChunkPos(x, y, z));

        PhysicsOffsets = physOffsets.ToArray();
    }

    private Chunk? GetChunk(int x, int y, int z) { return _chunks.GetValueOrDefault(new ChunkPos(x, y, z)); }

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

                    removed.Unload(physicsWorld);
                }

                continue;
            }

            if (!readyChunk.IsDirty) continue;

            readyChunk.Upload(physicsWorld);

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
                    chunk.Unload(physicsWorld);
                    if (_activePhysicsChunks.Contains(pos)) _activePhysicsChunks.Remove(pos);
                }
            }
        }

        UpdatePhysics(camCx, (int)Math.Floor(cameraPos.Y / 16), camCz);
    }

    private void UpdatePhysics(int cx, int cy, int cz) {

        var needed = new HashSet<ChunkPos>();

        foreach (var off in PhysicsOffsets) {

            var pos = new ChunkPos(cx + off.X, cy + off.Y, cz + off.Z);

            if (!_chunks.TryGetValue(pos, out var chunk) || chunk.IsDirty) continue;

            needed.Add(pos);

            if (!_activePhysicsChunks.Add(pos)) continue;

            chunk.EnablePhysics(physicsWorld);
        }

        var toRemove = _activePhysicsChunks.Where(pos => !needed.Contains(pos)).ToList();

        foreach (var pos in toRemove) {

            if (_chunks.TryGetValue(pos, out var chunk)) chunk.DisablePhysics(physicsWorld);

            _activePhysicsChunks.Remove(pos);
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

        foreach (var chunk in _chunks.Values) chunk.Unload(physicsWorld);
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