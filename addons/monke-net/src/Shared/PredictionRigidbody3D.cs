using Godot;
using System.Collections.Generic;

namespace MonkeNet.Shared;

/// <summary>
/// Prediction-friendly wrapper around a <see cref="RigidBody3D"/>. Mirrors Fish-Net's
/// PredictionRigidbody: forces, impulses, torques, and velocity sets are queued via
/// the wrapper instead of applied to the body directly. <see cref="Simulate"/> flushes
/// the queue to the body once per tick. This makes resimulation deterministic — the
/// same call sequence in <c>OnProcessTick</c> and <c>ResimulateTick</c> produces the
/// same body state when followed by <c>SpaceStep</c>.
///
/// Pair with <see cref="ClientPredictedEntity"/>: the entity's tick handler enqueues
/// inputs through this wrapper, the framework calls <c>Simulate</c>, then
/// <c>SpaceStep</c> integrates. <see cref="Reconcile"/> restores the body to an
/// authoritative snapshot before the resimulation loop replays subsequent ticks.
/// </summary>
[GlobalClass, Icon("res://addons/monke-net/resources/circle_nodes_solid.png")]
public partial class PredictionRigidbody3D : Node
{
    [Export] private RigidBody3D _body;

    /// <summary>
    /// Optional. When set, the smoother auto-detects body teleports (from
    /// Reconcile, SyncSleepState, or any other transform write) and absorbs them
    /// into a decaying visual offset. No notification call is needed — wiring
    /// the smoother in the inspector is enough. Leave null on entities that
    /// don't need it (sphere meshes where rotation snaps are invisible, props
    /// that never jump under normal play).
    /// </summary>
    [Export] private PredictionVisualSmoothing3D _smoothing;

    public RigidBody3D Body => _body;

    /// <summary>
    /// Optional visual smoother wired in the inspector. Exposed for diagnostics
    /// / tests that want to observe smoothing state. Returns null when none is
    /// wired.
    /// </summary>
    public PredictionVisualSmoothing3D Smoothing => _smoothing;

    /// <summary>
    /// Enables contact-monitor on the wrapped <see cref="RigidBody3D"/> and
    /// connects body_entered / body_exited signals so each contact pair that
    /// starts or ends during physics integration emits a [CONTACT-START] /
    /// [CONTACT-END] debug log. Used to diagnose stack-spawn races where the
    /// server's body experiences a contact one or more ticks before the client's
    /// replica exists locally: with this enabled the server log directly shows
    /// "[CONTACT-START] eid=3 with body=ServerCube (auth=0)" at server tick T,
    /// while the client log shows the same event at a later tick — proof that
    /// the contact happened asymmetrically rather than inferring it from
    /// position/velocity divergence.
    ///
    /// Default 4 reported contacts — the most cubes the framework expects to
    /// touch any one body at the same time in the existing demos. Adjust per
    /// body if a different ceiling is needed.
    /// </summary>
    [Export] public int MaxContactsReportedForDiagnostics { get; set; } = 4;

    public override void _Ready()
    {
        if (_body == null) return;
        // Contact monitoring is OPT-IN per body in Godot. Enable it here so the
        // body emits body_entered / body_exited signals for diagnostic logging.
        // Note: this has a (very small) per-physics-tick cost; if profiles ever
        // surface it as a hot spot, gate it on a debug flag.
        _body.ContactMonitor = true;
        _body.MaxContactsReported = MaxContactsReportedForDiagnostics;
        _body.BodyEntered += OnBodyContactStart;
        _body.BodyExited += OnBodyContactEnd;
    }

    public override void _ExitTree()
    {
        if (_body == null) return;
        // Defensive — the parent rigid body may be freed independently in some
        // teardown orders (rare, but harmless to be careful).
        if (IsInstanceValid(_body))
        {
            _body.BodyEntered -= OnBodyContactStart;
            _body.BodyExited -= OnBodyContactEnd;
        }
    }

    private void OnBodyContactStart(Node body)
    {
        if (_body == null || body == null) return;
        MonkeLogger.Debug(
            $"[CONTACT-START] body={_body.Name} (layer={_body.CollisionLayer} mask={_body.CollisionMask}) " +
            $"otherBody={body.Name} otherType={body.GetType().Name} " +
            $"thisPos=({_body.GlobalPosition.X:F3},{_body.GlobalPosition.Y:F3},{_body.GlobalPosition.Z:F3}) " +
            $"thisVel=({_body.LinearVelocity.X:F3},{_body.LinearVelocity.Y:F3},{_body.LinearVelocity.Z:F3})");
    }

