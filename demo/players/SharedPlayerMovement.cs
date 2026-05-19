using Godot;
using ImGuiNET;
using MonkeNet.Shared;

namespace GameDemo;

public enum InputFlags
{
    Space = 0b_0000_0001,
    Shift = 0b_0000_0010,
    Interact = 0b_0000_0100,
}

/// <summary>
/// Shared player movement code, used to move both client and server players.
/// </summary>
public partial class SharedPlayerMovement : Node
{
    [Export] private CharacterBody3D _characterBody;
    [Export] private float _rigidBodyPushStrength = 1.5f;
    public static readonly float MaxRunSpeed = 5;
    public static readonly float MaxWalkSpeed = 2;
    public static readonly float Gravity = 9.8f;
    public static readonly float JumpVelocity = 6.0f;

    // Layer 3 = OfflineProps (project.godot). Bodies on this layer don't physically
    // collide with the player, so PushOfflineProps does an explicit shape query to
    // detect overlaps and apply one-way impulses (player pushes them, they don't push back).
    private const uint OfflinePropsLayerMask = 1u << 2;

    private CollisionShape3D _cachedCollisionShape;

    /// <summary>
    /// Running count of <see cref="AdvancePhysics"/> calls in which the player slid
    /// against at least one <see cref="RigidBody3D"/>. Surfaced in the ImGui overlay so
    /// the user can compare client- vs server-side rigidbody contact frequency. Note:
    /// the client side increments on resim ticks too, so under sustained misprediction
    /// the client count will run slightly ahead of the server's; that divergence is the
    /// signal, not an artefact.
    /// </summary>
    public int RigidbodyContactTicks { get; private set; }

    /// <summary>
    /// Wires the movement controller to a CharacterBody3D programmatically. Used by
    /// tests; in scenes the [Export] field is set in the editor.
    /// </summary>
    public void Initialize(CharacterBody3D body)
    {
        _characterBody = body;
    }

    public void AdvancePhysics(CharacterInputMessage input)
    {
        Vector3 prePos = _characterBody.GlobalPosition;
        Vector3 newVelocity = CalculateVelocity(_characterBody, input);
        _characterBody.Velocity = newVelocity;
        PhysicsUtils.MoveAndSlide(_characterBody);
        Vector3 postPos = _characterBody.GlobalPosition;
        int slides = _characterBody.GetSlideCollisionCount();
        MonkeLogger.Debug($"[PHYS-PLAYER] body={_characterBody.Name} input=({input}) attemptedVel=({newVelocity.X:F3},{newVelocity.Y:F3},{newVelocity.Z:F3}) postSlideVel=({_characterBody.Velocity.X:F3},{_characterBody.Velocity.Y:F3},{_characterBody.Velocity.Z:F3}) prePos=({prePos.X:F3},{prePos.Y:F3},{prePos.Z:F3}) postPos=({postPos.X:F3},{postPos.Y:F3},{postPos.Z:F3}) slides={slides} onFloor={_characterBody.IsOnFloor()}");
        bool hitRigidbodyThisCall = false;
        for (int i = 0; i < slides; i++)
        {
            var c = _characterBody.GetSlideCollision(i);
            string colliderName = c.GetCollider() is Node n ? n.Name : "<null>";
            Vector3 normal = c.GetNormal();
            Vector3 cpos = c.GetPosition();
            MonkeLogger.Debug($"[PHYS-PLAYER]   slide[{i}] collider={colliderName} normal=({normal.X:F3},{normal.Y:F3},{normal.Z:F3}) at=({cpos.X:F3},{cpos.Y:F3},{cpos.Z:F3})");
            if (c.GetCollider() is RigidBody3D) hitRigidbodyThisCall = true;
        }
        if (hitRigidbodyThisCall) RigidbodyContactTicks++;
        // Pass the pre-MoveAndSlide velocity. MoveAndSlide zeroes the component into any
        // surface it slides against, so reading _characterBody.Velocity after the call
        // would always report ~0 component into a head-on collision and we'd never push.
        PushRigidBodies(newVelocity);
        PushOfflineProps(newVelocity);
    }

    // CharacterBody3D.MoveAndSlide does not push RigidBody3Ds it slides against — it just
    // resolves the collision against the player. To push the ball, apply impulse along the
    // collision normal scaled by how fast the player was moving into the body.
    private void PushRigidBodies(Vector3 attemptedVelocity)
    {
        int count = _characterBody.GetSlideCollisionCount();
        for (int i = 0; i < count; i++)
        {
            var collision = _characterBody.GetSlideCollision(i);
            if (collision.GetCollider() is not RigidBody3D rb)
                continue;

            Vector3 pushDir = -collision.GetNormal();
            // Drive the impulse from the player's INPUT velocity only — not the relative
            // velocity (attemptedVel - rb.LinearVelocity). On a listen server the same
            // cube is simulated independently on the client- and server-layer pairs by
            // Jolt; its live LinearVelocity drifts by tiny floating-point amounts every
            // tick, and feeding that drift into the impulse turns sub-millimetre cube
            // divergences into systematically different player impulses on each side.
            // The compounding mismatch shows up as steady player mispredictions whenever
            // the player walks into a pile of cubes / a vehicle (see logs
            // monke-net_2026-05-11_13-39-30_pid23300.log vs pid33672.log: player z velocity
            // diverging 1.77 → 2.51 while static cubes show zero delta).
            float speedIntoBody = attemptedVelocity.Dot(pushDir);
            if (speedIntoBody <= 0f)
                continue;

            Vector3 impulse = pushDir * speedIntoBody * _rigidBodyPushStrength;
            Vector3 contactOffset = collision.GetPosition() - rb.GlobalPosition;
            MonkeLogger.Debug($"[PHYS-PUSH] kind=rigid pusher={_characterBody.Name} pushed={rb.Name} impulse=({impulse.X:F3},{impulse.Y:F3},{impulse.Z:F3}) at=({contactOffset.X:F3},{contactOffset.Y:F3},{contactOffset.Z:F3}) speedInto={speedIntoBody:F3}");

            // Apply directly on the body, not through PredictionRigidbody3D's queue: ball
            // entities (LocalBall/ServerBall) don't call Simulate() in their tick handlers
            // so a queued impulse never reaches the body. The push is still resim-correct
            // because the player's ResimulateTick re-runs MoveAndSlide + PushRigidBodies,
            // which re-detects the collision and reapplies this impulse during rollback.
            rb.ApplyImpulse(impulse, contactOffset);
        }
    }

