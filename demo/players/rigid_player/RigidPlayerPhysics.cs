using Godot;
using MonkeNet.Shared;

namespace GameDemo;

/// <summary>
/// Shared, deterministic rigid-body player simulation. Both
/// <see cref="ServerRigidPlayerStateSyncronizer"/> and
/// <see cref="LocalRigidPlayerPrediction"/> route through this so the server's
/// authoritative tick and the client's predicted tick produce identical body
/// trajectories given identical input — the property the rollback resimulation loop
/// relies on.
///
/// Horizontal motion is driven through Jolt's solver via capped impulses, not by
/// overwriting the body's post-contact velocity. Replacing horizontal velocity with
/// the input target every tick (the previous design) defeats the contact constraint:
/// when a player runs into an immovable surface — a wall, or a cube wedged against
/// a wall — the integrator advances the body by speed × dt before Jolt's constraint
/// solver reabsorbs the freshly-injected velocity, so the body penetrates the
/// surface by ~8 cm/tick during the transient. In-game this presented as the rigid
/// player creeping forward into the cube and snapping back to its original spot
/// every few frames once Jolt's positional correction caught up.
///
/// Capping the per-tick velocity change at MaxHorizontalAccel × dt lets the contact
/// constraint absorb each small impulse before the integrator can shove the body
/// past the surface. Free locomotion still reaches MaxRunSpeed within a few ticks
/// (~100 ms from standstill at 50 m/s²), and the determinism property is preserved:
/// both client and server compute the same target velocity from the same input and
/// add the same delta to whatever the previous tick's contact response left on the
/// body, so identical input still produces identical trajectories.
/// </summary>
public static class RigidPlayerPhysics
{
    public const float MaxRunSpeed = 5f;
    public const float MaxWalkSpeed = 1f;
    public const float JumpVelocity = 6f;

    // Cap on horizontal velocity change per physics tick (m/s²). 50 m/s² ≈ 5 g —
    // high enough that free-walking reaches MaxRunSpeed in ~6 ticks (100 ms at 60 Hz),
    // low enough that one tick of input cannot blow past a contact constraint. The
    // previous SetLinearVelocity path was effectively infinite acceleration and is
    // what produced the wall-penetration / snap-back bug.
    private const float MaxHorizontalAccel = 50f;
    private const float TickDt = 1f / 60f;

    // Ground probe ray: starts a few cm ABOVE body center (so the origin is never
    // on a surface — IntersectRay returns no hit when the origin is on/inside a
    // body) and extends only a small distance below it. The capsule's bottom in
    // LocalRigidPlayer.tscn sits at body.Y (CollisionShape3D offset Y=+1 with
    // capsule half-height 1.0), so body.Y is the ground-contact reference and we
    // only want IsOnGround to fire when something is within ~5 cm below it.
    //
    // The previous 1.5 m reach made IsOnGround return true even when the player
    // was 1.4 m airborne — holding Space during a jump re-fired the jump impulse
    // every tick, the player ascended like a rocket, and across two Jolt
    // instances the cumulative Y trajectory diverged enough to trip the
    // misprediction threshold. A tight ~5 cm tolerance restricts ground detection
    // to genuine "feet on the floor" cases: after the first jump's first
    // SpaceStep the body has already risen ~8 cm, so the next tick's space input
    // is correctly rejected and the player follows a single parabolic arc.
    private const float GroundProbeOriginUp = 0.1f;
    private const float GroundProbeReachDown = 0.05f;

    // Gravity magnitude (m/s²) read from the Godot project setting. Used only by
    // LogPostPhysics to compute the "expected" post-step velocity (preVel + queued
    // impulse + gravity*dt) so residual = post - expected isolates the contact-impulse
    // Jolt applied during the step. Cached at static-init time; if the project setting
    // is missing falls back to 9.81 (matching project.godot default and MainScene.tscn
    // value of 9.8 close enough for diagnostics).
    private static readonly float Gravity = (float)(double)ProjectSettings.GetSetting(
        "physics/3d/default_gravity", 9.81);

