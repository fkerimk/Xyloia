using System.Numerics;
using System.Runtime.InteropServices;
using Raylib_cs;
using static Raylib_cs.Raylib;

internal class InteractionSystem(World world) {

    private byte _selectedBlockId = 1;
    private readonly byte[] _availableBlocks = [1, 2, 3, 4]; // Grass, Dirt, Sand, Glow
    private int _selectionIndex;

    private World.RaycastResult _currentHit;

    private RenderTexture2D _uiTexture;
    private Camera3D _uiCamera;
    private Mesh _uiMesh;
    private Material _uiMaterial;
    private bool _initialized;

    public void Initialize() {

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

        var uv = Registry.GetUv(_selectedBlockId);

        var mesh = new Mesh {
            VertexCount = 24,
            TriangleCount = 12,
            Vertices = (float*)NativeMemory.Alloc(24 * 3 * sizeof(float)),
            Normals = (float*)NativeMemory.Alloc(24 * 3 * sizeof(float)),
            TexCoords = (float*)NativeMemory.Alloc(24 * 2 * sizeof(float)),
            Indices = (ushort*)NativeMemory.Alloc(36 * sizeof(ushort))
        };

        float[] verts = [

            // Front
            -0.5f, -0.5f, 0.5f, 0.5f, -0.5f, 0.5f, 0.5f, 0.5f, 0.5f, -0.5f, 0.5f, 0.5f,

            // Back
            0.5f, -0.5f, -0.5f, -0.5f, -0.5f, -0.5f, -0.5f, 0.5f, -0.5f, 0.5f, 0.5f, -0.5f,

            // Top
            -0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, -0.5f, -0.5f, 0.5f, -0.5f,

            // Bottom
            -0.5f, -0.5f, -0.5f, 0.5f, -0.5f, -0.5f, 0.5f, -0.5f, 0.5f, -0.5f, -0.5f, 0.5f,

            // Right
            0.5f, -0.5f, 0.5f, 0.5f, -0.5f, -0.5f, 0.5f, 0.5f, -0.5f, 0.5f, 0.5f, 0.5f,

            // Left
            -0.5f, -0.5f, -0.5f, -0.5f, -0.5f, 0.5f, -0.5f, 0.5f, 0.5f, -0.5f, 0.5f, -0.5f
        ];

        Marshal.Copy(verts, 0, (IntPtr)mesh.Vertices, verts.Length);

        short[] indices = [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7, 8, 9, 10, 8, 10, 11, 12, 13, 14, 12, 14, 15, 16, 17, 18, 16, 18, 19, 20, 21, 22, 20, 22, 23];
        Marshal.Copy(indices, 0, (IntPtr)mesh.Indices, indices.Length);

        var u0 = uv.X;
        var v0 = uv.Y;
        var u1 = uv.X + uv.Width;
        var v1 = uv.Y + uv.Height;

        var texCoords = new float[48];

        for (var i = 0; i < 6; i++) {

            var off = i * 8;

            texCoords[off + 0] = u0;
            texCoords[off + 1] = v1;
            texCoords[off + 2] = u1;
            texCoords[off + 3] = v1;
            texCoords[off + 4] = u1;
            texCoords[off + 5] = v0;
            texCoords[off + 6] = u0;
            texCoords[off + 7] = v0;
        }

        Marshal.Copy(texCoords, 0, (IntPtr)mesh.TexCoords, texCoords.Length);

        float[] normals = [

            // Front (0, 0, 1)
            0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1,

            // Back (0, 0, -1)
            0, 0, -1, 0, 0, -1, 0, 0, -1, 0, 0, -1,

            // Top (0, 1, 0)
            0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0,

            // Bottom (0, -1, 0)
            0, -1, 0, 0, -1, 0, 0, -1, 0, 0, -1, 0,

            // Right (1, 0, 0)
            1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0,

            // Left (-1, 0, 0)
            -1, 0, 0, -1, 0, 0, -1, 0, 0, -1, 0, 0
        ];

        Marshal.Copy(normals, 0, (IntPtr)mesh.Normals, normals.Length);

        UploadMesh(ref mesh, false);
        _uiMesh = mesh;
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

                world.SetBlock(_currentHit.X + _currentHit.FaceX, _currentHit.Y + _currentHit.FaceY, _currentHit.Z + _currentHit.FaceZ, _selectedBlockId);
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