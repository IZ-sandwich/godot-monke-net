using Godot;

namespace MonkeNet.Shared;

/// <summary>
/// Continuous visual smoothing for predicted physics bodies. Once per physics
/// tick the smoother diffs the body's actual motion since the last tick against
/// the motion the body's <c>LinearVelocity</c>/<c>AngularVelocity</c> would
/// have produced naturally over the tick. Anything left over is treated as an
/// unexplained teleport (reconcile, sleep-sync, manual transform write) and
/// absorbed into a world-space offset that decays exponentially toward zero.
/// Visual is written as <c>Body + offset</c> in <c>_PhysicsProcess</c>, then
/// Godot's SceneTreeFTI lerps it between consecutive physics-tick poses every
/// render frame — same lerp path the body's other inherited children
/// (CollisionShape3D debug wireframe, labels, prompts) use, so the mesh
/// renders in lockstep with them.
///
///   - Natural physics motion: offset doesn't grow, visual rides body lockstep.
///   - Body teleports: jump is captured, visual stays where it was, offset
///     decays over <see cref="DecayTime"/>.
///
/// Pair with a <b>regular (non-top_level)</b> Visual node parented under the
/// body. The smoother writes <c>Visual.GlobalPosition</c>; Godot converts to
/// the local position relative to the body, and FTI then lerps the body's
/// transform (and through it the Visual's render-frame pose) automatically.
/// A top_level Visual bypasses that lerp and stair-steps at physics-tick rate
/// — verified empirically against the wireframe on the same body.
/// </summary>
[GlobalClass, Icon("res://addons/monke-net/resources/circle_nodes_solid.png")]
public partial class PredictionVisualSmoothing3D : Node3D
{
    [Export] public Node3D Body { get; set; }
    [Export] public Node3D Visual { get; set; }

    /// <summary>Time constant (seconds) for exponential offset decay. After
    /// DecayTime seconds the offset has decayed to ~37% of its captured value;
    /// after 3×DecayTime it is effectively zero. 0.1 s ≈ 6 ticks at 60Hz.</summary>
    [Export] public float DecayTime { get; set; } = 0.1f;

    /// <summary>Smallest position jump (meters) over a single frame, after
    /// subtracting <c>LinearVelocity * dt</c>, that counts as a teleport. Below
    /// this everything is treated as physics integration noise and ignored.</summary>
    [Export] public float PositionJumpEpsilon { get; set; } = 0.002f;

    /// <summary>Smallest rotation jump (radians) over a single frame, after
    /// subtracting the rotation implied by <c>AngularVelocity * dt</c>, that
    /// counts as a teleport.</summary>
    [Export] public float RotationJumpEpsilonRad { get; set; } = 0.005f;

    /// <summary>If the accumulated offset ever exceeds this, snap it to zero
    /// instead of smoothing. Lerping a multi-meter correction looks worse than
    /// a teleport because the visual trails far behind collision. 0 disables
    /// the threshold.</summary>
    [Export] public float TeleportDistance { get; set; } = 5f;


    /// <summary>When false, the smoother only manages POSITION offsets and never
    /// writes the visual's rotation — caller-owned rotation (e.g. a knight rig
    /// whose Y yaw is driven from camera input every tick) is preserved. Default
    /// true preserves the original prop/vehicle behaviour where the visual's
    /// rotation is fully derived from the body.</summary>
    [Export] public bool SmoothRotation { get; set; } = true;

    // World-space position offset between visual and body. Decays toward zero.
    private Vector3 _posOffset;
    // World-space rotation offset: Visual.Quaternion = _rotOffset * Body.Quaternion.
    // Decays toward identity. World-frame (left-multiplied) keeps the teleport
    // capture math symmetric with the position path — body rotates by R in
    // world space, offset absorbs R⁻¹.
    private Quaternion _rotOffset = Quaternion.Identity;

    private Vector3 _prevBodyPos;
    private Quaternion _prevBodyRot = Quaternion.Identity;
    private bool _hasPrev;

