using System.Numerics;
using Raylib_cs;

internal class PlayerController(World? world, Vector3 position) {

    private World? _world = world;

    public void SetWorld(World world) {

        _world = world;
        Velocity = Vector3.Zero;
    }

    public Vector3 Position = position;
    public Vector3 Velocity;

    // Player AABB dimensions
    private const float PlayerWidth = 0.6f;
    private const float PlayerHeight = 1.8f;
    public const float EyeHeight = 1.62f;

    public BoundingBox GetAabb() => GetAabb(Position);
    private static BoundingBox GetAabb(Vector3 pos) { return new BoundingBox(pos + new Vector3(-PlayerWidth / 2, 0, -PlayerWidth / 2), pos + new Vector3(PlayerWidth / 2, PlayerHeight, PlayerWidth / 2)); }

    private const float MoveSpeed = 7.5f;
    private const float JumpForce = 10.5f;
    private const float Gravity = 24.0f;
    private const float FallGravityMultiplier = 1.6f;
    private const float Friction = 25.0f;
    private const float Acceleration = 80.0f;
    private const float AirControl = 5.0f;

    private const float CoyoteDuration = 0.15f;
    private const float JumpBufferDuration = 0.1f;

    private float _coyoteTimer;
    private float _jumpBufferTimer;

    public Vector3 CameraPosition => Position with { Y = Position.Y + EyeHeight };

    private float _yaw;
    private float _pitch;
    private bool _isGrounded;
    private bool _isJumping;

    public void SetRotation(float yaw, float pitch) {

        _yaw = yaw;
        _pitch = pitch;
    }

    private void GetInput(out Vector3 moveDir) {

        moveDir = Vector3.Zero;

        var fwd = Vector3.Normalize(new Vector3((float)Math.Cos(_yaw), 0, (float)Math.Sin(_yaw)));
        var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitY));

        if (Raylib.IsKeyDown(KeyboardKey.W)) moveDir += fwd;
        if (Raylib.IsKeyDown(KeyboardKey.S)) moveDir -= fwd;
        if (Raylib.IsKeyDown(KeyboardKey.A)) moveDir -= right;
        if (Raylib.IsKeyDown(KeyboardKey.D)) moveDir += right;

        if (moveDir.LengthSquared() > 0) moveDir = Vector3.Normalize(moveDir);
    }

    public void FrameUpdate(float dt) {

        // Mouse Look
        var mouseDelta = Raylib.GetMouseDelta();
        _yaw += mouseDelta.X * 0.003f;
        _pitch -= mouseDelta.Y * 0.003f;
        _pitch = Math.Clamp(_pitch, -1.55f, 1.55f);

        // Update Timers
        if (_isGrounded)
            _coyoteTimer = CoyoteDuration;
        else
            _coyoteTimer -= dt;

        _jumpBufferTimer -= dt;
        if (Raylib.IsKeyPressed(KeyboardKey.Space)) _jumpBufferTimer = JumpBufferDuration;

        GetInput(out var inputDir);

        // Physics integration
        ApplyPhysics(inputDir, dt);

        PushOutOfBlocks();
    }

    private void PushOutOfBlocks() {

        if (_world == null) return;

        // Use a shrunk box to avoid triggering on wall touches
        const float shrink = 0.1f;

        var minX = (int)Math.Floor(Position.X - PlayerWidth / 2 + shrink);
        var maxX = (int)Math.Floor(Position.X + PlayerWidth / 2 - shrink);
        var minY = (int)Math.Floor(Position.Y + shrink);
        var maxY = (int)Math.Floor(Position.Y + PlayerHeight - shrink);
        var minZ = (int)Math.Floor(Position.Z - PlayerWidth / 2 + shrink);
        var maxZ = (int)Math.Floor(Position.Z + PlayerWidth / 2 - shrink);

        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++) {

            var block = _world.GetBlock(x, y, z);

            if (!block.Solid) continue;

            // If inside a solid block, snap to the top of it.
            var topY = y + 1.0f + 0.001f; // Slightly above

            if (!(Position.Y < topY)) continue;

            Position.Y = topY;
            Velocity.Y = 0;
        }
    }

    private void ApplyPhysics(Vector3 inputDir, float dt) {

        // Dynamic gravity
        var currentGravity = Gravity;

        if (!_isGrounded) {

            if (Velocity.Y < 0) {

                currentGravity *= FallGravityMultiplier;
            }
        }

        Velocity.Y -= currentGravity * dt;

        // Horizontal movement
        var targetVel = inputDir * MoveSpeed;
        var currentH = new Vector2(Velocity.X, Velocity.Z);
        var targetH = new Vector2(targetVel.X, targetVel.Z);

        float accelRate;

        if (_isGrounded) {

            // High responsiveness
            accelRate = inputDir.LengthSquared() > 0.01f ? Acceleration : Friction;

        } else {

            accelRate = AirControl;
        }

        // Snap to target more decisively
        currentH = Vector2.Lerp(currentH, targetH, Math.Clamp(accelRate * dt, 0, 1));

        Velocity.X = currentH.X;
        Velocity.Z = currentH.Y;

        // Jump (Buffered + Coyote)
        if (_jumpBufferTimer > 0 && _coyoteTimer > 0) {

            Velocity.Y = JumpForce;
            _jumpBufferTimer = 0;
            _coyoteTimer = 0;
            _isGrounded = false;
            _isJumping = true;
        }

        // Variable jump height
        if (_isJumping && Velocity.Y > 0 && !Raylib.IsKeyDown(KeyboardKey.Space)) {

            Velocity.Y *= 0.5f;
            _isJumping = false;
        }

        if (_isGrounded) _isJumping = false;

        // Collision & Integration
        MoveAndSlide(dt);
    }

    private void MoveAndSlide(float dt) {

        _isGrounded = false;

        // X Axis
        if (Math.Abs(Velocity.X) > 0.001f) {

            var dx = Velocity.X * dt;

            if (!TestCollision(Position.X + dx, Position.Y, Position.Z)) {

                Position.X += dx;

            } else {

                Velocity.X = 0; // Bonk
            }
        }

        // Z Axis
        if (Math.Abs(Velocity.Z) > 0.001f) {

            var dz = Velocity.Z * dt;

            if (!TestCollision(Position.X, Position.Y, Position.Z + dz)) {

                Position.Z += dz;

            } else {

                Velocity.Z = 0; // Bonk
            }
        }

        // Y Axis
        if (Math.Abs(Velocity.Y) > 0.001f) {

            var dy = Velocity.Y * dt;

            if (!TestCollision(Position.X, Position.Y + dy, Position.Z)) {

                Position.Y += dy;

            } else {

                if (Velocity.Y < 0) {

                    _isGrounded = true;
                    Position.Y = (float)Math.Round(Position.Y + dy);
                }

                Velocity.Y = 0;
            }
        }
    }

    private bool TestCollision(float x, float y, float z) {

        var minX = x - PlayerWidth * 0.5f;
        var maxX = x + PlayerWidth * 0.5f;
        var maxY = y + PlayerHeight;
        var minZ = z - PlayerWidth * 0.5f;
        var maxZ = z + PlayerWidth * 0.5f;

        return _world != null && _world.GetAabbCollision(minX, y, minZ, maxX, maxY, maxZ);
    }

    public Vector3 GetCameraTarget() {

        var dir = new Vector3((float)(Math.Cos(_yaw) * Math.Cos(_pitch)), (float)Math.Sin(_pitch), (float)(Math.Sin(_yaw) * Math.Cos(_pitch)));

        return CameraPosition + dir;
    }
}