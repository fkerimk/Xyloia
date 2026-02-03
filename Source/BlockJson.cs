internal class BlockJson {

    public string Model { get; init; } = "";
    public float[] Luminance { get; init; } = [0, 0, 0];
    public bool Solid { get; init; } = true;
    public bool Opaque { get; init; } = true;
    public string Facing { get; init; } = "Fixed";
    public int Yaw { get; init; } = 90;
    public string Connect { get; init; } = "";
    public bool NotSimple { get; init; }
}