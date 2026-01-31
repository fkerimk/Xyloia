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

        var physicsWorld = new Jitter2.World();
        var world = new World(physicsWorld);

        var cam = new Camera3D {
            FovY = 90,
            Up = Vector3.UnitY,
            Target = new Vector3(0, 40, 0),
            Position = new Vector3(20, 50, 20),
            Projection = CameraProjection.Perspective,
        };

        var controller = new PlayerController(physicsWorld, cam.Position);
        var freeCamMode = false;

        var initialDir = Vector3.Normalize(cam.Target - cam.Position);
        var pitch = (float)Math.Asin(initialDir.Y);
        var yaw = (float)Math.Atan2(initialDir.Z, initialDir.X);

        const float physicsStep = 1.0f / 60.0f;
        double accumulator = 0;

        DisableCursor();
        
        while (!WindowShouldClose()) {

            var dt = GetFrameTime();

            // Fixed Physics Step
            accumulator += dt;

            while (accumulator >= physicsStep) {

                if (!freeCamMode) controller.FixedUpdate();

                physicsWorld.Step(physicsStep);
                accumulator -= physicsStep;
            }

            world.Update(cam.Position);

            // Toggle camera
            if (IsKeyPressed(KeyboardKey.F)) {

                freeCamMode = !freeCamMode;

                if (freeCamMode) {

                    // Switch to free cam
                    cam.Position = controller.CameraPosition;
                    var direction = controller.GetCameraTarget() - cam.Position;
                    direction = Vector3.Normalize(direction);
                    pitch = (float)Math.Asin(direction.Y);
                    yaw = (float)Math.Atan2(direction.Z, direction.X);

                } else {

                    // Switch to character
                    controller.Body.Position = new Jitter2.LinearMath.JVector(cam.Position.X, cam.Position.Y - 0.8f, cam.Position.Z);
                    controller.SetRotation(yaw, pitch);
                    controller.ResetInput();
                }
            }

            if (freeCamMode) {

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

            } else {

                controller.FrameUpdate();
                cam.Position = controller.CameraPosition;
                cam.Target = controller.GetCameraTarget();
            }

            BeginDrawing();
            ClearBackground(new Color(135, 206, 235, 255));

            BeginMode3D(cam);
            world.Render(cam, material);
            EndMode3D();

            DrawFPS(10, 10);
            DrawText(freeCamMode ? "[F] Free Cam" : "[F] Character", 10, 40, 20, Color.Black);

            EndDrawing();
        }

        world.Unload();
        CloseWindow();

        return 0;
    }
}