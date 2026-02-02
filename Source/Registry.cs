using System.Text.Json;
using Raylib_cs;
using static Raylib_cs.Raylib;

internal static class Registry {

    public static Texture2D AtlasTexture;

    private static readonly BlockInfo[] Blocks = new BlockInfo[256];
    private static readonly Dictionary<string, byte> BlockIdMap = [];

    private static readonly Dictionary<string, UvInfo> TextureMap = [];

    // 0:North(Z-), 1:East(X+), 2:South(Z+), 3:West(X-), 4:Up(Y+), 5:Down(Y-)
    private static readonly UvInfo[][] BlockFaceUvs = new UvInfo[256][];

    public static void Initialize(string projectRoot) {

        // 0 is air
        Blocks[0] = new BlockInfo { Name = "air", Solid = false, Opaque = false, Id = 0 };
        BlockIdMap["air"] = 0;
        BlockFaceUvs[0] = new UvInfo[6];

        var blocksDir = Path.Combine(projectRoot, "Assets/Blocks");
        var modelsDir = Path.Combine(projectRoot, "Assets/Models");

        // Find all blocks and required textures
        var blockFiles = Directory.GetFiles(blocksDir, "*.json");
        byte nextId = 1;

        var texturePaths = new HashSet<string>();
        var loadedBlocks = new List<BlockInfo>();

        foreach (var file in blockFiles) {

            try {

                var json = File.ReadAllText(file);
                var blockDef = JsonSerializer.Deserialize<BlockJson>(json);

                if (blockDef == null) continue;

                var name = Path.GetFileNameWithoutExtension(file); // Case-sensitive from file name

                // Load Model
                var modelPath = Path.Combine(modelsDir, blockDef.Model + ".json");
                ModelJson? model = null;

                if (File.Exists(modelPath)) {
                    var modelJson = File.ReadAllText(modelPath);
                    model = JsonSerializer.Deserialize<ModelJson>(modelJson);

                    // Recursive Move Up Parent Chain to collect textures and elements
                    var currentModel = model;
                    var inheritanceChain = new List<ModelJson>();
                    var depth = 0;

                    while (currentModel != null && depth < 10) {

                        inheritanceChain.Add(currentModel);

                        if (!string.IsNullOrEmpty(currentModel.Parent)) {

                            // Parent path is relative to Assets/ (e.g. "Models/block" or "block/cube_all")
                            var parentPath = Path.Combine(projectRoot, "Assets", currentModel.Parent + ".json");

                            if (File.Exists(parentPath)) {

                                var pJson = File.ReadAllText(parentPath);
                                currentModel = JsonSerializer.Deserialize<ModelJson>(pJson);

                            } else {

                                // Try in Models/ if not starting with Models/
                                parentPath = Path.Combine(modelsDir, currentModel.Parent + ".json");

                                if (File.Exists(parentPath)) {

                                    var pJson = File.ReadAllText(parentPath);
                                    currentModel = JsonSerializer.Deserialize<ModelJson>(pJson);

                                } else {

                                    currentModel = null;
                                }
                            }
                        } else {

                            currentModel = null;
                        }

                        depth++;
                    }

                    // We iterate from Parent (Base) to Child (Top) to build final state.
                    inheritanceChain.Reverse(); // Now [Base, ..., Child]

                    var finalTextures = new Dictionary<string, string>();
                    var finalElements = new List<ModelElementJson>();

                    foreach (var m in inheritanceChain) {

                        foreach (var kvp in m.Textures) finalTextures[kvp.Key] = kvp.Value;

                        // If child has no elements, it inherits parent's elements.
                        if (m.Elements.Count > 0) finalElements = m.Elements;
                    }

                    // Update the model on the block to reflect the flattened data
                    if (model != null) {

                        model.Textures = finalTextures;
                        model.Elements = finalElements;

                        // Collect textures for Atlas
                        foreach (var tex in model.Textures.Values.Where(tex => !string.IsNullOrEmpty(tex) && !tex.StartsWith('#'))) {

                            texturePaths.Add(tex);
                        }
                    }
                }

                var lum = new Color((int)Math.Clamp(blockDef.Luminance[0] * 255, 0, 255), (int)Math.Clamp(blockDef.Luminance[1] * 255, 0, 255), (int)Math.Clamp(blockDef.Luminance[2] * 255, 0, 255), 255);

                var info = new BlockInfo {
                    Id = nextId++,
                    Name = name,
                    Solid = blockDef.Solid,
                    Opaque = blockDef.Opaque,
                    Luminance = lum,
                    Model = model,
                    Facing = Enum.TryParse<FacingMode>(blockDef.Facing, true, out var facing) ? facing : FacingMode.Fixed,
                    DefaultYaw = blockDef.Yaw
                };

                loadedBlocks.Add(info);

            } catch (Exception) {
                // Ignore
            }
        }

        // Build Atlas
        GenerateAtlas(projectRoot, texturePaths);

        // Register Blocks and Bake UVs
        foreach (var b in loadedBlocks.TakeWhile(b => b.Id < 255)) {

            Blocks[b.Id] = b;
            BlockIdMap[b.Name] = b.Id;
            BlockIdMap[b.Name.ToLower()] = b.Id; // Register lower case too

            // 0:North(-Z), 1:East(+X), 2:South(+Z), 3:West(-X), 4:Up(+Y), 5:Down(-Y)
            BlockFaceUvs[b.Id] = new UvInfo[6];

            if (b.Model is { Elements.Count: > 0 }) {

                // Use first element for simple blocks
                var el = b.Model.Elements[0];

                BlockFaceUvs[b.Id][0] = ResolveFaceUv(b.Model, el.Faces.GetValueOrDefault("north"));
                BlockFaceUvs[b.Id][1] = ResolveFaceUv(b.Model, el.Faces.GetValueOrDefault("east"));
                BlockFaceUvs[b.Id][2] = ResolveFaceUv(b.Model, el.Faces.GetValueOrDefault("south"));
                BlockFaceUvs[b.Id][3] = ResolveFaceUv(b.Model, el.Faces.GetValueOrDefault("west"));
                BlockFaceUvs[b.Id][4] = ResolveFaceUv(b.Model, el.Faces.GetValueOrDefault("up"));
                BlockFaceUvs[b.Id][5] = ResolveFaceUv(b.Model, el.Faces.GetValueOrDefault("down"));
            }
        }
    }

