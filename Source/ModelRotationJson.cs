using System.Text.Json.Serialization;

// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

internal class ModelRotationJson {

    [JsonPropertyName("origin")] public float[] Origin { get; set; } = [8, 8, 8];
    [JsonPropertyName("axis")] public string Axis { get; set; } = "y";
    [JsonPropertyName("angle")] public float Angle { get; set; }
    [JsonPropertyName("rescale")] public bool Rescale { get; set; }
}