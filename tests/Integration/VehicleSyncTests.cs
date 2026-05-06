using System.Threading.Tasks;
using GameDemo;
using GdUnit4;
using Godot;
using MonkeNet.Shared;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Integration;

/// <summary>
/// V-01..V-02: Vehicle prediction template tests.
///
/// The vehicle entity uses <see cref="PredictionRigidbody3D"/> end-to-end. The same
/// <see cref="VehiclePhysics.AdvancePhysics"/> call is made on both the server's
/// authoritative tick and the client's predicted tick — these tests confirm that
/// produces deterministic identical trajectories given identical input, which is
/// the property the rollback resimulation loop depends on for vehicles.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class VehicleSyncTests
{
    private ISceneRunner _runner;
    private Rid _space;

    [BeforeTest]
    public async Task SetUp()
    {
        MonkeNetConfig.Instance = null;
        _runner = ISceneRunner.Load("res://demo/MainScene.tscn", autoFree: true);
        await _runner.AwaitIdleFrame();
        _space = _runner.Scene().GetViewport().World3D.Space;
    }

    [AfterTest]
    public void TearDown()
    {
        _runner?.Dispose();
        MonkeNetConfig.Instance = null;
    }

    // V-01 ─────────────────────────────────────────────────────────────────────
    // Running the same input sequence twice from the same starting state must
    // produce the same final state. This is the determinism the rollback loop
    // relies on: the server's authoritative tick and the client's resimulated
    // tick are the *same function* of (state, input).
    //
    // Single-body pattern (vs. two bodies in the same space) avoids Jolt's
    // body-iteration-order float variance. Real server vs. client run in
    // separate processes / separate spaces, so this is the right model.
    [TestCase]
    public void Vehicle_AppliedForceProducesDeterministicTrajectory()
    {
        var (body, predictionRb) = CreateVehicle(Vector3.Zero);

        var inputs = new CharacterInputMessage[20];
        for (int i = 0; i < inputs.Length; i++)
            inputs[i] = new CharacterInputMessage
            {
                MoveX = (i % 5 < 3) ? -0.5f : 0.5f,
                MoveY = -1f,
                CameraYaw = 0,
                Keys = 0,
            };

        // First run.
        for (int t = 0; t < inputs.Length; t++)
        {
            VehiclePhysics.AdvancePhysics(predictionRb, inputs[t]);
            PhysicsServer3D.SpaceStep(_space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(_space);
        }
        Vector3 firstFinalPos = body.GlobalPosition;
        Vector3 firstFinalVel = body.LinearVelocity;
        Vector3 firstFinalAng = body.AngularVelocity;

        // Sanity: throttle actually moved the body, otherwise the assertion below is meaningless.
        AssertThat(firstFinalPos.LengthSquared()).IsGreater(0.1f);

        // Reset to the initial state and run identically.
        predictionRb.Reconcile(new RigidbodyState
        {
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            LinearVelocity = Vector3.Zero,
            AngularVelocity = Vector3.Zero,
        });
        for (int t = 0; t < inputs.Length; t++)
        {
            VehiclePhysics.AdvancePhysics(predictionRb, inputs[t]);
            PhysicsServer3D.SpaceStep(_space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(_space);
        }

        AssertThat((firstFinalPos - body.GlobalPosition).LengthSquared())
            .OverrideFailureMessage($"second-run position {body.GlobalPosition} differs from first {firstFinalPos}")
            .IsLess(0.0001f);
        AssertThat((firstFinalVel - body.LinearVelocity).LengthSquared()).IsLess(0.0001f);
        AssertThat((firstFinalAng - body.AngularVelocity).LengthSquared()).IsLess(0.0001f);
    }

    // V-02 ─────────────────────────────────────────────────────────────────────
    // Resim across a Reconcile call must reproduce the same final state — the
    // direct guarantee a vehicle's HandleReconciliation + ResimulateTick path needs.
    [TestCase]
    public void Vehicle_ResimAcrossReconcileReproducesFinalState()
    {
        var (body, predictionRb) = CreateVehicle(Vector3.Zero);

        var inputs = new CharacterInputMessage[15];
        for (int i = 0; i < inputs.Length; i++)
            inputs[i] = new CharacterInputMessage
            {
                MoveX = ((i % 3) - 1) * 0.5f,
                MoveY = -0.8f,
                CameraYaw = 0,
                Keys = 0,
            };

        // First run: full 0..14, capture state at tick 7 and final state.
        RigidbodyState atTick7 = default;
        for (int t = 0; t < inputs.Length; t++)
        {
            VehiclePhysics.AdvancePhysics(predictionRb, inputs[t]);
            PhysicsServer3D.SpaceStep(_space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(_space);
            if (t == 7) atTick7 = predictionRb.SnapshotState();
        }
        Vector3 firstRunFinalPosition = body.GlobalPosition;
        Vector3 firstRunFinalVelocity = body.LinearVelocity;

        // Sanity: the body actually moved.
        AssertThat(firstRunFinalPosition.LengthSquared()).IsGreater(0.1f);

        // Reconcile to mid-run, replay inputs[8..14], assert final matches.
        predictionRb.Reconcile(atTick7);
        for (int t = 8; t < inputs.Length; t++)
        {
            VehiclePhysics.AdvancePhysics(predictionRb, inputs[t]);
            PhysicsServer3D.SpaceStep(_space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(_space);
        }

        AssertThat((firstRunFinalPosition - body.GlobalPosition).LengthSquared())
            .OverrideFailureMessage($"resim position {body.GlobalPosition} differs from first-run {firstRunFinalPosition}")
            .IsLess(0.0001f);
        AssertThat((firstRunFinalVelocity - body.LinearVelocity).LengthSquared()).IsLess(0.0001f);
    }

    // V-03 ─────────────────────────────────────────────────────────────────────
    // Steering sign flips when the vehicle is moving backward — pressing "left"
    // while reversing rotates the body opposite to pressing "left" while moving
    // forward. This is the "back the car out" feel real cars have.
    [TestCase]
    public void Vehicle_SteeringInvertsWhenMovingBackward()
    {
        // Forward run: accelerate forward for 30 ticks, then steer left for 10.
        var (bodyForward, prForward) = CreateVehicle(Vector3.Zero);
        AccelerateInDirection(prForward, throttle: -1f, ticks: 30);
        for (int t = 0; t < 10; t++)
            StepWithInput(prForward, new CharacterInputMessage { MoveX = -1f, MoveY = 0 });
        float forwardYawRate = bodyForward.AngularVelocity.Y;

        // Reverse run: accelerate backward for 30 ticks, then steer left for 10.
        var (bodyReverse, prReverse) = CreateVehicle(new Vector3(0, 0, 100));
        AccelerateInDirection(prReverse, throttle: 1f, ticks: 30);
        for (int t = 0; t < 10; t++)
            StepWithInput(prReverse, new CharacterInputMessage { MoveX = -1f, MoveY = 0 });
        float reverseYawRate = bodyReverse.AngularVelocity.Y;

        // Sanity: both runs produced meaningful yaw, otherwise the sign comparison is moot.
        AssertThat(Mathf.Abs(forwardYawRate)).IsGreater(0.05f);
        AssertThat(Mathf.Abs(reverseYawRate)).IsGreater(0.05f);

        // Signs must differ — that's the whole point of the change.
        AssertThat(forwardYawRate * reverseYawRate)
            .OverrideFailureMessage($"forward yaw {forwardYawRate} and reverse yaw {reverseYawRate} have the same sign — steering should invert when reversing")
            .IsLess(0f);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private void AccelerateInDirection(PredictionRigidbody3D predictionRb, float throttle, int ticks)
    {
        // throttle convention follows CharacterInputMessage: MoveY = -1 → forward, +1 → reverse.
        // Pass throttle = -1 for full forward, +1 for full reverse.
        var input = new CharacterInputMessage { MoveX = 0, MoveY = throttle, CameraYaw = 0, Keys = 0 };
        for (int t = 0; t < ticks; t++)
            StepWithInput(predictionRb, input);
    }

    private void StepWithInput(PredictionRigidbody3D predictionRb, CharacterInputMessage input)
    {
        VehiclePhysics.AdvancePhysics(predictionRb, input);
        PhysicsServer3D.SpaceStep(_space, 1f / 60f);
        PhysicsServer3D.SpaceFlushQueries(_space);
    }


    private (RigidBody3D body, PredictionRigidbody3D predictionRb) CreateVehicle(Vector3 position)
    {
        var body = new RigidBody3D
        {
            Position = position,
            Mass = 1f,
            GravityScale = 0f,  // isolate vehicle physics from gravity for deterministic tests
            LinearDamp = 0f,
            AngularDamp = 0f,
        };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(2, 1, 4) } });
        _runner.Scene().AddChild(body);

        var predictionRb = new PredictionRigidbody3D();
        body.AddChild(predictionRb);
        predictionRb.Initialize(body);

        return (body, predictionRb);
    }
}
