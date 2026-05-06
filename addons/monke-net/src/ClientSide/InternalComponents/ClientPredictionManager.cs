using Godot;
using ImGuiNET;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Shared;
using System.Collections.Generic;
using System.Linq;

namespace MonkeNet.Client;

/// <summary>
/// Stores predicted game states for entities, upon receiving an snapshot, will check for deviation and perform rollback and re-simulation if needed.
/// </summary>
[GlobalClass]
public partial class ClientPredictionManager : InternalClientComponent
{
    // Hard cap on prediction history depth. Default 120 = 2 seconds at 60Hz.
    // Under sustained network degradation _predictedStates would otherwise grow without
    // bound and rollback would resimulate every entry — which is what overflows the
    // Jolt job ring buffer. When the cap is hit, oldest entries are dropped; a snapshot
    // arriving for a dropped tick is treated as a missed local state (counted, no rollback).
    [Export] public int MaxRollbackTicks { get; set; } = 120;

    private readonly List<PredictedState> _predictedStates = [];
    private int _lastTickReceived = 0;
    private int _misspredictionsCount = 0;
    private int _missedLocalState = 0;
    private int _trimmedTotal = 0;
    private ulong _lastTrimWarningMsec;
    private EntitySpawner _subscribedSpawner;
    // Per-second cap on misprediction diagnostic logs to avoid log spam at 60Hz when
    // mispredictions are rapid-fire. State counts are kept in _misspredictionsCount.
    private ulong _mispredictionWindowStartMsec;
    private int _mispredictionLogsThisWindow;
    private const int MispredictionLogsPerSecond = 5;

    public override void _Ready()
    {
        base._Ready();
        // Subscribe to EntityDestroyed so we can drop stale entries from _predictedStates.
        // Without this, an authority transfer (which destroys+recreates the local entity on
        // the previous owner) leaves the prediction loop iterating freed Godot objects.
        _subscribedSpawner = EntitySpawner.Instance;
        if (_subscribedSpawner != null)
            _subscribedSpawner.EntityDestroyed += OnEntityDestroyed;
    }

    public override void _ExitTree()
    {
        if (_subscribedSpawner != null && IsInstanceValid(_subscribedSpawner))
            _subscribedSpawner.EntityDestroyed -= OnEntityDestroyed;
        _subscribedSpawner = null;
    }

    private void OnEntityDestroyed(int entityId)
    {
        // Remove every PredictedState entry that references this entity, so subsequent
        // rollback iterations don't touch the now-freed Godot object.
        foreach (var state in _predictedStates)
        {
            var key = state.Entities.Keys.FirstOrDefault(k => k != null && k.EntityId == entityId);
            if (key != null) state.Entities.Remove(key);
        }
    }

    protected override void OnCommandReceived(IPackableMessage command)
    {
        if (!NetworkReady)
            return;

        if (command is GameSnapshotMessage snapshot)
        {
            if (snapshot.Tick > _lastTickReceived)
            {
                _lastTickReceived = snapshot.Tick;
                ProcessServerState(snapshot);
            }
        }
    }

    public void Predict(int tick, IPackableElement input)
    {
        if (input == null) return;
        EntitySpawner.Instance.ClientEntities.ForEach(entity =>
        {
            var clientPredictedEntity = entity.GetComponent<ClientPredictedEntity>();
            clientPredictedEntity?.OnProcessTick(tick, input);
        });
    }

    public void RegisterPrediction(int tick, IPackableElement input)
    {
        if (input == null) return;
        var predictedState = new PredictedState
        {
            Tick = tick,
            Input = input,
            Entities = []
        };

        _predictedStates.Add(predictedState);

        if (_predictedStates.Count > MaxRollbackTicks)
        {
            int toRemove = _predictedStates.Count - MaxRollbackTicks;
            _predictedStates.RemoveRange(0, toRemove);
            _trimmedTotal += toRemove;
            LogTrimWarningThrottled();
        }

        //TODO: use array of ClientPredictedEntity that updates each time a new entity is spawned/despawned
        //TODO: store entity state inside entity itself instead of having everything here on PredictionManager
        EntitySpawner.Instance.ClientEntities.ForEach(entity =>
        {
            var clientPredictedEntity = entity.GetComponent<ClientPredictedEntity>();
            if (clientPredictedEntity != null)
            {
                predictedState.Entities.Add(clientPredictedEntity, clientPredictedEntity.GetPosition());
            }
        });
    }

