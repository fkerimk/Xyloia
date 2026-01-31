internal struct Block(byte id = 0) {
    
    public readonly byte Id = id;
    public readonly bool Solid = id != 0;
}