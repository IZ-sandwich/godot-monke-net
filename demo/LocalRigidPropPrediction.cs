using Godot;
using MonkeNet.Client;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace GameDemo;

/// <summary>
/// Generic client-side prediction wrapper for a server-authoritative rigid prop
/// (ball, cube, or any other RigidBody3D-backed entity). The body simulates
/// locally every physics tick and is reconciled against the server's snapshot
/// once per tick; sub-threshold drift is absorbed by <see cref="PredictionVisualSmoothing3D"/>,
/// and steady-state rest is anchored via <c>SyncSleepState</c> →
/// <see cref="PredictionRigidbody3D.SnapToRest"/> so persistent contact manifolds
/// don't get invalidated every snapshot.
///
/// Used by <c>LocalBall.tscn</c>, <c>LocalCube.tscn</c>, and <c>DummyVehicle.tscn</c>.
/// Originally lived as <c>LocalBallPrediction</c> in <c>demo/ball/</c>; renamed
/// once it was clear the script applied to any rigid prop, not just balls.
/// </summary>
public partial class LocalRigidPropPrediction : ClientPredictedEntity
{
    // Mirror LocalVehiclePrediction: 20 cm tolerance accommodates Jolt collision-response
    // nondeterminism between client and server (contact normals, friction, persistent-
    // contact caches all diverge by a few cm per impact). 3 cm causes every wall/player/
    // vehicle hit to trigger reconcile, which then desyncs the prop further.
    [Export] private float _maxDeviationAllowedSquared = 0.04f;
    // Linear-velocity divergence threshold (squared, m²/s²). Without this the ball can
    // share the server's position to within tolerance while carrying noticeably different
    // momentum — the wrong velocity then writes new wrong positions every tick until the
    // position threshold trips and a hard snap fires. ~0.5 m/s default.
    [Export] private float _maxVelocityDeviationSquared = 0.25f;
    // Rotation divergence threshold (degrees). Catches the case where position and linear
    // velocity stay within bounds but cross-process Jolt drift accumulates yaw / tumble
    // error during a push. Without this, rotation only ever gets corrected by SyncSleepState
    // at rest — visible to the player as a flip at end-of-motion (the visual smoother now
    // hides this on the rendered mesh, but the body's contact basis still snaps). 5° is
    // loose enough not to fire on per-tick contact noise (typical drift during a 1-2 s
    // push is ~3° on a knocked-around vehicle, well under the threshold) but tight enough
    // to catch real divergence before it compounds into a player-visible orientation flip.
    [Export] private float _maxRotationDeviationDegrees = 5f;
    [Export] private PredictionRigidbody3D _predictionRb;

    public override void OnProcessTick(int tick, IPackableElement input) { }

    public override Vector3 GetPosition()
    {
        return _predictionRb.Body.GlobalPosition;
    }

    public override RigidbodyState GetSnapshotState() => _predictionRb.SnapshotState();

    public override Vector3 ExtractAuthoritativePosition(IEntityStateData state) =>
        ((EntityStateMessage)state).Position;

    public override bool HasMisspredicted(int tick, IEntityStateData receivedState, RigidbodyState savedState)
    {
        EntityStateMessage state = (EntityStateMessage)receivedState;
        if ((state.Position - savedState.Position).LengthSquared() > _maxDeviationAllowedSquared)
            return true;
        if ((state.Velocity - savedState.LinearVelocity).LengthSquared() > _maxVelocityDeviationSquared)
            return true;
        if (state.Rotation.AngleTo(savedState.Rotation) > Mathf.DegToRad(_maxRotationDeviationDegrees))
            return true;
        return false;
    }

    public override void HandleReconciliation(IEntityStateData receivedState)
    {
        var state = (EntityStateMessage)receivedState;
        MonkeLogger.Debug($"[ENTITY-RECONCILE] LocalRigidProp eid={EntityId} auth={state}");
        _predictionRb.Reconcile(new RigidbodyState
        {
            Position = state.Position,
            Rotation = state.Rotation,
            LinearVelocity = state.Velocity,
            AngularVelocity = state.AngularVelocity,
        });
        SyncSleepState(state);
    }

