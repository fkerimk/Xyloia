using System.Numerics;
using Jitter2.Dynamics;
using Jitter2.Collision.Shapes;
using Jitter2.LinearMath;
using Raylib_cs;

internal class PlayerController {

    public RigidBody Body { get; }
    private const float Speed = 7.5f;
    private const float JumpSpeed = 5f;

    public Vector3 CameraPosition => new(Body.Position.X, Body.Position.Y + 1.75f, Body.Position.Z);
    private float _yaw;
    private float _pitch;

    public void SetRotation(float yaw, float pitch) {

        _yaw = yaw;
        _pitch = pitch;
    }

    private Vector3 _moveInput;
    private bool _jumpRequested;

    public PlayerController(Jitter2.World world, Vector3 position) {

        var shape = new CapsuleShape(0.25f, 1.75f);

        Body = world.CreateRigidBody();
        Body.AddShape(shape);
        Body.Position = new JVector(position.X, position.Y, position.Z);

        Body.Friction = 0.0f;
        Body.AffectedByGravity = true;
    }

    public void FrameUpdate() {

        var mouseDelta = Raylib.GetMouseDelta();
        _yaw += mouseDelta.X * 0.005f;
        _pitch -= mouseDelta.Y * 0.005f;
        _pitch = Math.Clamp(_pitch, -1.5f, 1.5f);

        var fwd = Vector3.Normalize(new Vector3((float)Math.Cos(_yaw), 0, (float)Math.Sin(_yaw)));
        var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitY));

        _moveInput = Vector3.Zero;
        if (Raylib.IsKeyDown(KeyboardKey.W)) _moveInput += fwd;
        if (Raylib.IsKeyDown(KeyboardKey.S)) _moveInput -= fwd;
        if (Raylib.IsKeyDown(KeyboardKey.A)) _moveInput -= right;
        if (Raylib.IsKeyDown(KeyboardKey.D)) _moveInput += right;

        if (Raylib.IsKeyPressed(KeyboardKey.Space)) _jumpRequested = true;
    }

    public void ResetInput() {

        _moveInput = Vector3.Zero;
        _jumpRequested = false;
        Body.Velocity = new JVector(0, Body.Velocity.Y, 0);
    }

    public void FixedUpdate() {

        Body.AngularVelocity = JVector.Zero;

        var moveDir = JVector.Zero;

        if (_moveInput.LengthSquared() > 0) {

            var input = Vector3.Normalize(_moveInput);
            moveDir = new JVector(input.X, 0, input.Z) * Speed;
        }

        Body.Velocity = new JVector(moveDir.X, Body.Velocity.Y, moveDir.Z);

        if (_jumpRequested) {

            if (IsGrounded()) {

                Body.Velocity = new JVector(Body.Velocity.X, JumpSpeed, Body.Velocity.Z);
            }

            _jumpRequested = false;
        }
    }

    private bool IsGrounded() { return true; }

    public Vector3 GetCameraTarget() {

        var dir = new Vector3((float)(Math.Cos(_yaw) * Math.Cos(_pitch)), (float)Math.Sin(_pitch), (float)(Math.Sin(_yaw) * Math.Cos(_pitch)));

        return CameraPosition + dir;
    }
}