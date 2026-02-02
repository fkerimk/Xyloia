using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

internal static class Program {

    public static int Main() {

        SetTraceLogLevel(TraceLogLevel.Warning);

        SetConfigFlags(ConfigFlags.VSyncHint | ConfigFlags.Msaa4xHint);

        InitWindow(1600, 900, "Xyloia");

        SetWindowMonitor(0);

        Registry.Initialize(".");

        var material = LoadMaterialDefault();
        SetMaterialTexture(ref material, MaterialMapIndex.Albedo, Registry.AtlasTexture);

        var chunkShader = LoadShader("Assets/Shaders/Chunk.vs", "Assets/Shaders/Chunk.fs");
        
        material.Shader = chunkShader;
        
        var crosshairTexture = LoadTexture("Assets/Textures/Crosshair.png");
        
        var dynLightLoc = GetShaderLocation(chunkShader, "dynLightPos");

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
        var dynamicLight = false;

        var initialDir = Vector3.Normalize(cam.Target - cam.Position);
        var pitch = (float)Math.Asin(initialDir.Y);
        var yaw = (float)Math.Atan2(initialDir.Z, initialDir.X);

        var freeCamVelocity = Vector3.Zero;
        var spawned = false;

        DisableCursor();

        while (!WindowShouldClose()) {

            var dt = GetFrameTime();

            if (!freeCamMode) {

                controller.FrameUpdate(dt);
            }

            // Dynamic Light Toggle
            if (IsKeyPressed(KeyboardKey.F)) dynamicLight = !dynamicLight;
            
            world.UpdateDynamicLight(cam.Position, dynamicLight);

            SetShaderValue(chunkShader, dynLightLoc, dynamicLight ? cam.Position : new Vector3(99999, 99999, 99999), ShaderUniformDataType.Vec3);

            world.Update(cam.Position);

            if (!spawned && world.IsChunkLoaded(0, 0, 0)) {
                
                const int spawnX = Chunk.Width / 2;
                const int spawnZ = Chunk.Depth / 2;
                var spawnY = world.GetTopBlockHeight(spawnX, spawnZ) + 2;
                
                controller.Position = new Vector3(spawnX, spawnY, spawnZ);
                controller.SetRotation(yaw, pitch);
                spawned = true;
            }

            // Toggle camera
            if (IsKeyPressed(KeyboardKey.N)) {

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
                    controller.Position = cam.Position with { Y = cam.Position.Y - PlayerController.EyeHeight };
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
            DrawCompass();
            EndMode3D();

            interaction.DrawUi();

            const float crosshairScale = 1.0f;
            var cw = crosshairTexture.Width * crosshairScale;
            var ch = crosshairTexture.Height * crosshairScale;
            
            Rlgl.SetBlendFactors(0x0307, 0x0303, 0x8006); // GL_ONE_MINUS_DST_COLOR, GL_ONE_MINUS_SRC_ALPHA, GL_FUNC_ADD
            Rlgl.SetBlendMode(BlendMode.Custom);
            DrawTextureEx(crosshairTexture, new Vector2(GetScreenWidth() / 2f - cw / 2f, GetScreenHeight() / 2f - ch / 2f), 0, crosshairScale, Color.White);
            Rlgl.SetBlendMode(BlendMode.Alpha);

            DrawFPS(10, 10);
            DrawText($"{controller.Position:F2}".TrimStart('<').TrimEnd('>'), 10, 30, 20, Color.Yellow);
            DrawText("[N] Free Cam", 10, 50, 20, freeCamMode ? Color.Green : Color.Red);
            DrawText("[F] Dynamic Light", 10, 70, 20, dynamicLight ? Color.Green : Color.Red);

            EndDrawing();
        }

        world.Unload();
        UnloadTexture(crosshairTexture);
        UnloadShader(chunkShader);
        CloseWindow();

        return 0;

        // Compass 
        void DrawCompass() {
            
            if (!freeCamMode) return;
            
            var center = cam.Position; 
            center.Y -= 1.0f; 
            const float radius = 1.0f;

            // Draw Circle
            const int segments = 32;
            
            for (var i = 0; i < segments; i++) {
                
                var a1 = (float)i / segments * Math.PI * 2;
                var a2 = (float)(i + 1) / segments * Math.PI * 2;
                var p1 = center + new Vector3((float)Math.Cos(a1) * radius, 0, (float)Math.Sin(a1) * radius);
                var p2 = center + new Vector3((float)Math.Cos(a2) * radius, 0, (float)Math.Sin(a2) * radius);
                DrawLine3D(p1, p2, Color.White);
            }

            // N (-Z)
            var nPos = center + new Vector3(0, 0, -radius);
            DrawLine3D(nPos + new Vector3(-0.05f, 0, 0), nPos + new Vector3(-0.05f, 0, -0.1f), Color.Red);
            DrawLine3D(nPos + new Vector3(-0.05f, 0, -0.1f), nPos + new Vector3(0.05f, 0, 0), Color.Red);
            DrawLine3D(nPos + new Vector3(0.05f, 0, 0), nPos + new Vector3(0.05f, 0, -0.1f), Color.Red);

            // S (+Z)
            var sPos = center + new Vector3(0, 0, radius);
            DrawLine3D(sPos + new Vector3(0.05f, 0, 0), sPos + new Vector3(-0.05f, 0, 0), Color.Blue);
            DrawLine3D(sPos + new Vector3(-0.05f, 0, 0), sPos + new Vector3(-0.05f, 0, 0.05f), Color.Blue);
            DrawLine3D(sPos + new Vector3(-0.05f, 0, 0.05f), sPos + new Vector3(0.05f, 0, 0.05f), Color.Blue);
            DrawLine3D(sPos + new Vector3(0.05f, 0, 0.1f), sPos + new Vector3(0.05f, 0, 0.1f), Color.Blue);
            DrawLine3D(sPos + new Vector3(0.05f, 0, 0.1f), sPos + new Vector3(-0.05f, 0, 0.1f), Color.Blue);

            // E (+X)
            var ePos = center + new Vector3(radius, 0, 0);
            DrawLine3D(ePos + new Vector3(0.1f, 0, -0.05f), ePos + new Vector3(0, 0, -0.05f), Color.Green);
            DrawLine3D(ePos + new Vector3(0, 0, -0.05f), ePos + new Vector3(0, 0, 0.05f), Color.Green);
            DrawLine3D(ePos + new Vector3(0, 0, 0), ePos + new Vector3(0.08f, 0, 0), Color.Green);
            DrawLine3D(ePos + new Vector3(0, 0, 0.05f), ePos + new Vector3(0.1f, 0, 0.05f), Color.Green);
            
            // W (-X)
            var wPos = center + new Vector3(-radius, 0, 0);
            DrawLine3D(wPos + new Vector3(-0.1f, 0, -0.05f), wPos + new Vector3(-0.1f, 0, 0.05f), Color.Yellow);
            DrawLine3D(wPos + new Vector3(0, 0, -0.05f), wPos + new Vector3(0, 0, 0.05f), Color.Yellow);
            DrawLine3D(wPos + new Vector3(-0.1f, 0, 0.05f), wPos + new Vector3(-0.05f, 0, 0), Color.Yellow);
            DrawLine3D(wPos + new Vector3(-0.05f, 0, 0), wPos + new Vector3(0, 0, 0.05f), Color.Yellow);
        }
    }
}