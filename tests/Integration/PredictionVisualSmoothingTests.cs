using System.Threading.Tasks;
using GdUnit4;
using Godot;
using MonkeNet.Shared;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Integration;

/// <summary>
/// PR-04..PR-08 — <see cref="PredictionVisualSmoothing3D"/> behaviour tests.
///
/// The smoother is a client-only, render-frame-driven visual offset-decay layer
/// that hides the discrete pose jump a prediction reconcile produces on the
/// physics body. These tests run in a single Godot process / single physics
/// space because the smoother is purely a function of (prevBodyPose,
/// currentBodyPose, prevVisualPose) — it has no networking or cross-process
/// state, and an extra Godot process would add nothing.
///
/// Previously these lived inside <c>PredictionRigidbodyTests</c>, but the
/// concerns (impulse-queue API vs. visual offset decay) are separable and now
/// live in their own file.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class PredictionVisualSmoothingTests
{
    private ISceneRunner _runner;
    private RigidBody3D _body;
    private PredictionRigidbody3D _predictionRb;
    private Rid _space;

    [BeforeTest]
    public async Task SetUp()
    {
        MonkeNetConfig.Instance = null;
        _runner = ISceneRunner.Load("res://demo/MainScene.tscn", autoFree: true);
        await _runner.AwaitIdleFrame();
        _space = _runner.Scene().GetViewport().World3D.Space;

        _body = new RigidBody3D
        {
            GravityScale = 0f,
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

    // PR-04 ─────────────────────────────────────────────────────────────────
    // When a smoothing component is wired to PredictionRigidbody3D, Reconcile
    // captures the visual's pre-jump pose. The visible mesh should *not* snap
    // to the new body pose — it should lag behind, scheduled to catch up.
    [TestCase]
    public async Task PredictionRigidbody_Smoothing_VisualLagsAfterReconcile()
    {
        var (visualRoot, smoothing) = AttachSmoothing(_body, decayTime: 0.5f);
        _predictionRb.Initialize(_body, smoothing);

        _body.GlobalPosition = new Vector3(1, 0, 0);
        visualRoot.GlobalPosition = new Vector3(1, 0, 0);
        // Drive several physics frames so the dynamically-attached smoother
        // is firmly in the engine's _PhysicsProcess list with a valid
        // prev-tick baseline before the Reconcile happens. A single physics
        // frame doesn't suffice: when a node is AddChild'd between ticks,
        // Godot doesn't include it in the current tick's _PhysicsProcess
        // iteration, so the baseline-establishing fire only happens on the
        // second tick after attach.
        for (int i = 0; i < 5; i++) await AwaitPhysicsFrame();

        _predictionRb.Reconcile(new RigidbodyState
        {
            Position = new Vector3(50, 0, 0),
            Rotation = Quaternion.Identity,
            LinearVelocity = Vector3.Zero,
            AngularVelocity = Vector3.Zero,
        });

        // PredictionVisualSmoothing3D observes the teleport on its next
        // _PhysicsProcess, not synchronously inside Reconcile — drive a few
        // physics frames so the body delta is captured into the smoother.
        for (int i = 0; i < 3; i++) await AwaitPhysicsFrame();

        AssertThat(smoothing.IsSmoothing)
            .OverrideFailureMessage($"smoother did not capture jump; offset={smoothing.CurrentOffset}, body={_body.GlobalPosition}, visual={visualRoot.GlobalPosition}")
            .IsTrue();
        AssertThat(smoothing.CurrentOffset.Length()).IsGreater(40f);
    }

    // PR-05 ─────────────────────────────────────────────────────────────────
    // After enough _Process frames have elapsed (more than DurationSec), the
    // visual catches up and IsSmoothing returns false. The remaining offset
    // should be ~zero and the visual aligned with the body.
    [TestCase]
    public async Task PredictionRigidbody_Smoothing_VisualConvergesAfterDuration()
    {
        var (visualRoot, smoothing) = AttachSmoothing(_body, decayTime: 0.01f);
        _predictionRb.Initialize(_body, smoothing);

        _body.GlobalPosition = new Vector3(1, 0, 0);
        visualRoot.GlobalPosition = new Vector3(1, 0, 0);
        // Two physics frames: the first lets a dynamically AddChild'd node
        // register for processing (Godot skips its _PhysicsProcess in the
        // tick its parent attaches it); the second is the smoother's first
        // actual fire, which establishes its prev-tick baseline at (1,0,0).
        await AwaitPhysicsFrame();
        await AwaitPhysicsFrame();

        _predictionRb.Reconcile(new RigidbodyState
        {
            Position = new Vector3(20, 0, 0),
            Rotation = Quaternion.Identity,
            LinearVelocity = Vector3.Zero,
            AngularVelocity = Vector3.Zero,
        });

        for (int i = 0; i < 60; i++) await AwaitPhysicsFrame();

        AssertThat(smoothing.IsSmoothing).IsFalse();
        AssertThat((visualRoot.GlobalPosition - _body.GlobalPosition).LengthSquared())
            .OverrideFailureMessage($"visual {visualRoot.GlobalPosition} did not converge to body {_body.GlobalPosition}")
            .IsLess(0.001f);
    }

    // PR-06 ─────────────────────────────────────────────────────────────────
    // Without a smoothing component, Reconcile teleports the body and there's
    // nothing to lag — the entity behaves as it did before the smoother existed.
    [TestCase]
    public void PredictionRigidbody_Smoothing_DisabledByDefault_TeleportsCleanly()
    {
        _body.GlobalPosition = new Vector3(1, 0, 0);

        _predictionRb.Reconcile(new RigidbodyState
        {
            Position = new Vector3(20, 0, 0),
            Rotation = Quaternion.Identity,
            LinearVelocity = Vector3.Zero,
            AngularVelocity = Vector3.Zero,
        });

        AssertThat((_body.GlobalPosition - new Vector3(20, 0, 0)).LengthSquared()).IsLess(0.001f);
    }

    // PR-07 ─────────────────────────────────────────────────────────────────
    // When the pre-reconcile offset exceeds TeleportDistance, the smoother bails out:
    // a 50m correction looks worse lerped than snapped, so we skip smoothing entirely.
    [TestCase]
    public async Task PredictionRigidbody_Smoothing_TeleportThresholdSkipsSmoothing()
    {
        var (visualRoot, smoothing) = AttachSmoothing(_body, decayTime: 0.5f);
        smoothing.TeleportDistance = 5f;
        _predictionRb.Initialize(_body, smoothing);

        _body.GlobalPosition = new Vector3(1, 0, 0);
        visualRoot.GlobalPosition = new Vector3(1, 0, 0);
        // Two physics frames: the first lets a dynamically AddChild'd node
        // register for processing (Godot skips its _PhysicsProcess in the
        // tick its parent attaches it); the second is the smoother's first
        // actual fire, which establishes its prev-tick baseline at (1,0,0).
        await AwaitPhysicsFrame();
        await AwaitPhysicsFrame();

        _predictionRb.Reconcile(new RigidbodyState
        {
            Position = new Vector3(50, 0, 0),
            Rotation = Quaternion.Identity,
            LinearVelocity = Vector3.Zero,
            AngularVelocity = Vector3.Zero,
        });

        await AwaitPhysicsFrame();

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
        var (visualRoot, smoothing) = AttachSmoothing(_body, decayTime: 0.5f);
        _predictionRb.Initialize(_body, smoothing);

        Vector3 preVisualPos = new(10, 0, 0);
        _body.GlobalPosition = preVisualPos;
        visualRoot.GlobalPosition = preVisualPos;
        await AwaitPhysicsFrame();

        _predictionRb.Reconcile(new RigidbodyState
        {
            Position = new Vector3(8, 0, 0),
            Rotation = Quaternion.Identity,
            LinearVelocity = Vector3.Zero,
            AngularVelocity = Vector3.Zero,
        });

        _predictionRb.AddImpulse(new Vector3(120, 0, 0));
        _predictionRb.Simulate();
        PhysicsServer3D.SpaceStep(_space, 1f / 60f);
        PhysicsServer3D.SpaceFlushQueries(_space);

        AssertThat((_body.GlobalPosition - preVisualPos).LengthSquared())
            .OverrideFailureMessage($"resim did not return body to preVisual; body={_body.GlobalPosition}")
            .IsLess(0.01f);

        _body.LinearVelocity = Vector3.Zero;
        await AwaitPhysicsFrame();

        AssertThat((visualRoot.GlobalPosition - preVisualPos).LengthSquared())
            .OverrideFailureMessage($"visual {visualRoot.GlobalPosition} overshot pre-visual {preVisualPos}")
            .IsLess(0.5f);
    }

    // Drives one physics tick on the scene tree. GdUnit4's AwaitIdleFrame
    // waits for SceneTree.process_frame which doesn't guarantee a physics
    // tick has fired since the test's last action; the smoother does all
    // its work in _PhysicsProcess so tests need explicit physics-frame
    // synchronisation to observe its effects.
    private async Task AwaitPhysicsFrame()
    {
        var tree = _runner.Scene().GetTree();
        await tree.ToSignal(tree, SceneTree.SignalName.PhysicsFrame);
    }

    private (Node3D visualRoot, PredictionVisualSmoothing3D smoothing) AttachSmoothing(
        RigidBody3D body, float decayTime)
    {
        // Regular (non-top_level) child of the body so Godot's SceneTreeFTI
        // lerps it through the parent chain alongside other inherited children.
        // Top_level=true would skip FTI's parent concat and leave the visual
        // stair-stepping at physics-tick rate.
        var visualRoot = new Node3D();
        body.AddChild(visualRoot);

        var smoothing = new PredictionVisualSmoothing3D
        {
            Body = body,
            Visual = visualRoot,
            DecayTime = decayTime,
            TeleportDistance = 0f,
        };
        body.AddChild(smoothing);
        return (visualRoot, smoothing);
    }
}
