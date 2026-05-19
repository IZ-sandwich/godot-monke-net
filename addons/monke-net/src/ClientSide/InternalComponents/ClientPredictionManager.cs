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
    // Per-entity last input as observed by the server. Populated from
    // GameSnapshotMessage.Inputs on every snapshot. Used by Predict() to drive
    // forward-prediction of entities this client doesn't own — without it, an
    // observer can only coast the entity at its last-known velocity, drifting
    // from the server's input-driven trajectory every tick and forcing a
    // reconcile on every snapshot. Lookup falls back to null for entities the
    // server never reports an input for (server-authoritative passive props).
    private readonly Dictionary<int, IPackableElement> _lastInputByEntityId = [];
    // Entity IDs whose first authoritative snapshot has already been folded into
    // the prediction state. The first snapshot for a freshly-spawned entity does
    // NOT count as a misprediction even if it triggers a rollback: the client's
    // body has been integrated for at most one or two physics ticks since spawn,
    // while the server's body has been integrated since whatever earlier server
    // tick the entity was actually spawned at — so the comparison is guaranteed
    // to fail for reasons that have nothing to do with prediction quality. We
    // route that first snapshot through the existing rollback+resim path (which
    // catches the body up to the current client tick) but suppress the count.
    private readonly HashSet<int> _initializedEntityIds = [];
    private int _lastTickReceived = 0;
    private int _misspredictionsCount = 0;
    /// <summary>
    /// Number of mispredictions detected this session. Surfaced for harness-driven tests
    /// (see <c>MultiClientHarness.MispredictCount</c>) so a multi-process scenario can
    /// assert misprediction budgets after driving a known input pattern.
    /// </summary>
    public int MispredictionsCount => _misspredictionsCount;
    private int _missedLocalState = 0;
    private int _trimmedTotal = 0;
    private ulong _lastTrimWarningMsec;
    private bool _wasRecentlyTrimmed;   // set when buffer hits cap; signals degraded network to the next misprediction log
    private EntitySpawner _subscribedSpawner;
    // Per-second cap on misprediction diagnostic logs to avoid log spam at 60Hz when
    // mispredictions are rapid-fire. State counts are kept in _misspredictionsCount.
    private ulong _mispredictionWindowStartMsec;
    private int _mispredictionLogsThisWindow;
    private const int MispredictionLogsPerSecond = 5;
    // Diffs above this are attributed to an external force (remote player hit, server impulse,
    // physics-object collision). Below it the drift is consistent with Jolt non-determinism.
    private const float ExternalForceThresholdM = 0.5f;

    // Listen-server: when ClientManager defers RegisterPrediction (because the SpaceStep
    // happens in ServerManager later this frame), the input + tick are stashed here and
    // committed by OnServerPostPhysicsTick after the step.
    private int _pendingTick;
    private IPackableElement _pendingInput;
    private bool _hasPendingPrediction;
    private Server.ServerManager _subscribedServer;

    public override void _Ready()
    {
        base._Ready();
        // Subscribe to EntityDestroyed so we can drop stale entries from _predictedStates.
        // Without this, an authority transfer (which destroys+recreates the local entity on
        // the previous owner) leaves the prediction loop iterating freed Godot objects.
        _subscribedSpawner = EntitySpawner.Instance;
        if (_subscribedSpawner != null)
            _subscribedSpawner.EntityDestroyed += OnEntityDestroyed;

        // In listen-server mode, ServerManager exists in the same process and its
        // _PhysicsProcess runs after ClientManager's. Subscribe to PostPhysicsTick so we
        // can commit the deferred prediction after the shared SpaceStep.
        _subscribedServer = Server.ServerManager.Instance;
        if (_subscribedServer != null)
            _subscribedServer.PostPhysicsTick += OnServerPostPhysicsTick;
    }

    public override void _ExitTree()
    {
        if (_subscribedSpawner != null && IsInstanceValid(_subscribedSpawner))
            _subscribedSpawner.EntityDestroyed -= OnEntityDestroyed;
        _subscribedSpawner = null;

        if (_subscribedServer != null && IsInstanceValid(_subscribedServer))
            _subscribedServer.PostPhysicsTick -= OnServerPostPhysicsTick;
        _subscribedServer = null;

        base._ExitTree();
    }

    /// <summary>
    /// Listen-server only: ClientManager calls this in place of <see cref="RegisterPrediction"/>
    /// while the SpaceStep is still pending in ServerManager. The actual RegisterPrediction
    /// fires from <see cref="OnServerPostPhysicsTick"/> after the step completes.
    /// </summary>
    public void StashForLatePrediction(int tick, IPackableElement input)
    {
        _pendingTick = tick;
        _pendingInput = input;
        _hasPendingPrediction = true;
    }

    private void OnServerPostPhysicsTick(int serverTick)
    {
        if (!_hasPendingPrediction) return;
        _hasPendingPrediction = false;
        var input = _pendingInput;
        _pendingInput = null;
        RegisterPrediction(_pendingTick, input);
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
        // Drop the init flag so an authority-swap or reclaim respawn under the same
        // EntityId is treated as a fresh entity by the spawn-tick alignment guard.
        _initializedEntityIds.Remove(entityId);
    }

    protected override void OnCommandReceived(IPackableMessage command)
    {
        if (!NetworkReady)
            return;

        if (command is GameSnapshotMessage snapshot)
        {
            if (snapshot.Tick > _lastTickReceived)
            {
                MonkeLogger.Debug($"[NET-SNAP-RX] tick={snapshot.Tick} entities={snapshot.States.Length} (last={_lastTickReceived})");
                for (int i = 0; i < snapshot.States.Length; i++)
                    MonkeLogger.Debug($"[NET-SNAP-RX]   state[{i}]={snapshot.States[i]}");
                _lastTickReceived = snapshot.Tick;
                CacheInputsFromSnapshot(snapshot);
                ProcessServerState(snapshot);
            }
            else
            {
                MonkeLogger.Debug($"[NET-SNAP-RX] dropped out-of-order tick={snapshot.Tick} (last={_lastTickReceived})");
            }
        }
    }

    // Each snapshot carries the input the server applied to each owner-driven entity
    // this tick. Cache them so Predict() can re-apply the same input locally when
    // simulating entities this client doesn't own.
    private void CacheInputsFromSnapshot(GameSnapshotMessage snapshot)
    {
        if (snapshot.Inputs == null) return;
        foreach (var entry in snapshot.Inputs)
        {
            _lastInputByEntityId[entry.EntityId] = entry.Input;
        }
    }

    /// <summary>
    /// Returns the most recent input the server reported for <paramref name="entityId"/>,
    /// or null if no input has ever been received (typical for passive props the server
    /// owns directly). Used by the entity prediction path when this client is not the
    /// driver — see <see cref="Predict"/>.
    /// </summary>
    public IPackableElement GetLastInputForEntity(int entityId) =>
        _lastInputByEntityId.TryGetValue(entityId, out var v) ? v : null;

    public void Predict(int tick, IPackableElement input)
    {
        // Run OnProcessTick on every predicted entity regardless of whether the
        // local input source produced an input for this tick. Skipping on null
        // input means the entity's OnProcessTick (which for a player applies
        // movement impulses, for a cube does nothing) is bypassed, and then the
        // matching RegisterPrediction call below ALSO skips — so the tick's
        // post-step state for every predicted body is never recorded. A later
        // rollback's resim loop then iterates _predictedStates and silently
        // skips the un-registered ticks, dropping that many ticks' worth of
        // physics from the resim. The player free-falls correctly on the server
        // and on the regular client tick path, but the resim path falls short
        // by N*gravity-dt² metres on the Y axis (N = un-registered ticks since
        // rollback), accumulating into a 0.2 m+ position drift over a few
        // dozen idle ticks — observed as a fresh misprediction at the moment
        // the running drift crosses the hard threshold.
        int myId = ClientManager.Instance?.GetNetworkId() ?? 0;
        EntitySpawner.Instance.ClientEntities.ForEach(entity =>
        {
            var clientPredictedEntity = entity.GetComponent<ClientPredictedEntity>();
            if (clientPredictedEntity == null) return;
            // Per-entity input routing: the locally-driven entity gets fresh local
            // input; every other predicted entity gets the input the server last
            // reported for it (via GameSnapshotMessage.Inputs). Without this, the
            // local input would either be applied to all entities (pushing remote
            // players around) or no input would be applied to remote entities (they
            // would coast at last-known velocity, drifting every tick).
            IPackableElement perEntityInput = (entity.Authority == myId)
                ? input
                : GetLastInputForEntity(entity.EntityId);
            clientPredictedEntity.OnProcessTick(tick, perEntityInput);
        });
    }

    /// <summary>
    /// Per-tick post-step hook. Invoked by ClientManager after the SpaceStep
    /// has integrated this tick (so every predicted body's GlobalPosition now
    /// holds its post-step pose) and BEFORE <see cref="RegisterPrediction"/>
    /// captures the snapshot. Entities that need to read another entity's
    /// post-step pose this tick — e.g. a kinematic rider anchoring to the
    /// just-integrated vehicle — override
    /// <see cref="ClientPredictedEntity.OnPostPhysicsTick"/> to do it here.
    /// </summary>
    public void RunPostPhysicsTick(int tick, IPackableElement input)
    {
        int myId = ClientManager.Instance?.GetNetworkId() ?? 0;
        EntitySpawner.Instance.ClientEntities.ForEach(entity =>
        {
            var clientPredictedEntity = entity.GetComponent<ClientPredictedEntity>();
            if (clientPredictedEntity == null) return;
            IPackableElement perEntityInput = (entity.Authority == myId)
                ? input
                : GetLastInputForEntity(entity.EntityId);
            clientPredictedEntity.OnPostPhysicsTick(tick, perEntityInput);
        });
    }

    public void RegisterPrediction(int tick, IPackableElement input)
    {
        // Always record a PredictedState entry — see Predict() for why a null
        // input must NOT short-circuit this path. The Entities dict captures
        // post-step body state regardless of whether a real input was applied;
        // the resim loop needs an entry per tick so SpaceStep is replayed the
        // correct number of times.
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
            _wasRecentlyTrimmed = true;
            LogTrimWarningThrottled();
        }

        //TODO: use array of ClientPredictedEntity that updates each time a new entity is spawned/despawned
        //TODO: store entity state inside entity itself instead of having everything here on PredictionManager
        EntitySpawner.Instance.ClientEntities.ForEach(entity =>
        {
            var clientPredictedEntity = entity.GetComponent<ClientPredictedEntity>();
            if (clientPredictedEntity != null)
            {
                var snap = clientPredictedEntity.GetSnapshotState();
                predictedState.Entities.Add(clientPredictedEntity, snap);
                MonkeLogger.Debug($"[PRED-REG] tick={tick} eid={clientPredictedEntity.EntityId} input={input} pos=({snap.Position.X:F3},{snap.Position.Y:F3},{snap.Position.Z:F3}) vel=({snap.LinearVelocity.X:F3},{snap.LinearVelocity.Y:F3},{snap.LinearVelocity.Z:F3}) angvel=({snap.AngularVelocity.X:F3},{snap.AngularVelocity.Y:F3},{snap.AngularVelocity.Z:F3})");
            }
        });
    }

    private void ProcessServerState(GameSnapshotMessage receivedSnapshot)
    {
        // Capture and clear the trim flag so it applies to this snapshot only.
        bool networkDegraded = _wasRecentlyTrimmed;
        _wasRecentlyTrimmed = false;

        var predictedStateData = _predictedStates.Find(prediction => prediction.Tick == receivedSnapshot.Tick);
        _predictedStates.RemoveAll(predictedState => predictedState.Tick <= receivedSnapshot.Tick);

        if (predictedStateData == default(PredictedState) || predictedStateData.Tick != receivedSnapshot.Tick)
        {
            // No locally-owned predicted entities means there's nothing to reconcile this
            // tick — RegisterPrediction never recorded a state for it (no input, or no
            // ClientPredictedEntity in the scene). This is the normal pre-spawn / spectator
            // path, not a fault. Don't count it or log it.
            if (!HasAnyPredictedEntity()) return;
            _missedLocalState++;
            MonkeLogger.Debug($"[PRED-CHECK] tick={receivedSnapshot.Tick} MISSED-LOCAL-STATE (no matching predicted entry; total missed={_missedLocalState})");
            return;
        }

        // Spawn-tick alignment: if any predicted entity in this snapshot is appearing
        // in an authoritative state for the FIRST time, the recorded predicted state
        // for it at this tick was generated from a body that had only been integrated
        // for a couple of physics ticks since EntityEventMessage.Created — while the
        // server's snapshot reflects however many ticks the server has been simulating
        // it, which is normally larger by (clientCreateTick - serverSpawnTick) ticks.
        // The comparison would always fail by a consistent fraction of a metre per
        // affected entity (one gravity step per spawn-tick offset for a free-falling
        // body), but that "misprediction" is structural — there's no prior auth state
        // for the client to predict from. Catch the newly-spawned entity up to the
        // current local tick via a targeted reconcile+resim, but don't disturb other
        // already-initialized entities (their stable Jolt contact manifolds would
        // otherwise be invalidated by an unnecessary body teleport — producing the
        // observed top-of-stack micro-bounce when the player spawns near a tower).
        HashSet<int> newlyInitializedIds = null;
        foreach (ClientPredictedEntity predictableEntity in predictedStateData.Entities.Keys)
        {
            if (_initializedEntityIds.Add(predictableEntity.EntityId))
            {
                newlyInitializedIds ??= new HashSet<int>();
                newlyInitializedIds.Add(predictableEntity.EntityId);
            }
        }
        if (newlyInitializedIds != null)
        {
            MonkeLogger.Debug($"[PRED-CHECK] tick={receivedSnapshot.Tick} initial-snapshot for new predicted entity → catch-up resim (no misprediction count)");
            CatchUpNewlySpawnedEntities(receivedSnapshot.States, predictedStateData, newlyInitializedIds);
            return;
        }

        // Iterate all entities saved for the tick
        foreach (ClientPredictedEntity predictableEntity in predictedStateData.Entities.Keys)
        {
            // Get predicted and authoritative state for the entity
            var predictedState = predictedStateData.Entities[predictableEntity];
            var authoritativeState = FindStateForEntityId(predictableEntity.EntityId, receivedSnapshot.States);

            Vector3 authPos = predictableEntity.ExtractAuthoritativePosition(authoritativeState);
            Vector3 posDiff = authPos - predictedState.Position;
            MonkeLogger.Debug($"[PRED-CHECK] tick={receivedSnapshot.Tick} eid={predictableEntity.EntityId} predPos=({predictedState.Position.X:F3},{predictedState.Position.Y:F3},{predictedState.Position.Z:F3}) authPos=({authPos.X:F3},{authPos.Y:F3},{authPos.Z:F3}) |posDiff|={posDiff.Length():F4}m predVel=({predictedState.LinearVelocity.X:F3},{predictedState.LinearVelocity.Y:F3},{predictedState.LinearVelocity.Z:F3})");

            if (predictableEntity.HasMisspredicted(receivedSnapshot.Tick, authoritativeState, predictedState))
            {
                _misspredictionsCount++;
                MonkeLogger.Debug($"[PRED-CHECK] tick={receivedSnapshot.Tick} eid={predictableEntity.EntityId} MISPREDICTED -> rollback");
                LogMispredictionThrottled(predictableEntity, predictedState.Position, authoritativeState, receivedSnapshot.Tick, networkDegraded);
                RollbackAndResimulate(receivedSnapshot.States, predictedStateData);
                return;
            }

            // Below the hard reconcile threshold — no body teleport needed.
            // But entities can still react to the snapshot for non-pose state
            // sync (e.g. sleep-state coherence on cubes: the client's Jolt may
            // have woken a body that the server has settled, and that wake
            // produces a tiny per-tick drift that won't trip HasMisspredicted
            // for a long time. ApplyAuthoritativeNonPoseState lets each
            // entity sync any such auxiliary state every snapshot without
            // touching the body's transform).
            MonkeLogger.Debug($"[PRED-CHECK] tick={receivedSnapshot.Tick} eid={predictableEntity.EntityId} OK");
            predictableEntity.ApplyAuthoritativeNonPoseState(authoritativeState);
        }
    }

    // Spawn-tick-alignment catch-up. Unlike RollbackAndResimulate (used for genuine
    // mispredictions where every body needs to be reset to the rollback tick so the
    // mispredicted entity's contact interactions during resim reproduce correctly),
    // this path is invoked when a NEW entity's first authoritative snapshot arrives
    // alongside already-correctly-predicted ones. Already-predicted entities don't
    // need their bodies disturbed — reconciling them invalidates Jolt's persistent
    // contact manifolds for a body teleport that, in pose terms, is a no-op.
    //
    // Strategy: only HandleReconciliation on the newly-spawned entity. Don't touch
    // the body transforms of anyone else. The resim's SpaceStep calls integrate
    // every body in the world (unavoidable — Jolt steps the space holistically),
    // so non-new entities advance by N ticks of velocity-driven motion they
    // wouldn't have otherwise undergone. That's a small one-time forward shift
    // (a few mm at typical stack-settle velocities) which PredictionVisualSmoothing3D
    // observes as an unexplained body delta and decays away as a visual offset over
    // DecayTime. Their recorded predictedStates entries stay at their original
    // (correctly-predicted) values so subsequent PRED-CHECKs still compare against
    // the right tick. Net effect: the visible mesh stays smooth across the spawn
    // and no Jolt contact manifolds are invalidated by a redundant transform write.
    private void CatchUpNewlySpawnedEntities(
        IEntityStateData[] authoritativeStates,
        PredictedState predictedStateData,
        HashSet<int> newlyInitializedIds)
    {
        // Listen-server: same rationale as RollbackAndResimulate — client and server
        // share the World3D.Space, so any SpaceStep here would advance every peer's
        // body in the shared space. Just skip; nothing to catch up.
        if (MonkeNetManager.Instance != null && MonkeNetManager.Instance.IsServer)
        {
            MonkeLogger.Debug($"[PRED-SPAWN-CATCHUP] tick={predictedStateData.Tick} SKIPPED (listen-server, shared physics space)");
            return;
        }

        int otherCount = predictedStateData.Entities.Count - newlyInitializedIds.Count;
        MonkeLogger.Debug($"[PRED-SPAWN-CATCHUP] tick={predictedStateData.Tick} newEntities={newlyInitializedIds.Count} otherEntities={otherCount} resimTicks={_predictedStates.Count}");

        OfflineRigidbody3D.SnapshotAll();

        // Reconcile newly-spawned entities to their auth state at the rollback tick.
        // Leave every other predicted entity's body completely alone — no snapshot,
        // no transform write — so their contact manifolds stay intact across the
        // catch-up.
        foreach (ClientPredictedEntity predictableEntity in predictedStateData.Entities.Keys)
        {
            if (!newlyInitializedIds.Contains(predictableEntity.EntityId)) continue;
            var authoritativeState = FindStateForEntityId(predictableEntity.EntityId, authoritativeStates);
            MonkeLogger.Debug($"[PRED-RECONCILE] tick={predictedStateData.Tick} eid={predictableEntity.EntityId} -> auth={authoritativeState} (newly spawned)");
            predictableEntity.HandleReconciliation(authoritativeState);
        }

        for (int i = 0; i < _predictedStates.Count; i++)
        {
            var remainingInput = _predictedStates[i];
            MonkeLogger.Debug($"[PRED-SPAWN-RESIM] resimTick={remainingInput.Tick} input={remainingInput.Input}");
            // Only re-apply inputs for the newly-spawned entities. Other entities were
            // already correctly stepped during natural predict; replaying their inputs
            // would double-apply them.
            foreach (ClientPredictedEntity predictableEntity in remainingInput.Entities.Keys)
            {
                if (newlyInitializedIds.Contains(predictableEntity.EntityId))
                    predictableEntity.ResimulateTick(remainingInput.Input);
            }

            PhysicsServer3D.SpaceStep(MonkeNetManager.Instance.PhysicsSpace, PhysicsUtils.DeltaTime);
            PhysicsServer3D.SpaceFlushQueries(MonkeNetManager.Instance.PhysicsSpace);

            // Only capture post-step state for newly-spawned entities. Non-new
            // entities' recorded predicted states stay at their original (correctly-
            // predicted) values; their bodies are now N ticks of velocity ahead of
            // where natural prediction would have left them, and the smoother will
            // decay that small offset out over DecayTime.
            foreach (ClientPredictedEntity predictableEntity in remainingInput.Entities.Keys)
            {
                if (!newlyInitializedIds.Contains(predictableEntity.EntityId)) continue;
                var post = predictableEntity.GetSnapshotState();
                remainingInput.Entities[predictableEntity] = post;
                MonkeLogger.Debug($"[PRED-SPAWN-RESIM]   eid={predictableEntity.EntityId} postPos=({post.Position.X:F3},{post.Position.Y:F3},{post.Position.Z:F3}) postVel=({post.LinearVelocity.X:F3},{post.LinearVelocity.Y:F3},{post.LinearVelocity.Z:F3})");
            }
        }

        OfflineRigidbody3D.RestoreAll();
        MonkeLogger.Debug($"[PRED-SPAWN-CATCHUP] complete");
    }

    private void RollbackAndResimulate(IEntityStateData[] authoritativeStates, PredictedState predictedStateData)
    {
        bool isListenServer = MonkeNetManager.Instance != null && MonkeNetManager.Instance.IsServer;

        MonkeLogger.Debug($"[PRED-ROLLBACK] tick={predictedStateData.Tick} entities={predictedStateData.Entities.Count} resimTicks={_predictedStates.Count} listenServer={isListenServer}");

        // Snapshot non-networked rigidbodies so the resim's repeated SpaceStep calls
        // don't drift them. Restored after the loop. (Pure-client path only — the
        // listen-server branch short-circuits before SpaceStep, so no offline-body
        // restore is needed.)
        if (!isListenServer)
            OfflineRigidbody3D.SnapshotAll();

        // Set all entities to authoritative state. Critical for the unified-prediction
        // model where every client predicts every entity (including server-owned ones) —
        // reconcile is the only way the client's locally-simulated body learns the
        // server's authoritative state.
        foreach (ClientPredictedEntity predictableEntity in predictedStateData.Entities.Keys)
        {
            var authoritativeState = FindStateForEntityId(predictableEntity.EntityId, authoritativeStates);
            MonkeLogger.Debug($"[PRED-RECONCILE] tick={predictedStateData.Tick} eid={predictableEntity.EntityId} -> auth={authoritativeState}");
            predictableEntity.HandleReconciliation(authoritativeState);
        }

        // Listen-server short-circuit: skip the resim loop. Client and server share the
        // same World3D.Space, so stepping it N more times would double-step every body.
        // The per-entity HandleReconciliation above already pulled each client body to
        // the authoritative state — the resim was only ever needed in pure-client mode
        // where the client's separate physics space had to catch up.
        if (isListenServer)
        {
            MonkeLogger.Debug($"[PRED-ROLLBACK] tick={predictedStateData.Tick} SpaceStep-loop SKIPPED (listen-server)");
            return;
        }

        // Advance simulation forward for all remaining inputs
        int myId = ClientManager.Instance?.GetNetworkId() ?? 0;
        for (int i = 0; i < _predictedStates.Count; i++)
        {
            var remainingInput = _predictedStates[i];
            MonkeLogger.Debug($"[PRED-RESIM] resimTick={remainingInput.Tick} entities={remainingInput.Entities.Count} input={remainingInput.Input}");
            foreach (ClientPredictedEntity predictableEntity in remainingInput.Entities.Keys)
            {
                // Same per-entity input routing as Predict(): the locally-driven
                // entity replays the local input recorded at this past tick; other
                // entities replay the most recently-cached server input. The cache
                // is "latest" rather than per-tick, so resim of distant past ticks
                // for non-owned entities is approximate, but at the resim depths
                // we use (≤ 1s) the approximation tracks well enough to converge.
                IPackableElement entityInput = (predictableEntity.Authority == myId)
                    ? remainingInput.Input
                    : GetLastInputForEntity(predictableEntity.EntityId);
                predictableEntity.ResimulateTick(entityInput);
            }

            PhysicsServer3D.SpaceStep(MonkeNetManager.Instance.PhysicsSpace, PhysicsUtils.DeltaTime);
            PhysicsServer3D.SpaceFlushQueries(MonkeNetManager.Instance.PhysicsSpace);

            foreach (ClientPredictedEntity predictableEntity in remainingInput.Entities.Keys)
            {
                var post = predictableEntity.GetSnapshotState();
                remainingInput.Entities[predictableEntity] = post;
                MonkeLogger.Debug($"[PRED-RESIM]   eid={predictableEntity.EntityId} postPos=({post.Position.X:F3},{post.Position.Y:F3},{post.Position.Z:F3}) postVel=({post.LinearVelocity.X:F3},{post.LinearVelocity.Y:F3},{post.LinearVelocity.Z:F3})");
            }
        }

        OfflineRigidbody3D.RestoreAll();
        MonkeLogger.Debug($"[PRED-ROLLBACK] complete (offline bodies restored)");
    }

    private static bool HasAnyPredictedEntity()
    {
        if (EntitySpawner.Instance == null) return false;
        foreach (var entity in EntitySpawner.Instance.ClientEntities)
        {
            if (entity.GetComponent<ClientPredictedEntity>() != null)
                return true;
        }
        return false;
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

    private void LogMispredictionThrottled(ClientPredictedEntity entity, Vector3 predictedPos, IEntityStateData authoritativeState, int tick, bool networkDegraded)
    {
        ulong now = Time.GetTicksMsec();
        if (now - _mispredictionWindowStartMsec >= 1000)
        {
            _mispredictionWindowStartMsec = now;
            _mispredictionLogsThisWindow = 0;
        }
        if (_mispredictionLogsThisWindow >= MispredictionLogsPerSecond) return;
        _mispredictionLogsThisWindow++;

        // Log the actual authoritative-vs-predicted comparison the threshold check
        // is doing, plus its magnitude, so the log directly shows what triggered
        // reconcile. Previously this logged entity.GetPosition() — the body's
        // CURRENT pose, possibly several ticks past the snapshot tick — which made
        // small steady-state drifts look like the misprediction.
        Vector3 authPos = entity.ExtractAuthoritativePosition(authoritativeState);
        Vector3 diff = authPos - predictedPos;
        string cause = ClassifyMisprediction(diff.Length(), networkDegraded);
        MonkeLogger.Info($"Misprediction [{cause}]: entity {entity.EntityId} type {entity.EntityType} tick {tick} predicted {predictedPos} authoritative {authPos} diff {diff} |diff|={diff.Length():F3}m");
    }

    // Heuristic cause label for misprediction log messages, matching the known scenario table:
    //   degraded-network  — prediction buffer was trimmed before this snapshot arrived,
    //                       indicating snapshot packet-loss or an RTT spike (server used stale input).
    //   external-force    — diff well above threshold; another body applied an impulse the
    //                       client couldn't predict (remote player collision, server-side knockback).
    //   physics-nondeterminism — small drift near threshold; Jolt cross-process floating-point
    //                       divergence accumulating over time (expected in solo or low-contact play).
    private static string ClassifyMisprediction(float diffLength, bool networkDegraded)
    {
        if (networkDegraded)
            return "degraded-network";
        if (diffLength >= ExternalForceThresholdM)
            return "external-force";
        return "physics-nondeterminism";
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
        public Dictionary<ClientPredictedEntity, RigidbodyState> Entities;
    }
}
