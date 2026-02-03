using System.Text.Json;

// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable CollectionNeverUpdated.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

internal static class WorldGenConfig {

    public static Config Data { get; private set; } = new();

    public class Config {

        public GeneralSettings General { get; init; } = new();
        public TerrainSettings Terrain { get; init; } = new();
        public CaveSettings Caves { get; init; } = new();
        public List<LayerSettings> Layers { get; init; } = [];
        public BedrockSettings Bedrock { get; init; } = new();
        public BiomeConfig Biomes { get; init; } = new();
    }

    public class BiomeConfig {

        public double Scale { get; set; } = 0.002;
        public int BlendRadius { get; set; } = 4; // Not used yet but good for future
        public List<Biome> List { get; set; } = [];
    }

    public class Biome {

        public string Name { get; set; } = "Biome";
        public double Threshold { get; set; } // Noise value > This = this biome (sorted desc)

        // Terrain Overrides
        public int BaseHeight { get; set; }
        public int HeightAmplitude { get; set; }

        // Blocks
        public string SurfaceBlock { get; set; } = "Grass";
        public byte SurfaceBlockId { get; set; }

        public string SubSurfaceBlock { get; set; } = "Dirt";
        public byte SubSurfaceBlockId { get; set; }

        public string TransitionBlock { get; set; } = "";
        public byte TransitionBlockId { get; set; }
    }

    public class GeneralSettings {

        public int Seed { get; set; }
        public int WaterLevel { get; set; }
        public string WaterBlock { get; set; } = "Water";
    }

    public class TerrainSettings {

        public double Scale { get; set; } = 0.005;
        public int BaseHeight { get; set; } = 70;
        public int HeightAmplitude { get; set; } = 40;
        public int Octaves { get; set; } = 5;
        public double Persistence { get; set; } = 0.5;
        public double Lacunarity { get; set; } = 2.0;
    }

    public class CaveSettings {

        public bool Enabled { get; set; } = true;
        public double ScaleX { get; set; } = 0.015;
        public double ScaleY { get; set; } = 0.025;
        public double ScaleZ { get; set; } = 0.015;
        public double Threshold { get; set; } = 0.35;
        public int Octaves { get; set; } = 2; // For rougher caves
    }

    public class BedrockSettings {

        public string Block { get; set; } = "DeepSlate";
        public byte BlockId { get; set; } // Cache
        public int MinHeight { get; set; } = 1;
        public int MaxHeight { get; set; } = 4;
        public double Scale { get; set; } = 0.1;
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

            if (!string.IsNullOrEmpty(Data.Bedrock.Block)) Data.Bedrock.BlockId = Registry.GetId(Data.Bedrock.Block);

            foreach (var biome in Data.Biomes.List) {

                biome.SurfaceBlockId = Registry.GetId(biome.SurfaceBlock);
                biome.SubSurfaceBlockId = Registry.GetId(biome.SubSurfaceBlock);
                if (!string.IsNullOrEmpty(biome.TransitionBlock)) biome.TransitionBlockId = Registry.GetId(biome.TransitionBlock);
            }

        } catch {
            // Ignore
        }
    }
}