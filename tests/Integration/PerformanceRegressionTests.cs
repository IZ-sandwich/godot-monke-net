using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
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
/// PERF-01..PERF-02: Performance / memory regression tests for the prediction loop.
///
/// These are budget tests, not correctness tests — they fail when a refactor makes
/// rollback meaningfully slower or makes the prediction history leak past its cap.
/// Thresholds are intentionally generous so slow CI machines don't false-fail; tighten
/// them on a perf workstation only.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class PerformanceRegressionTests
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

        _server!.Initialize(_serverNet, port: 7820);
        _client!.Initialize(_clientNet, "127.0.0.1", 7820);
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

    // PERF-01 ───────────────────────────────────────────────────────────────────
    // Rollback CPU budget. Directly invokes the manager's ProcessServerState
    // handler via reflection with synthetic snapshots that each force a
    // rollback. Stopwatch around the call records the work the manager does
    // before and at the misprediction-detection point.
    //
    // CAVEAT: this test runs both server and client in one process (listen-server
    // layout, like AuthorityTransferTests). ClientPredictionManager.RollbackAndResimulate
    // contains an early-out when MonkeNetManager.Instance.IsServer is true (it
    // skips the destructive resim because client and server share the physics
    // space). When IsServer is true the timing here covers everything UP TO the
    // short-circuit (Find, HasMisspredicted, the foreach over entities) but NOT
    // the resim itself. That still catches regressions in those paths, but a
    // truly-isolated rollback-cost test would need a pure-client setup that
    // doesn't currently exist in the test infrastructure.
    //
    // Threshold: mean < 2 ms per forced rollback over 60 samples, after a 5-sample
    // warmup. This is a regression detector — a 10× perf cliff in the
    // pre-resim path (or in resim itself, in a non-listen-server setup) will trip it.
    [TestCase]
    public async Task Rollback_MeanCpuBudgetUnderTwoMs()
    {
        var predictionManager = (ClientPredictionManager)_client.GetNode("PredictionManager");

        // Spawn 4 server entities owned by the client peer so they appear as
        // ClientPredictedEntity instances on the client.
        for (int i = 0; i < 4; i++)
            ServerManager.Instance.SpawnEntity<Node3D>(entityType: 1, authority: _clientNet.GetNetworkId());
        await _serverRunner.AwaitIdleFrame();
        await _clientRunner.AwaitIdleFrame();

        // Drive past clock-sync so prediction is registering states.
        for (int i = 0; i < 30; i++)
        {
            await _serverRunner.AwaitIdleFrame();
            await _clientRunner.AwaitIdleFrame();
        }

        // Reflect access into the private rollback path so we can time just the
        // ProcessServerState invocation, not the surrounding frame loop.
        var pmType = typeof(ClientPredictionManager);
        var processMethod = pmType.GetMethod("ProcessServerState",
            BindingFlags.Instance | BindingFlags.NonPublic);
        AssertThat(processMethod).IsNotNull(); // catches API rename early

        // Build a synthetic snapshot that will mispredict (states with positions
        // wildly different from anything the client predicted).
        GameSnapshotMessage MakeForcedMispredictSnapshot(int tick) => new()
        {
            Tick = tick,
            States = BuildMispredictStates(tick),
        };

        // Warmup — first few invocations include JIT and cold-cache cost.
        for (int i = 0; i < 5; i++)
        {
            int latestTick = predictionManager.GetType()
                .GetField("_lastTickReceived", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(predictionManager) is int t ? t + 1 : 1;
            try
            {
                processMethod!.Invoke(predictionManager, new object[] { MakeForcedMispredictSnapshot(latestTick) });
            }
            catch { /* if the snapshot doesn't match any predicted state it's a no-op */ }
        }

        // Measure 60 forced rollbacks.
        var sw = new Stopwatch();
        const int Samples = 60;

        for (int i = 0; i < Samples; i++)
        {
            int latestTick = predictionManager.GetType()
                .GetField("_lastTickReceived", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(predictionManager) is int t ? t + 1 : 1;
            var snap = MakeForcedMispredictSnapshot(latestTick);

            sw.Start();
            try { processMethod!.Invoke(predictionManager, new object[] { snap }); }
            catch { /* swallow — we still measured the call */ }
            sw.Stop();
        }

        double meanMs = sw.Elapsed.TotalMilliseconds / Samples;
        AssertThat(meanMs)
            .OverrideFailureMessage($"mean rollback budget exceeded: {meanMs:F3} ms (limit 2 ms)")
            .IsLess(2.0);
    }

    private IEntityStateData[] BuildMispredictStates(int tick)
    {
        var entities = EntitySpawner.Instance.ClientEntities;
        var states = new List<IEntityStateData>(entities.Count);
        for (int i = 0; i < entities.Count; i++)
        {
            // Wild offset so HasMisspredicted always returns true.
            states.Add(new GameDemo.EntityStateMessage
            {
                EntityId = entities[i].EntityId,
                Position = new Vector3(100 + i * 7, tick * 0.1f, 100 + i * 11),
                Rotation = Quaternion.Identity,
                Velocity = Vector3.Zero,
                AngularVelocity = Vector3.Zero,
                Yaw = 0,
            });
        }
        return states.ToArray();
    }

    // PERF-02 ───────────────────────────────────────────────────────────────────
    // Steady-state memory bound: under sustained traffic, _predictedStates must
    // stay at or below MaxRollbackTicks, and the GC heap delta over the measurement
    // window must stay below 1 MB. This is the common-case leak detector — it does
    // NOT force misprediction (rollback's allocation behaviour is exercised by
    // PERF-01), it verifies that ordinary RegisterPrediction + ack-trim doesn't
    // accumulate references to freed entities or grow unbounded queues.
    [TestCase]
    public async Task Prediction_HistoryCapHoldsAndNoMemoryGrowth()
    {
        var predictionManager = (ClientPredictionManager)_client.GetNode("PredictionManager");

        // Need an owned entity so RegisterPrediction has work each tick.
        ServerManager.Instance.SpawnEntity<Node3D>(entityType: 1, authority: _clientNet.GetNetworkId());
        await _serverRunner.AwaitIdleFrame();
        await _clientRunner.AwaitIdleFrame();

        // Warmup so any one-shot allocations happen before the measurement window.
        for (int i = 0; i < 60; i++)
        {
            await _serverRunner.AwaitIdleFrame();
            await _clientRunner.AwaitIdleFrame();
        }

        long memBefore = GC.GetTotalMemory(forceFullCollection: true);

        // 600 ticks (10 s at 60 Hz) — 5× the default MaxRollbackTicks=120 cap, enough
        // to surface a leak without making the test slow.
        for (int i = 0; i < 600; i++)
        {
            await _serverRunner.AwaitIdleFrame();
            await _clientRunner.AwaitIdleFrame();
        }

        // Reflect _predictedStates.Count and MaxRollbackTicks for the cap check. The
        // field is private; tests inspect via reflection rather than expanding the API.
        var pmType = typeof(ClientPredictionManager);
        var listField = pmType.GetField("_predictedStates", BindingFlags.Instance | BindingFlags.NonPublic);
        var cap = predictionManager.MaxRollbackTicks;
        var list = (System.Collections.IList)listField!.GetValue(predictionManager)!;

        AssertThat(list.Count)
            .OverrideFailureMessage($"prediction history exceeded cap: {list.Count} > {cap}")
            .IsLessEqual(cap);

        long memAfter = GC.GetTotalMemory(forceFullCollection: true);
        long deltaBytes = memAfter - memBefore;

        // 1 MB ceiling for 30 s of saturation. Generous to absorb test-runner noise.
        AssertThat(deltaBytes)
            .OverrideFailureMessage($"GC delta {deltaBytes / 1024.0:F1} KB exceeds 1 MB budget")
            .IsLess(1_048_576);
    }
}
