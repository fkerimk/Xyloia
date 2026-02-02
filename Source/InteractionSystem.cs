using System.Numerics;
using System.Runtime.InteropServices;
using Raylib_cs;
using static Raylib_cs.Raylib;

internal class InteractionSystem(World world) {

    private int _selectionIndex;
    private byte _selectedBlockId = 1;
    private byte[] _availableBlocks = [];

    private RaycastResult _currentHit;
    private RenderTexture2D _uiTexture;
    private Camera3D _uiCamera;
    private Mesh _uiMesh;
    private Material _uiMaterial;
    private bool _initialized;

    public void Initialize() {

        var list = new List<byte>();

        for (byte i = 1; i < 255; i++) {

            if (Registry.GetFaceUv(i).Width > 0) list.Add(i);
        }

        _availableBlocks = list.ToArray();

        if (_availableBlocks.Length > 0) _selectedBlockId = _availableBlocks[0];

        _uiTexture = LoadRenderTexture(128, 128);

        _uiCamera = new Camera3D {
            Position = new Vector3(2.5f, 2.5f, 2.5f),
            Target = new Vector3(0, 0, 0),
            Up = Vector3.UnitY,
            FovY = 35,
            Projection = CameraProjection.Perspective
        };

        _uiMaterial = LoadMaterialDefault();
        SetMaterialTexture(ref _uiMaterial, MaterialMapIndex.Albedo, Registry.AtlasTexture);

        _initialized = true;
        UpdateUiMesh();
    }

