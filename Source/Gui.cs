using System.Numerics;
using System.Text.Json;
using Raylib_cs;
using static Raylib_cs.Raylib;

// ReSharper disable CollectionNeverUpdated.Global
// ReSharper disable ClassNeverInstantiated.Global
#pragma warning disable CS0649

internal class Gui {

    public class Definition {

        public List<Element> Elements { get; init; } = [];
        public bool FreeCursor { get; init; }
        public bool InGame { get; init; }
        public bool PauseGame { get; init; }
    }

    public class Element {

        public string Type { get; set; } = ""; // Text, Button, VerticalLayout
        public string Anchor { get; set; } = "Top, Left";
        public string Offset { get; set; } = "0, 0";
        public string Label { get; set; } = "";
        public string Size { get; set; } = "20";
        public string Action { get; set; } = "";
        public string Spacing { get; set; } = "0";
        public List<Element> Items { get; set; } = [];

        // Runtime props
        public Vector2 CalcPos;
        public Vector2 CalcSize;
        public bool IsHovered;
        public bool IsClicked;
    }

    private readonly List<Definition> _stack = [];
    private readonly Dictionary<string, Action<string>> _actions = new();
    private readonly Font _font = GetFontDefault();

    public bool HasMenuOpen => _stack.Count > 0;
    public bool IsCursorFree => _stack.Count > 0 && _stack.Any(d => d.FreeCursor);
    public bool IsPaused => _stack.Count > 0 && _stack.Any(d => d.PauseGame);
    public bool IsInteractionConsumed { get; private set; }

    public void Open(string path) {

        if (!path.StartsWith("Assets/")) path = "Assets/" + path;
        if (!path.EndsWith(".json")) path += ".json";

        if (!File.Exists(path)) return;

        var json = File.ReadAllText(path);

        try {

            var def = JsonSerializer.Deserialize<Definition>(json);
            if (def != null) _stack.Add(def); // Add to top (end of list)

        } catch {

            // Ignore
        }
    }

    public void Close() {

        if (_stack.Count > 0) _stack.RemoveAt(_stack.Count - 1);
    }

    public void CloseAll() => _stack.Clear();

    public void RegisterAction(string name, Action action) => _actions[name] = _ => action();

    public void RegisterAction(string name, Action<string> action) => _actions[name] = action;

    public void Update() {

        IsInteractionConsumed = false;

        if (_stack.Count == 0) return;

        // Only interact with the top menu
        var top = _stack[^1];

        var w = GetScreenWidth();
        var h = GetScreenHeight();
        var mouse = GetMousePosition();
        var click = IsMouseButtonPressed(MouseButton.Left);

        // Recalculate layout for top menu (in case of window resize)
        foreach (var el in top.Elements) {

            CalculateLayout(el, Vector2.Zero, w, h);
            UpdateInteraction(el, mouse, click);
        }
    }

    public void Draw() {

        // Draw all menus in stack (bottom to top) to allow overlays
        foreach (var el in _stack.SelectMany(def => def.Elements)) {

            DrawElement(el);
        }
    }

    private static Vector2 ParseOffset(string offset) {

        var parts = offset.Split(',');

        if (parts.Length < 2) return Vector2.Zero;
        if (float.TryParse(parts[0], out var x) && float.TryParse(parts[1], out var y)) return new Vector2(x, y);

        return Vector2.Zero;
    }