    // Detection + decay + Visual write all run in _PhysicsProcess. Writing
    // Visual every render frame from _Process would overwrite Godot's
    // RenderingServer-side physics-tick interpolation — RS lerps only between
    // transform values it observes, so a per-render-frame write collapses the
    // lerp window to a single frame and shows the latest write directly.
    // Inherited children of the body (label, prompt, knight rig on a rider,
    // camera) all rely on that RS lerp to render smoothly between ticks; the
    // top_level VisualRoot has to participate in the same lerp or the rider's
    // camera (at the body's un-interpolated current-tick pose, plus whatever
    // smoothing Godot applies) sees the vehicle visual slide and snap each
    // tick. Writing once per physics tick from here keeps mesh + label +
    // camera all in the same interpolation frame.
    public override void _EnterTree()
    {
        // Explicit enable — override of _PhysicsProcess does not always auto-
        // enable physics processing for Node3D nodes added programmatically
        // (verified empirically: a dynamically AddChild'd smoother's _Process
        // started firing one tick before _PhysicsProcess did, missing the
        // first-tick prev-pose baseline).
        SetPhysicsProcess(true);
        SetProcess(true);

        // Disable Godot SceneTreeFTI on the body's subtree so the visual mesh
        // and any inherited children (label, debug prompt, …) render at the
        // same un-interpolated physics-tick pose as the body's debug wireframe
        // — which is pushed to RenderingServer by CollisionObject3D's
        // _on_transform_changed using the body's current global transform
        // (no FTI lerp; RS no longer interpolates instances either).
        //
        // With FTI on, the mesh lerps smoothly between consecutive physics-
        // tick poses while the wireframe stair-steps; their relative position
        // alternates every render frame as `Engine.GetPhysicsInterpolationFraction()`
        // cycles 0 → 1 over each tick, which is perceived as the visual
        // "jumping back and forth" relative to the wireframe ~120 times per
        // second. Disabling FTI on the body costs the mesh's between-tick
        // smoothness (it stair-steps at physics rate, same as the wireframe)
        // but eliminates the oscillation. At 60 Hz physics and >=120 fps
        // render the body's per-tick motion is small enough that the eye
        // perceives the step pattern as smooth, the same way the wireframe
        // already looks smooth.
        if (Body != null)
            Body.PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Inherit;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (Body == null || Visual == null) return;

        Vector3 bodyPos = Body.GlobalPosition;
        Quaternion bodyRot = Body.Quaternion;
        float dt = (float)delta;

        if (_hasPrev && dt > 0f)
        {
            CaptureUnexplainedJump(bodyPos, bodyRot, dt);
        }

        float alpha = DecayTime > 0f ? Mathf.Exp(-dt / DecayTime) : 0f;
        _posOffset *= alpha;
        _rotOffset = Quaternion.Identity.Slerp(_rotOffset.Normalized(), alpha);

        if (TeleportDistance > 0f && _posOffset.Length() > TeleportDistance)
        {
            _posOffset = Vector3.Zero;
            _rotOffset = Quaternion.Identity;
        }

        // Write the global transform atomically. For a non-top_level Visual,
        // Visual.Quaternion would set LOCAL rotation — yielding global =
        // body.basis * (rotOffset * bodyRot) and rotating the visual by the
        // body's basis twice. Setting GlobalTransform routes through Godot's
        // setter, which converts the desired global to local via the parent's
        // current global, so the visual's resulting global rotation is exactly
        // (rotOffset * bodyRot) regardless of parent rotation.
        //
        // SmoothRotation=false skips the rotation write entirely — used when
        // the visual carries caller-owned rotation (e.g. a knight rig's yaw
        // driven from camera input every tick) that the smoother must not
        // overwrite. Only position is offset-corrected.
        if (SmoothRotation)
        {
            Visual.GlobalTransform = new Transform3D(new Basis((_rotOffset * bodyRot).Normalized()), bodyPos + _posOffset);
        }
        else
        {
            Visual.GlobalPosition = bodyPos + _posOffset;
        }

        _prevBodyPos = bodyPos;
        _prevBodyRot = bodyRot;
        _hasPrev = true;
    }

    // Per-render-frame smoothness trace. Only logs; does NOT write Visual —
    // _PhysicsProcess owns that so RenderingServer can interpolate. Removed
    // once verification is complete.
    private bool _loggedFtiState;

