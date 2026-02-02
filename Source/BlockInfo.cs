using Raylib_cs;

internal class BlockInfo {

    public byte Id;
    public string Name = "";

    public ModelJson? Model;
    public Color Luminance = Color.Blank;
    public bool Solid = true;
    public bool Opaque = true;
    public FacingMode Facing = FacingMode.Fixed;
    public int Yaw = 0;
    public string? Connect;

    public readonly HashSet<byte> ConnectIds = [];
    public bool Full = false;
    public bool Simple = false;
}

internal enum FacingMode {

    Fixed, Yaw, Rotate
}