    private void CalculateLayout(Element el, Vector2 parentPos, float areaW, float areaH) {

        var off = ParseOffset(el.Offset);
        var fontSize = int.TryParse(el.Size, out var s) ? s : 20;

        // Measure
        var size = Vector2.Zero;

        switch (el.Type) {

            case "Text":
            case "Button": {

                size = MeasureTextEx(_font, el.Label, fontSize, 1);

                if (el.Type == "Button") {

                    size.X += 20; // Padding
                    size.Y += 10;
                }

                break;
            }

            case "VerticalLayout": {

                float maxW = 0;
                float totalH = 0;
                var spacing = float.TryParse(el.Spacing, out var sp) ? sp : 0;

                foreach (var item in el.Items) {

                    var iSize = int.TryParse(item.Size, out var isz) ? isz : 20;
                    var iDim = MeasureTextEx(_font, item.Label, iSize, 1);

                    if (item.Type == "Button") {

                        iDim.X += 20;
                        iDim.Y += 10;
                    }

                    item.CalcSize = iDim;
                    if (iDim.X > maxW) maxW = iDim.X;
                    totalH += iDim.Y + spacing;
                }

                if (el.Items.Count > 0) totalH -= spacing;
                size = new Vector2(maxW, totalH);

                break;
            }
        }

        el.CalcSize = size;

        // Position
        float x, y;

        var anchor = el.Anchor.ToLower();

        if (anchor.Contains("left"))
            x = OffsetX(0); // Left edge
        else if (anchor.Contains("right"))
            x = OffsetX(areaW - size.X);
        else
            x = OffsetX(areaW / 2 - size.X / 2); // Center X

        if (anchor.Contains("top"))
            y = OffsetY(0);
        else if (anchor.Contains("bottom"))
            y = OffsetY(areaH - size.Y);
        else
            y = OffsetY(areaH / 2 - size.Y / 2); // Center Y

        el.CalcPos = parentPos + new Vector2(x, y);

        // Sub-layout
        if (el.Type == "VerticalLayout") {

            var curY = el.CalcPos.Y;
            var spacing = float.TryParse(el.Spacing, out var sp) ? sp : 0;

            foreach (var item in el.Items) {
                var itemX = el.CalcPos.X; // Align Left in layout
                item.CalcPos = new Vector2(itemX, curY);
                curY += item.CalcSize.Y + spacing;
            }
        }

        return;

        float OffsetX(float origin) => origin + (anchor.Contains("right") ? -off.X : off.X);
        float OffsetY(float origin) => origin + (anchor.Contains("bottom") ? -off.Y : off.Y);
    }

    private void UpdateInteraction(Element el, Vector2 mouse, bool click) {

        var rect = new Rectangle(el.CalcPos.X, el.CalcPos.Y, el.CalcSize.X, el.CalcSize.Y);
        el.IsHovered = CheckCollisionPointRec(mouse, rect);

        if (el.IsHovered && click && el.Type == "Button" && !string.IsNullOrEmpty(el.Action)) {

            IsInteractionConsumed = true;

            // Support multiple actions split by comma
            var actions = el.Action.Split(',');

            foreach (var rawStr in actions) {

                var str = rawStr.Trim();
                var args = "";
                var name = str;

                // Parse arguments "Action(Arg)"
                var pIdx = str.IndexOf('(');

                if (pIdx > 0 && str.EndsWith(')')) {
                    
                    name = str[..pIdx];
                    args = str.Substring(pIdx + 1, str.Length - pIdx - 2);
                }

                if (_actions.TryGetValue(name, out var act)) act.Invoke(args);
            }
        }

        foreach (var item in el.Items) UpdateInteraction(item, mouse, click);
    }

    private void DrawElement(Element el) {

        var fontSize = int.TryParse(el.Size, out var s) ? s : 20;

        switch (el.Type) {
            
            case "Text":
                
                // Shadow
                DrawTextEx(_font, el.Label, el.CalcPos + new Vector2(2, 2), fontSize, 1, Color.Black);
                DrawTextEx(_font, el.Label, el.CalcPos, fontSize, 1, Color.White);

                break;

            case "Button": {
                
                var col = el.IsHovered ? new Color(200, 200, 200, 255) : new Color(150, 150, 150, 255);
                DrawRectangleRec(new Rectangle(el.CalcPos.X, el.CalcPos.Y, el.CalcSize.X, el.CalcSize.Y), col);
                DrawRectangleLinesEx(new Rectangle(el.CalcPos.X, el.CalcPos.Y, el.CalcSize.X, el.CalcSize.Y), 2, Color.Black);

                var textSize = MeasureTextEx(_font, el.Label, fontSize, 1);
                var textPos = el.CalcPos + (el.CalcSize - textSize) / 2;

                DrawTextEx(_font, el.Label, textPos, fontSize, 1, Color.Black);

                break;
            }
        }

        foreach (var item in el.Items) DrawElement(item);
    }
}