    public override void _Process(double delta)
    {
        if (Body is not RigidBody3D rbDbg || Visual == null) return;

        // One-time log of FTI / interp configuration so we can verify the
        // assumed pipeline is active in the running build.
        if (!_loggedFtiState)
        {
            _loggedFtiState = true;
            var tree = GetTree();
            bool treeInterp = tree != null && tree.IsPhysicsInterpolationEnabled();
            bool bodyInTree = Body.IsInsideTree();
            bool bodyVisible = Body.Visible;
            string stateMsg = $"[SMOOTH-FTI-STATE] body={Body.Name} treeInterp={treeInterp} bodyInTree={bodyInTree} bodyVisible={bodyVisible} bodyInterpMode={Body.PhysicsInterpolationMode} visInterpMode={Visual.PhysicsInterpolationMode} physTicksPerSec={Engine.PhysicsTicksPerSecond} maxFps={Engine.MaxFps} visIsTopLevel={Visual.TopLevel}";
            MonkeLogger.Debug(stateMsg);
            // Also surface to stdout so the user can sanity-check FTI is on in
            // their interactive run without having to open the monke-net log.
            GD.Print(stateMsg);
        }

        // --- BODY state at render-frame read time ---
        Transform3D bodyGlobal = Body.GlobalTransform;
        Vector3 bodyPosUninterp = bodyGlobal.Origin;
        Quaternion bodyRotUninterp = bodyGlobal.Basis.GetRotationQuaternion();
        Transform3D bodyGlobalFti = Body.GetGlobalTransformInterpolated();
        Vector3 bodyPosFti = bodyGlobalFti.Origin;
        Quaternion bodyRotFti = bodyGlobalFti.Basis.GetRotationQuaternion();
        bool bodyFtiSame = bodyPosUninterp.IsEqualApprox(bodyPosFti);

        // --- VISUAL state at render-frame read time ---
        Transform3D visGlobal = Visual.GlobalTransform;
        Vector3 visPosUninterp = visGlobal.Origin;
        Quaternion visRotUninterp = visGlobal.Basis.GetRotationQuaternion();
        Transform3D visLocal = Visual.Transform; // Local transform (relative to parent)
        Transform3D visGlobalFti = Visual.GetGlobalTransformInterpolated();
        Vector3 visPosFti = visGlobalFti.Origin;
        Quaternion visRotFti = visGlobalFti.Basis.GetRotationQuaternion();
        bool visFtiSame = visPosUninterp.IsEqualApprox(visPosFti);

        // Δrotation between body and visual at render time — both via their
        // FTI-interpolated transforms. If FTI lerps both in lockstep with the
        // same window, this delta should be near-constant (== smoother
        // _rotOffset). If they're on different lerp paths, delta wobbles
        // every render frame — that's the "rotation out of sync" report.
        Quaternion bodyVsVisRotDelta = (bodyRotFti.Inverse() * visRotFti).Normalized();

        double interpFrac = Engine.GetPhysicsInterpolationFraction();
        ulong physFrame = Engine.GetPhysicsFrames();
        // Snapshot the network-synced tick the HUD would display at this same
        // _Process call. Tagging SMOOTH-FRAME with it lets downstream plot
        // tools cross-reference any MP4 frame (which captures the HUD's tick
        // number) with the visual position the user saw in that frame —
        // crucial for diagnosing per-tick visual artifacts that vanish when
        // the data is aggregated by physics-frame counter.
        int clientTick = MonkeNet.Client.ClientManager.Instance?
            .GetNodeOrNull<MonkeNet.Client.ClientNetworkClock>("ClientNetworkClock")?
            .GetCurrentTick() ?? -1;
        MonkeLogger.Debug($"[SMOOTH-FRAME] body={Body.Name} pf={physFrame} clientTick={clientTick} dt={delta:F5} pif={interpFrac:F3} " +
            $"raw=({bodyPosUninterp.X:F5},{bodyPosUninterp.Y:F5},{bodyPosUninterp.Z:F5}) " +
            $"bodyRot=({bodyRotUninterp.X:F4},{bodyRotUninterp.Y:F4},{bodyRotUninterp.Z:F4},{bodyRotUninterp.W:F4}) " +
            $"bodyFti=({bodyPosFti.X:F5},{bodyPosFti.Y:F5},{bodyPosFti.Z:F5}) bodyFtiSame={bodyFtiSame} " +
            $"interp=({bodyPosUninterp.X:F5},{bodyPosUninterp.Y:F5},{bodyPosUninterp.Z:F5}) " +
            $"vis=({visPosUninterp.X:F5},{visPosUninterp.Y:F5},{visPosUninterp.Z:F5}) " +
            $"visRot=({visRotUninterp.X:F4},{visRotUninterp.Y:F4},{visRotUninterp.Z:F4},{visRotUninterp.W:F4}) " +
            $"visLocal=({visLocal.Origin.X:F5},{visLocal.Origin.Y:F5},{visLocal.Origin.Z:F5}) " +
            $"visFti=({visPosFti.X:F5},{visPosFti.Y:F5},{visPosFti.Z:F5}) visFtiSame={visFtiSame} " +
            $"vsBodyRotΔ=({bodyVsVisRotDelta.X:F4},{bodyVsVisRotDelta.Y:F4},{bodyVsVisRotDelta.Z:F4},{bodyVsVisRotDelta.W:F4}) " +
            $"vel=({rbDbg.LinearVelocity.X:F4},{rbDbg.LinearVelocity.Y:F4},{rbDbg.LinearVelocity.Z:F4})");
    }