    private unsafe void UpdateUiMesh() {

        if (!_initialized) return;

        if (_uiMesh.Vertices != null) UnloadMesh(_uiMesh);

        var model = Registry.GetModel(_selectedBlockId);

        // Count faces
        var faceCount = 0;

        if (model is { Elements.Count: > 0 }) {

            faceCount += model.Elements.Sum(el => el.Faces.Count);
        } else {

            faceCount = 6; // Default cube
        }

        if (faceCount == 0) return;

        var mesh = new Mesh {
            VertexCount = faceCount * 4,
            TriangleCount = faceCount * 2,
            Vertices = (float*)NativeMemory.Alloc((nuint)(faceCount * 4 * 3 * sizeof(float))),
            Normals = (float*)NativeMemory.Alloc((nuint)(faceCount * 4 * 3 * sizeof(float))),
            TexCoords = (float*)NativeMemory.Alloc((nuint)(faceCount * 4 * 2 * sizeof(float))),
            Indices = (ushort*)NativeMemory.Alloc((nuint)(faceCount * 6 * sizeof(ushort)))
        };

        var vIdx = 0;
        var iIdx = 0;

        if (model is { Elements.Count: > 0 }) {

            foreach (var el in model.Elements) {

                var min = new Vector3(el.From[0], el.From[1], el.From[2]) / 16f - new Vector3(0.5f);
                var max = new Vector3(el.To[0], el.To[1], el.To[2]) / 16f - new Vector3(0.5f);

                // Faces
                if (el.Faces.TryGetValue("north", out var fN)) {

                    var uv = Registry.ResolveFaceUv(model, fN);

                    // North (Z-)
                    AddFace(
                        new Vector3(max.X, min.Y, min.Z), // P1: MaxX, MinY, MinZ
                        new Vector3(min.X, min.Y, min.Z), // P2: MinX, MinY, MinZ
                        new Vector3(min.X, max.Y, min.Z), // P3: MinX, MaxY, MinZ
                        new Vector3(max.X, max.Y, min.Z), // P4: MaxX, MaxY, MinZ
                        new Vector3(0, 0, -1),
                        uv
                    );
                }

                if (el.Faces.TryGetValue("south", out var fS)) {

                    var uv = Registry.ResolveFaceUv(model, fS);

                    // South (Z+)
                    AddFace(
                        new Vector3(min.X, min.Y, max.Z), // P1: MinX, MinY, MaxZ
                        new Vector3(max.X, min.Y, max.Z), // P2: MaxX, MinY, MaxZ
                        new Vector3(max.X, max.Y, max.Z), // P3: MaxX, MaxY, MaxZ
                        new Vector3(min.X, max.Y, max.Z), // P4: MinX, MaxY, MaxZ
                        new Vector3(0, 0, 1),
                        uv
                    );
                }

                if (el.Faces.TryGetValue("east", out var fE)) {

                    var uv = Registry.ResolveFaceUv(model, fE);

                    // East (X+)
                    AddFace(
                        new Vector3(max.X, min.Y, max.Z), // P1: MaxX, MinY, MaxZ
                        new Vector3(max.X, min.Y, min.Z), // P2: MaxX, MinY, MinZ
                        new Vector3(max.X, max.Y, min.Z), // P3: MaxX, MaxY, MinZ
                        new Vector3(max.X, max.Y, max.Z), // P4: MaxX, MaxY, MaxZ 
                        new Vector3(1, 0, 0),
                        uv
                    );
                }

                if (el.Faces.TryGetValue("west", out var fW)) {

                    var uv = Registry.ResolveFaceUv(model, fW);

                    AddFace(
                        new Vector3(min.X, min.Y, min.Z), // P1: MinX, MinY, MinZ
                        new Vector3(min.X, min.Y, max.Z), // P2: MinX, MinY, MaxZ
                        new Vector3(min.X, max.Y, max.Z), // P3: MinX, MaxY, MaxZ
                        new Vector3(min.X, max.Y, min.Z), // P4: MinX, MaxY, MinZ
                        new Vector3(-1, 0, 0),
                        uv
                    );
                }

                if (el.Faces.TryGetValue("up", out var fU)) {

                    var uv = Registry.ResolveFaceUv(model, fU);

                    // Up (Y+)
                    AddFace(
                        new Vector3(min.X, max.Y, max.Z), // P1: MinX, MaxY, MaxZ
                        new Vector3(max.X, max.Y, max.Z), // P2: MaxX, MaxY, MaxZ
                        new Vector3(max.X, max.Y, min.Z), // P3: MaxX, MaxY, MinZ
                        new Vector3(min.X, max.Y, min.Z), // P4: MinX, MaxY, MinZ
                        new Vector3(0, 1, 0),
                        uv
                    );
                }

                if (el.Faces.TryGetValue("down", out var fD)) {

                    var uv = Registry.ResolveFaceUv(model, fD);

                    // Down (Y-)
                    AddFace(
                        new Vector3(min.X, min.Y, min.Z), // P1: MinX, min.Y, MinZ
                        new Vector3(max.X, min.Y, min.Z), // P2: MaxX, min.Y, MinZ
                        new Vector3(max.X, min.Y, max.Z), // P3: MaxX, min.Y, max.Z
                        new Vector3(min.X, min.Y, max.Z), // P4: MinX, min.Y, max.Z
                        new Vector3(0, -1, 0),
                        uv
                    );
                }
            }

        } else {

            // Default Cube
            var uv = Registry.GetFaceUv(_selectedBlockId);
            var min = new Vector3(-0.5f);
            var max = new Vector3(0.5f);

            // Front(S), Back(N), Top(U), Bottom(D), Left(W), Right(E) - Just duplicating specific faces for simple cube
            AddFace(new Vector3(min.X, min.Y, max.Z), new Vector3(max.X, min.Y, max.Z), new Vector3(max.X, max.Y, max.Z), new Vector3(min.X, max.Y, max.Z), Vector3.UnitZ, uv);  // Front
            AddFace(new Vector3(max.X, min.Y, min.Z), new Vector3(min.X, min.Y, min.Z), new Vector3(min.X, max.Y, min.Z), new Vector3(max.X, max.Y, min.Z), -Vector3.UnitZ, uv); // Back
            AddFace(new Vector3(min.X, max.Y, max.Z), new Vector3(max.X, max.Y, max.Z), new Vector3(max.X, max.Y, min.Z), new Vector3(min.X, max.Y, min.Z), Vector3.UnitY, uv);  // Top
            AddFace(new Vector3(min.X, min.Y, min.Z), new Vector3(max.X, min.Y, min.Z), new Vector3(max.X, min.Y, max.Z), new Vector3(min.X, min.Y, max.Z), -Vector3.UnitY, uv); // Bottom
            AddFace(new Vector3(max.X, min.Y, max.Z), new Vector3(max.X, min.Y, min.Z), new Vector3(max.X, max.Y, min.Z), new Vector3(max.X, max.Y, max.Z), Vector3.UnitX, uv);  // Right
            AddFace(new Vector3(min.X, min.Y, min.Z), new Vector3(min.X, min.Y, max.Z), new Vector3(min.X, max.Y, max.Z), new Vector3(min.X, max.Y, min.Z), -Vector3.UnitX, uv); // Left
        }

        UploadMesh(ref mesh, false);
        _uiMesh = mesh;

        return;

        void AddFace(Vector3 p1, Vector3 p2, Vector3 p3, Vector3 p4, Vector3 n, UvInfo uv) {

            // Vertices
            var vPtr = mesh.Vertices + vIdx * 3;
            vPtr[0] = p1.X;
            vPtr[1] = p1.Y;
            vPtr[2] = p1.Z;
            vPtr[3] = p2.X;
            vPtr[4] = p2.Y;
            vPtr[5] = p2.Z;
            vPtr[6] = p3.X;
            vPtr[7] = p3.Y;
            vPtr[8] = p3.Z;
            vPtr[9] = p4.X;
            vPtr[10] = p4.Y;
            vPtr[11] = p4.Z;

            // Normals
            var nPtr = mesh.Normals + vIdx * 3;

            for (var k = 0; k < 4; k++) {

                nPtr[k * 3 + 0] = n.X;
                nPtr[k * 3 + 1] = n.Y;
                nPtr[k * 3 + 2] = n.Z;
            }

            // UVs - Apply Rotation if needed
            var tPtr = mesh.TexCoords + vIdx * 2;

            float u1 = uv.X, v1 = uv.Y;
            float u2 = uv.X + uv.Width, v2 = uv.Y + uv.Height;

            Vector2 uv1, uv2, uv3, uv4;

            switch (uv.Rotation) {

                case 90:
                    uv1 = new Vector2(u1, v1); // P1(BL) gets TL texture
                    uv2 = new Vector2(u1, v2); // P2(BR) gets BL texture
                    uv3 = new Vector2(u2, v2); // P3(TR) gets BR texture
                    uv4 = new Vector2(u2, v1); // P4(TL) gets TR texture

                    break;
                case 180:

                    uv1 = new Vector2(u2, v1);
                    uv2 = new Vector2(u1, v1);
                    uv3 = new Vector2(u1, v2);
                    uv4 = new Vector2(u2, v2);

                    break;
                case 270:

                    uv1 = new Vector2(u2, v2);
                    uv2 = new Vector2(u2, v1);
                    uv3 = new Vector2(u1, v1);
                    uv4 = new Vector2(u1, v2);

                    break;

                default:
                    uv1 = new Vector2(u1, v2); // BL
                    uv2 = new Vector2(u2, v2); // BR
                    uv3 = new Vector2(u2, v1); // TR
                    uv4 = new Vector2(u1, v1); // TL

                    break;
            }

            tPtr[0] = uv1.X;
            tPtr[1] = uv1.Y;
            tPtr[2] = uv2.X;
            tPtr[3] = uv2.Y;
            tPtr[4] = uv3.X;
            tPtr[5] = uv3.Y;
            tPtr[6] = uv4.X;
            tPtr[7] = uv4.Y;

            // Indices (0,1,2, 0,2,3) relative to vIdx
            var iPtr = mesh.Indices + iIdx;
            iPtr[0] = (ushort)(vIdx + 0);
            iPtr[1] = (ushort)(vIdx + 1);
            iPtr[2] = (ushort)(vIdx + 2);
            iPtr[3] = (ushort)(vIdx + 0);
            iPtr[4] = (ushort)(vIdx + 2);
            iPtr[5] = (ushort)(vIdx + 3);

            vIdx += 4;
            iIdx += 6;
        }
    }