    private void OnBodyContactEnd(Node body)
    {
        if (_body == null || body == null) return;
        MonkeLogger.Debug(
            $"[CONTACT-END] body={_body.Name} otherBody={body.Name} " +
            $"thisVel=({_body.LinearVelocity.X:F3},{_body.LinearVelocity.Y:F3},{_body.LinearVelocity.Z:F3})");
    }

    private enum PendingKind
    {
        Force,
        ForceAtPosition,
        Impulse,
        ImpulseAtPosition,
        Torque,
        TorqueImpulse,
        SetLinearVelocity,
        SetAngularVelocity,
    }

    private struct PendingOp
    {
        public PendingKind Kind;
        public Vector3 Vector;
        public Vector3 Position;
    }

    private readonly List<PendingOp> _pending = new();

    /// <summary>
    /// Wires the wrapper to a RigidBody3D programmatically (used by tests; in scenes
    /// the [Export] field is set in the editor). Safe to call multiple times.
    /// </summary>
    public void Initialize(RigidBody3D body, PredictionVisualSmoothing3D smoothing = null)
    {
        _body = body;
        _smoothing = smoothing;
        _pending.Clear();
    }

    public void AddForce(Vector3 force) =>
        _pending.Add(new PendingOp { Kind = PendingKind.Force, Vector = force });

    public void AddForceAtPosition(Vector3 force, Vector3 position) =>
        _pending.Add(new PendingOp { Kind = PendingKind.ForceAtPosition, Vector = force, Position = position });

    public void AddImpulse(Vector3 impulse) =>
        _pending.Add(new PendingOp { Kind = PendingKind.Impulse, Vector = impulse });

    public void AddImpulseAtPosition(Vector3 impulse, Vector3 position) =>
        _pending.Add(new PendingOp { Kind = PendingKind.ImpulseAtPosition, Vector = impulse, Position = position });

    public void AddTorque(Vector3 torque) =>
        _pending.Add(new PendingOp { Kind = PendingKind.Torque, Vector = torque });

    public void AddTorqueImpulse(Vector3 torque) =>
        _pending.Add(new PendingOp { Kind = PendingKind.TorqueImpulse, Vector = torque });

    /// <summary>
    /// Queues an absolute set of linear velocity. Use sparingly — overrides whatever
    /// the simulation produced. Useful for clamping max speed or directional jumps.
    /// </summary>
    public void SetLinearVelocity(Vector3 velocity) =>
        _pending.Add(new PendingOp { Kind = PendingKind.SetLinearVelocity, Vector = velocity });

    public void SetAngularVelocity(Vector3 velocity) =>
        _pending.Add(new PendingOp { Kind = PendingKind.SetAngularVelocity, Vector = velocity });

    /// <summary>
    /// Flushes all queued operations to the underlying RigidBody3D. Call once per
    /// tick after the entity's tick handler and before <c>PhysicsServer3D.SpaceStep</c>.
    /// </summary>
    public void Simulate()
    {
        if (_body == null) return;
        if (_pending.Count > 0)
        {
            MonkeLogger.Debug($"[PHYS-RB-SIMULATE] body={_body.Name} pending={_pending.Count} prePos=({_body.GlobalPosition.X:F3},{_body.GlobalPosition.Y:F3},{_body.GlobalPosition.Z:F3}) preVel=({_body.LinearVelocity.X:F3},{_body.LinearVelocity.Y:F3},{_body.LinearVelocity.Z:F3})");
        }
        for (int i = 0; i < _pending.Count; i++)
        {
            var p = _pending[i];
            string atSuffix = (p.Kind == PendingKind.ForceAtPosition || p.Kind == PendingKind.ImpulseAtPosition)
                ? $" at=({p.Position.X:F3},{p.Position.Y:F3},{p.Position.Z:F3})" : "";
            MonkeLogger.Debug($"[PHYS-RB-SIMULATE]   {p.Kind} v=({p.Vector.X:F3},{p.Vector.Y:F3},{p.Vector.Z:F3}){atSuffix}");
            switch (p.Kind)
            {
                case PendingKind.Force: _body.ApplyCentralForce(p.Vector); break;
                case PendingKind.ForceAtPosition: _body.ApplyForce(p.Vector, p.Position); break;
                case PendingKind.Impulse: _body.ApplyCentralImpulse(p.Vector); break;
                case PendingKind.ImpulseAtPosition: _body.ApplyImpulse(p.Vector, p.Position); break;
                case PendingKind.Torque: _body.ApplyTorque(p.Vector); break;
                case PendingKind.TorqueImpulse: _body.ApplyTorqueImpulse(p.Vector); break;
                case PendingKind.SetLinearVelocity: _body.LinearVelocity = p.Vector; break;
                case PendingKind.SetAngularVelocity: _body.AngularVelocity = p.Vector; break;
            }
        }
        _pending.Clear();
    }

