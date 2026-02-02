using System.Text.Json.Serialization;

// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable CollectionNeverUpdated.Global
internal class ModelElementJson {

    [JsonPropertyName("from")] public float[] From { get; set; } = [];
    [JsonPropertyName("to")] public float[] To { get; set; } = [];
    [JsonPropertyName("faces")] public Dictionary<string, ModelFaceJson> Faces { get; set; } = [];
}