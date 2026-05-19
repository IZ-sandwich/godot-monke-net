using System.Threading.Tasks;
using GdUnit4;
using Godot;
using MonkeNet.Shared;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Integration;

/// <summary>
/// PR-01..PR-03 + PD-01..PD-03 — API-contract tests for
/// <see cref="PredictionRigidbody3D"/> running in a single physics space.
///
/// These were previously split across <c>PhysicsDeterminismTests</c> and
/// <c>PredictionRigidbodyTests</c>, both of which oversold their scope by
/// using the word "determinism" in the netcode sense — the same physics space
/// stepped twice with identical inputs is NOT the cross-process determinism
/// the network code cares about. What these tests actually validate is the
/// <c>PredictionRigidbody3D</c> API contract: impulse queuing, reconcile
/// round-trip exactness, replay-from-snapshot exactness, no inter-body
/// solver-state leak. They run in a single Godot process / single Jolt space
/// and are intentionally cheap (sub-second each).
///
/// The cross-process determinism story lives in <c>MultiProcess/</c> —
/// specifically <c>MultiProcessPredictReplayTests</c> (cross-process reconcile)
/// and <c>MultiProcessStackDeterminismTests</c> (cross-process stack drift envelope).
///
/// Visual-smoothing tests for <c>PredictionVisualSmoothing3D</c> live separately
/// in <c>PredictionVisualSmoothingTests</c>.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class PredictionRigidbodyApiTests
{
    private ISceneRunner _runner;
    private RigidBody3D _body;
    private PredictionRigidbody3D _predictionRb;
    private Rid _space;

    [BeforeTest]
    public async Task SetUp()
    {
        // MainScene sets up MonkeNetConfig and a viewport with a Jolt-backed
        // physics space. MonkeNetManager's _EnterTree calls SpaceSetActive(false)
        // so we can step the space manually and deterministically.
        MonkeNetConfig.Instance = null;
        _runner = ISceneRunner.Load("res://demo/MainScene.tscn", autoFree: true);
        await _runner.AwaitIdleFrame();
        _space = _runner.Scene().GetViewport().World3D.Space;

        (_body, _predictionRb) = MakeBody();
        await _runner.AwaitIdleFrame();
    }

    [AfterTest]
    public void TearDown()
    {
        _runner?.Dispose();
        MonkeNetConfig.Instance = null;
    }

    // PR-01 ─────────────────────────────────────────────────────────────────
    // Forces queued via AddImpulse are not applied to the body until Simulate runs;
    // after Simulate, the body's velocity reflects the impulse once physics steps.
    [TestCase]
    public void PredictionRigidbody_AppliesQueuedForcesEachTick()
    {
        _predictionRb.AddImpulse(new Vector3(5, 0, 0));

        AssertThat(_predictionRb.PendingCount).IsEqual(1);
        AssertThat(_body.LinearVelocity.LengthSquared()).IsLess(0.001f);

        _predictionRb.Simulate();

        AssertThat(_predictionRb.PendingCount).IsEqual(0);

        PhysicsServer3D.SpaceStep(_space, 1f / 60f);
        PhysicsServer3D.SpaceFlushQueries(_space);

        Vector3 expected = new(5, 0, 0);
        AssertThat((_body.LinearVelocity - expected).LengthSquared())
            .OverrideFailureMessage($"velocity {_body.LinearVelocity} does not match expected {expected}")
            .IsLess(0.01f);
    }

    // PR-02 ─────────────────────────────────────────────────────────────────
    // Reconcile snaps the body to the snapshot, drops any queued ops that haven't
    // been flushed, and clears the body's velocity / position.
    [TestCase]
    public void PredictionRigidbody_ReconcileRestoresStateAndClearsPending()
    {
        _body.GlobalPosition = new Vector3(10, 10, 10);
        _body.LinearVelocity = new Vector3(2, 0, 0);
        _predictionRb.AddImpulse(new Vector3(99, 0, 0));
        AssertThat(_predictionRb.PendingCount).IsEqual(1);

        var authoritative = new RigidbodyState
        {
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            LinearVelocity = Vector3.Zero,
            AngularVelocity = Vector3.Zero,
        };
        _predictionRb.Reconcile(authoritative);

        AssertThat(_body.GlobalPosition.LengthSquared()).IsLess(0.001f);
        AssertThat(_body.LinearVelocity.LengthSquared()).IsLess(0.001f);
        AssertThat(_predictionRb.PendingCount).IsEqual(0);
    }

    // PR-03 ─────────────────────────────────────────────────────────────────
    // The determinism guarantee underpinning rollback: starting from the same
    // RigidbodyState, applying the same input sequence followed by SpaceStep,
    // produces the same final state. SAME-SPACE only — see class doc-comment.
    [TestCase]
    public void PredictionRigidbody_ResimReproducesSameTrajectory()
    {
        Vector3[] inputs = new Vector3[10];
        for (int i = 0; i < 10; i++)
            inputs[i] = new Vector3(0.5f, 0, (i % 3) - 1f);

        RigidbodyState atTick4 = default;
        for (int t = 0; t < 10; t++)
        {
            _predictionRb.AddImpulse(inputs[t]);
            _predictionRb.Simulate();
            PhysicsServer3D.SpaceStep(_space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(_space);
            if (t == 4) atTick4 = _predictionRb.SnapshotState();
        }
        Vector3 firstRunFinalPosition = _body.GlobalPosition;
        Vector3 firstRunFinalVelocity = _body.LinearVelocity;
        AssertThat(firstRunFinalPosition.LengthSquared()).IsGreater(0.01f);

        _predictionRb.Reconcile(atTick4);
        for (int t = 5; t < 10; t++)
        {
            _predictionRb.AddImpulse(inputs[t]);
            _predictionRb.Simulate();
            PhysicsServer3D.SpaceStep(_space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(_space);
        }

        AssertThat((firstRunFinalPosition - _body.GlobalPosition).LengthSquared())
            .OverrideFailureMessage($"resim position {_body.GlobalPosition} does not match first-run {firstRunFinalPosition}")
            .IsLess(0.0001f);
        AssertThat((firstRunFinalVelocity - _body.LinearVelocity).LengthSquared())
            .OverrideFailureMessage($"resim velocity {_body.LinearVelocity} does not match first-run {firstRunFinalVelocity}")
            .IsLess(0.0001f);
    }

    // PD-01 ───────────────────────────────────────────────────────────────────
    // Sequential-replay exactness with mixed linear + angular impulses over 60
    // ticks. Two runs on the same body in the same space must produce identical
    // final pose AND rotation. Tighter tolerances than PR-03 (1e-8 / 1e-6 vs
    // 1e-4) and exercises angular integration alongside translation — catches
    // per-axis float-order regressions and rotation-specific drift in our
    // Simulate() impulse-flush ordering.
    //
    // Caveat (also stated on the class doc-comment): this is same-space, not
    // truly cross-instance. The cross-process equivalent lives in
    // MultiProcessPredictReplayTests.
    [TestCase]
    public void Determinism_TwoRunsWithIdenticalInputs_ProduceIdenticalState()
    {
        var linearImpulses = new Vector3[60];
        var torqueImpulses = new Vector3[60];
        for (int i = 0; i < 60; i++)
        {
            linearImpulses[i] = new Vector3(0.3f * Mathf.Cos(i * 0.4f), 0.05f, 0.3f * Mathf.Sin(i * 0.4f));
            torqueImpulses[i] = new Vector3(0.05f * Mathf.Sin(i * 0.3f), 0.05f * Mathf.Cos(i * 0.5f), 0.05f);
        }

        // Run 1.
        for (int t = 0; t < 60; t++)
        {
            _predictionRb.AddImpulse(linearImpulses[t]);
            _predictionRb.AddTorqueImpulse(torqueImpulses[t]);
            _predictionRb.Simulate();
            PhysicsServer3D.SpaceStep(_space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(_space);
        }
        Vector3 run1Pos = _body.GlobalPosition;
        Vector3 run1Vel = _body.LinearVelocity;
        Vector3 run1AngVel = _body.AngularVelocity;
        Quaternion run1Rot = _body.Quaternion;

        AssertThat(run1Pos.LengthSquared()).IsGreater(0.01f);
        AssertThat(run1AngVel.LengthSquared()).IsGreater(0.001f);
        AssertThat(1f - Mathf.Abs(run1Rot.Dot(Quaternion.Identity))).IsGreater(1e-4f);

        _predictionRb.Reconcile(new RigidbodyState
        {
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            LinearVelocity = Vector3.Zero,
            AngularVelocity = Vector3.Zero,
        });

        // Run 2.
        for (int t = 0; t < 60; t++)
        {
            _predictionRb.AddImpulse(linearImpulses[t]);
            _predictionRb.AddTorqueImpulse(torqueImpulses[t]);
            _predictionRb.Simulate();
            PhysicsServer3D.SpaceStep(_space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(_space);
        }

        AssertThat((run1Pos - _body.GlobalPosition).LengthSquared())
            .OverrideFailureMessage($"position determinism: run1={run1Pos} run2={_body.GlobalPosition}")
            .IsLess(1e-8f);
        AssertThat((run1Vel - _body.LinearVelocity).LengthSquared())
            .OverrideFailureMessage($"velocity determinism: run1={run1Vel} run2={_body.LinearVelocity}")
            .IsLess(1e-6f);
        AssertThat((run1AngVel - _body.AngularVelocity).LengthSquared())
            .IsLess(1e-6f);
        AssertThat(1f - Mathf.Abs(run1Rot.Dot(_body.Quaternion))).IsLess(1e-4f);
    }

    // PD-02 ───────────────────────────────────────────────────────────────────
    // Reconciliation must be idempotent under active contact: place the body on
    // a static floor with gravity, capture state at tick T, run forward 30 ticks
    // (resting contact + lateral motion), Reconcile back to T, run forward 30
    // ticks again. The two runs must match. The floor + gravity ensures a
    // non-trivial contact manifold is active when state is captured, so this
    // exercises Reconcile's ResetPhysicsInterpolation + Jolt warm-start
    // invalidation paths — the parts most likely to silently leak state across
    // the rollback.
    [TestCase]
    public void Determinism_ReconciliationIsIdempotent()
    {
        AddStaticFloor(yPosition: -1f);
        // Swap the default body for one with gravity so contact is non-trivial.
        _body.GlobalPosition = new Vector3(0, 0.5f, 0);
        _body.GravityScale = 1f;

        var inputs = new Vector3[30];
        for (int i = 0; i < 30; i++)
            inputs[i] = new Vector3((i % 5 - 2) * 0.4f, 0f, ((i + 2) % 4 - 2) * 0.4f);

        // Phase 1: settle onto the floor.
        for (int t = 0; t < 10; t++)
        {
            _predictionRb.AddImpulse(inputs[t]);
            _predictionRb.Simulate();
            PhysicsServer3D.SpaceStep(_space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(_space);
        }
        RigidbodyState captured = _predictionRb.SnapshotState();

        // Phase 2: forward 30 ticks.
        for (int t = 0; t < 30; t++)
        {
            _predictionRb.AddImpulse(inputs[t]);
            _predictionRb.Simulate();
            PhysicsServer3D.SpaceStep(_space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(_space);
        }
        Vector3 firstFinalPos = _body.GlobalPosition;
        Vector3 firstFinalVel = _body.LinearVelocity;

        // Phase 3: Reconcile, forward 30 ticks again with the SAME inputs.
        _predictionRb.Reconcile(captured);
        for (int t = 0; t < 30; t++)
        {
            _predictionRb.AddImpulse(inputs[t]);
            _predictionRb.Simulate();
            PhysicsServer3D.SpaceStep(_space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(_space);
        }

        AssertThat((firstFinalPos - _body.GlobalPosition).LengthSquared())
            .OverrideFailureMessage($"reconcile-replay position drift: first={firstFinalPos} second={_body.GlobalPosition}")
            .IsLess(1e-4f);
        AssertThat((firstFinalVel - _body.LinearVelocity).LengthSquared())
            .IsLess(1e-4f);
    }

    // PD-03 ───────────────────────────────────────────────────────────────────
    // Replay with a cold start: spawn a fresh body, run a recorded input list.
    // Spawn another fresh body, run the same list. Final states must match.
    // Catches solver-state leakage between bodies (e.g. shared persistent
    // buffers, ordering-dependent initial state).
    [TestCase]
    public void Determinism_ColdStartReplayMatches()
    {
        // Discard SetUp's default body — this test specifically validates that
        // creating two fresh bodies produces matching trajectories.
        _body.GetParent().RemoveChild(_body);
        _body.Free();

        var inputs = new Vector3[60];
        for (int i = 0; i < 60; i++)
            inputs[i] = new Vector3(Mathf.Sin(i * 0.3f), Mathf.Cos(i * 0.5f), Mathf.Sin(i * 0.7f)) * 0.4f;

        // Cold body 1.
        var (body1, pr1) = MakeBody();
        for (int t = 0; t < 60; t++)
        {
            pr1.AddImpulse(inputs[t]);
            pr1.Simulate();
            PhysicsServer3D.SpaceStep(_space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(_space);
        }
        Vector3 body1Pos = body1.GlobalPosition;
        Vector3 body1Vel = body1.LinearVelocity;

        // Synchronous removal so body1's collider doesn't sit at origin during
        // body2's first SpaceStep. QueueFree is deferred to the next idle
        // frame — too late.
        body1.GetParent().RemoveChild(body1);
        body1.Free();

        // Cold body 2.
        var (body2, pr2) = MakeBody();
        for (int t = 0; t < 60; t++)
        {
            pr2.AddImpulse(inputs[t]);
            pr2.Simulate();
            PhysicsServer3D.SpaceStep(_space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(_space);
        }

        AssertThat((body1Pos - body2.GlobalPosition).LengthSquared())
            .OverrideFailureMessage($"cold-start replay drift: body1={body1Pos} body2={body2.GlobalPosition}")
            .IsLess(1e-4f);
        AssertThat((body1Vel - body2.LinearVelocity).LengthSquared())
            .IsLess(1e-4f);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private (RigidBody3D body, PredictionRigidbody3D predictionRb) MakeBody()
    {
        var body = new RigidBody3D
        {
            GravityScale = 0f,
            Mass = 1f,
            LinearDamp = 0f,
            AngularDamp = 0f,
        };
        body.AddChild(new CollisionShape3D { Shape = new SphereShape3D() });
        _runner.Scene().AddChild(body);

        var predictionRb = new PredictionRigidbody3D();
        body.AddChild(predictionRb);
        predictionRb.Initialize(body);

        // Warm-up step: the FIRST ApplyTorqueImpulse on a freshly-AddChild'd
        // RigidBody3D is silently dropped by Jolt (the body isn't fully
        // registered in the physics server until at least one SpaceStep runs).
        // Doing one no-op step here makes test setups match what production
        // would see (where bodies live in the scene tree across idle frames
        // before any impulse is applied).
        PhysicsServer3D.SpaceStep(_space, 0f);
        PhysicsServer3D.SpaceFlushQueries(_space);
        return (body, predictionRb);
    }

    private void AddStaticFloor(float yPosition)
    {
        var floor = new StaticBody3D { Position = new Vector3(0, yPosition, 0) };
        var shape = new BoxShape3D { Size = new Vector3(20, 0.5f, 20) };
        floor.AddChild(new CollisionShape3D { Shape = shape });
        _runner.Scene().AddChild(floor);
    }
}
