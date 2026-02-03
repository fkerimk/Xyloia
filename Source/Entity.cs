using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

// ReSharper disable UnusedAutoPropertyAccessor.Global

internal class Entity {

    public EntityInfo Info { get; }
    public float Yaw { get; set; }

    private Vector3 Position { get; set; }

    private readonly Model _model;
    private readonly ModelAnimation[] _animations;
    private readonly unsafe ModelAnimation* _animsPtr;
    private readonly int _animCount;
    private float _animTime;

    private Vector4 _prevLightVec;
    private Vector4 _targetLightVec;
    private float _lightLerp = 1.0f;
    private bool _firstRender = true;

    public unsafe Entity(EntityInfo info, Vector3 position) {

        Info = info;
        Position = position;

        _model = LoadModel(info.ModelPath);

        // Assign entity shader to all materials
        for (var i = 0; i < _model.MaterialCount; i++) {

            _model.Materials[i].Shader = Registry.EntityShader;
        }

        var count = 0;
        _animsPtr = LoadModelAnimations(info.ModelPath, ref count);
        _animCount = count;

        _animations = new ModelAnimation[_animCount];

        for (var i = 0; i < _animCount; i++) {

            _animations[i] = _animsPtr[i];
        }
    }

    public void Update(float dt) {

        if (_animCount <= 0) return;

        _animTime += dt;

        var frames = _animations[0].FrameCount;

        if (frames <= 0) return;

        var currentFrame = (int)(_animTime * 30) % frames;
        UpdateModelAnimation(_model, _animations[0], currentFrame);
    }

    public void Render(World world) {

        var lx = (int)Math.Floor(Position.X);
        var ly = (int)Math.Floor(Position.Y + 0.5f);
        var lz = (int)Math.Floor(Position.Z);

        var light = world.GetLight(lx, ly, lz);
        var newTarget = new Vector4(light & 0xF, (light >> 4) & 0xF, (light >> 8) & 0xF, (light >> 12) & 0xF);

        if (_firstRender) {

            _prevLightVec = newTarget;
            _targetLightVec = newTarget;
            _lightLerp = 1.0f;
            _firstRender = false;
        }

        if (newTarget != _targetLightVec) {

            // Start a new fade from the currently interpolated value to the new target
            _prevLightVec = Vector4.Lerp(_prevLightVec, _targetLightVec, Math.Min(_lightLerp * 10.0f, 1.0f));
            _targetLightVec = newTarget;
            _lightLerp = 0;
        }

        _lightLerp += GetFrameTime();

        var t = Math.Min(_lightLerp * 10.0f, 1.0f);

        var currentLight = Vector4.Lerp(_prevLightVec, _targetLightVec, t);

        var loc = GetShaderLocation(Registry.EntityShader, "lightValues");
        SetShaderValue(Registry.EntityShader, loc, currentLight, ShaderUniformDataType.Vec4);

        DrawModelEx(_model, Position, new Vector3(0, 1, 0), Yaw, Vector3.One, Color.White);
    }

    public void Unload() {

        UnloadModel(_model);

        if (_animCount > 0) {

            unsafe {

                UnloadModelAnimations(_animsPtr, _animCount);
            }
        }
    }
}