    // Diff actual body motion against the motion implied by Velocity/AngVel*dt.
    // Excess is folded into the offsets so the visual stays at its rendered pose
    // across whatever produced the jump (Reconcile, SyncSleepState, etc.).
    private void CaptureUnexplainedJump(Vector3 bodyPos, Quaternion bodyRot, float dt)
    {
        Vector3 linVel = Body is RigidBody3D rbLin ? rbLin.LinearVelocity : Vector3.Zero;
        Vector3 expectedPosDelta = linVel * dt;
        Vector3 jumpPos = (bodyPos - _prevBodyPos) - expectedPosDelta;
        if (jumpPos.LengthSquared() > PositionJumpEpsilon * PositionJumpEpsilon)
        {
            _posOffset -= jumpPos;
        }

        Vector3 angVel = Body is RigidBody3D rbAng ? rbAng.AngularVelocity : Vector3.Zero;
        Quaternion expectedRot = AngularDelta(angVel, dt);
        Quaternion actualRot = (bodyRot * _prevBodyRot.Inverse()).Normalized();
        Quaternion jumpRot = (expectedRot.Inverse() * actualRot).Normalized();
        // |jumpRot.W| is cos(angle/2); guard against sign flips by taking abs.
        float jumpAngle = 2f * Mathf.Acos(Mathf.Clamp(Mathf.Abs(jumpRot.W), -1f, 1f));
        if (jumpAngle > RotationJumpEpsilonRad)
        {
            _rotOffset = (_rotOffset * jumpRot.Inverse()).Normalized();
        }
    }

    // Quaternion that rotates by (angVel * dt) around the angVel axis. Mirrors
    // the semi-implicit Euler step Godot/Jolt uses for free-flying bodies, so
    // for natural motion expectedRot ≈ actualRot to within float precision.
    private static Quaternion AngularDelta(Vector3 angVel, float dt)
    {
        float speed = angVel.Length();
        if (speed < 1e-6f) return Quaternion.Identity;
        Vector3 axis = angVel / speed;
        return new Quaternion(axis, speed * dt);
    }

    /// <summary>
    /// Synchronously absorb a body teleport that's about to happen / just
    /// happened in the same physics frame. Called from
    /// <see cref="PredictionRigidbody3D.Reconcile"/> after the body's transform
    /// has been written to the authoritative pose. Updates the offset so the
    /// visual stays at the PRE-teleport pose, baselines <c>_prevBodyPos</c> to
    /// the POST-teleport pose so the next <c>_PhysicsProcess</c> doesn't double-
    /// capture the same jump, and writes <c>Visual.GlobalTransform</c>
    /// immediately so any same-frame reader (CSV samples, render frame between
    /// the reconcile and the next physics step) sees Visual at the pre-teleport
    /// pose rather than following the body to its new location.
    ///
    /// Without this hook the smoother would still capture the jump on its next
    /// <c>_PhysicsProcess</c>, but Visual (a non-top_level child of Body) auto-
    /// follows the body's transform in the interim — producing a single-frame
    /// visual jump that any reader hitting that window observes as a teleport.
    /// </summary>
    public void AbsorbBodyTeleport(Vector3 prePos, Quaternion preRot, Vector3 postPos, Quaternion postRot)
    {
        if (Body == null || Visual == null)
        {
            MonkeLogger.Debug($"[SMOOTH-ABSORB] body={Body?.Name} SKIPPED Body/Visual null (Body={(Body==null?"null":"set")} Visual={(Visual==null?"null":"set")})");
            return;
        }

        Vector3 jump = postPos - prePos;
        _posOffset -= jump;
        MonkeLogger.Debug($"[SMOOTH-ABSORB] body={Body.Name} prePos=({prePos.X:F3},{prePos.Y:F3},{prePos.Z:F3}) postPos=({postPos.X:F3},{postPos.Y:F3},{postPos.Z:F3}) jump=({jump.X:F3},{jump.Y:F3},{jump.Z:F3}) newOffset=({_posOffset.X:F3},{_posOffset.Y:F3},{_posOffset.Z:F3})");

        Quaternion rotJump = (postRot * preRot.Inverse()).Normalized();
        _rotOffset = (_rotOffset * rotJump.Inverse()).Normalized();

        if (TeleportDistance > 0f && _posOffset.Length() > TeleportDistance)
        {
            _posOffset = Vector3.Zero;
            _rotOffset = Quaternion.Identity;
        }

        _prevBodyPos = postPos;
        _prevBodyRot = postRot;
        _hasPrev = true;

        if (SmoothRotation)
        {
            Visual.GlobalTransform = new Transform3D(
                new Basis((_rotOffset * postRot).Normalized()),
                postPos + _posOffset);
        }
        else
        {
            Visual.GlobalPosition = postPos + _posOffset;
        }
    }

