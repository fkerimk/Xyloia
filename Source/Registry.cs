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
    private static readonly Color[] Luminance = new Color[256];
    private static readonly bool[] Translucent = new bool[256];
    public static Texture2D AtlasTexture;

    public static void Initialize(params string[] texturePaths) {

        if (texturePaths.Length == 0) return;

        var images = new List<Image>();

        var totalWidth = 0;
        var maxHeight = 0;

        const int spacing = 2;

        foreach (var path in texturePaths) {

            var img = LoadImage(path);

            images.Add(img);
            totalWidth += img.Width + spacing;

            if (img.Height > maxHeight) maxHeight = img.Height;
        }

        var atlasImg = GenImageColor(totalWidth, maxHeight, new Color(0, 0, 0, 255));
        var currentX = 1; // Start with offset for left dilation

        byte nextId = 1;

        for (var i = 0; i < images.Count; i++) {

            var img = images[i];
            var name = Path.GetFileNameWithoutExtension(texturePaths[i]).ToLower();

            // Border dilation
            ImageDraw(ref atlasImg, img, new Rectangle(0, 0, img.Width, img.Height), new Rectangle(currentX - 1, 0, img.Width, img.Height), Color.White);
            ImageDraw(ref atlasImg, img, new Rectangle(0, 0, img.Width, img.Height), new Rectangle(currentX + 1, 0, img.Width, img.Height), Color.White);
            ImageDraw(ref atlasImg, img, new Rectangle(0, 0, img.Width, img.Height), new Rectangle(currentX, -1, img.Width, img.Height), Color.White);
            ImageDraw(ref atlasImg, img, new Rectangle(0, 0, img.Width, img.Height), new Rectangle(currentX, 1, img.Width, img.Height), Color.White);

            // Draw actual image
            ImageDraw(ref atlasImg, img, new Rectangle(0, 0, img.Width, img.Height), new Rectangle(currentX, 0, img.Width, img.Height), Color.White);

            var id = nextId++;
            BlockIds[name] = id;

            Uvv[id] = new UvInfo { X = (float)currentX / totalWidth, Y = 0, Width = (float)img.Width / totalWidth, Height = (float)img.Height / maxHeight };

            Translucent[id] = false;

            currentX += img.Width + spacing;
            UnloadImage(img);
        }

        Uvv[0] = new UvInfo { X = 0, Y = 0, Width = 0, Height = 0 };
        Translucent[0] = true;

        AtlasTexture = LoadTextureFromImage(atlasImg);

        GenTextureMipmaps(ref AtlasTexture);
        SetTextureFilter(AtlasTexture, TextureFilter.Point);
        SetTextureWrap(AtlasTexture, TextureWrap.Clamp);
        UnloadImage(atlasImg);
    }

    public static UvInfo GetUv(byte id) => Uvv[id];

    public static byte GetId(string name) => BlockIds.GetValueOrDefault(name, (byte)0);

    public static Color GetLuminance(byte id) => Luminance[id];
    public static void SetLuminance(byte id, Color value) => Luminance[id] = value;

    public static bool IsTranslucent(byte id) => Translucent[id];
}