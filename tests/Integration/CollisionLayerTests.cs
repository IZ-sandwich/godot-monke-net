using GameDemo;
using GdUnit4;
using Godot;
using MonkeNet.Client;
using MonkeNet.Shared;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Integration;

/// <summary>
/// CL-01..CL-03: Regression tests for entity scene collision layers.
///
/// Jolt assigns a body's broad-phase layer at AddChild time from the initial
/// node values, so the .tscn baseline must already carry the correct layer/mask.
/// EntitySpawner runtime overrides cannot move a body between broad-phases.
///
/// These tests catch the specific class of bug where a contributor edits a
/// demo .tscn in the editor and accidentally resets collision_layer to 0,
/// which silently breaks all collision interactions.
///
/// Layer convention (project.godot):
///   layer 1  = Environment
///   layer 2  = ClientPlayers (LocalBall, DummyBall, LocalPlayer, DummyPlayer)
///   layer 16 = ServerPlayers (ServerBall, ServerPlayer — listen-server hidden)
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class CollisionLayerTests
{
    private const uint LayerEnvironment = 1u;
    private const uint LayerClientPlayers = 2u;
    private const uint LayerServerPlayers = 1u << 15;
    private const uint MaskClient = LayerEnvironment | LayerClientPlayers;
    private const uint MaskServer = LayerEnvironment | LayerServerPlayers;

    // CL-01 ────────────────────────────────────────────────────────────────────
    [TestCase]
    public void ServerBall_HasServerLayerAndEnvironmentMask()
    {
        var scene = ResourceLoader.Load<PackedScene>("res://demo/ball/ServerBall.tscn");
        AssertThat(scene).IsNotNull();
        var instance = scene.Instantiate<RigidBody3D>();

        AssertThat(instance.CollisionLayer).IsEqual(LayerServerPlayers);
        AssertThat(instance.CollisionMask).IsEqual(MaskServer);

        instance.QueueFree();
    }

    // CL-02 ────────────────────────────────────────────────────────────────────
    [TestCase]
    public void LocalBall_HasClientLayerAndClientMask()
    {
        var scene = ResourceLoader.Load<PackedScene>("res://demo/ball/LocalBall.tscn");
        AssertThat(scene).IsNotNull();
        var instance = scene.Instantiate<RigidBody3D>();

        AssertThat(instance.CollisionLayer).IsEqual(LayerClientPlayers);
        AssertThat(instance.CollisionMask).IsEqual(MaskClient);

        instance.QueueFree();
    }

    // CL-03 ────────────────────────────────────────────────────────────────────
    [TestCase]
    public void DummyBall_ExistsAndHasClientLayerAndExtendsClientInterpolatedEntity()
    {
        var scene = ResourceLoader.Load<PackedScene>("res://demo/ball/DummyBall.tscn");
        AssertThat(scene).OverrideFailureMessage("DummyBall.tscn must exist so non-authority clients can render the ball")
                         .IsNotNull();
        var instance = scene.Instantiate<CollisionObject3D>();

        AssertThat(instance.CollisionLayer).IsEqual(LayerClientPlayers);
        AssertThat(instance.CollisionMask).IsEqual(MaskClient);

        // The script node child must extend ClientInterpolatedEntity so the
        // snapshot interpolator can drive its transform.
        ClientInterpolatedEntity scriptNode = null;
        foreach (Node child in instance.GetChildren())
        {
            if (child is ClientInterpolatedEntity cie)
            {
                scriptNode = cie;
                break;
            }
        }
        AssertThat(scriptNode)
            .OverrideFailureMessage("DummyBall.tscn must contain a child node that extends ClientInterpolatedEntity")
            .IsNotNull();

        instance.QueueFree();
    }

    // CL-05 ────────────────────────────────────────────────────────────────────
    // DummyBall must be a RigidBody3D so non-owner players can push it via
    // CharacterBody3D slide-collision impulses (StaticBody3D rejects impulses).
    [TestCase]
    public void DummyBall_IsRigidBody3D_SoLocalPushesAreFelt()
    {
        var scene = ResourceLoader.Load<PackedScene>("res://demo/ball/DummyBall.tscn");
        var instance = scene.Instantiate<CollisionObject3D>();

        AssertThat(instance is RigidBody3D)
            .OverrideFailureMessage("DummyBall must be a RigidBody3D so other players' push impulses take effect locally; was " + instance.GetType().Name)
            .IsTrue();

        instance.QueueFree();
    }

    // CL-06 ────────────────────────────────────────────────────────────────────
    // DummyBallStateInterpolation must overwrite LinearVelocity/AngularVelocity from
    // the server snapshot so local physics extrapolates along the authoritative vector
    // between snapshots. Otherwise the body would integrate stale velocity for a tick.
    [TestCase]
    public void DummyBallStateInterpolation_WritesVelocityFromSnapshot()
    {
        var scene = ResourceLoader.Load<PackedScene>("res://demo/ball/DummyBall.tscn");
        var ball = scene.Instantiate<RigidBody3D>();

        DummyBallStateInterpolation interp = null;
        foreach (Node child in ball.GetChildren())
            if (child is DummyBallStateInterpolation d) { interp = d; break; }
        AssertThat(interp).IsNotNull();

        var past = new EntityStateMessage
        {
            EntityId = 1,
            Position = Vector3.Zero,
            Rotation = Vector3.Zero,
            Velocity = Vector3.Zero,
            AngularVelocity = Vector3.Zero,
        };
        var future = new EntityStateMessage
        {
            EntityId = 1,
            Position = new Vector3(0.1f, 0, 0),       // small delta — soft-correct path
            Rotation = Vector3.Zero,
            Velocity = new Vector3(3, 0, 0),
            AngularVelocity = new Vector3(0, 5, 0),
        };

        interp!.HandleStateInterpolation(past, future, 1f);

        AssertThat(ball.LinearVelocity).IsEqual(future.Velocity);
        AssertThat(ball.AngularVelocity).IsEqual(future.AngularVelocity);

        ball.QueueFree();
    }

    // CL-04 ────────────────────────────────────────────────────────────────────
    [TestCase]
    public void AllBallScenes_HaveNonZeroCollisionLayer()
    {
        // Catches the specific regression we saw: editor sets collision_layer=0
        // which removes the body from any Jolt broad-phase and silently breaks all collisions.
        string[] paths = {
            "res://demo/ball/ServerBall.tscn",
            "res://demo/ball/LocalBall.tscn",
            "res://demo/ball/DummyBall.tscn",
        };
        foreach (string path in paths)
        {
            var scene = ResourceLoader.Load<PackedScene>(path);
            AssertThat(scene).OverrideFailureMessage($"{path} could not be loaded").IsNotNull();
            var instance = scene.Instantiate<CollisionObject3D>();
            AssertThat((int)instance.CollisionLayer)
                .OverrideFailureMessage($"{path} has collision_layer = 0; Jolt will assign no broad-phase layer and the body will not collide with anything")
                .IsGreater(0);
            instance.QueueFree();
        }
    }
}
