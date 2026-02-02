internal readonly struct Block(byte id = 0, byte data = 0) {

    public readonly byte Id = id;
    public readonly byte Data = data;
    public bool Solid => Registry.IsSolid(Id);
    public bool Opaque => Registry.IsOpaque(Id);
}