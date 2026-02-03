internal class BlockJson {

    public BlockModelJson Model { get; init; } = new();
    public BlockRotationJson Rotation { get; init; } = new();
    public bool Solid { get; init; } = true;
    public float[] Luminance { get; init; } = [0, 0, 0];
}

internal class BlockModelJson {

    public string Main { get; init; } = "";
    public bool Opaque { get; init; } = true;
    public bool Simple { get; init; } = true;
    public string Connect { get; init; } = "";
}

internal class BlockRotationJson {

    public string Facing { get; init; } = "Fixed";
    public BlockYawJson Yaw { get; init; } = new();
}

internal class BlockYawJson {

    public int Angle { get; init; } = 90;
}