    /// <summary>
    /// Re-anchor the smoother's POSITION offset against the body's CURRENT
    /// pose so that <c>Visual = Body + offset</c> evaluates to the supplied
    /// pre-reconcile visual position. Called by
    /// <see cref="Client.ClientPredictionManager"/> at the end of
    /// <c>RollbackAndResimulate</c>, after the resim loop has advanced the
    /// body from <c>auth_pose</c> through N replayed ticks to
    /// <c>post_resim_pose</c>.
    ///
    /// Why this is load-bearing: <see cref="AbsorbBodyTeleport"/> only sees
    /// the (prePos, postPos) at the moment of reconcile — i.e. (live_pose,
    /// auth_pose) — and sizes <c>_posOffset</c> against the auth pose. The
    /// resim that runs IMMEDIATELY after the reconcile moves the body from
    /// auth_pose to post_resim_pose, often by several metres (deep rollback
    /// with velocity / input correction). Without this re-anchor the offset
    /// is stale by exactly <c>(auth_pose − post_resim_pose)</c>, and the next
    /// <c>_PhysicsProcess</c> renders <c>Visual = post_resim_pose + offset</c>
    /// = a multi-metre teleport away from where the body actually is.
    ///
    /// Rotation is intentionally NOT re-anchored here. Re-anchoring rotation
    /// against the post-resim body would lock the visual to its pre-reconcile
    /// orientation and then decay back toward the (post-resim) body rotation
    /// over <see cref="DecayTime"/>. For bodies with small reconcile-driven
    /// rotation deltas (resting cubes, balls just settling) that decay is a
    /// visible 6-tick rotation wobble repeated on every reconcile (~7 Hz at
    /// C4) — much more noticeable than the underlying rotation jump, which
    /// <see cref="AbsorbBodyTeleport"/> already absorbs adequately. Only
    /// position needed the post-resim fixup; rotation didn't.
    /// </summary>
    public void FixupOffsetAfterResim(Vector3 preReconcileVisualPos)
    {
        if (Body == null || Visual == null) return;

        Vector3 postResimBodyPos = Body.GlobalPosition;
        Quaternion postResimBodyRot = Body.Quaternion;

        // Solve for the position offset that reproduces the pre-reconcile
        // visual position against the post-resim body position:
        // Body + offset == preVisualPos.
        _posOffset = preReconcileVisualPos - postResimBodyPos;

        if (TeleportDistance > 0f && _posOffset.Length() > TeleportDistance)
        {
            _posOffset = Vector3.Zero;
            _rotOffset = Quaternion.Identity;
        }

        // Baseline prev=post-resim so the next _PhysicsProcess doesn't see the
        // resim's body motion as an unexplained jump and double-absorb it.
        _prevBodyPos = postResimBodyPos;
        _prevBodyRot = postResimBodyRot;
        _hasPrev = true;

        // Write Visual immediately for any same-frame reader (CSV sample,
        // render-frame between the reconcile and the next _PhysicsProcess).
        if (SmoothRotation)
        {
            Visual.GlobalTransform = new Transform3D(
                new Basis((_rotOffset * postResimBodyRot).Normalized()),
                postResimBodyPos + _posOffset);
        }
        else
        {
            Visual.GlobalPosition = postResimBodyPos + _posOffset;
        }

        MonkeLogger.Debug($"[SMOOTH-FIXUP] body={Body.Name} preVis=({preReconcileVisualPos.X:F3},{preReconcileVisualPos.Y:F3},{preReconcileVisualPos.Z:F3}) postBody=({postResimBodyPos.X:F3},{postResimBodyPos.Y:F3},{postResimBodyPos.Z:F3}) newOffset=({_posOffset.X:F3},{_posOffset.Y:F3},{_posOffset.Z:F3})");
    }

    /// <summary>True while the visual is meaningfully offset from the body.</summary>
    public bool IsSmoothing =>
        _posOffset.LengthSquared() > 1e-8f
        || Mathf.Abs(_rotOffset.W) < 0.99999f;

    /// <summary>Current world-space position offset between visual and body.</summary>
    public Vector3 CurrentOffset => _posOffset;
}
