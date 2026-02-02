using System.Text.Json.Serialization;

internal class ModelJson {

    [JsonPropertyName("parent")] public string? Parent { get; init; }
    [JsonPropertyName("textures")] public Dictionary<string, string> Textures { get; set; } = [];
    [JsonPropertyName("elements")] public List<ModelElementJson> Elements { get; set; } = [];
}