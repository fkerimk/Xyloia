using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

internal static class Program {

    public static int Main() {

        SetTraceLogLevel(TraceLogLevel.Warning);

        SetConfigFlags(ConfigFlags.VSyncHint | ConfigFlags.Msaa4xHint);

        InitWindow(1600, 900, "Xyloia");

        SetWindowMonitor(0);

        Registry.Initialize("Assets/Textures/Grass.png", "Assets/Textures/Dirt.png", "Assets/Textures/Sand.png", "Assets/Textures/Glow_Red.png", "Assets/Textures/Glow_Green.png", "Assets/Textures/Glow_Blue.png", "Assets/Textures/Glow_Yellow.png", "Assets/Textures/Glow_Magenta.png");

        Registry.SetLuminance(Registry.GetId("glow_red"), new Color(255, 0, 0, 255));
        Registry.SetLuminance(Registry.GetId("glow_green"), new Color(0, 255, 0, 255));
        Registry.SetLuminance(Registry.GetId("glow_blue"), new Color(0, 0, 255, 255));
        Registry.SetLuminance(Registry.GetId("glow_yellow"), new Color(255, 255, 0, 255));
        Registry.SetLuminance(Registry.GetId("glow_magenta"), new Color(255, 0, 255, 255));

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

        var controller = new PlayerController(world, cam.Position);
        var interaction = new InteractionSystem(world);
        interaction.Initialize();

        var freeCamMode = false;

        var initialDir = Vector3.Normalize(cam.Target - cam.Position);
        var pitch = (float)Math.Asin(initialDir.Y);
        var yaw = (float)Math.Atan2(initialDir.Z, initialDir.X);

        var freeCamVelocity = Vector3.Zero;

        DisableCursor();

        while (!WindowShouldClose()) {

            var dt = GetFrameTime();

            if (!freeCamMode) {

                controller.FrameUpdate(dt);
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
                    controller.Position = cam.Position with { Y = cam.Position.Y - 0.8f };
                    controller.Velocity = Vector3.Zero;
                    controller.SetRotation(yaw, pitch);
                }
            }

            if (freeCamMode) {

                var md = GetMouseDelta();
                yaw += md.X * 0.005f;
                pitch -= md.Y * 0.005f;
                pitch = Math.Clamp(pitch, -1.5f, 1.5f);

                var dir = new Vector3((float)(Math.Cos(yaw) * Math.Cos(pitch)), (float)Math.Sin(pitch), (float)(Math.Sin(yaw) * Math.Cos(pitch)));
                var rgt = Vector3.Normalize(Vector3.Cross(dir, Vector3.UnitY));
                var up = Vector3.Normalize(Vector3.Cross(rgt, dir));

                var speed = IsKeyDown(KeyboardKey.LeftControl) ? 200f : IsKeyDown(KeyboardKey.LeftShift) ? 60f : 30f;
                
                const float accel = 10.0f;
                const float friction = 6.0f;

                var input = Vector3.Zero;
                if (IsKeyDown(KeyboardKey.W)) input += dir;
                if (IsKeyDown(KeyboardKey.S)) input -= dir;
                if (IsKeyDown(KeyboardKey.A)) input -= rgt;
                if (IsKeyDown(KeyboardKey.D)) input += rgt;
                if (IsKeyDown(KeyboardKey.E)) input += up;
                if (IsKeyDown(KeyboardKey.Q)) input -= up;

                if (input.LengthSquared() > 0) input = Vector3.Normalize(input);

                freeCamVelocity = Vector3.Lerp(freeCamVelocity, input * speed, Math.Clamp(accel * dt, 0, 1));
                
                if (input.LengthSquared() < 0.01f) {
                    
                    freeCamVelocity = Vector3.Lerp(freeCamVelocity, Vector3.Zero, Math.Clamp(friction * dt, 0, 1));
                }

                cam.Position += freeCamVelocity * dt;
                cam.Target = cam.Position + dir;

            } else {
                
                freeCamVelocity = Vector3.Zero; // Reset when not in use
                cam.Position = controller.CameraPosition;
                cam.Target = controller.GetCameraTarget();
            }

            var camDir = Vector3.Normalize(cam.Target - cam.Position);
            interaction.Update(cam.Position, camDir);

            BeginDrawing();
            ClearBackground(new Color(135, 206, 235, 255));

            BeginMode3D(cam);
            world.Render(cam, material);
            interaction.Draw3D();
            EndMode3D();

            interaction.DrawUi();

            DrawFPS(10, 10);
            DrawText(freeCamMode ? "[F] Free Cam" : "[F] Character", 10, 40, 20, Color.Black);
            DrawText($"Pos: {controller.Position:F2}", 10, 70, 20, Color.Black);

            EndDrawing();
        }

        world.Unload();
        CloseWindow();

        return 0;
    }
}