    // OfflineRigidbody3D bodies (e.g. the demo's OfflineBall) sit on layer 3 with a mask
    // of just Environment, so the player's CharacterBody3D walks straight through them —
    // necessary because a bidirectional collision would push the player back and desync
    // it from the server (which has no offline-ball collider for the server-player), and
    // the resulting reconciliation jerks the camera. To still let the player push these
    // bodies, do a separate shape query on the OfflineProps layer and apply impulses
    // one-way: player → body.
    private void PushOfflineProps(Vector3 attemptedVelocity)
    {
        if (_cachedCollisionShape == null)
        {
            foreach (Node child in _characterBody.GetChildren())
            {
                if (child is CollisionShape3D cs && cs.Shape != null)
                {
                    _cachedCollisionShape = cs;
                    break;
                }
            }
            if (_cachedCollisionShape == null) return;
        }

        var space = _characterBody.GetWorld3D().DirectSpaceState;
        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = _cachedCollisionShape.Shape,
            Transform = _cachedCollisionShape.GlobalTransform,
            CollisionMask = OfflinePropsLayerMask,
            CollideWithBodies = true,
            CollideWithAreas = false,
            Exclude = new Godot.Collections.Array<Rid> { _characterBody.GetRid() },
        };

        var hits = space.IntersectShape(query, maxResults: 8);
        foreach (var hit in hits)
        {
            if (!hit.TryGetValue("collider", out var colliderVar)) continue;
            if (colliderVar.AsGodotObject() is not RigidBody3D rb) continue;

            // Push horizontally toward the body, scaled by how fast the player is moving
            // into it. Y is zeroed so a player taller than the body doesn't shove it
            // through the floor.
            Vector3 toBody = rb.GlobalPosition - _characterBody.GlobalPosition;
            toBody.Y = 0;
            if (toBody.LengthSquared() < 0.0001f) continue;
            Vector3 dir = toBody.Normalized();

            float speedIntoBody = attemptedVelocity.Dot(dir);
            if (speedIntoBody <= 0f) continue;

            Vector3 impulse = dir * speedIntoBody * _rigidBodyPushStrength;
            Vector3 contactOffset = _characterBody.GlobalPosition - rb.GlobalPosition;
            MonkeLogger.Debug($"[PHYS-PUSH] kind=offlineProp pusher={_characterBody.Name} pushed={rb.Name} impulse=({impulse.X:F3},{impulse.Y:F3},{impulse.Z:F3}) at=({contactOffset.X:F3},{contactOffset.Y:F3},{contactOffset.Z:F3}) speedInto={speedIntoBody:F3}");
            rb.ApplyImpulse(impulse, contactOffset);
        }
    }

    public static Vector3 CalculateVelocity(CharacterBody3D body, CharacterInputMessage input)
    {
        // MoveX/MoveY are analog: -1..1 from either keyboard or controller stick.
        // Clamp the 2D magnitude to 1 so keyboard diagonals don't exceed max speed,
        // while partial stick tilts produce proportionally reduced speed.
        var move2D = new Vector2(input.MoveX, input.MoveY);
        float inputMagnitude = Mathf.Min(move2D.Length(), 1f);

        bool isWalking = ReadInput(input.Keys, InputFlags.Shift);
        bool isJumping = ReadInput(input.Keys, InputFlags.Space);
        Vector3 velocity = body.Velocity;

        bool isOnFloor = body.IsOnFloor();
        Vector3 direction = move2D.IsZeroApprox()
            ? Vector3.Zero
            : new Vector3(move2D.X, 0, move2D.Y).Normalized();
        direction = direction.Rotated(Vector3.Up, input.CameraYaw);

        if (!direction.IsZeroApprox())
        {
            float speed = (isWalking ? MaxWalkSpeed : MaxRunSpeed) * inputMagnitude;
            velocity.X = direction.X * speed;
            velocity.Z = direction.Z * speed;
        }
        else
        {
            velocity.X = 0;
            velocity.Z = 0;
        }

        if (!isOnFloor)
            velocity.Y -= Gravity * PhysicsUtils.DeltaTime;

        if (isJumping && isOnFloor)
            velocity.Y = JumpVelocity;

        return velocity;
    }

    public static bool ReadInput(byte input, InputFlags flag)
    {
        return (input & (byte)flag) > 0;
    }

    public void DisplayDebugInformation()
    {
        if (ImGui.CollapsingHeader("Movement"))
        {
            ImGui.Text($"Position ({_characterBody.GlobalPosition.X:0.00}, {_characterBody.GlobalPosition.Y:0.00}, {_characterBody.GlobalPosition.Z:0.00})");
            ImGui.Text($"Velocity ({_characterBody.Velocity.X:0.00}, {_characterBody.Velocity.Y:0.00}, {_characterBody.Velocity.Z:0.00})");
        }
    }
}