    // Phase tag for the [PHYS-RIGIDPLAYER] log line. "live" = called from OnProcessTick
    // (the regular per-tick predict path on client OR server); "resim" = called from
    // ResimulateTick during a client-side rollback resimulation; "spawn-catchup" =
    // called from the spawn-tick catch-up path. Without this tag the log mixes
    // live-tick and resim-tick entries with no way to distinguish them — a misprediction
    // can produce 3-30 PHYS-RIGIDPLAYER lines for the same tick number, all tagged
    // identically, making it impossible to tell which corresponds to the body's real
    // post-step state vs an intermediate resim state.
    /// <summary>
    /// Snapshot of the pre-step state and the impulse this tick's
    /// <see cref="AdvancePhysics"/> queued into the body. Returned by AdvancePhysics
    /// so the caller can hand it to <see cref="LogPostPhysics"/> after SpaceStep
    /// completes and isolate "what Jolt applied during the step" from "what
    /// AdvancePhysics applied before the step" in the post-step diagnostic.
    /// </summary>
    public struct AdvanceResult
    {
        public Vector3 PreVel;
        public Vector3 QueuedImpulse;   // (deltaVel) if applied; plus the Y-component of a jump if it fired
        public bool Jumped;
    }

    public static AdvanceResult AdvancePhysics(PredictionRigidbody3D predictionRb, CharacterInputMessage input, string phase = "live")
    {
        var body = predictionRb?.Body;
        if (body == null) return default;

        var move2D = new Vector2(input.MoveX, input.MoveY);
        float inputMagnitude = Mathf.Min(move2D.Length(), 1f);
        bool isWalking = SharedPlayerMovement.ReadInput(input.Keys, InputFlags.Shift);
        bool isJumping = SharedPlayerMovement.ReadInput(input.Keys, InputFlags.Space);

        Vector3 direction = move2D.IsZeroApprox()
            ? Vector3.Zero
            : new Vector3(move2D.X, 0, move2D.Y).Normalized();
        direction = direction.Rotated(Vector3.Up, input.CameraYaw);

        float targetSpeed = !direction.IsZeroApprox()
            ? (isWalking ? MaxWalkSpeed : MaxRunSpeed) * inputMagnitude
            : 0f;
        Vector3 desiredHoriz = direction * targetSpeed;

        // Capped impulse toward the desired horizontal velocity. Y stays untouched
        // so gravity and contact lift flow through unchanged.
        Vector3 currentHoriz = new Vector3(body.LinearVelocity.X, 0, body.LinearVelocity.Z);
        Vector3 deltaVel = desiredHoriz - currentHoriz;
        float maxDelta = MaxHorizontalAccel * TickDt;
        if (deltaVel.LengthSquared() > maxDelta * maxDelta)
            deltaVel = deltaVel.Normalized() * maxDelta;

        bool onGround = IsOnGround(body);

        // Mirrors SharedPlayerMovement's [PHYS-PLAYER] block. RigidBody3D's SpaceStep is
        // run by the framework after this method returns, so postPos/postSlideVel can't
        // be logged here — those land in [PRED-REG] / the next tick's preVel. The shape
        // query below is the rigid-body analogue of MoveAndSlide's slide list: it reports
        // the bodies currently in contact going INTO the step.
        Vector3 prePos = body.GlobalPosition;
        Vector3 preVel = body.LinearVelocity;
        Quaternion preRot = body.Quaternion;
        Vector3 preAngVel = body.AngularVelocity;
        var contacts = QueryContacts(body);
        MonkeLogger.Debug($"[PHYS-RIGIDPLAYER] phase={phase} body={body.Name} input=({input}) desiredHoriz=({desiredHoriz.X:F3},{desiredHoriz.Z:F3}) deltaVel=({deltaVel.X:F3},{deltaVel.Z:F3}) prePos=({prePos.X:F3},{prePos.Y:F3},{prePos.Z:F3}) preVel=({preVel.X:F3},{preVel.Y:F3},{preVel.Z:F3}) preRot=({preRot.X:F3},{preRot.Y:F3},{preRot.Z:F3},{preRot.W:F3}) preAngVel=({preAngVel.X:F3},{preAngVel.Y:F3},{preAngVel.Z:F3}) preContacts={contacts.Count} onGround={onGround} jumping={isJumping} walking={isWalking} sleeping={body.Sleeping}");
        for (int i = 0; i < contacts.Count; i++)
        {
            var c = contacts[i];
            MonkeLogger.Debug($"[PHYS-RIGIDPLAYER]   preContact[{i}] collider={c.Name} at=({c.Position.X:F3},{c.Position.Y:F3},{c.Position.Z:F3})");
        }

        Vector3 horizImpulse = new(deltaVel.X, 0, deltaVel.Z);
        if (deltaVel.LengthSquared() > 0)
            predictionRb.AddImpulse(horizImpulse * body.Mass);

        bool jumped = false;
        if (isJumping && onGround)
        {
            // Jump: replace Y velocity outright so we don't compound an existing fall
            // velocity. Done after the horizontal impulse so the queued ops both flush
            // in one Simulate call.
            Vector3 jumpVel = body.LinearVelocity;
            jumpVel.Y = JumpVelocity;
            predictionRb.SetLinearVelocity(jumpVel);
            jumped = true;
        }

        predictionRb.Simulate();

        // Effective velocity delta this tick queued by AdvancePhysics (before
        // SpaceStep runs). For a non-jump tick this is the horizontal-only capped
        // delta. For a jump tick the Y velocity was REPLACED (not added) — express
        // that as a delta from preVel.Y so LogPostPhysics' "expected post = preVel
        // + queuedImpulse + gravity*dt" formula remains correct.
        Vector3 queuedImpulse = horizImpulse;
        if (jumped) queuedImpulse.Y = JumpVelocity - preVel.Y;

        return new AdvanceResult
        {
            PreVel = preVel,
            QueuedImpulse = queuedImpulse,
            Jumped = jumped,
        };
    }

