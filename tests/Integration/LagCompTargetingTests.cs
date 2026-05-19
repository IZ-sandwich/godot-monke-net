using System.Threading.Tasks;
using GdUnit4;
using Godot;
using MonkeNet.Serializer;
using MonkeNet.Server;
using MonkeNet.Shared;
using MonkeNet.Tests.Infrastructure;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Integration;

/// <summary>
/// LCT-01..LCT-02: Lag-compensation targeting accuracy and graceful degradation
/// at the history-window boundary.
///
/// Existing LC-01..LC-04 (in LagCompensationTests.cs) verify the buffer eviction
/// policy, rewind/restore, and the missing-tick counter. These two tests verify
/// the *quantitative* targeting guarantee: a raycast at a past tick must hit
/// the body at its captured pose (not its current pose), and a raycast outside
/// the history window must fall back gracefully without crashing.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class LagCompTargetingTests
{
    private FakeNetworkEndpoint _serverNet;
    private FakeNetworkEndpoint _clientNet;
    private ISceneRunner _serverRunner;
    private ISceneRunner _mainSceneRunner;
    private ServerManager _server;
    private LagCompensation _lagComp;

    [BeforeTest]
    public async Task SetUp()
    {
        MonkeNetConfig.Instance = null;
        FakeNetworkBridge.Reset();
        MessageSerializer.RegisterNetworkMessages();
        (_serverNet, _clientNet) = FakeNetworkBridge.CreatePair();

        _mainSceneRunner = ISceneRunner.Load("res://demo/MainScene.tscn", autoFree: true);
        await _mainSceneRunner.AwaitIdleFrame();

        _serverRunner = ISceneRunner.Load("res://addons/monke-net/scenes/ServerManager.tscn", autoFree: true);
        await _serverRunner.AwaitIdleFrame();
        _server = _serverRunner.Scene() as ServerManager;
        _server!.Initialize(_serverNet, port: 7850);
        await _serverRunner.AwaitIdleFrame();

        // Programmatic addition — same pattern as LagCompensationTests.
        _lagComp = new LagCompensation { HistoryTicks = 12 };
        _server.AddChild(_lagComp);
        await _serverRunner.AwaitIdleFrame();
    }

    [AfterTest]
    public void TearDown()
    {
        _serverRunner?.Dispose();
        _mainSceneRunner?.Dispose();
        MonkeNetConfig.Instance = null;
    }

    // LCT-01 (suggestion #17) ───────────────────────────────────────────────────
    // Hit-registration accuracy: a target moves linearly along +X. At tick T the
    // target is at known position P_T. We then move the target far away, and
    // raycast from a known origin toward P_T using rewindTick=T. The raycast
    // must hit the body — proving that LagCompensation moved the body back to
    // P_T for the duration of the query (otherwise the ray would miss because
    // the body is at its current, far-away pose).
    //
    // This is the quantitative guarantee LagCompensation provides: hit
    // registration uses the perceived (past) pose, not the current pose.
    [TestCase]
    public async Task LagComp_RaycastAtPastTickHitsAtPastPose()
    {
        ServerManager.Instance.SpawnEntity<Node3D>(entityType: 1, authority: 0);

        // The spawn process runs OnEntitySpawned hooks that may move the body.
        // Wait for spawn-time logic to finish, THEN set the position we want
        // recorded into history.
        await _serverRunner.AwaitIdleFrame();
        var entityRoot = EntitySpawner.Instance.GetEntityRoot(EntitySpawner.Instance.Entities[0])!;

        // Ball is a sphere at the body's origin, so aim the ray at y=0 (the sphere
        // center) rather than the capsule-center offset the old CharacterPlayer test
        // used.
        Vector3 pastPos = new(5, 0, 0);
        Vector3 rayY = Vector3.Zero;
        entityRoot.GlobalPosition = pastPos;
        await _serverRunner.AwaitIdleFrame();
        await _serverRunner.AwaitIdleFrame();

        int rewindTick = _lagComp.NewestTick;
        AssertThat(rewindTick)
            .OverrideFailureMessage("LagCompensation never recorded the spawned target")
            .IsGreaterEqual(0);

        // Move the target far away so a current-tick raycast aimed at pastPos would miss.
        entityRoot.GlobalPosition = new Vector3(100, 0, 0);
        for (int i = 0; i < 3; i++) await _serverRunner.AwaitIdleFrame();

        // Raycast aimed at pastPos along -Z. With rewind, body is at pastPos
        // for the duration of the query; ray must hit.
        Vector3 origin = pastPos + new Vector3(0, 0, 5) + rayY;
        Vector3 dir = Vector3.Forward;  // (0, 0, -1) — toward target along -Z
        bool hit = _lagComp.RaycastAtTick(rewindTick, origin, dir, length: 10f, out var hitInfo);

        // Body must be restored to (close to) current pose after the query. Ball is a
        // rigid body and drifts slightly under gravity each tick, so we tolerate a few
        // cm of Y drift — the important thing is X/Z weren't reset to the past pose.
        AssertThat(System.MathF.Abs(entityRoot.GlobalPosition.X - 100f))
            .OverrideFailureMessage($"target X was not restored to current pose after rewind: {entityRoot.GlobalPosition}")
            .IsLess(0.1f);
        AssertThat(System.MathF.Abs(entityRoot.GlobalPosition.Z))
            .OverrideFailureMessage($"target Z was not restored to current pose after rewind: {entityRoot.GlobalPosition}")
            .IsLess(0.1f);

        // Hard hit assertion. If the demo player scene's collision_layer doesn't
        // satisfy the default raycast (mask=all-layers in LagCompensation.Raycast),
        // this fails — and the test correctly reports the targeting promise broken.
        AssertThat(hit)
            .OverrideFailureMessage($"raycast at past tick {rewindTick} did not hit; target was at {pastPos} during the rewound window")
            .IsTrue();

        // Compare hit point to the past pose (offset by rayY so it's near the
        // capsule center, where the ray hit) and the current pose.
        float distToPast = (hitInfo.Point - (pastPos + rayY)).Length();
        float distToCurrent = (hitInfo.Point - new Vector3(100, 1, 0)).Length();
        AssertThat(distToPast)
            .OverrideFailureMessage($"hit point {hitInfo.Point} is closer to current pose (100,0,0) than past pose ({pastPos})")
            .IsLess(distToCurrent);
    }

    // LCT-02 (suggestion #18) ───────────────────────────────────────────────────
    // History-window boundary semantics. LagCompensation.FindSnapshot has three
    // distinct cases worth pinning:
    //   (a) exact match in window → no missing-counter increment
    //   (b) within-window but not exact (e.g. an "in-between" tick) → returns
    //       closest-older snapshot, no missing-counter increment
    //   (c) older than oldest → falls through to default(TickSnapshot), missing
    //       counter increments by 1, raycast falls back to current pose
    [TestCase]
    public async Task LagComp_BoundaryHandling()
    {
        ServerManager.Instance.SpawnEntity<Node3D>(entityType: 1, authority: 0);
        for (int i = 0; i < 30; i++) await _serverRunner.AwaitIdleFrame();

        int newest = _lagComp.NewestTick;
        int oldest = _lagComp.OldestTick;
        int historyDepth = _lagComp.HistoryDepth;

        AssertThat(historyDepth).IsGreater(0);
        AssertThat(historyDepth).IsLessEqual(_lagComp.HistoryTicks);

        // Case (a): exact match — newest tick IS in history.
        int missingBefore_exact = _lagComp.MissingTickCount;
        _lagComp.RaycastAtTick(newest, Vector3.Zero, Vector3.Forward, length: 1f, out _);
        AssertThat(_lagComp.MissingTickCount - missingBefore_exact)
            .OverrideFailureMessage($"exact-match tick {newest} bumped missing counter")
            .IsEqual(0);

        // Case (b): in-window but not exact — only meaningful if our buffer has
        // gaps. With HistoryTicks=12 and the loop running each physics tick we
        // expect contiguous ticks, so an "in-between" tick may not exist. Skip
        // this assertion if newest - oldest equals historyDepth - 1 (contiguous).
        if (newest - oldest > historyDepth - 1)
        {
            int gapTick = oldest + 1;
            int missingBefore_gap = _lagComp.MissingTickCount;
            _lagComp.RaycastAtTick(gapTick, Vector3.Zero, Vector3.Forward, length: 1f, out _);
            AssertThat(_lagComp.MissingTickCount - missingBefore_gap)
                .OverrideFailureMessage($"in-window-non-exact tick {gapTick} bumped missing counter")
                .IsEqual(0);
        }

        // Case (c): just before the oldest — falls off the back, increments.
        int missingBefore_under = _lagComp.MissingTickCount;
        _lagComp.RaycastAtTick(oldest - 1, Vector3.Zero, Vector3.Forward, length: 1f, out _);
        AssertThat(_lagComp.MissingTickCount - missingBefore_under)
            .OverrideFailureMessage($"out-of-window tick {oldest - 1} did not bump missing counter")
            .IsEqual(1);

        // Case (c) extreme: very far in the past. Must not throw.
        int missingBefore_extreme = _lagComp.MissingTickCount;
        _lagComp.RaycastAtTick(int.MinValue / 2, Vector3.Zero, Vector3.Forward, length: 1f, out _);
        AssertThat(_lagComp.MissingTickCount - missingBefore_extreme).IsEqual(1);
    }
}
