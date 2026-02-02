internal readonly struct Block(byte id = 0) {
    
    public readonly byte Id = id;
    public bool Solid => Registry.IsSolid(Id);
    public bool Opaque => Registry.IsOpaque(Id);
}