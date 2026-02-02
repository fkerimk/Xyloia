using Raylib_cs;

internal class BlockInfo {

    public byte Id;
    public string Name = "";
    public bool Solid;
    public bool Opaque;
    public Color Luminance;
    public ModelJson? Model;

    public FacingMode Facing = FacingMode.Fixed;
    public int DefaultYaw = 0;
    public string? ConnectRaw;
    public readonly HashSet<byte> ConnectIds = [];
}

internal enum FacingMode {

    Fixed, Yaw, Rotate
}