using System.Text.Json.Serialization;

// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable CollectionNeverUpdated.Global
// ReSharper disable  UnusedAutoPropertyAccessor.Global
internal class ModelFaceJson {

    [JsonPropertyName("texture")] public string Texture { get; set; } = ""; // e.g., "#1" or "#particle"
    [JsonPropertyName("uv")] public float[] Uv { get; set; } = [0, 0, 16, 16];
    [JsonPropertyName("cullface")] public string CullFace { get; set; } = "";
    [JsonPropertyName("rotation")] public int Rotation { get; set; }
}