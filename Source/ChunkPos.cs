internal readonly struct ChunkPos(int x, int y, int z) : IEquatable<ChunkPos> {

    public readonly int X = x, Y = y, Z = z;

    public override bool Equals(object? obj) => obj is ChunkPos p && p.X == X && p.Y == Y && p.Z == Z;

    public override int GetHashCode() => HashCode.Combine(X, Y, Z);

    public bool Equals(ChunkPos other) => X == other.X && Y == other.Y && Z == other.Z;
}