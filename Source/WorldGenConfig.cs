using System.Text.Json;

namespace Xyloia;

// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
public static class WorldGenConfig {

    public static Config Data { get; private set; } = new();

    public class Config {

        public GeneralSettings General { get; init; } = new();
        public TerrainSettings Terrain { get; init; } = new();
        public CaveSettings Caves { get; init; } = new();
        public List<LayerSettings> Layers { get; init; } = [];
    }

    public class GeneralSettings {

        public int Seed { get; set; }
        public int WaterLevel { get; set; }
    }

    public class TerrainSettings {

        public double Scale { get; set; }
        public int BaseHeight { get; set; }
        public int HeightAmplitude { get; set; }
        public double DetailScale { get; set; }
        public int DetailAmplitude { get; set; }
    }

    public class CaveSettings {

        public bool Enabled { get; set; }
        public double ScaleX { get; set; }
        public double ScaleY { get; set; }
        public double ScaleZ { get; set; }
        public double Threshold { get; set; }
    }

    public class LayerSettings {

        public string Name { get; set; } = "";
        public int Depth { get; set; }
        public string Block { get; set; } = "";
        public byte BlockId { get; set; } // Runtime cache
    }

    public static void Load(string projectRoot) {

        var path = Path.Combine(projectRoot, "Assets/Generation/World.json");

        if (!File.Exists(path)) return;

        try {

            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<Config>(json);

            if (config == null) return;

            Data = config;

            // Pre-resolve Block IDs for performance
            foreach (var layer in Data.Layers) layer.BlockId = Registry.GetId(layer.Block);

        } catch {
            // Ignore
        }
    }
}