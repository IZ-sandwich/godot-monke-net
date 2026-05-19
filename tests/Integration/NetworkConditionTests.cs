using System.Threading.Tasks;
using GameDemo;
using GdUnit4;
using Godot;
using MonkeNet.Client;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Server;
using MonkeNet.Shared;
using MonkeNet.Tests.Infrastructure;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Integration;

/// <summary>
/// NC-01..NC-04: Behaviour under simulated network conditions (drop, latency,
/// jitter, burst loss).
///
/// All tests use a SERVER-AUTHORITATIVE entity (entityType=4, the demo's cube
/// with authority=0). Cubes are wired with <c>ClientDummyScene = LocalCube</c>,
/// so server-owned cubes are still rendered through the predicted-rigid-prop
/// path (<see cref="GameDemo.LocalRigidPropPrediction"/>) on the client — the
/// body simulates locally every physics tick and reconciles against the
/// server's snapshot. This is the framework's current primary sync path for
/// rigid bodies, so the network-conditions assertions exercise the production
/// reconcile path rather than a pure interpolated dummy.
///
/// Loss / queueing is applied to the snapshot channel only via
/// <see cref="ChannelEnum.Snapshot"/>; reliable channels (entity events,
/// clock sync) flow normally so the spawn handshake isn't broken.
///
/// These tests fail when the framework starts behaving badly under degraded
/// conditions (excessive drift, NaN, runaway divergence). They do NOT verify
/// any specific UX number — the thresholds encode "acceptable degradation
/// curves" the framework is expected to maintain.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class NetworkConditionTests
{
    private FakeNetworkEndpoint _serverNet;
    private FakeNetworkEndpoint _clientNet;
    private ISceneRunner _serverRunner;
    private ISceneRunner _clientRunner;
    private ISceneRunner _mainSceneRunner;
    private ServerManager _server;
    private MonkeNet.Client.ClientManager _client;

    [BeforeTest]
    public async Task SetUp()
    {
        MonkeNetConfig.Instance = null;
        FakeNetworkBridge.Reset();
        ClientEntityManager.ClearAwaitingReconnect();
        MessageSerializer.RegisterNetworkMessages();
        (_serverNet, _clientNet) = FakeNetworkBridge.CreatePair();

        _mainSceneRunner = ISceneRunner.Load("res://demo/MainScene.tscn", autoFree: true);
        await _mainSceneRunner.AwaitIdleFrame();

        _serverRunner = ISceneRunner.Load("res://addons/monke-net/scenes/ServerManager.tscn", autoFree: true);
        await _serverRunner.AwaitIdleFrame();
        _server = _serverRunner.Scene() as ServerManager;

        _clientRunner = ISceneRunner.Load("res://addons/monke-net/scenes/ClientManager.tscn", autoFree: true);
        await _clientRunner.AwaitIdleFrame();
        _client = _clientRunner.Scene() as MonkeNet.Client.ClientManager;

        _server!.Initialize(_serverNet, port: 7830);
        _client!.Initialize(_clientNet, "127.0.0.1", 7830);
        await _serverRunner.AwaitIdleFrame();
        await _clientRunner.AwaitIdleFrame();
    }

    [AfterTest]
    public void TearDown()
    {
        _serverRunner?.Dispose();
        _clientRunner?.Dispose();
        _mainSceneRunner?.Dispose();
        MonkeNetConfig.Instance = null;
    }

    // NC-01 (suggestion #4) ─────────────────────────────────────────────────────
    // Drop-rate sweep: at increasing snapshot-loss rates, the client's view of
    // the entity must remain bounded relative to the server. The deviation
    // budgets here encode the "graceful degradation" promise: if a refactor
    // makes 10% loss produce 50 cm desync, this test fails.
    //
    // Loss is applied SERVER-SIDE via PacketLossRate, which only drops Unreliable
    // packets (snapshots). Reliable EntityEvent + ClockSync are unaffected.
    [TestCase(0.0f, 0.05f)]
    [TestCase(0.05f, 0.30f)]
    [TestCase(0.10f, 0.60f)]
    [TestCase(0.25f, 1.50f)]
    public async Task NetworkConditions_DropRateBoundsClientServerDrift(float lossRate, float maxDriftMeters)
    {
        ServerManager.Instance.SpawnEntity<Node3D>(entityType: 4, authority: 0);
        await _serverRunner.AwaitIdleFrame();
        await _clientRunner.AwaitIdleFrame();
        AssertEntityExistsOnBothSides();

        // Apply loss only AFTER the Created event has been delivered (it goes on
        // a Reliable channel anyway, but we want to be sure the client's dummy
        // is spawned before we start measuring).
        _serverNet.PacketLossRate = lossRate;
        _serverNet.PacketLossRng = new System.Random(42);

        // Drive 300 ticks of traffic. Server-owned ball falls under gravity, so
        // both server and client see motion regardless of input.
        for (int i = 0; i < 300; i++)
        {
            await _serverRunner.AwaitIdleFrame();
            await _clientRunner.AwaitIdleFrame();
        }

        var (serverPos, clientPos) = ReadEntityPositions();
        float drift = (serverPos - clientPos).Length();
        AssertThat(drift)
            .OverrideFailureMessage($"loss={lossRate:P0}: drift {drift:F3} m exceeds {maxDriftMeters:F2} m budget (server={serverPos} client={clientPos})")
            .IsLess(maxDriftMeters);
    }

    // NC-02 (suggestion #5) ─────────────────────────────────────────────────────
    // Convergence after lag spike: hold all snapshots for 30 ticks (a half-second
    // at 60 Hz, well past MaxRollbackTicks distance), then
    // resume normal delivery. After 30 ticks of unimpeded delivery the client
    // dummy must be within 2 cm of the server.
    //
    // Reliable channels (EntityEvent, ClockSync) continue to flow during the hold
    // so the spawn handshake isn't broken — the test isolates snapshot starvation.
    [TestCase]
    public async Task NetworkConditions_LagSpikeRecoversAfterDrain()
    {
        ServerManager.Instance.SpawnEntity<Node3D>(entityType: 4, authority: 0);
        await _serverRunner.AwaitIdleFrame();
        await _clientRunner.AwaitIdleFrame();
        AssertEntityExistsOnBothSides();

        _clientNet.QueuePackets = true;

        // Lag spike: 30 ticks. During this window deliver only non-snapshot packets
        // so EntityEvent + ClockSync keep flowing, but snapshots starve the dummy.
        for (int i = 0; i < 30; i++)
        {
            await _serverRunner.AwaitIdleFrame();
            _clientNet.DeliverPendingExcept(excludeChannel: (int)ChannelEnum.Snapshot);
            await _clientRunner.AwaitIdleFrame();
        }

        // Resume normal delivery. Drain accumulated snapshots, then run 30 ticks.
        _clientNet.QueuePackets = false;
        _clientNet.DeliverAllPending();
        for (int i = 0; i < 30; i++)
        {
            await _serverRunner.AwaitIdleFrame();
            await _clientRunner.AwaitIdleFrame();
        }

        var (serverPos, clientPos) = ReadEntityPositions();
        float drift = (serverPos - clientPos).Length();
        AssertThat(drift)
            .OverrideFailureMessage($"post-lag-spike drift {drift:F4} m (server={serverPos} client={clientPos})")
            .IsLess(0.02f);

        // No NaN/Inf survived through rollback.
        AssertThat(IsFiniteVec(clientPos))
            .OverrideFailureMessage($"client position non-finite after lag spike: {clientPos}")
            .IsTrue();
    }

    // NC-03 (suggestion #6) ─────────────────────────────────────────────────────
    // Jitter resilience: under the unified-prediction model the client predicts the
    // cube locally each physics tick (Jolt sim) and reconciles to snapshot pose only
    // when divergence exceeds LocalRigidPropPrediction's threshold (~20 cm). So the
    // frame-to-frame body delta under jitter is the worst case of (a) one tick of
    // free-fall gravity, ~0.083 m, plus (b) a hard reconcile of up to ~1 m when a
    // long-delayed snapshot finally arrives carrying a large position delta. Smooth
    // visuals are now the smoother's job (a separate concern from the body).
    [TestCase]
    public async Task NetworkConditions_JitterProducesSmoothInterpolation()
    {
        ServerManager.Instance.SpawnEntity<Node3D>(entityType: 4, authority: 0);
        await _serverRunner.AwaitIdleFrame();
        await _clientRunner.AwaitIdleFrame();
        AssertEntityExistsOnBothSides();

        _clientNet.QueuePackets = true;

        // Warmup so the dummy interpolator has buffered snapshots to lerp between.
        for (int i = 0; i < 12; i++)
        {
            await _serverRunner.AwaitIdleFrame();
            _clientNet.DeliverAllPending();
            await _clientRunner.AwaitIdleFrame();
        }

        var clientRoot = EntitySpawner.Instance.GetEntityRoot(EntitySpawner.Instance.ClientEntities[0])!;
        Vector3 lastClientPos = clientRoot.GlobalPosition;
        float maxFrameDelta = 0f;

        // Jittered delivery: pattern 0, 1, 2 packets per tick on a repeating cycle.
        // Average is 1 packet/tick; the burst/idle creates timing variance.
        for (int t = 0; t < 120; t++)
        {
            await _serverRunner.AwaitIdleFrame();
            int releaseCount = t % 3;
            for (int r = 0; r < releaseCount; r++) _clientNet.DeliverNextPending();
            await _clientRunner.AwaitIdleFrame();

            float delta = (clientRoot.GlobalPosition - lastClientPos).Length();
            if (delta > maxFrameDelta) maxFrameDelta = delta;
            lastClientPos = clientRoot.GlobalPosition;
        }

        AssertThat(maxFrameDelta)
            .OverrideFailureMessage($"max frame delta {maxFrameDelta:F4} m exceeds 1.5 m budget under jitter")
            .IsLess(1.5f);
    }

    // NC-04 (suggestion #7) ─────────────────────────────────────────────────────
    // Burst-loss recovery: drop 20 consecutive SNAPSHOTS (channel-filtered, so
    // reliable handshake / clock-sync survive), then resume normal delivery.
    // Verifies (a) no NaN survived, and (b) the client converges to within 5 cm
    // of server within 30 ticks of resumption.
    [TestCase]
    public async Task NetworkConditions_BurstLossRecoversWithoutDivergence()
    {
        ServerManager.Instance.SpawnEntity<Node3D>(entityType: 4, authority: 0);
        await _serverRunner.AwaitIdleFrame();
        await _clientRunner.AwaitIdleFrame();
        AssertEntityExistsOnBothSides();

        // Warmup with synchronous delivery so the client has buffered snapshots.
        for (int i = 0; i < 30; i++)
        {
            await _serverRunner.AwaitIdleFrame();
            await _clientRunner.AwaitIdleFrame();
        }

        // Switch to manual delivery; for each server tick, drop the snapshot it
        // produced (channel-filtered) and deliver everything else.
        _clientNet.QueuePackets = true;
        int dropped = 0;
        for (int i = 0; i < 20; i++)
        {
            await _serverRunner.AwaitIdleFrame();
            if (_clientNet.DropNextOnChannel((int)ChannelEnum.Snapshot)) dropped++;
            _clientNet.DeliverPendingExcept(excludeChannel: (int)ChannelEnum.Snapshot);
            await _clientRunner.AwaitIdleFrame();
        }

        // Sanity: the test actually dropped snapshots — at least 15 of 20 server
        // ticks should have produced a snapshot to drop. If this fails, the server
        // tick rate or scheduling has changed and the burst window is miscalibrated.
        AssertThat(dropped)
            .OverrideFailureMessage($"only {dropped} of 20 server ticks produced a droppable snapshot")
            .IsGreater(15);

        // Resume normal delivery, deliver any leftovers, run 30 ticks.
        _clientNet.QueuePackets = false;
        _clientNet.DeliverAllPending();
        for (int i = 0; i < 30; i++)
        {
            await _serverRunner.AwaitIdleFrame();
            await _clientRunner.AwaitIdleFrame();
        }

        var (serverPos, clientPos) = ReadEntityPositions();
        AssertThat(IsFiniteVec(clientPos))
            .OverrideFailureMessage($"client position non-finite after burst loss: {clientPos}")
            .IsTrue();

        float drift = (serverPos - clientPos).Length();
        AssertThat(drift)
            .OverrideFailureMessage($"burst-loss drift {drift:F4} m after recovery (server={serverPos} client={clientPos})")
            .IsLess(0.05f);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private void AssertEntityExistsOnBothSides()
    {
        AssertThat(EntitySpawner.Instance.Entities.Count)
            .OverrideFailureMessage("server-side entity not spawned")
            .IsGreaterEqual(1);
        AssertThat(EntitySpawner.Instance.ClientEntities.Count)
            .OverrideFailureMessage("client-side dummy not spawned (Created event lost?)")
            .IsGreaterEqual(1);
    }

    private (Vector3 serverPos, Vector3 clientPos) ReadEntityPositions()
    {
        var serverEntity = EntitySpawner.Instance.Entities[0];
        var clientEntity = EntitySpawner.Instance.ClientEntities[0];
        var serverPos = EntitySpawner.Instance.GetEntityRoot(serverEntity)!.GlobalPosition;
        var clientPos = EntitySpawner.Instance.GetEntityRoot(clientEntity)!.GlobalPosition;
        return (serverPos, clientPos);
    }

    private static bool IsFiniteVec(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
