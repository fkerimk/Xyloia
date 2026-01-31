using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

internal static class Program {

    public static int Main() {

        SetTraceLogLevel(TraceLogLevel.Warning);
        InitWindow(1600, 900, "Xyloia");
        
        SetTargetFPS(GetMonitorRefreshRate(GetCurrentMonitor()));

        Registry.Initialize("Assets/Textures/Grass.png", "Assets/Textures/Dirt.png", "Assets/Textures/Sand.png");

        var material = LoadMaterialDefault();
        SetMaterialTexture(ref material, MaterialMapIndex.Albedo, Registry.AtlasTexture);

        var world = new World();

        var cam = new Camera3D {
            FovY = 90,
            Up = Vector3.UnitY,
            Target = new Vector3(0, 40, 0),
            Position = new Vector3(20, 50, 20),
            Projection = CameraProjection.Perspective,
        };

        var initialDir = Vector3.Normalize(cam.Target - cam.Position);
        var pitch = (float)Math.Asin(initialDir.Y);
        var yaw = (float)Math.Atan2(initialDir.Z, initialDir.X);

        while (!WindowShouldClose()) {

            var dt = GetFrameTime();

            world.Update(cam.Position);

            if (IsMouseButtonPressed(MouseButton.Right)) DisableCursor();
            if (IsMouseButtonReleased(MouseButton.Right)) EnableCursor();

            if (IsMouseButtonDown(MouseButton.Right)) {

                var md = GetMouseDelta();
                yaw += md.X * 0.005f;
                pitch -= md.Y * 0.005f;
                pitch = Math.Clamp(pitch, -1.5f, 1.5f);

                var dir = new Vector3((float)(Math.Cos(yaw) * Math.Cos(pitch)), (float)Math.Sin(pitch), (float)(Math.Sin(yaw) * Math.Cos(pitch)));
                var fwd = Vector3.Normalize(dir with { Y = 0 });
                var rgt = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitY));

                var speed = IsKeyDown(KeyboardKey.LeftShift) ? 120f : 40f;
                if (IsKeyDown(KeyboardKey.W)) cam.Position += fwd * speed * dt;
                if (IsKeyDown(KeyboardKey.S)) cam.Position -= fwd * speed * dt;
                if (IsKeyDown(KeyboardKey.A)) cam.Position -= rgt * speed * dt;
                if (IsKeyDown(KeyboardKey.D)) cam.Position += rgt * speed * dt;
                if (IsKeyDown(KeyboardKey.E)) cam.Position += Vector3.UnitY * speed * dt;
                if (IsKeyDown(KeyboardKey.Q)) cam.Position -= Vector3.UnitY * speed * dt;
                cam.Target = cam.Position + dir;
            }

            BeginDrawing();
            ClearBackground(new Color(135, 206, 235, 255));

            BeginMode3D(cam);
            world.Render(cam, material);
            EndMode3D();

            DrawFPS(10, 10);
            EndDrawing();
        }

        world.Unload();
        CloseWindow();

        return 0;
    }
}