    public void Update(Vector3 camPos, Vector3 camDir) {

        _currentHit = world.Raycast(camPos, camDir, 5.0f);

        var wheel = GetMouseWheelMove();

        if (wheel != 0) {

            _selectionIndex -= Math.Sign(wheel);

            if (_selectionIndex < 0) _selectionIndex = _availableBlocks.Length - 1;
            if (_selectionIndex >= _availableBlocks.Length) _selectionIndex = 0;

            _selectedBlockId = _availableBlocks[_selectionIndex];

            UpdateUiMesh();
        }

        if (_currentHit.Hit && !IsKeyDown(KeyboardKey.LeftAlt)) {

            if (IsMouseButtonPressed(MouseButton.Left)) {

                world.SetBlock(_currentHit.X, _currentHit.Y, _currentHit.Z, 0);

            } else if (IsMouseButtonPressed(MouseButton.Right)) {

                var facing = Registry.GetFacing(_selectedBlockId);
                byte data = 0;

                switch (facing) {

                    case FacingMode.Yaw: {

                        var step = Registry.GetYawStep(_selectedBlockId);
                        if (step <= 0) step = 90;

                        // Yaw relative to camera
                        var angle = Math.Atan2(camDir.X, camDir.Z) * (180 / Math.PI);

                        // Normalize angle to 0..360
                        if (angle < 0) angle += 360;

                        var q = (int)Math.Round(angle / step);

                        data = (byte)q;

                        break;
                    }

                    case FacingMode.Rotate when _currentHit.FaceX != 0: data = 1; break; // X aligned
                    case FacingMode.Rotate when _currentHit.FaceZ != 0: data = 2; break; // Z aligned
                    case FacingMode.Rotate:                             data = 0; break; // Y aligned

                    case FacingMode.Fixed:
                    default:
                        break;
                }

                world.SetBlock(_currentHit.X + _currentHit.FaceX, _currentHit.Y + _currentHit.FaceY, _currentHit.Z + _currentHit.FaceZ, new Block(_selectedBlockId, data));
            }
        }
    }

    public void Draw3D() {

        if (_currentHit.Hit) {

            DrawCubeWires(new Vector3(_currentHit.X + 0.5f, _currentHit.Y + 0.5f, _currentHit.Z + 0.5f), 1.01f, 1.01f, 1.01f, Color.Black);
        }
    }

    public void DrawUi() {

        BeginTextureMode(_uiTexture);
        ClearBackground(new Color(50, 50, 50, 100));

        BeginMode3D(_uiCamera);
        DrawMesh(_uiMesh, _uiMaterial, Matrix4x4.Identity);
        EndMode3D();

        EndTextureMode();

        DrawText("Selected:", GetScreenWidth() - 135, 10, 20, Color.Black);
        DrawTextureRec(_uiTexture.Texture, new Rectangle(0, 0, _uiTexture.Texture.Width, -_uiTexture.Texture.Height), new Vector2(GetScreenWidth() - 140, 40), Color.White);
    }
}