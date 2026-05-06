using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using GameDemo;
using GdUnit4;
using MonkeNet.Client;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Shared;
using MonkeNet.Tests.Infrastructure;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Integration;

/// <summary>
/// I-01..I-05: Client-side prediction and reconciliation.
///
/// ClientPredictionManager tightly couples to EntitySpawner.Instance.ClientEntities and
/// ClientPredictedEntity.GetPosition/HasMisspredicted/HandleReconciliation/ResimulateTick.
/// These tests verify the state-tracking logic (PredictedState list management) by
/// delivering GameSnapshotMessages directly and inspecting private fields via reflection.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class PredictionTests
{
    private FakeNetworkEndpoint _serverNet;
    private FakeNetworkEndpoint _clientNet;
    private ISceneRunner _clientRunner;
    private ClientManager _client;
    private ClientPredictionManager _predictionManager;

    [BeforeTest]
    public async Task SetUp()
    {
        FakeNetworkBridge.Reset();
        MessageSerializer.RegisterNetworkMessages();
        (_serverNet, _clientNet) = FakeNetworkBridge.CreatePair();

        _clientRunner = ISceneRunner.Load("res://addons/monke-net/scenes/ClientManager.tscn", autoFree: true);
        await _clientRunner.AwaitIdleFrame();
        _client = _clientRunner.Scene() as ClientManager;
        _client!.Initialize(_clientNet, "127.0.0.1", 7100);
        await _clientRunner.AwaitIdleFrame();

        _predictionManager = _client.GetNode<ClientPredictionManager>("PredictionManager");

        // Manually set _networkReady = true on InternalClientComponent so prediction
        // manager processes incoming snapshots (it guards on NetworkReady).
        var networkReadyField = typeof(InternalClientComponent)
            .GetField("_networkReady", BindingFlags.NonPublic | BindingFlags.Instance);
        networkReadyField?.SetValue(_predictionManager, true);
    }

    [AfterTest]
    public void TearDown()
    {
        _clientRunner?.Dispose();
    }

    // I-01 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task Prediction_NoRollback_WhenNoEntitiesRegistered()
    {
        // With no client entities, receiving a snapshot should not crash and
        // should not increment _misspredictionsCount.
        var snap = new GameSnapshotMessage
        {
            Tick = 10,
            States = System.Array.Empty<IEntityStateData>()
        };
        _clientNet.SimulateIncomingPacket(1, MessageSerializer.Serialize(snap));
        await _clientRunner.AwaitIdleFrame();

        int misspredictions = GetMisspredictions();
        AssertThat(misspredictions).IsEqual(0);
    }

    // I-02 (mapped to I-03 in plan) ────────────────────────────────────────────
    [TestCase]
    public async Task Prediction_PredictedStateOlderThanSnapshot_IsDropped()
    {
        // Clear any states added by _PhysicsProcess during BeforeTest's AwaitIdleFrame
        var field = typeof(ClientPredictionManager)
            .GetField("_predictedStates", BindingFlags.NonPublic | BindingFlags.Instance);
        (field?.GetValue(_predictionManager) as System.Collections.IList)?.Clear();

        // Manually register 4 predicted states (ticks 8,9,10,11)
        for (int t = 8; t <= 11; t++)
            AddFakePredictedState(t);

        AssertThat(GetPredictedStateCount()).IsEqual(4);

        // Deliver snapshot for tick 10 — states ≤ 10 should be removed
        var snap = new GameSnapshotMessage
        {
            Tick = 10,
            States = System.Array.Empty<IEntityStateData>()
        };
        // SimulateIncomingPacket is synchronous — snapshot is processed immediately.
        // Don't AwaitIdleFrame here because that would add another state via RegisterPrediction.
        _clientNet.SimulateIncomingPacket(1, MessageSerializer.Serialize(snap));

        // Only tick 11 should remain
        AssertThat(GetPredictedStateCount()).IsLessEqual(1);
    }

    // I-03 (mapped to I-04 in plan) ────────────────────────────────────────────
    [TestCase]
    public async Task Prediction_MissedLocalState_Increments_WhenNoPredictionForTick()
    {
        // Empty _predictedStates list — no prediction for tick 99
        var snap = new GameSnapshotMessage
        {
            Tick = 99,
            States = System.Array.Empty<IEntityStateData>()
        };
        _clientNet.SimulateIncomingPacket(1, MessageSerializer.Serialize(snap));
        await _clientRunner.AwaitIdleFrame();

        int missed = GetMissedLocalState();
        AssertThat(missed).IsGreaterEqual(1);
    }

    // I-04 (mapped to I-05 in plan) ────────────────────────────────────────────
    [TestCase]
    public async Task Prediction_OlderSnapshot_IsIgnored()
    {
        // Deliver tick 20 first
        var snap20 = new GameSnapshotMessage { Tick = 20, States = System.Array.Empty<IEntityStateData>() };
        _clientNet.SimulateIncomingPacket(1, MessageSerializer.Serialize(snap20));
        await _clientRunner.AwaitIdleFrame();

        int missedAfterFirst = GetMissedLocalState();

        // Deliver tick 15 (older) — should be rejected, _lastTickReceived stays 20
        var snap15 = new GameSnapshotMessage { Tick = 15, States = System.Array.Empty<IEntityStateData>() };
        _clientNet.SimulateIncomingPacket(1, MessageSerializer.Serialize(snap15));
        await _clientRunner.AwaitIdleFrame();

        // _missedLocalState should not have been incremented again by the old snapshot
        int missedAfterSecond = GetMissedLocalState();
        AssertThat(missedAfterSecond).IsEqual(missedAfterFirst);
    }

    // I-05: LastTickReceived tracks correctly ──────────────────────────────────
    [TestCase]
    public async Task Prediction_LastTickReceived_UpdatesOnForwardSnapshots()
    {
        var snap = new GameSnapshotMessage { Tick = 50, States = System.Array.Empty<IEntityStateData>() };
        _clientNet.SimulateIncomingPacket(1, MessageSerializer.Serialize(snap));
        await _clientRunner.AwaitIdleFrame();

        int lastTick = GetLastTickReceived();
        AssertThat(lastTick).IsEqual(50);
    }

    // I-06: history cap drops oldest entries ──────────────────────────────────
    [TestCase]
    public void Prediction_HistoryCappedAtMax_DropsOldestEntries()
    {
        ClearPredictedStates();
        _predictionManager.MaxRollbackTicks = 10;

        for (int t = 1; t <= 20; t++)
            _predictionManager.RegisterPrediction(t, default(CharacterInputMessage));

        AssertThat(GetPredictedStateCount()).IsEqual(10);
        AssertThat(GetOldestTick()).IsEqual(11);
        AssertThat(GetNewestTick()).IsEqual(20);
    }

    // I-07: snapshot for a trimmed-away tick increments missed-state counter ──
    [TestCase]
    public void Prediction_RollbackBeyondCapTreatedAsMissedState()
    {
        ClearPredictedStates();
        _predictionManager.MaxRollbackTicks = 5;
        int initialMissed = GetMissedLocalState();

        // Register 10 — first 5 (ticks 1..5) get trimmed
        for (int t = 1; t <= 10; t++)
            _predictionManager.RegisterPrediction(t, default(CharacterInputMessage));
        AssertThat(GetPredictedStateCount()).IsEqual(5);

        // Snapshot for a trimmed tick: predicted state is gone, count it as missed.
        var snap = new GameSnapshotMessage { Tick = 2, States = System.Array.Empty<IEntityStateData>() };
        _clientNet.SimulateIncomingPacket(1, MessageSerializer.Serialize(snap));

        AssertThat(GetMissedLocalState()).IsGreater(initialMissed);
    }

    // ── reflection helpers ─────────────────────────────────────────────────────

    private int GetPredictedStateCount()
    {
        var field = typeof(ClientPredictionManager)
            .GetField("_predictedStates", BindingFlags.NonPublic | BindingFlags.Instance);
        var list = field?.GetValue(_predictionManager) as System.Collections.IList;
        return list?.Count ?? 0;
    }

    private int GetMisspredictions()
    {
        var field = typeof(ClientPredictionManager)
            .GetField("_misspredictionsCount", BindingFlags.NonPublic | BindingFlags.Instance);
        return (int)(field?.GetValue(_predictionManager) ?? 0);
    }

    private int GetMissedLocalState()
    {
        var field = typeof(ClientPredictionManager)
            .GetField("_missedLocalState", BindingFlags.NonPublic | BindingFlags.Instance);
        return (int)(field?.GetValue(_predictionManager) ?? 0);
    }

    private int GetLastTickReceived()
    {
        var field = typeof(ClientPredictionManager)
            .GetField("_lastTickReceived", BindingFlags.NonPublic | BindingFlags.Instance);
        return (int)(field?.GetValue(_predictionManager) ?? 0);
    }

    private void ClearPredictedStates()
    {
        var field = typeof(ClientPredictionManager)
            .GetField("_predictedStates", BindingFlags.NonPublic | BindingFlags.Instance);
        (field?.GetValue(_predictionManager) as System.Collections.IList)?.Clear();
    }

    private int GetOldestTick()
    {
        var field = typeof(ClientPredictionManager)
            .GetField("_predictedStates", BindingFlags.NonPublic | BindingFlags.Instance);
        var list = field?.GetValue(_predictionManager) as System.Collections.IList;
        if (list == null || list.Count == 0) return -1;
        var stateType = typeof(ClientPredictionManager).GetNestedType("PredictedState", BindingFlags.NonPublic);
        return (int)stateType!.GetField("Tick")!.GetValue(list[0]);
    }

    private int GetNewestTick()
    {
        var field = typeof(ClientPredictionManager)
            .GetField("_predictedStates", BindingFlags.NonPublic | BindingFlags.Instance);
        var list = field?.GetValue(_predictionManager) as System.Collections.IList;
        if (list == null || list.Count == 0) return -1;
        var stateType = typeof(ClientPredictionManager).GetNestedType("PredictedState", BindingFlags.NonPublic);
        return (int)stateType!.GetField("Tick")!.GetValue(list[list.Count - 1]);
    }

    private void AddFakePredictedState(int tick)
    {
        // PredictedState is a private nested class; add an entry via the list's Add(object) method.
        var field = typeof(ClientPredictionManager)
            .GetField("_predictedStates", BindingFlags.NonPublic | BindingFlags.Instance);
        var list = field?.GetValue(_predictionManager);

        // Create instance of the private PredictedState type
        var stateType = typeof(ClientPredictionManager)
            .GetNestedType("PredictedState", BindingFlags.NonPublic);
        if (stateType == null || list == null) return;

        var state = System.Activator.CreateInstance(stateType);
        stateType.GetField("Tick")?.SetValue(state, tick);
        stateType.GetField("Input")?.SetValue(state, null);

        // Create empty dictionary for Entities field
        var entitiesType = typeof(Dictionary<ClientPredictedEntity, Godot.Vector3>);
        stateType.GetField("Entities")?.SetValue(state, System.Activator.CreateInstance(entitiesType));

        (list as System.Collections.IList)?.Add(state);
    }
}
