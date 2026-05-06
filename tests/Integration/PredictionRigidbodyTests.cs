using System.Threading.Tasks;
using GdUnit4;
using Godot;
using MonkeNet.Shared;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Integration;

/// <summary>
/// PR-01..PR-03: PredictionRigidbody3D wrapper tests.
///
/// These verify the queue → Simulate → SpaceStep cycle that lets entity tick
/// handlers describe input forces deterministically. The final test (PR-03)
/// exercises the determinism guarantee that resimulation depends on: starting
/// from the same RigidbodyState and applying the same input sequence must
/// reproduce the same trajectory.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class PredictionRigidbodyTests
{
    private ISceneRunner _runner;
    private RigidBody3D _body;
    private PredictionRigidbody3D _predictionRb;
    private Rid _space;

    [BeforeTest]
    public async Task SetUp()
    {
        // MainScene sets up MonkeNetConfig and a viewport with a Jolt-backed physics
        // space. MonkeNetManager's _EnterTree calls SpaceSetActive(false) so we can
        // step the space manually and deterministically.
        MonkeNetConfig.Instance = null;
        _runner = ISceneRunner.Load("res://demo/MainScene.tscn", autoFree: true);
        await _runner.AwaitIdleFrame();
        _space = _runner.Scene().GetViewport().World3D.Space;

        _body = new RigidBody3D
        {
            GravityScale = 0f, // no gravity → impulse-only velocity for deterministic checks
            Mass = 1f,
            LinearDamp = 0f,
            AngularDamp = 0f,
        };
        _body.AddChild(new CollisionShape3D { Shape = new SphereShape3D() });
        _runner.Scene().AddChild(_body);

        _predictionRb = new PredictionRigidbody3D();
        _body.AddChild(_predictionRb);
        _predictionRb.Initialize(_body);

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
        // Queue is in-flight; body is untouched.
        AssertThat(_body.LinearVelocity.LengthSquared()).IsLess(0.001f);

        _predictionRb.Simulate();

        AssertThat(_predictionRb.PendingCount).IsEqual(0);

        // One physics step integrates the impulse into LinearVelocity.
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
        // Move the body and queue an unflushed op that should never reach the body.
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
    // produces the same final state.
    [TestCase]
    public void PredictionRigidbody_ResimReproducesSameTrajectory()
    {
        Vector3[] inputs = new Vector3[10];
        for (int i = 0; i < 10; i++)
            inputs[i] = new Vector3(0.5f, 0, (i % 3) - 1f);

        // First run, ticks 0..9. Capture the state after tick 4 for resim's starting point.
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

        // Sanity check: the body actually moved, otherwise the test is meaningless.
        AssertThat(firstRunFinalPosition.LengthSquared()).IsGreater(0.01f);

        // Reconcile to mid-run snapshot, then replay inputs[5..9] and assert the
        // final state matches the original run within float epsilon.
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

    // PR-04 ─────────────────────────────────────────────────────────────────
    // When a smoothing component is wired to PredictionRigidbody3D, Reconcile
    // captures the visual's pre-jump pose. The visible mesh should *not* snap
    // to the new body pose — it should lag behind, scheduled to catch up.
    [TestCase]
    public async Task PredictionRigidbody_Smoothing_VisualLagsAfterReconcile()
    {
        var (visualRoot, smoothing) = AttachSmoothing(_body, durationSec: 0.5f);
        _predictionRb.Initialize(_body, smoothing);

        // Place the body and visual at a known starting pose.
        _body.GlobalPosition = new Vector3(1, 0, 0);
        visualRoot.GlobalPosition = new Vector3(1, 0, 0);
        await _runner.AwaitIdleFrame();

        // Reconcile the body to a far-away pose. Visual should NOT follow immediately.
        _predictionRb.Reconcile(new RigidbodyState
        {
            Position = new Vector3(50, 0, 0),
            Rotation = Quaternion.Identity,
            LinearVelocity = Vector3.Zero,
            AngularVelocity = Vector3.Zero,
        });

        AssertThat(smoothing.IsSmoothing).IsTrue();
        // The captured offset should be (1 - 50, 0, 0) = ~49 units along -X.
        AssertThat(smoothing.CurrentOffset.Length()).IsGreater(40f);
    }

    // PR-05 ─────────────────────────────────────────────────────────────────
    // After enough _Process frames have elapsed (more than DurationSec), the
    // visual catches up and IsSmoothing returns false. The remaining offset
    // should be ~zero and the visual aligned with the body.
    [TestCase]
    public async Task PredictionRigidbody_Smoothing_VisualConvergesAfterDuration()
    {
        // Tiny duration so a handful of idle frames is enough to converge.
        var (visualRoot, smoothing) = AttachSmoothing(_body, durationSec: 0.01f);
        _predictionRb.Initialize(_body, smoothing);

        _body.GlobalPosition = new Vector3(1, 0, 0);
        visualRoot.GlobalPosition = new Vector3(1, 0, 0);
        await _runner.AwaitIdleFrame();

        _predictionRb.Reconcile(new RigidbodyState
        {
            Position = new Vector3(20, 0, 0),
            Rotation = Quaternion.Identity,
            LinearVelocity = Vector3.Zero,
            AngularVelocity = Vector3.Zero,
        });

        // Drive enough idle frames to cover the smoothing window.
        for (int i = 0; i < 60; i++) await _runner.AwaitIdleFrame();

        AssertThat(smoothing.IsSmoothing).IsFalse();
        AssertThat((visualRoot.GlobalPosition - _body.GlobalPosition).LengthSquared())
            .OverrideFailureMessage($"visual {visualRoot.GlobalPosition} did not converge to body {_body.GlobalPosition}")
            .IsLess(0.001f);
    }

    // PR-06 ─────────────────────────────────────────────────────────────────
    // Without a smoothing component, Reconcile teleports the body and there's
    // nothing to lag — the entity behaves as it did before Step 4.
    [TestCase]
    public void PredictionRigidbody_Smoothing_DisabledByDefault_TeleportsCleanly()
    {
        // _predictionRb here was Initialized in SetUp with no smoothing.
        _body.GlobalPosition = new Vector3(1, 0, 0);

        _predictionRb.Reconcile(new RigidbodyState
        {
            Position = new Vector3(20, 0, 0),
            Rotation = Quaternion.Identity,
            LinearVelocity = Vector3.Zero,
            AngularVelocity = Vector3.Zero,
        });

        AssertThat((_body.GlobalPosition - new Vector3(20, 0, 0)).LengthSquared()).IsLess(0.001f);
        // No smoothing component — nothing observable beyond the body's new pose.
    }

    // PR-07 ─────────────────────────────────────────────────────────────────
    // When the pre-reconcile offset exceeds TeleportDistance, the smoother bails out:
    // a 50m correction looks worse lerped than snapped, so we skip smoothing entirely.
    [TestCase]
    public async Task PredictionRigidbody_Smoothing_TeleportThresholdSkipsSmoothing()
    {
        var (visualRoot, smoothing) = AttachSmoothing(_body, durationSec: 0.5f);
        smoothing.TeleportDistance = 5f; // explicit, regardless of helper default
        _predictionRb.Initialize(_body, smoothing);

        _body.GlobalPosition = new Vector3(1, 0, 0);
        visualRoot.GlobalPosition = new Vector3(1, 0, 0);
        await _runner.AwaitIdleFrame();

        // 50m jump, well past the 5m threshold.
        _predictionRb.Reconcile(new RigidbodyState
        {
            Position = new Vector3(50, 0, 0),
            Rotation = Quaternion.Identity,
            LinearVelocity = Vector3.Zero,
            AngularVelocity = Vector3.Zero,
        });

        AssertThat(smoothing.IsSmoothing).IsFalse();
        AssertThat(smoothing.CurrentOffset.LengthSquared()).IsLess(0.001f);
    }

    // PR-08 ─────────────────────────────────────────────────────────────────
    // Regression: in real prediction, Reconcile fires inside the resim loop —
    // body teleports to authoritative, then the loop replays N ticks of input
    // before the next _Process. If the smoother captures (preVisual - body) at
    // OnReconciled time, the offset is measured against the just-teleported pose,
    // not the post-resim one. The smoother then renders Visual = body_postresim
    // + offset, which makes Visual visibly *overshoot* its pre-reconcile pose by
    // the resim distance and lerp back — the bug the user reported as the vehicle
    // "moving more than it should and snapping back". This test reproduces that
    // sequence (Reconcile + body-moves-during-resim + first _Process) and asserts
    // Visual starts at preVisualPos, not at 2*preVisual − authoritative.
    [TestCase]
    public async Task PredictionRigidbody_Smoothing_VisualMatchesPreVisualPosAfterResim()
    {
        var (visualRoot, smoothing) = AttachSmoothing(_body, durationSec: 0.5f);
        _predictionRb.Initialize(_body, smoothing);

        // Pre-reconcile: body and visual at predicted pose (10, 0, 0).
        Vector3 preVisualPos = new(10, 0, 0);
        _body.GlobalPosition = preVisualPos;
        visualRoot.GlobalPosition = preVisualPos;
        await _runner.AwaitIdleFrame();

        // Reconcile teleports body to authoritative (8, 0, 0) — 2 units behind.
        _predictionRb.Reconcile(new RigidbodyState
        {
            Position = new Vector3(8, 0, 0),
            Rotation = Quaternion.Identity,
            LinearVelocity = Vector3.Zero,
            AngularVelocity = Vector3.Zero,
        });

        // Simulate the resim loop: an impulse + SpaceStep that returns the body
        // exactly to its pre-reconcile predicted pose. With unit mass and 1/60s
        // timestep, an impulse of (120, 0, 0) integrates to a 2-unit translation.
        _predictionRb.AddImpulse(new Vector3(120, 0, 0));
        _predictionRb.Simulate();
        PhysicsServer3D.SpaceStep(_space, 1f / 60f);
        PhysicsServer3D.SpaceFlushQueries(_space);

        // Sanity: body is back at (~10, 0, 0).
        AssertThat((_body.GlobalPosition - preVisualPos).LengthSquared())
            .OverrideFailureMessage($"resim did not return body to preVisual; body={_body.GlobalPosition}")
            .IsLess(0.01f);

        // First _Process consumes the pending recompute, measuring the offset
        // against the post-resim body pose.
        await _runner.AwaitIdleFrame();

        // With the bug: Visual = body_postresim + (preVisual - bodyAuthoritative)
        //             = 10 + (10 - 8) = 12 — overshoots preVisual by 2 units.
        // With the fix: offset is recomputed against post-resim body, ≈ 0; Visual ≈ preVisual.
        AssertThat((visualRoot.GlobalPosition - preVisualPos).LengthSquared())
            .OverrideFailureMessage($"visual {visualRoot.GlobalPosition} overshot pre-visual {preVisualPos}")
            .IsLess(0.5f);
    }

    private (Node3D visualRoot, PredictionVisualSmoothing3D smoothing) AttachSmoothing(
        RigidBody3D body, float durationSec)
    {
        var visualRoot = new Node3D { TopLevel = true };
        body.AddChild(visualRoot);

        var smoothing = new PredictionVisualSmoothing3D
        {
            Body = body,
            Visual = visualRoot,
            DurationSec = durationSec,
            TeleportDistance = 0f, // disable threshold so PR-04/PR-05 multi-unit reconciles still smooth
        };
        body.AddChild(smoothing);
        return (visualRoot, smoothing);
    }
}
