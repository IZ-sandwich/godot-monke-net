using System.Threading.Tasks;
using GameDemo;
using GdUnit4;
using Godot;
using MonkeNet.Shared;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Integration;

/// <summary>
/// PUSH-01..PUSH-03: SharedPlayerMovement.PushRigidBodies tests.
///
/// CharacterBody3D.MoveAndSlide does not propagate impulses to RigidBody3Ds it slides
/// against — the framework's PushRigidBodies hook applies a manual impulse along the
/// collision normal. The bug these tests guard against is that MoveAndSlide zeroes the
/// component of velocity *into* the wall during sliding, so reading
/// `_characterBody.Velocity` after the call always yields ~0 component into a head-on
/// collision and would never push. The fix passes the pre-MoveAndSlide ("attempted")
/// velocity to PushRigidBodies; these tests verify that.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class PlayerPushTests
{
    private ISceneRunner _runner;
    private Rid _space;
    private CharacterBody3D _player;
    private SharedPlayerMovement _movement;
    private RigidBody3D _ball;

    [BeforeTest]
    public async Task SetUp()
    {
        // MainScene sets up MonkeNetConfig and a viewport with a Jolt-backed physics space.
        // MonkeNetManager._EnterTree calls SpaceSetActive(false) so we step manually.
        MonkeNetConfig.Instance = null;
        _runner = ISceneRunner.Load("res://demo/MainScene.tscn", autoFree: true);
        await _runner.AwaitIdleFrame();
        _space = _runner.Scene().GetViewport().World3D.Space;

        // Disable physics_interpolation on test bodies — the project enables it globally
        // but our test sets transforms directly per call, and interpolation would lag the
        // physics-server view of the bodies behind the script-set transforms.
        var interpOff = Node.PhysicsInterpolationModeEnum.Off;

        // Player CharacterBody3D with capsule shape, mirroring LocalPlayer.tscn collision
        // setup (layer 2 = ClientPlayers, mask 3 = Environment + ClientPlayers). Position
        // is set BEFORE AddChild — if both bodies entered the tree at (0,0,0) the first
        // SpaceStep would resolve their overlap by ejecting the ball to a junk position
        // that no later GlobalPosition assignment seems to override cleanly.
        _player = new CharacterBody3D
        {
            CollisionLayer = 2,
            CollisionMask = 3,
            PhysicsInterpolationMode = interpOff,
            Position = new Vector3(0, -1f, 0),
        };
        _player.AddChild(new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Radius = 0.5f, Height = 2f },
        });
        _runner.Scene().AddChild(_player);

        _movement = new SharedPlayerMovement();
        _player.AddChild(_movement);
        _movement.Initialize(_player);

        // Ball RigidBody3D with sphere shape, mirroring LocalBall.tscn setup. GravityScale
        // = 0 isolates the push effect from gravity. Position will be reset per-test, but
        // start it well clear of the player so the first SpaceStep doesn't resolve any
        // initial overlap.
        _ball = new RigidBody3D
        {
            CollisionLayer = 2,
            CollisionMask = 3,
            Mass = 1f,
            GravityScale = 0f,
            LinearDamp = 0f,
            PhysicsInterpolationMode = interpOff,
            Position = new Vector3(50, 50, 50),
        };
        _ball.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 0.5f } });
        _runner.Scene().AddChild(_ball);

        await _runner.AwaitIdleFrame();

        // Pump the physics space once so Jolt registers both bodies in the broadphase.
        PhysicsServer3D.SpaceStep(_space, 1f / 60f);
        PhysicsServer3D.SpaceFlushQueries(_space);
    }

    // Teleport-style position set for a RigidBody3D: PhysicsServer3D owns the transform,
    // so writing Node3D.GlobalPosition alone doesn't always commit immediately. Use the
    // PhysicsServer state setter to force the new pose in one step, then mirror to the
    // node so reads from script see the same value.
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

    // PUSH-01 ────────────────────────────────────────────────────────────────
    // Head-on push: the regression case. Player walks straight at the ball; ball must
    // pick up velocity along the push direction. Before the fix, MoveAndSlide zeroed
    // the velocity-into-ball component before PushRigidBodies read it, so speedIntoBody
    // ≈ 0 → no impulse → ball did not move.
    [TestCase]
    public void PlayerPush_HeadOnIntoBall_TransfersImpulse()
    {
        // MainScene has a floor at Y=-2 (top); the player capsule (radius 0.5, height 2)
        // rests with its center at Y=-1 when bottomed-out on the floor. Place both at
        // that height so gravity doesn't tilt the player's trajectory below the ball
        // mid-walk and cause it to pass underneath. Centers 1.5 m apart in Z = ~0.5 m
        // surface-to-surface gap, which the player covers in a few ticks at MaxRunSpeed.
        _player.GlobalPosition = new Vector3(0, -1f, 0);
        SetBodyPosition(_ball, new Vector3(0, -1f, -1.5f));
        PhysicsServer3D.SpaceFlushQueries(_space);

        // MoveY = -1 → forward (Godot convention: -Z is forward in the input mapping).
        var input = new CharacterInputMessage { MoveX = 0, MoveY = -1, CameraYaw = 0 };

        // Drive the player into the ball over enough ticks to ensure contact occurs.
        int slidesWithBall = 0;
        int slidesWithOther = 0;
        Godot.Collections.Array<string> otherColliders = new();
        for (int i = 0; i < 20; i++)
        {
            _movement.AdvancePhysics(input);
            for (int s = 0; s < _player.GetSlideCollisionCount(); s++)
            {
                var c = _player.GetSlideCollision(s);
                var collider = c.GetCollider();
                if (collider == _ball) slidesWithBall++;
                else
                {
                    slidesWithOther++;
                    if (collider != null) otherColliders.Add(((Node)collider).Name);
                }
            }
            PhysicsServer3D.SpaceStep(_space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(_space);
        }

        AssertThat(slidesWithBall)
            .OverrideFailureMessage($"Player never slide-collided with ball. Player ended at {_player.GlobalPosition}, ball at {_ball.GlobalPosition}. Other slides: {slidesWithOther} with [{string.Join(", ", otherColliders)}].")
            .IsGreater(0);
        AssertThat(_ball.LinearVelocity.LengthSquared())
            .OverrideFailureMessage($"Ball did not move; ball vel={_ball.LinearVelocity}, slides-with-ball={slidesWithBall}")
            .IsGreater(0.001f);
        AssertThat(_ball.LinearVelocity.Z)
            .OverrideFailureMessage($"Ball pushed in wrong direction; velocity={_ball.LinearVelocity}")
            .IsLess(0f);
    }

    // PUSH-02 ────────────────────────────────────────────────────────────────
    // Glancing collision: player walks at an angle past the ball. The contact normal is
    // not aligned with player velocity, but the slide preserves enough of the velocity
    // along the push direction that an impulse is still applied. This case worked before
    // the fix as well (the surviving sliding component was non-zero) — included to guard
    // against a regression where the fix accidentally regresses the glancing path.
    [TestCase]
    public void PlayerPush_GlancingPastBall_StillPushes()
    {
        _player.GlobalPosition = new Vector3(0, -1f, 0);
        // Ball offset along X+Z so the player's forward (-Z) motion grazes its side.
        SetBodyPosition(_ball, new Vector3(0.7f, -1f, -1.5f));
        PhysicsServer3D.SpaceFlushQueries(_space);

        var input = new CharacterInputMessage { MoveX = 0, MoveY = -1, CameraYaw = 0 };

        for (int i = 0; i < 20; i++)
        {
            _movement.AdvancePhysics(input);
            PhysicsServer3D.SpaceStep(_space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(_space);
        }

        AssertThat(_ball.LinearVelocity.LengthSquared())
            .OverrideFailureMessage($"Glancing push did not move ball; velocity={_ball.LinearVelocity}")
            .IsGreater(0.001f);
    }

    // PUSH-03 ────────────────────────────────────────────────────────────────
    // No input ⇒ no push. With a stationary ball directly in front but no input applied,
    // CalculateVelocity yields zero horizontal velocity (gravity adds tiny -Y, but X/Z
    // stay zero). The ball must remain at rest — verifies we're not accidentally pushing
    // bodies just from being in contact.
    [TestCase]
    public void PlayerPush_NoInput_DoesNotPushBall()
    {
        _player.GlobalPosition = new Vector3(0, -1f, 0);
        SetBodyPosition(_ball, new Vector3(0, -1f, -1.0f));
        PhysicsServer3D.SpaceFlushQueries(_space);

        var input = new CharacterInputMessage { MoveX = 0, MoveY = 0, CameraYaw = 0 };

        for (int i = 0; i < 20; i++)
        {
            _movement.AdvancePhysics(input);
            PhysicsServer3D.SpaceStep(_space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(_space);
        }

        // Tolerate tiny numerical noise but reject any meaningful push.
        AssertThat(_ball.LinearVelocity.LengthSquared())
            .OverrideFailureMessage($"Ball moved without input; velocity={_ball.LinearVelocity}")
            .IsLess(0.01f);
    }
}
