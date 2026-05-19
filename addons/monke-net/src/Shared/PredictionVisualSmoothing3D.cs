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
        Visual.GlobalTransform = new Transform3D(new Basis((_rotOffset * bodyRot).Normalized()), bodyPos + _posOffset);

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
        MonkeLogger.Debug($"[SMOOTH-FRAME] body={Body.Name} pf={physFrame} dt={delta:F5} pif={interpFrac:F3} " +
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

    /// <summary>True while the visual is meaningfully offset from the body.</summary>
    public bool IsSmoothing =>
        _posOffset.LengthSquared() > 1e-8f
        || Mathf.Abs(_rotOffset.W) < 0.99999f;

    /// <summary>Current world-space position offset between visual and body.</summary>
    public Vector3 CurrentOffset => _posOffset;
}
