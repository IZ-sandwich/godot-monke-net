using System.Threading.Tasks;
using GdUnit4;
using Godot;
using MonkeNet.Shared;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Integration;

/// <summary>
/// OR-01..OR-02: OfflineRigidbody3D snapshot/restore tests.
///
/// During the client's resimulation loop, every dynamic body in the shared physics
/// space gets re-stepped N times. Without OfflineRigidbody3D, non-networked scenery
/// (loose props, debris, etc.) drifts each rollback. These tests verify that snapshot
/// + restore cancels that drift.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class OfflineRigidbodyTests
{
    private ISceneRunner _runner;
    private RigidBody3D _offlineBody;
    private OfflineRigidbody3D _offlineMarker;
    private Rid _space;

    [BeforeTest]
    public async Task SetUp()
    {
        MonkeNetConfig.Instance = null;
        _runner = ISceneRunner.Load("res://demo/MainScene.tscn", autoFree: true);
        await _runner.AwaitIdleFrame();
        _space = _runner.Scene().GetViewport().World3D.Space;

        _offlineBody = new RigidBody3D
        {
            GravityScale = 0f,
            Mass = 1f,
            LinearDamp = 0f,
            AngularDamp = 0f,
        };
        _offlineBody.AddChild(new CollisionShape3D { Shape = new SphereShape3D() });
        _runner.Scene().AddChild(_offlineBody);

        _offlineMarker = new OfflineRigidbody3D { Body = _offlineBody };
        _offlineBody.AddChild(_offlineMarker);

        await _runner.AwaitIdleFrame();
    }

    [AfterTest]
    public void TearDown()
    {
        _runner?.Dispose();
        MonkeNetConfig.Instance = null;
    }

    // OR-01 ────────────────────────────────────────────────────────────────────
    // Place the body, snapshot, give it velocity and step it (simulating what would
    // happen during a resim loop), then restore. Pose should match the snapshot.
    [TestCase]
    public void OfflineRigidbody_SnapshotRestoreCancelsResimDrift()
    {
        _offlineBody.GlobalPosition = new Vector3(7, 3, -2);
        _offlineBody.LinearVelocity = Vector3.Zero;

        OfflineRigidbody3D.SnapshotAll();

        // Simulate the body being affected by a resim loop — give it velocity, step.
        _offlineBody.LinearVelocity = new Vector3(10, 0, 0);
        for (int i = 0; i < 10; i++)
        {
            PhysicsServer3D.SpaceStep(_space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(_space);
        }

        AssertThat((_offlineBody.GlobalPosition - new Vector3(7, 3, -2)).LengthSquared())
            .IsGreater(0.5f); // sanity: it moved

        OfflineRigidbody3D.RestoreAll();

        AssertThat((_offlineBody.GlobalPosition - new Vector3(7, 3, -2)).LengthSquared())
            .OverrideFailureMessage($"position {_offlineBody.GlobalPosition} not restored to (7,3,-2)")
            .IsLess(0.0001f);
        AssertThat(_offlineBody.LinearVelocity.LengthSquared())
            .OverrideFailureMessage($"velocity {_offlineBody.LinearVelocity} not restored to zero")
            .IsLess(0.0001f);
    }

    // OR-02 ────────────────────────────────────────────────────────────────────
    // ExitTree must unregister so freed bodies aren't touched by Snapshot/Restore.
    [TestCase]
    public async Task OfflineRigidbody_UnregistersOnExitTree()
    {
        int countBefore = OfflineRigidbody3D.InstanceCount;

        var extraBody = new RigidBody3D();
        extraBody.AddChild(new CollisionShape3D { Shape = new SphereShape3D() });
        _runner.Scene().AddChild(extraBody);
        var extraMarker = new OfflineRigidbody3D { Body = extraBody };
        extraBody.AddChild(extraMarker);
        await _runner.AwaitIdleFrame();

        AssertThat(OfflineRigidbody3D.InstanceCount).IsEqual(countBefore + 1);

        extraBody.QueueFree();
        await _runner.AwaitIdleFrame();
        await _runner.AwaitIdleFrame();

        AssertThat(OfflineRigidbody3D.InstanceCount).IsEqual(countBefore);
    }
}
