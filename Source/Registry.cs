using Raylib_cs;
using static Raylib_cs.Raylib;

internal struct UvInfo {

    public float X;
    public float Y;
    public float Width;
    public float Height;
}

internal static class Registry {

    private static readonly Dictionary<string, byte> BlockIds = [];
    private static readonly UvInfo[] Uvv = new UvInfo[256];
    public static Texture2D AtlasTexture;

    public static void Initialize(params string[] texturePaths) {

        if (texturePaths.Length == 0) return;

        var images = new List<Image>();
        var totalWidth = 0;
        var maxHeight = 0;

        foreach (var path in texturePaths) {

            var img = LoadImage(path);
            images.Add(img);
            totalWidth += img.Width;
            if (img.Height > maxHeight) maxHeight = img.Height;
        }

        var atlasImg = GenImageColor(totalWidth, maxHeight, Color.Blank);
        var currentX = 0;

        byte nextId = 1;

        for (var i = 0; i < texturePaths.Length; i++) {

            var img = images[i];
            var name = Path.GetFileNameWithoutExtension(texturePaths[i]).ToLower();

            ImageDraw(ref atlasImg, img, new Rectangle(0, 0, img.Width, img.Height), new Rectangle(currentX, 0, img.Width, img.Height), Color.White);

            var id = nextId++;

            BlockIds[name] = id;
            Uvv[id] = new UvInfo { X = (float)currentX / totalWidth, Y = 0, Width = (float)img.Width / totalWidth, Height = (float)img.Height / maxHeight };

            currentX += img.Width;
            UnloadImage(img);
        }

        Uvv[0] = new UvInfo { X = 0, Y = 0, Width = 0, Height = 0 };

        AtlasTexture = LoadTextureFromImage(atlasImg);

        SetTextureFilter(AtlasTexture, TextureFilter.Point);
        UnloadImage(atlasImg);
    }

    public static UvInfo GetUv(byte id) => Uvv[id];

    public static byte GetId(string name) => BlockIds.GetValueOrDefault(name, (byte)0);
}