    /// <summary>Number of pending operations not yet flushed by <see cref="Simulate"/>.</summary>
    public int PendingCount => _pending.Count;

    public RigidbodyState SnapshotState()
    {
        if (_body == null) return default;
        return new RigidbodyState
        {
            Position = _body.GlobalPosition,
            Rotation = _body.Quaternion,
            LinearVelocity = _body.LinearVelocity,
            AngularVelocity = _body.AngularVelocity,
        };
    }

    /// <summary>
    /// Restores the body to an authoritative snapshot, clears any pending operations,
    /// and flushes the new transform to the physics server. Call from
    /// <see cref="Client.ClientPredictedEntity.HandleReconciliation"/>.
    /// </summary>
    public void Reconcile(RigidbodyState authoritative)
    {
        if (_body == null) return;

        Vector3 prePos = _body.GlobalPosition;
        Quaternion preRot = _body.Quaternion;
        Vector3 preVel = _body.LinearVelocity;
        Vector3 posDelta = authoritative.Position - prePos;
        Vector3 velDelta = authoritative.LinearVelocity - preVel;
        bool smoothingEnabled = _smoothing != null && _smoothing.Visual != null;
        MonkeLogger.Debug($"[PHYS-RB-RECONCILE] body={_body.Name} prePos=({prePos.X:F3},{prePos.Y:F3},{prePos.Z:F3}) -> authPos=({authoritative.Position.X:F3},{authoritative.Position.Y:F3},{authoritative.Position.Z:F3}) |posDelta|={posDelta.Length():F4} preVel=({preVel.X:F3},{preVel.Y:F3},{preVel.Z:F3}) -> authVel=({authoritative.LinearVelocity.X:F3},{authoritative.LinearVelocity.Y:F3},{authoritative.LinearVelocity.Z:F3}) |velDelta|={velDelta.Length():F4} smoothing={smoothingEnabled} pendingDropped={_pending.Count}");

        // Atomic Transform set propagates Position + Rotation to the physics
        // server in a single update; setting them separately can leave the body
        // briefly inconsistent and (more importantly) leaves Basis with whatever
        // scale/shear drift accumulated from prior frames. Reconstructing Basis
        // from the authoritative quaternion guarantees a clean orthonormal basis.
        _body.GlobalTransform = new Transform3D(new Basis(authoritative.Rotation), authoritative.Position);
        _body.LinearVelocity = authoritative.LinearVelocity;
        _body.AngularVelocity = authoritative.AngularVelocity;
        // Clear residual continuous forces / torques. RigidBody3D keeps
        // ConstantForce/ConstantTorque applied every tick until cleared; if game
        // code ever called AddConstantForce (not AddImpulse) those would otherwise
        // outlive the rollback and corrupt the resimulated trajectory.
        _body.ConstantForce = Vector3.Zero;
        _body.ConstantTorque = Vector3.Zero;
        _pending.Clear();
        _body.ForceUpdateTransform();

        // With physics_interpolation enabled the visual mesh lerps between the body's
        // _previous and _current transforms each render frame. After a teleport-style
        // reset the _previous transform is still the pre-rollback predicted pose — if
        // the renderer hits before the next SpaceStep overwrites it, the mesh shows a
        // bogus interpolated frame between two unrelated rotations. ResetPhysicsInterpolation
        // collapses both buffers to the new transform, so the body teleports cleanly.
        _body.ResetPhysicsInterpolation();

        // Synchronously hand the (pre, post) pair to the smoother. Without this,
        // the smoother only sees the jump on its next _PhysicsProcess, by which
        // point Visual (parented under Body, non-top_level) has already auto-
        // followed the body's new pose for one frame — producing a single-frame
        // visual snap that any sampler/renderer hitting that window observes as
        // a teleport. The smoother updates its offset, baselines _prevBodyPos
        // to the post pose so the next capture doesn't double-count, and writes
        // Visual.GlobalTransform immediately so the visual stays at prePos.
        if (smoothingEnabled)
            _smoothing.AbsorbBodyTeleport(prePos, preRot, authoritative.Position, authoritative.Rotation);
    }

