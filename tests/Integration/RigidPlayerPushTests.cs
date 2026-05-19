using System.Threading.Tasks;
using GameDemo;
using GdUnit4;
using Godot;
using MonkeNet.Shared;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Integration;

/// <summary>
/// RP-PUSH-06 / RP-JUMP-01: single-process <see cref="RigidPlayerPhysics"/> tests
/// that have no clean multi-process analogue.
///
/// The head-on push-into-RigidBody scenarios (vehicle/ball convergence, advance,
/// no-input) were rewritten as multi-process+video tests in
/// <c>tests/MultiProcess/MultiProcessRigidPlayerPushTests.cs</c>. The two cases
/// kept here exercise behaviour that requires a custom in-test rig (static wall
/// spawn, capsule-with-offset jump body) which the harness doesn't expose, so
/// they remain ISceneRunner tests stepping a Jolt physics space directly.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class RigidPlayerPushTests
{
    private ISceneRunner _runner;
    private Rid _space;
    private RigidBody3D _player;
    private PredictionRigidbody3D _predictionRb;

    [BeforeTest]
    public async Task SetUp()
    {
        // MainScene sets up MonkeNetConfig and a viewport with a Jolt-backed physics space.
        // MonkeNetManager._EnterTree calls SpaceSetActive(false) so we step manually.
        MonkeNetConfig.Instance = null;
        _runner = ISceneRunner.Load("res://demo/MainScene.tscn", autoFree: true);
        await _runner.AwaitIdleFrame();
        _space = _runner.Scene().GetViewport().World3D.Space;

        var interpOff = Node.PhysicsInterpolationModeEnum.Off;

        // RigidBody3D player. GravityScale = 0 isolates horizontal collision physics; lock
        // rotation so the body stays upright without leaning. Layers/mask mirror
        // LocalRigidPlayer.tscn (2 = ClientPlayers, 3 = Environment + ClientPlayers).
        _player = new RigidBody3D
        {
            CollisionLayer = 2,
            CollisionMask = 3,
            Mass = 1f,
            GravityScale = 0f,
            LinearDamp = 0f,
            AngularDamp = 0f,
            LockRotation = true,
            PhysicsInterpolationMode = interpOff,
            Position = new Vector3(0, -1f, 0),
        };
        _player.AddChild(new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Radius = 0.5f, Height = 2f },
        });
        _runner.Scene().AddChild(_player);

        _predictionRb = new PredictionRigidbody3D();
        _player.AddChild(_predictionRb);
        _predictionRb.Initialize(_player);

        await _runner.AwaitIdleFrame();
        PhysicsServer3D.SpaceStep(_space, 1f / 60f);
        PhysicsServer3D.SpaceFlushQueries(_space);
    }

    // Teleport-style position set for a RigidBody3D: PhysicsServer3D owns the transform,
    // so writing Node3D.GlobalPosition alone doesn't always commit immediately.
    private void SetBodyPosition(RigidBody3D body, Vector3 pos)
    {
        var t = body.GlobalTransform;
        t.Origin = pos;
        PhysicsServer3D.BodySetState(body.GetRid(), PhysicsServer3D.BodyState.Transform, t);
        body.GlobalPosition = pos;
        body.LinearVelocity = Vector3.Zero;
        body.AngularVelocity = Vector3.Zero;
    }

    [AfterTest]
    public void TearDown()
    {
        _runner?.Dispose();
        MonkeNetConfig.Instance = null;
    }

    // RP-JUMP-01 ────────────────────────────────────────────────────────────
    // Jumping must work when the player rests on a flat floor. This test
    // replicates the LocalRigidPlayer.tscn geometry where the CollisionShape3D
    // is offset by Y=+1 from the body — so when the player settles on the floor,
    // `body.GlobalPosition.Y` equals the floor's top surface. Under the bug, the
    // ground-probe ray started AT body.GlobalPosition.Y, which sat exactly on the
    // floor surface; PhysicsServer3D.IntersectRay returns no hit when the origin
    // is on/inside a body, so IsOnGround silently returned false and jump input
    // was ignored on flat ground. The fix lifts the ray origin a few cm above
    // the body so the start is unambiguously in free space.
    [TestCase]
    public async Task RigidPlayer_OnFlatFloor_JumpsWhenSpacePressed()
    {
        // Park the existing _player far away — this test builds a fresh body that
        // mirrors the scene's CollisionShape3D offset.
        SetBodyPosition(_player, new Vector3(60, 60, 60));

        var interpOff = Node.PhysicsInterpolationModeEnum.Off;

        // Body.Position.Y = -2 puts body's origin on the floor's top surface (the
        // floor in MainScene is a CSGBox3D centered at Y=-2.5 with size Y=1, so
        // its top is at Y=-2). The CollisionShape3D is offset Y=+1 to match the
        // scene, so the capsule extends from body.Y to body.Y + 2 — bottom flush
        // with the floor.
        var body = new RigidBody3D
        {
            CollisionLayer = 2,
            CollisionMask = 3,
            Mass = 1f,
            LinearDamp = 0f,
            AngularDamp = 0f,
            LockRotation = true,
            PhysicsInterpolationMode = interpOff,
            Position = new Vector3(0, -2f, 0),
        };
        var shape = new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Radius = 0.5f, Height = 2f },
            Position = new Vector3(0, 1f, 0),
        };
        body.AddChild(shape);
        _runner.Scene().AddChild(body);

        var predictionRb = new PredictionRigidbody3D();
        body.AddChild(predictionRb);
        predictionRb.Initialize(body);

        await _runner.AwaitIdleFrame();

        // Settle the body so any spawn-time penetration resolution finishes.
        for (int i = 0; i < 5; i++)
        {
            PhysicsServer3D.SpaceStep(_space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(_space);
        }

        float yBeforeJump = body.GlobalPosition.Y;
        var jumpInput = new CharacterInputMessage
        {
            MoveX = 0,
            MoveY = 0,
            CameraYaw = 0,
            Keys = (byte)InputFlags.Space,
        };

        // One tick of jump input. AdvancePhysics should detect ground, set
        // LinearVelocity.Y to JumpVelocity, and the body should leave the floor
        // immediately on the next SpaceStep.
        RigidPlayerPhysics.AdvancePhysics(predictionRb, jumpInput);
        PhysicsServer3D.SpaceStep(_space, 1f / 60f);
        PhysicsServer3D.SpaceFlushQueries(_space);

        // After one tick at JumpVelocity = 6 m/s the body should have moved up by
        // ~6/60 = 0.1 m (less Jolt's gravity tick, ~0.083 m). Anything above
        // a couple cm rules out "jump silently ignored" while remaining loose
        // enough not to assume an exact integration scheme.
        float yAfterJump = body.GlobalPosition.Y;
        AssertThat(yAfterJump - yBeforeJump)
            .OverrideFailureMessage(
                $"Jump on flat floor did not lift the body — IsOnGround likely returned " +
                $"false because the ground probe ray started on the floor's top surface. " +
                $"yBeforeJump={yBeforeJump:F4}, yAfterJump={yAfterJump:F4}, " +
                $"delta={yAfterJump - yBeforeJump:F4} m (expected > 0.05 m).")
            .IsGreater(0.05f);
    }

    // RP-PUSH-06 ────────────────────────────────────────────────────────────
    // Player runs head-on into an immovable static wall (no cube — direct contact
    // chain ends at infinite mass). The user-visible bug is: the rigid player
    // visibly creeps forward into the cube/wall stack and snaps back to its
    // original spot every few frames. The single-process root cause is that
    // RigidPlayerPhysics overwrites velocity to -MaxRunSpeed every tick via
    // SetLinearVelocity; when the player is in contact with an immovable surface,
    // Jolt's contact constraint has to reabsorb that fresh -5 m/s every step, but
    // the integrator has already advanced the body ~8 cm into the wall before
    // the constraint runs — so during the transient the player penetrates the
    // wall noticeably before being shoved back.
    //
    // The fix: drive horizontal velocity through Jolt's solver (impulse with a
    // max-acceleration cap) instead of replacing the body's post-contact velocity
    // wholesale. The contact constraint then absorbs each small impulse before
    // the integrator can push the body past the contact surface, keeping
    // overshoot to sub-centimeter.
    [TestCase]
    public void RigidPlayerPush_HeadOnIntoStaticWall_DoesNotPenetrate()
    {
        SetBodyPosition(_player, new Vector3(0, -1f, 0));

        // Static wall front face at z = -2.5 (center -3, half-size 0.5). Player
        // capsule front face is 0.5 ahead of body, so first contact at
        // player.Z = -2.0. Anything more forward = wall penetration.
        var wall = new StaticBody3D
        {
            CollisionLayer = 1,
            PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Off,
            Position = new Vector3(0, -1f, -3f),
        };
        wall.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(20, 4, 1) },
        });
        _runner.Scene().AddChild(wall);

        PhysicsServer3D.SpaceFlushQueries(_space);

        var input = new CharacterInputMessage { MoveX = 0, MoveY = -1, CameraYaw = 0 };

        // Phase 1: drive up to the wall and settle. After ~30 ticks the body has lost
        // its free-flight inertia to the wall constraint and Jolt has zeroed its
        // velocity at rest.
        for (int i = 0; i < 30; i++)
        {
            RigidPlayerPhysics.AdvancePhysics(_predictionRb, input);
            PhysicsServer3D.SpaceStep(_space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(_space);
        }

        // Sanity: body really is at rest against the wall now. If this fails the test
        // setup is wrong, not the physics under test.
        AssertThat(Mathf.Abs(_player.LinearVelocity.Z))
            .OverrideFailureMessage(
                $"Test setup: player did not reach rest against the wall. " +
                $"vel.Z={_player.LinearVelocity.Z:F3}, pos.Z={_player.GlobalPosition.Z:F3}.")
            .IsLess(0.5f);

        // Now press forward one more tick. AdvancePhysics queues its drive op and
        // Simulate flushes it to the body. The body velocity AFTER this call (and
        // before the next SpaceStep) is what the integrator will use to advance the
        // body next step, and it's what the rollback system would record as the
        // body's post-tick velocity for cross-process comparison.
        //
        // The bug: SetLinearVelocity overwrites this to -MaxRunSpeed regardless of
        // contact, so each tick the integrator advances the body by speed × dt deep
        // into the wall before the next-tick constraint solver pulls it back. The
        // intra-step excursion is what the user sees as forward creep + snap-back,
        // and the per-tick velocity discontinuity (0 → -5 → 0 → -5...) feeds
        // cross-Jolt nondeterminism into the saved tick states, causing the
        // velocity-divergence threshold in LocalRigidPlayerPrediction to trip and
        // hard-reconcile the body — every few snapshots in netplay.
        //
        // The fix caps the per-tick velocity change at MaxHorizontalAccel × dt
        // (~0.83 m/s at 60 Hz). With the body at rest, that's the worst-case post-
        // Simulate velocity we should ever see — well below the previous design's
        // -5 m/s.
        float preSimulateVz = _player.LinearVelocity.Z;
        RigidPlayerPhysics.AdvancePhysics(_predictionRb, input);
        float postSimulateVz = _player.LinearVelocity.Z;
        float velocityInjected = Mathf.Abs(postSimulateVz - preSimulateVz);

        AssertThat(velocityInjected)
            .OverrideFailureMessage(
                $"After one tick of forward input against an immovable wall, the body's " +
                $"horizontal velocity jumped by {velocityInjected:F3} m/s " +
                $"(pre={preSimulateVz:F3}, post={postSimulateVz:F3}). The contact constraint " +
                $"had already zeroed the body's velocity, so the input drive should add at " +
                $"most ~MaxHorizontalAccel × dt ≈ 0.83 m/s. A jump straight to MaxRunSpeed " +
                $"means SetLinearVelocity is overriding contact response — the integrator " +
                $"will then shove the body 8 cm/tick into the wall before the next constraint " +
                $"pass, creating the forward-creep / snap-back pattern. Expected < 1.5 m/s.")
            .IsLess(1.5f);
    }
}