    public static UvInfo ResolveFaceUv(ModelJson model, ModelFaceJson? face) {

        if (face == null) return new UvInfo();

        // Resolve texture reference (e.g., "#1" -> "Textures/GrassSide")
        var texRef = face.Texture;

        while (texRef.StartsWith('#')) {

            var key = texRef[1..];

            if (!model.Textures.TryGetValue(key, out var val)) break;

            texRef = val;
        }

        if (!TextureMap.TryGetValue(texRef, out var atlasUv)) return new UvInfo();

        // Face UV is in pixels 0-16. 
        // Maps the sub-region to the Atlas UV region.
        // AtlasUv provides X, Y, W, H in 0-1 range.

        var subX = face.Uv[0] / 16.0f;
        var subY = face.Uv[1] / 16.0f;
        var subW = (face.Uv[2] - face.Uv[0]) / 16.0f;
        var subH = (face.Uv[3] - face.Uv[1]) / 16.0f;

        return new UvInfo {
            X = atlasUv.X + subX * atlasUv.Width,
            Y = atlasUv.Y + subY * atlasUv.Height,
            Width = subW * atlasUv.Width,
            Height = subH * atlasUv.Height,
            Rotation = face.Rotation
        };
    }

    private static void GenerateAtlas(string root, HashSet<string> distinctTextures) {

        var images = new List<(string Name, Image Img)>();
        var totalWidth = 0;
        var maxHeight = 0;
        const int spacing = 0; // Padding handled manually or not needed if exact mapping

        foreach (var texPath in distinctTextures) {

            var fullPath = Path.Combine(root, "Assets", texPath + ".png");

            if (!File.Exists(fullPath)) continue;

            var img = LoadImage(fullPath);
            images.Add((texPath, img));

            totalWidth += img.Width + spacing;
            if (img.Height > maxHeight) maxHeight = img.Height;
        }

        if (totalWidth == 0) totalWidth = 16;
        if (maxHeight == 0) maxHeight = 16;

        var atlasImg = GenImageColor(totalWidth, maxHeight, new Color(0, 0, 0, 0));
        var currentX = 0;

        foreach (var (name, img) in images) {

            ImageDraw(ref atlasImg, img, new Rectangle(0, 0, img.Width, img.Height), new Rectangle(currentX, 0, img.Width, img.Height), Color.White);

            TextureMap[name] = new UvInfo { X = (float)currentX / totalWidth, Y = 0, Width = (float)img.Width / totalWidth, Height = (float)img.Height / maxHeight };

            currentX += img.Width + spacing;
            UnloadImage(img);
        }

        AtlasTexture = LoadTextureFromImage(atlasImg);
        GenTextureMipmaps(ref AtlasTexture);
        SetTextureFilter(AtlasTexture, TextureFilter.Point);
        SetTextureWrap(AtlasTexture, TextureWrap.Clamp);
        UnloadImage(atlasImg);
    }

    public static byte GetId(string name) {

        if (BlockIdMap.TryGetValue(name, out var id) || BlockIdMap.TryGetValue(name.ToLower(), out id)) return id;

        return 0;
    }

    // Get UV for specific face: 0:N (-Z), 1:E (+X), 2:S (+Z), 3:W (-X), 4:U (+Y), 5:D (-Y)
    public static UvInfo GetFaceUv(byte id, int faceIndex = 4) {

        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (id == 0 || BlockFaceUvs[id] == null || faceIndex < 0 || faceIndex > 5) return new UvInfo();

        return BlockFaceUvs[id][faceIndex];
    }

    public static Color GetLuminance(byte id) => Blocks[id].Luminance;
    public static bool IsSolid(byte id) => Blocks[id].Solid;
    public static bool IsOpaque(byte id) => Blocks[id].Opaque;
    public static ModelJson? GetModel(byte id) => Blocks[id].Model;
    public static FacingMode GetFacing(byte id) => Blocks[id].Facing;
    public static int GetYawStep(byte id) => Blocks[id].DefaultYaw;
}