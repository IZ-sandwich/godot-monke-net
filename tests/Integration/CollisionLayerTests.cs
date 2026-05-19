using GameDemo;
using GdUnit4;
using Godot;
using MonkeNet.Client;
using MonkeNet.Shared;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Integration;

/// <summary>
/// CL-01, CL-02, CL-04: Regression tests for entity scene collision layers.
///
/// Jolt assigns a body's broad-phase layer at AddChild time from the initial
/// node values, so the .tscn baseline must already carry the correct layer/mask.
/// EntitySpawner runtime overrides cannot move a body between broad-phases.
///
/// Layer convention (project.godot):
///   layer 1  = Environment
///   layer 2  = ClientPlayers (LocalBall and all client-side networked scenes)
///   layer 16 = ServerPlayers (ServerBall — listen-server hidden)
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

    // CL-04 ────────────────────────────────────────────────────────────────────
    [TestCase]
    public void AllBallScenes_HaveNonZeroCollisionLayer()
    {
        // Catches the specific regression we saw: editor sets collision_layer=0
        // which removes the body from any Jolt broad-phase and silently breaks all collisions.
        string[] paths = {
            "res://demo/ball/ServerBall.tscn",
            "res://demo/ball/LocalBall.tscn",
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