    public override void ResimulateTick(IPackableElement input) { }

    public override void RestoreBodyState(RigidbodyState state) => _predictionRb.Reconcile(state);

    public override void ApplyAuthoritativeNonPoseState(IEntityStateData receivedState)
    {
        // Keep the client body's sleep state aligned with the server's even
        // when no full reconcile fires. If the server cube has settled to
        // sleep (vel and angvel both ~zero) and the client's Jolt instance
        // has not yet, the client's body keeps producing tiny per-tick
        // contact micro-impulses that drift it away from the sleeping
        // server state. Without this per-snapshot sync the drift takes
        // many ticks to cross the hard reconcile threshold, by which time
        // sleep state on the two processes can be more than 5 ticks apart
        // — exactly the budget the CubeRest test enforces. Conversely if
        // the server cube has woken (player pushed it), wake the client
        // cube so contact response simulates in lockstep.
        SyncSleepState((EntityStateMessage)receivedState);
    }

    // Client body must be at near-rest BEFORE we force it to sleep — this prevents
    // the locally-predicted player from being frozen the instant it hits a cube.
    // When the player contacts a cube on the client, Jolt wakes the cube and gives
    // it contact-response velocity; the matching snapshot from the server for that
    // tick (latency ticks behind real time) still reports the cube as sleeping, so
    // without this guard SyncSleepState would re-sleep the cube every snapshot —
    // turning the cube into an infinite-mass wall and stopping the player dead in
    // its tracks. 0.25 m/s linear is well above contact-impulse jitter but well
    // below any "actually moving cube" velocity (cubes pushed by the player
    // accelerate past 1 m/s in a tick).
    private const float ClientNearRestVelocitySquared = 0.0625f;

    private void SyncSleepState(EntityStateMessage state)
    {
        var body = _predictionRb?.Body;
        if (body == null) return;

        bool clientNearRest = body.LinearVelocity.LengthSquared() < ClientNearRestVelocitySquared
                              && body.AngularVelocity.LengthSquared() < ClientNearRestVelocitySquared;

        // Server-driven sleep hint replaces velocity inference. RigidBody3D.Sleeping
        // is the authoritative bit Jolt itself toggles when its sleep timer expires;
        // mirroring it directly removes flicker on the wake-up tick (where velocity
        // can briefly be zero on a body that's been impulsed but not yet integrated)
        // and on the going-to-sleep tick (where velocity can still be 0.02 m/s on a
        // body Jolt has already decided is asleep).
        if (!state.ServerSleeping || !clientNearRest)
        {
            // Server is awake, or the client body is moving — leave the body alone
            // and let the normal predict / misprediction path handle the rest.
            return;
        }

        // Surgical re-anchor. SnapToRest does its own sub-mm / sub-0.1° / sub-μm/s
        // idempotency check (see PredictionRigidbody3D.SnapToRest) and only writes
        // the deltas that actually differ. When the body is already sitting on the
        // server pose this is a complete no-op — no transform write, no broadphase
        // invalidation, no manifold churn — so calling it every snapshot while
        // both sides are at rest is essentially free. An external "is the body
        // already at rest here" latch in this caller would just duplicate
        // SnapToRest's own tolerance check and create a separate source of truth
        // for "are we currently anchored" that has to be invalidated whenever the
        // server re-poses an otherwise-sleeping body (teleport, scripted nudge);
        // letting SnapToRest decide every time keeps a single source of truth.
        //
        // We do NOT call Body.Sleeping = true. Putting the body in Jolt's sleeping
        // state makes it respond to contact as if it were briefly kinematic until
        // Jolt's solver wakes it on a later tick, which slammed the predicted player
        // to a dead stop the moment it touched a cube. Leaving the body awake-but-
        // at-rest lets Jolt's normal contact resolution run on the first impact (cube
        // absorbs momentum, player keeps some forward velocity), and Jolt's own sleep
        // threshold + timer puts the body back to sleep cleanly between contacts.
        _predictionRb.SnapToRest(state.Position, state.Rotation.Normalized());
    }
}