    /// <summary>
    /// Re-pump this body's SceneTreeFTI <c>local_transform_prev</c> to the
    /// current local transform AFTER the rollback's resim loop completes.
    /// Required because <see cref="Reconcile"/> calls
    /// <c>ResetPhysicsInterpolation()</c> BEFORE the resim runs (setting
    /// prev=curr=auth_pose), and the resim then steps the body forward
    /// while leaving prev stuck at the auth pose. The next render frame
    /// would otherwise lerp the body from auth → post_resim across its
    /// child mesh chain, visible as a sliding-backwards visual artifact
    /// every rollback (this is what S7-C4 reproduces on the ball and rigid-
    /// player meshes). Called by <c>ClientPredictionManager</c> at the end
    /// of <c>RollbackAndResimulate</c> for every reconciled entity, whether
    /// or not it has a wired smoother. Pure FTI tracking update —
    /// propagates <c>NOTIFICATION_RESET_PHYSICS_INTERPOLATION</c> through
    /// the body's subtree but does NOT touch <c>PhysicsServer3D</c>: no
    /// body activation, no manifold invalidation, no contact-cache flush
    /// (verified by grepping <c>scene/3d/physics/</c> — only
    /// <c>VehicleBody3D</c> overrides the notification, for its wheels).
    /// </summary>
    public void ResetBodyFtiAfterResim()
    {
        if (_body == null) return;
        _body.ResetPhysicsInterpolation();
    }

    // Tight tolerances for "we already match this target" — used only by SnapToRest,
    // which only ever runs when both sides agree the body is at rest. 1 mm² and 0.1°
    // are well below any legitimate Jolt micro-drift, so passing them means the body
    // is already where the server wants it and no write is needed.
    private const float SnapToRestPosToleranceSq = 1e-6f;     // (1e-3 m)² = 1 mm²
    private const float SnapToRestRotToleranceRad = 0.001745f; // ~0.1°
    // Velocities are considered "already zero" when each component is below this
    // magnitude. Slightly looser than the position tolerance because Jolt's integrator
    // can leave a few μm/s residue even on bodies that finished the step at rest.
    private const float SnapToRestVelEpsilonSq = 1e-8f;

    /// <summary>
    /// Surgical "at-rest re-anchor" for sleep sync. Compares the body's current pose
    /// and velocities against the target; writes only what's actually different and
    /// skips <c>ResetPhysicsInterpolation</c> / <c>ForceUpdateTransform</c> when the
    /// transform doesn't change. Unlike <see cref="Reconcile"/> this does NOT clear
    /// <c>ConstantForce</c> / <c>ConstantTorque</c> or the pending queue — those are
    /// the caller's responsibility on misprediction rollback, not on sleep sync (a
    /// resting body has neither constant forces nor queued ops by definition).
    ///
    /// The whole point is to avoid invalidating Jolt's persistent contact manifolds
    /// on every snapshot when the body is already where the server says it is.
    /// Any body-interface transform write activates the body and flags its broadphase
    /// entry dirty, forcing manifold re-detection on the next step — which produces
    /// μm-scale normal-impulse drift that propagates up a stack as visible jitter on
    /// stacked / piled bodies. Mirror, FishNet, and Fusion 2 all gate body writes on
    /// a state-delta tolerance for exactly this reason.
    /// </summary>
    public void SnapToRest(Vector3 position, Quaternion rotation)
    {
        if (_body == null) return;

        Vector3 curPos = _body.GlobalPosition;
        Quaternion curRot = _body.Quaternion;
        Vector3 curLinVel = _body.LinearVelocity;
        Vector3 curAngVel = _body.AngularVelocity;

        float posDeltaSq = (position - curPos).LengthSquared();
        float rotDelta = curRot.AngleTo(rotation);
        bool poseNeedsUpdate = posDeltaSq > SnapToRestPosToleranceSq
                               || rotDelta > SnapToRestRotToleranceRad;
        bool velNeedsZero = curLinVel.LengthSquared() > SnapToRestVelEpsilonSq
                            || curAngVel.LengthSquared() > SnapToRestVelEpsilonSq;

        if (!poseNeedsUpdate && !velNeedsZero)
        {
            // Already at the target — no body writes, no manifold invalidation. This
            // is the hot path for a body that has fully settled on the server pose.
            MonkeLogger.Debug($"[PHYS-RB-SNAP-REST] body={_body.Name} noop posDeltaSq={posDeltaSq:F8} rotDelta={rotDelta:F6}");
            return;
        }

        MonkeLogger.Debug($"[PHYS-RB-SNAP-REST] body={_body.Name} poseWrite={poseNeedsUpdate} velZero={velNeedsZero} posDeltaSq={posDeltaSq:F8} rotDelta={rotDelta:F6}");

        if (poseNeedsUpdate)
        {
            _body.GlobalTransform = new Transform3D(new Basis(rotation), position);
            _body.ForceUpdateTransform();
            // Only collapse physics-interpolation buffers when we actually moved the
            // body; for a velocity-only zero-out the existing interpolated frame is
            // already correct.
            _body.ResetPhysicsInterpolation();
        }
        if (velNeedsZero)
        {
            _body.LinearVelocity = Vector3.Zero;
            _body.AngularVelocity = Vector3.Zero;
        }
    }
}
