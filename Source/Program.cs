using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;
using Xyloia;

internal static class Program {

    public enum GameState {
        Menu, Game
    }

    public static GameState State = GameState.Menu;

    public static int Main() {

        SetTraceLogLevel(TraceLogLevel.Warning);

        SetConfigFlags(ConfigFlags.VSyncHint | ConfigFlags.Msaa4xHint);

        InitWindow(1600, 900, "Xyloia");
        SetExitKey(KeyboardKey.Null);

        SetWindowMonitor(0);

        Registry.Initialize(".");
        WorldGenConfig.Load(".");

        var material = LoadMaterialDefault();
        SetMaterialTexture(ref material, MaterialMapIndex.Albedo, Registry.AtlasTexture);

        var chunkShader = LoadShader("Assets/Shaders/Chunk.vs", "Assets/Shaders/Chunk.fs");

        material.Shader = chunkShader;

        var crosshairTexture = LoadTexture("Assets/Textures/Crosshair.png");

        // Worlds
        World? world = null;

        LoadMenuWorld();

        var cam = new Camera3D {
            FovY = 90,
            Up = Vector3.UnitY,
            Target = new Vector3(0, 40, 0),
            Position = new Vector3(20, 50, 20),
            Projection = CameraProjection.Perspective,
        };

        var controller = new PlayerController(world, cam.Position);
        var interaction = new InteractionSystem(world);
        interaction.SetPlayer(controller);
        interaction.Initialize();

        var spawned = false;
        var exit = false;

        var gui = new Gui();
        gui.RegisterAction(
            "StartGame",
            () => {
                LoadGameWorld();
                interaction.SetWorld(world!);
                controller.SetWorld(world!);
                spawned = false; // Trigger respawn logic
                State = GameState.Game;
                gui.CloseAll(); // Close Main Menu
            }
        );
        gui.RegisterAction("ExitGame", () => exit = true);
        gui.RegisterAction("CloseMenu", gui.Close);
        gui.RegisterAction("OpenMenu", gui.Open); // e.g. OpenMenu(Assets/GUI/Menu.json)
        gui.RegisterAction(
            "StopGame",
            () => {
                LoadMenuWorld();

                if (world != null) {
                    controller.SetWorld(world);
                    interaction.SetWorld(world);
                }

                State = GameState.Menu;
            }
        );

        // START IN MENU
        gui.Open("Assets/GUI/Menu.json");

        var noclip = false;
        var dynamicLight = false;

        var initialDir = Vector3.Normalize(cam.Target - cam.Position);
        var pitch = (float)Math.Asin(initialDir.Y);
        var yaw = (float)Math.Atan2(initialDir.Z, initialDir.X);

        var freeCamVelocity = Vector3.Zero;

        while (!WindowShouldClose()) {

            // Global GUI Update
            gui.Update();

            if (exit) break;

            // Cursor Management
            if (gui.IsCursorFree || State == GameState.Menu) {
                
                if (IsCursorHidden()) EnableCursor();
                
            } else {
                
                if (!IsCursorHidden()) DisableCursor();
            }

            if (State == GameState.Menu) {

                BeginDrawing();
                ClearBackground(Color.SkyBlue);

                // Draw a simple rotating camera background for menu
                BeginMode3D(cam);
                cam.Position = new Vector3((float)Math.Sin(GetTime() * 0.05) * 64, 110, (float)Math.Cos(GetTime() * 0.05) * 64);
                cam.Target = new Vector3(0, 50, 0);

                // Update menu world for loading chunks
                world!.Update(cam.Position);

                world!.Render(cam, material);
                EndMode3D();

                gui.Draw();

            } else {

                var dt = GetFrameTime();

                // Only update game if NOT paused
                if (!gui.IsPaused) {

                    if (!noclip) controller.FrameUpdate(dt);

                    if (IsKeyPressed(KeyboardKey.L)) dynamicLight = !dynamicLight;

                    world!.UpdatePlayerLight(cam.Position, dynamicLight);

                    world.Update(cam.Position);

                    // Game Logic
                    if (!spawned && world.IsChunkLoaded(0, 0, 0)) {

                        const int spawnX = Chunk.Width / 2;
                        const int spawnZ = Chunk.Depth / 2;
                        var spawnY = world.GetTopBlockHeight(spawnX, spawnZ) + 2;

                        controller.Position = new Vector3(spawnX, spawnY, spawnZ);
                        controller.SetRotation(yaw, pitch);
                        spawned = true;
                    }

                    var camDir = Vector3.Normalize(cam.Target - cam.Position);
                    if (!gui.IsInteractionConsumed) interaction.Update(cam.Position, camDir);
                }

                // Toggle camera
                if (!gui.IsPaused && IsKeyPressed(KeyboardKey.V)) {

                    noclip = !noclip;

                    if (noclip) {
                        
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

                if (IsKeyPressed(KeyboardKey.Escape)) {

                    if (gui.HasMenuOpen) {
                        gui.Close();
                    } else {
                        gui.Open("Assets/GUI/Pause.json");
                    }
                }

                switch (gui.IsPaused) {
                    
                    case false when noclip: {
                        
                        var md = GetMouseDelta();
                        yaw += md.X * 0.005f;
                        pitch -= md.Y * 0.005f;
                        pitch = Math.Clamp(pitch, -1.5f, 1.5f);

                        var dir = new Vector3((float)(Math.Cos(yaw) * Math.Cos(pitch)), (float)Math.Sin(pitch), (float)(Math.Sin(yaw) * Math.Cos(pitch)));
                        var rgt = Vector3.Normalize(Vector3.Cross(dir, Vector3.UnitY));
                        var up = Vector3.Normalize(Vector3.Cross(rgt, dir));

                        var speed = IsKeyDown(KeyboardKey.LeftControl)
                            ? 200f
                            : IsKeyDown(KeyboardKey.LeftShift)
                                ? 60f
                                : 30f;
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
                        if (input.LengthSquared() < 0.01f) freeCamVelocity = Vector3.Lerp(freeCamVelocity, Vector3.Zero, Math.Clamp(friction * dt, 0, 1));

                        cam.Position += freeCamVelocity * dt;
                        cam.Target = cam.Position + dir;

                        break;
                    }

                    case false:
                        
                        freeCamVelocity = Vector3.Zero; // Reset when not in use
                        cam.Position = controller.CameraPosition;
                        cam.Target = controller.GetCameraTarget();

                        break;
                }

                BeginDrawing();
                ClearBackground(new Color(135, 206, 235, 255));

                BeginMode3D(cam);
                world!.Render(cam, material);
                interaction.Draw3D();
                DrawCompass();
                EndMode3D();

                // Draw In-Game UI (Menus)
                gui.Draw();

                if (!gui.HasMenuOpen) {
                    
                    // HUD elements only when no menu is open
                    interaction.DrawUi();

                    const float crosshairScale = 1.0f;
                    var cw = crosshairTexture.Width * crosshairScale;
                    var ch = crosshairTexture.Height * crosshairScale;

                    Rlgl.SetBlendFactors(0x0307, 0x0303, 0x8006);
                    Rlgl.SetBlendMode(BlendMode.Custom);
                    DrawTextureEx(crosshairTexture, new Vector2(GetScreenWidth() / 2f - cw / 2f, GetScreenHeight() / 2f - ch / 2f), 0, crosshairScale, Color.White);
                    Rlgl.SetBlendMode(BlendMode.Alpha);

                    DrawFPS(10, 10);
                    DrawText($"{controller.Position:F2}".TrimStart('<').TrimEnd('>'), 10, 30, 20, Color.Yellow);
                    DrawText("[V] Noclip", 10, 50, 20, noclip ? Color.Green : Color.Red);
                    DrawText("[L] Dynamic Light", 10, 70, 20, dynamicLight ? Color.Green : Color.Red);
                }

            }

            EndDrawing();
        }

        world?.Unload();
        UnloadTexture(crosshairTexture);
        UnloadShader(chunkShader);
        CloseWindow();

        return 0;

        void LoadMenuWorld() {

            world?.Unload();
            GC.Collect();
            world = new World(0, 8);
        }

        void LoadGameWorld() {

            world?.Unload();
            GC.Collect();
            world = new World();
        }

        // Compass 
        void DrawCompass() {

            if (!noclip) return;

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