    // Post-step diagnostic. Call AFTER SpaceStep completes (so the body holds its
    // post-step pose and velocity, and any Jolt-internal contact impulses have been
    // resolved). Reports:
    //   - post-step pos / vel / rot
    //   - contact list at the post-step pose (what the body is touching now)
    //   - "unexplained" velocity delta (post − expected) where expected = preVel +
    //     (queued impulse / mass) + gravity*dt; anything beyond a small ULP-tolerance
    //     here was applied by Jolt during the step itself, i.e. a contact response
    //     (or wake impulse, or some other Jolt-internal effect).
    //
    // The combination of these is what disambiguates "the body collided with X
    // during the step" from "AdvancePhysics applied a strange impulse". The
    // pre-step contact list inside AdvancePhysics misses contacts that come into
    // existence DURING the step (the player capsule moves into a cube at
    // 5 m/s and the broadphase only flags it once the swept AABB crosses) —
    // exactly the case at tick=943 of the user's 2026-05-19 18:48 session, where
    // pre-step contacts=0 but the body's post-step velocity changed by 4 m/s
    // along the contact normal.
    public static void LogPostPhysics(RigidBody3D body, Vector3 preVel, Vector3 expectedImpulse, string phase = "live")
    {
        if (body == null) return;
        Vector3 postPos = body.GlobalPosition;
        Vector3 postVel = body.LinearVelocity;
        Vector3 postAngVel = body.AngularVelocity;
        // Expected post-step velocity assuming free-flight (impulse + gravity).
        // Anything else in the residual is contact / Jolt-internal.
        Vector3 expectedPost = preVel + expectedImpulse + new Vector3(0, -Gravity, 0) * TickDt;
        Vector3 residual = postVel - expectedPost;
        var contacts = QueryContacts(body);
        MonkeLogger.Debug($"[PHYS-RIGIDPLAYER-POST] phase={phase} body={body.Name} postPos=({postPos.X:F3},{postPos.Y:F3},{postPos.Z:F3}) postVel=({postVel.X:F3},{postVel.Y:F3},{postVel.Z:F3}) postAngVel=({postAngVel.X:F3},{postAngVel.Y:F3},{postAngVel.Z:F3}) residualVel=({residual.X:F3},{residual.Y:F3},{residual.Z:F3}) |residual|={residual.Length():F3}m/s postContacts={contacts.Count}");
        for (int i = 0; i < contacts.Count; i++)
        {
            var c = contacts[i];
            MonkeLogger.Debug($"[PHYS-RIGIDPLAYER-POST]   postContact[{i}] collider={c.Name} at=({c.Position.X:F3},{c.Position.Y:F3},{c.Position.Z:F3})");
        }
    }

    private struct ContactInfo
    {
        public string Name;
        public Vector3 Position;
    }

