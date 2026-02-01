using System.Numerics;
using Raylib_cs;

internal class PlayerController(World world, Vector3 position) {

    public Vector3 Position = position;
    public Vector3 Velocity;
    
    // Player AABB dimensions
    private const float Width = 0.6f;
    private const float Height = 1.8f; 
    private const float EyeHeight = 1.62f;

    private const float MoveSpeed = 6.0f;
    private const float JumpForce = 8.5f;
    private const float Gravity = 24.0f;
    private const float Friction = 10.0f;
    private const float AirControl = 2.0f;

    private const float CoyoteDuration = 0.15f;
    private const float JumpBufferDuration = 0.1f;

    private float _coyoteTimer;
    private float _jumpBufferTimer;

    public Vector3 CameraPosition => Position with { Y = Position.Y + EyeHeight };
    
    private float _yaw;
    private float _pitch;
    private bool _isGrounded;

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
        if (_isGrounded) _coyoteTimer = CoyoteDuration;
        else _coyoteTimer -= dt;

        _jumpBufferTimer -= dt;
        if (Raylib.IsKeyPressed(KeyboardKey.Space)) _jumpBufferTimer = JumpBufferDuration;

        GetInput(out var inputDir);
        
        // Physics integration
        ApplyPhysics(inputDir, dt);
    }

    private void ApplyPhysics(Vector3 inputDir, float dt) {

        // Gravity
        Velocity.Y -= Gravity * dt;

        // Movement Logic
        var targetVel = inputDir * MoveSpeed;
        
        // Horizontal Velocity
        var currentH = new Vector2(Velocity.X, Velocity.Z);
        var targetH = new Vector2(targetVel.X, targetVel.Z);

        var acceleration = _isGrounded ? Friction * 6.0f : AirControl; 

        // If no input on ground, stop quickly (Snappy)
        if (_isGrounded && inputDir.LengthSquared() < 0.01f) {
            acceleration = Friction * 10.0f; 
        }

        // Moves current velocity towards target velocity
        currentH = Vector2.Lerp(currentH, targetH, Math.Clamp(acceleration * dt, 0, 1));

        Velocity.X = currentH.X;
        Velocity.Z = currentH.Y;

        // Jump (Buffered + Coyote)
        if (_jumpBufferTimer > 0 && _coyoteTimer > 0) {
            
            Velocity.Y = JumpForce;
            _jumpBufferTimer = 0;
            _coyoteTimer = 0;
            _isGrounded = false;
        }

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
        
        var minX = x - Width * 0.5f;
        var maxX = x + Width * 0.5f;
        var maxY = y + Height;
        var minZ = z - Width * 0.5f;
        var maxZ = z + Width * 0.5f;

        return world.GetAabbCollision(minX, y, minZ, maxX, maxY, maxZ);
    }

    public Vector3 GetCameraTarget() {

        var dir = new Vector3((float)(Math.Cos(_yaw) * Math.Cos(_pitch)), (float)Math.Sin(_pitch), (float)(Math.Sin(_yaw) * Math.Cos(_pitch)));
        return CameraPosition + dir;
    }
}