    private void ProcessServerState(GameSnapshotMessage receivedSnapshot)
    {
        var predictedStateData = _predictedStates.Find(prediction => prediction.Tick == receivedSnapshot.Tick);
        _predictedStates.RemoveAll(predictedState => predictedState.Tick <= receivedSnapshot.Tick);

        if (predictedStateData == default(PredictedState) || predictedStateData.Tick != receivedSnapshot.Tick)
        {
            _missedLocalState++;
            return;
        }

        // Iterate all entities saved for the tick
        foreach (ClientPredictedEntity predictableEntity in predictedStateData.Entities.Keys)
        {
            // Get predicted and authoritative state for the entity
            var predictedState = predictedStateData.Entities[predictableEntity];
            var authoritativeState = FindStateForEntityId(predictableEntity.EntityId, receivedSnapshot.States);

            if (predictableEntity.HasMisspredicted(receivedSnapshot.Tick, authoritativeState, predictedState))
            {
                _misspredictionsCount++;
                LogMispredictionThrottled(predictableEntity, predictedState, receivedSnapshot.Tick);
                RollbackAndResimulate(receivedSnapshot.States, predictedStateData);
                return;
            }

            // Below the hard reconcile threshold — apply gentle silent correction. Mostly
            // a no-op (default impl), but entities like the local vehicle override this to
            // pull body state toward authoritative each snapshot, preventing the small
            // collision-response drifts from accumulating until they exceed the threshold.
            predictableEntity.ApplySoftCorrection(authoritativeState, predictedState);
        }
    }

    private void RollbackAndResimulate(IEntityStateData[] authoritativeStates, PredictedState predictedStateData)
    {
        // Snapshot non-networked rigidbodies so the resim's repeated SpaceStep calls
        // don't drift them. Restored after the loop.
        OfflineRigidbody3D.SnapshotAll();

        // Set all entities to authoritative state
        foreach (ClientPredictedEntity predictableEntity in predictedStateData.Entities.Keys)
        {
            var authoritativeState = FindStateForEntityId(predictableEntity.EntityId, authoritativeStates);
            predictableEntity.HandleReconciliation(authoritativeState);
        }

        // Advance simulation forward for all remaining inputs
        for (int i = 0; i < _predictedStates.Count; i++)
        {
            var remainingInput = _predictedStates[i];
            foreach (ClientPredictedEntity predictableEntity in remainingInput.Entities.Keys)
            {
                predictableEntity.ResimulateTick(remainingInput.Input);
            }

            PhysicsServer3D.SpaceStep(MonkeNetManager.Instance.PhysicsSpace, PhysicsUtils.DeltaTime);
            PhysicsServer3D.SpaceFlushQueries(MonkeNetManager.Instance.PhysicsSpace);

            foreach (ClientPredictedEntity predictableEntity in remainingInput.Entities.Keys)
            {
                remainingInput.Entities[predictableEntity] = predictableEntity.GetPosition();
            }
        }

        OfflineRigidbody3D.RestoreAll();
    }

    private static IEntityStateData FindStateForEntityId(int entityId, IEntityStateData[] authStates)
    {
        foreach (IEntityStateData state in authStates)
        {
            if (state.EntityId == entityId)
            {
                return state;
            }
        }

        return null;
    }

    public void DisplayDebugInformation()
    {
        if (ImGui.CollapsingHeader("Prediction Manager"))
        {
            ImGui.Text($"Misspredictions: {_misspredictionsCount}");
            ImGui.Text($"Missed Local States: {_missedLocalState}");
            ImGui.Text($"Prediction History: {_predictedStates.Count} / {MaxRollbackTicks}");
            ImGui.Text($"Trimmed Total: {_trimmedTotal}");
        }
    }

    private void LogMispredictionThrottled(ClientPredictedEntity entity, Vector3 predictedPos, int tick)
    {
        ulong now = Time.GetTicksMsec();
        if (now - _mispredictionWindowStartMsec >= 1000)
        {
            _mispredictionWindowStartMsec = now;
            _mispredictionLogsThisWindow = 0;
        }
        if (_mispredictionLogsThisWindow >= MispredictionLogsPerSecond) return;
        _mispredictionLogsThisWindow++;

        // ClientPredictedEntity inherits from NetworkBehaviour so EntityType is directly
        // accessible. The log reads like:
        //   "Misprediction: entity 5 type 2 tick 1234 predicted (1.2, 0.5, 3.1) now (1.8, 0.5, 3.1)"
        // — enough to disambiguate which entity class is drifting (player=0, ball=1, vehicle=2).
        Vector3 currentPos = entity.GetPosition();
        MonkeLogger.Info($"Misprediction: entity {entity.EntityId} type {entity.EntityType} tick {tick} predicted {predictedPos} now {currentPos}");
    }

    private void LogTrimWarningThrottled()
    {
        ulong now = Time.GetTicksMsec();
        if (now - _lastTrimWarningMsec < 1000) return;
        _lastTrimWarningMsec = now;
        MonkeLogger.Warn($"ClientPredictionManager: prediction history hit cap of {MaxRollbackTicks} ticks; oldest entries dropped (degraded network conditions or no snapshots received)");
    }

    private class PredictedState
    {
        public int Tick;                                            // Tick at which the input was taken
        public IPackableElement Input;                              // Input message sent to the server
        public Dictionary<ClientPredictedEntity, Vector3> Entities;
    }
}