    /// <summary>
    /// Returns the live RigidBody3D references this body is currently overlapping.
    /// Same query plumbing as <see cref="QueryContacts"/> (shape cast against the
    /// body's own collision mask, excluding itself) but exposes the actual
    /// collider Godot objects rather than just names, so the caller can walk up
    /// the scene tree to the owning ClientPredictedEntity and upgrade it to
    /// Resim tier. Marked <c>internal</c> so the prediction-side caller in the
    /// same assembly (<see cref="LocalRigidPlayerPrediction"/>) can use it
    /// without exposing the contact-list internals to demo gameplay code that
    /// shouldn't reach into the player's physics.
    /// </summary>
    internal static System.Collections.Generic.List<RigidBody3D> QueryContactBodies(RigidBody3D body)
    {
        var hits = new System.Collections.Generic.List<RigidBody3D>();
        if (body == null) return hits;
        var space = body.GetWorld3D()?.DirectSpaceState;
        if (space == null) return hits;

        Node collisionShapeNode = null;
        foreach (Node child in body.GetChildren())
        {
            if (child is CollisionShape3D cs && cs.Shape != null)
            {
                collisionShapeNode = cs;
                break;
            }
        }
        if (collisionShapeNode is not CollisionShape3D shapeNode) return hits;

        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = shapeNode.Shape,
            Transform = shapeNode.GlobalTransform,
            CollisionMask = body.CollisionMask,
            CollideWithBodies = true,
            CollideWithAreas = false,
            Exclude = new Godot.Collections.Array<Rid> { body.GetRid() },
        };

        var results = space.IntersectShape(query, maxResults: 8);
        foreach (var hit in results)
        {
            if (hit.TryGetValue("collider", out var cv) && cv.AsGodotObject() is RigidBody3D rb)
                hits.Add(rb);
        }
        return hits;
    }

    // Shape query against the body's own collision mask, excluding itself, to find what
    // it's currently overlapping. Equivalent in spirit to CharacterBody3D's slide
    // collisions, but read pre-step (slides are only resolved during SpaceStep on a
    // RigidBody3D, and contact_monitor isn't enabled on the demo player scene).
    private static System.Collections.Generic.List<ContactInfo> QueryContacts(RigidBody3D body)
    {
        var hits = new System.Collections.Generic.List<ContactInfo>();
        var space = body.GetWorld3D()?.DirectSpaceState;
        if (space == null) return hits;

        Node collisionShapeNode = null;
        foreach (Node child in body.GetChildren())
        {
            if (child is CollisionShape3D cs && cs.Shape != null)
            {
                collisionShapeNode = cs;
                break;
            }
        }
        if (collisionShapeNode is not CollisionShape3D shapeNode) return hits;

        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = shapeNode.Shape,
            Transform = shapeNode.GlobalTransform,
            CollisionMask = body.CollisionMask,
            CollideWithBodies = true,
            CollideWithAreas = false,
            Exclude = new Godot.Collections.Array<Rid> { body.GetRid() },
        };

        var results = space.IntersectShape(query, maxResults: 8);
        foreach (var hit in results)
        {
            string name = (hit.TryGetValue("collider", out var cv) && cv.AsGodotObject() is Node n) ? n.Name : "<null>";
            Vector3 pos = body.GlobalPosition;
            hits.Add(new ContactInfo { Name = name, Position = pos });
        }
        return hits;
    }

    private static bool IsOnGround(RigidBody3D body)
    {
        var space = body.GetWorld3D()?.DirectSpaceState;
        if (space == null) return false;

        Vector3 origin = body.GlobalPosition;
        Vector3 from = origin + Vector3.Up * GroundProbeOriginUp;
        Vector3 to = origin + Vector3.Down * GroundProbeReachDown;

        var query = PhysicsRayQueryParameters3D.Create(from, to);
        // Reuse the body's own collision mask so the player only "stands" on the same
        // layers it physically collides with — environment in single-player, plus
        // ServerPlayers/ClientPlayers split in listen-server mode.
        query.CollisionMask = body.CollisionMask;
        query.Exclude = new Godot.Collections.Array<Rid> { body.GetRid() };

        var hit = space.IntersectRay(query);
        return hit.Count > 0;
    }
}
