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
    // Hard cap on prediction history depth + maximum rollback resim depth in
    // ticks. Default 10 ≈ 166ms at 60Hz. The cap bounds the per-snapshot resim
    // CPU cost: at C4 conditions the holistic SpaceStep over ~40 rigid bodies
    // costs ~3-5ms per resim tick, so a 47-tick rollback would freeze the
    // engine for ~200ms and cascade into more dropped physics ticks. The
    // smaller the cap, the smaller the worst-case freeze.
    //
    // Snapshots arriving for ticks older than (current - MaxRollbackTicks) are
    // dropped (counted as MissedLocalState) since the matching PredictedState
    // entry has already been trimmed. This biases the trade-off toward
    // "smooth rendering with occasional accepted desync" over "perfect
    // reconciliation with engine freezes". SnapNet's rollback analysis cites
    // ~15-frame practical cap on 60Hz before the spiral-of-death pattern
    // sets in for any non-trivial simulation; Photon Fusion 2 sidesteps the
    // cap entirely by only resimming the own player and snapshot-
    // interpolating everything else (the architectural change MonkeNet has
    // not yet adopted).
    [Export] public int MaxRollbackTicks { get; set; } = 25;



    private readonly List<PredictedState> _predictedStates = [];
    // Per-entity per-tick input cache populated from GameSnapshotMessage.Inputs
    // on every snapshot. Each snapshot carries the last N inputs the server
    // applied per entity, each stamped with its server tick — observers look
    // up by (entityId, tick) so forward prediction at tick T uses the input
    // applied at server tick T (when known), and the rollback resim loop
    // replays each past tick with its actual applied input rather than
    // approximating with "latest cached input for everything in the window".
    private readonly Dictionary<int, SortedDictionary<int, IPackableElement>> _inputByEntityIdAndTick = [];
    // Per-entity most recent server tick we have cached input for. Used by
    // ResolveRemoteInput when the requested tick isn't in the cache (typical
    // for forward prediction at ticks beyond the latest snapshot): falls back
    // to the latest known input rather than null, so observers extrapolate
    // forward with "continue last input" instead of coasting on velocity.
    private readonly Dictionary<int, int> _latestCachedTickByEntityId = [];
    // Entity IDs of remote (non-locally-owned) predicted entities for which we
    // currently have NO cached server input. Used to edge-log the "predicting
    // remote entity without input" condition — see ResolveRemoteInput. Tracking
    // edges (rather than logging every tick) keeps the log readable: one line
    // when input goes missing, one line when it comes back.
    private readonly HashSet<int> _remoteEntitiesAwaitingInput = [];
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
    private int _mispredictsExternalForce = 0;
    private int _mispredictsPhysicsNondeterminism = 0;
    private int _mispredictsDegradedNetwork = 0;
    private int _lastRollbackDepth = 0;
    /// <summary>
    /// Number of mispredictions detected this session. Surfaced for harness-driven tests
    /// (see <c>MultiClientHarness.MispredictCount</c>) so a multi-process scenario can
    /// assert misprediction budgets after driving a known input pattern.
    /// </summary>
    public int MispredictionsCount => _misspredictionsCount;
    /// <summary>
    /// Count of mispredictions absorbed by the per-entity Interpolate-tier blend path
    /// (see <see cref="ClientPredictedEntity.HandleInterpolateReconciliation"/>). These
    /// are NOT full-scene rollbacks — the body is gently nudged toward the snapshot pose
    /// over a few ticks. Surfaced for the T1 two-tier test to verify drift on idle props
    /// is being absorbed by blends rather than the rollback path.
    /// </summary>
    public int InterpolateReconcileCount => _interpolateReconcileCount;
    private int _interpolateReconcileCount = 0;
    // Per-entity last-reported EffectiveTier, used by NoteTierIfChanged to detect
    // edges. Held on the manager rather than the entity so the entity stays
    // tick-agnostic — the manager owns the per-tick polling.
    private readonly Dictionary<int, PredictionTier> _lastReportedTierByEid = [];
    /// <summary>Per-class mispredict count: server-side impulses or remote-player collisions
    /// the client didn't replay. The user-visible class — these are the mispredicts that
    /// actually matter for sync quality.</summary>
    public int MispredictsExternalForce => _mispredictsExternalForce;
    /// <summary>Per-class mispredict count: cross-process Jolt FP divergence (contact-manifold
    /// rounding, bounce-timing skew). Unavoidable noise floor — not a sync failure.</summary>
    public int MispredictsPhysicsNondeterminism => _mispredictsPhysicsNondeterminism;
    /// <summary>Per-class mispredict count: snapshots arriving for ticks already trimmed
    /// from the prediction history buffer. Must be zero under normal operating conditions;
    /// a non-zero count indicates packet loss or RTT spikes exceeded the rollback window.</summary>
    public int MispredictsDegradedNetwork => _mispredictsDegradedNetwork;
    /// <summary>Depth (in ticks) of the most recent rollback: <c>currentTick − reconcileTick</c>.
    /// Updated each time <see cref="RollbackAndResimulate"/> runs. Used by quantitative tests
    /// to assemble the rollback-depth distribution (P50/P95/P99) across a scenario.</summary>
    public int LastRollbackDepth => _lastRollbackDepth;
    /// <summary>Cumulative count of (tick, entity) pairs where the prediction loop did
    /// NOT have the EXACT current-tick input cached for a remote entity (that the
    /// server has previously reported input for). The predictor still continues —
    /// extrapolating from the most recent preceding cached input — but each such
    /// extrapolation counts as one missed-input event. The metric thereby measures
    /// how often the server-reported input stream is too sparse or too stale to
    /// keep up with the client's prediction tick.</summary>
    public int MissedInputCount => _missedInputCount;
    private int _missedInputCount = 0;
    /// <summary>Cumulative count of snapshots that arrived too old for a forward
    /// resim (depth &gt; <see cref="MaxRollbackTicks"/>) and were corrected by
    /// teleport-snap via <see cref="SnapToAuthOverflow"/> instead. Quantitative-
    /// suite M11 metric — distinguishes "predictions were wrong" (M3) from
    /// "couldn't run a resim, snapped instead". High values at C3/C4 are
    /// expected with a tight cap; high values at C0/C1 indicate the cap is
    /// binding more often than it should.</summary>
    public int SnapToAuthCount => _snapOverflowCount;

    // Set of entity ids for which the server has reported at least one input.
    // Used by ResolveRemoteInput to gate the missed-input counter so that
    // server-owned passive props (which never have input) do not inflate the
    // M9 metric — see ResolveRemoteInput for the rationale.
    private readonly HashSet<int> _entitiesEverHadInput = new();
    private int _missedLocalState = 0;
    private int _snapOverflowCount = 0;
    private int _trimmedTotal = 0;
    private ulong _lastTrimWarningMsec;
    private bool _wasRecentlyTrimmed;   // set when buffer hits cap; signals degraded network to the next misprediction log
    private EntitySpawner _subscribedSpawner;
    // Per-second cap on misprediction diagnostic logs to avoid log spam at 60Hz when
    // mispredictions are rapid-fire. State counts are kept in _misspredictionsCount.
    private ulong _mispredictionWindowStartMsec;
    private int _mispredictionLogsThisWindow;
    private const int MispredictionLogsPerSecond = 5;
    // Velocity-residual threshold (m/s) above which the misprediction is attributable
    // to a genuine external force — a server-side impulse or remote-player collision
    // the client didn't replay. Pure cross-process Jolt nondeterminism (gravity steps,
    // contact-manifold rounding) produces position drift with NEAR-IDENTICAL velocity
    // because both sides applied the same gravity at the same dt; a horizontal velocity
    // delta of a meter per second only appears when something on the server delivered
    // an impulse the client's input replay didn't reproduce. Squared form to avoid the
    // sqrt in the classifier. Replaced the prior position-magnitude classifier
    // (ExternalForceThresholdM = 0.5 m): a 0.5 m position delta with matching velocity
    // is just accumulated gravity drift, not a missed external force.
    private const float ExternalForceVelThresholdSquared = 1.0f; // 1 m/s squared

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
        _remoteEntitiesAwaitingInput.Remove(entityId);
        _entitiesEverHadInput.Remove(entityId);
        _lastReportedTierByEid.Remove(entityId);
    }

    protected override void OnCommandReceived(IPackableMessage command)
    {
        if (!NetworkReady)
            return;

        if (command is GameSnapshotMessage snapshot)
        {
            if (snapshot.Tick > _lastTickReceived)
            {
                // Capture the full clock state at the moment of reception so
                // we can attribute rollback depth to each source term.
                int rawTick = ClientManager.Instance?.GetNodeOrNull<ClientNetworkClock>("ClientNetworkClock")?.RawTick ?? -1;
                int avgLat = ClientManager.Instance?.GetNodeOrNull<ClientNetworkClock>("ClientNetworkClock")?.AverageLatencyInTicks ?? -1;
                int jitter = ClientManager.Instance?.GetNodeOrNull<ClientNetworkClock>("ClientNetworkClock")?.JitterInTicks ?? -1;
                int curTick = ClientManager.Instance?.GetNodeOrNull<ClientNetworkClock>("ClientNetworkClock")?.GetCurrentTick() ?? -1;
                int depth = curTick - snapshot.Tick;
                int rawAge = rawTick - snapshot.Tick;
                MonkeLogger.Debug($"[NET-SNAP-RX] tick={snapshot.Tick} entities={snapshot.States.Length} (last={_lastTickReceived}) | curTick={curTick} rawTick={rawTick} rawAge={rawAge} avgLat={avgLat} jitter={jitter} depth={depth} bias={avgLat - rawAge}");

                // Option C: extract the server's view of this client's
                // input-buffer depth and forward it to ClientInputManager.
                // The signal is bufferDepth = LastInputTick − snapshot.Tick
                // = how many ticks of input the server has queued AHEAD of
                // the tick it just simulated. Big bufferDepth means lots
                // of safety against jitter (and lots of input lag); small
                // means the server is close to starving on our inputs.
                //
                // The right actuator for this signal in a physics-sync
                // system is the per-client input delay (the GGPO/Quantum-
                // style InputOffset), NOT the local simulation clock — the
                // simulation clock controls "how far the client predicts
                // ahead" and stretching it produces visible judder on every
                // predicted entity. See ClientInputManager.OnServerInputBufferReport
                // for the adjustment logic and the rationale for why
                // InputDelayTicks is the correct knob.
                if (snapshot.InputFrontiers != null && snapshot.InputFrontiers.Length > 0)
                {
                    int myId = ClientManager.Instance?.GetNetworkId() ?? -1;
                    foreach (var frontier in snapshot.InputFrontiers)
                    {
                        if (frontier.ClientNetworkId != myId) continue;
                        if (frontier.LastInputTick > 0)
                        {
                            int bufferDepth = frontier.LastInputTick - snapshot.Tick;
                            var inputMgr = ClientManager.Instance?.GetNodeOrNull<ClientInputManager>("ClientInputManager");
                            inputMgr?.OnServerInputBufferReport(bufferDepth, jitter);
                        }
                        break;
                    }
                }
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

    // Cap on per-entity client-side input cache. Larger than the server's
    // per-snapshot history depth so the cache can accumulate across multiple
    // snapshots before the oldest entries are dropped — gives the rollback
    // resim a deep-enough window that a typical reconcile finds per-tick input
    // for every replayed tick even when the resim depth exceeds one
    // snapshot's history span.
    private const int InputCacheCapPerEntity = 60;

    // Each snapshot carries the last N (tick, input) pairs the server applied
    // per entity. Cache them by (entityId, tick) so Predict() and
    // RollbackAndResimulate can look up the actual per-tick input the server
    // used, not just the latest cached one.
    private void CacheInputsFromSnapshot(GameSnapshotMessage snapshot)
    {
        if (snapshot.Inputs == null) return;
        foreach (var entry in snapshot.Inputs)
        {
            if (entry.Input == null) continue;
            if (!_inputByEntityIdAndTick.TryGetValue(entry.EntityId, out var perTick))
            {
                perTick = new SortedDictionary<int, IPackableElement>();
                _inputByEntityIdAndTick[entry.EntityId] = perTick;
            }
            perTick[entry.Tick] = entry.Input;
            while (perTick.Count > InputCacheCapPerEntity)
                perTick.Remove(perTick.Keys.First());

            if (!_latestCachedTickByEntityId.TryGetValue(entry.EntityId, out int latest)
                || entry.Tick > latest)
            {
                _latestCachedTickByEntityId[entry.EntityId] = entry.Tick;
            }
            // Mark this entity as one that has received non-null server input
            // at some point. ResolveRemoteInput uses this set to scope the M9
            // missed-input counter to entities that should have input (i.e.,
            // not passive server-authoritative props).
            _entitiesEverHadInput.Add(entry.EntityId);
        }
    }

    /// <summary>
    /// Returns the most recent input the server reported for <paramref name="entityId"/>,
    /// or null if no input has ever been received (typical for passive props the server
    /// owns directly). Used by the entity prediction path when this client is not the
    /// driver — see <see cref="Predict"/>.
    /// </summary>
    public IPackableElement GetLastInputForEntity(int entityId)
    {
        if (!_inputByEntityIdAndTick.TryGetValue(entityId, out var perTick) || perTick.Count == 0)
            return null;
        return perTick[perTick.Keys.Last()];
    }

    /// <summary>
    /// Resolve the input that should be applied at <paramref name="tick"/> for a
    /// remote-owned entity. Returns the exact per-tick input from the snapshot
    /// history when available; otherwise the most recent cached input the
    /// observer knows about (a "last-input-continues" extrapolation, which is
    /// the best the observer can do for ticks the server hasn't reported on yet);
    /// otherwise null.
    ///
    /// Per-tick lookup is the main quality knob this method exists for —
    /// during a rollback resim the loop iterates past ticks one by one, and a
    /// per-tick cache lets the resim replay each tick with the correct input
    /// rather than approximating with "latest cached input for everything in
    /// the rollback window" (the prior behaviour flagged in
    /// <see cref="RollbackAndResimulate"/>).
    /// </summary>
    private IPackableElement ResolveRemoteInput(int entityId, int tick)
    {
        IPackableElement resolved = null;
        bool exactTickHit = false;
        if (_inputByEntityIdAndTick.TryGetValue(entityId, out var perTick) && perTick.Count > 0)
        {
            // Exact tick hit first.
            if (perTick.TryGetValue(tick, out resolved))
            {
                exactTickHit = true;
            }
            else
            {
                // Closest preceding tick — the input that was applied last at
                // or before the requested tick. Server's snapshot history is
                // sparse (last N applied), so a tick we don't have explicit
                // input for is the same input as the most recent preceding
                // tick we DO have input for.
                int chosenTick = int.MinValue;
                foreach (var kv in perTick)
                {
                    if (kv.Key > tick) break;
                    chosenTick = kv.Key;
                    resolved = kv.Value;
                }
                // If even the oldest cached tick is in the future (e.g. the
                // observer just connected and the first snapshot covered
                // future ticks for some reason), fall back to the latest
                // cached. Better to extrapolate from a known input than to
                // hand the entity null.
                if (chosenTick == int.MinValue)
                    resolved = perTick[perTick.Keys.Last()];
            }
        }

        // M9 missed-input metric: counts the gap between "have exact per-tick
        // server input" and "had to extrapolate / coast". For an entity that
        // has been seen with input at some point, every tick where we DON'T
        // have the exact tick's input counts as one missed-input event —
        // regardless of whether we found a fallback to extrapolate from. The
        // prediction still uses the extrapolated value (good for behaviour),
        // but the metric correctly measures how often the server-reported
        // input stream is too sparse / too late to keep up with the client's
        // prediction tick.
        if (!exactTickHit && _entitiesEverHadInput.Contains(entityId))
            _missedInputCount++;

        if (resolved == null)
        {
            if (_remoteEntitiesAwaitingInput.Add(entityId))
                MonkeLogger.Debug($"[PRED-NO-INPUT] eid={entityId} tick={tick} no cached server input for remote entity — predicting without input (entity will coast on last-known velocity / use defaults)");
        }
        else if (_remoteEntitiesAwaitingInput.Remove(entityId))
        {
            MonkeLogger.Debug($"[PRED-NO-INPUT] eid={entityId} tick={tick} cached server input now available — resuming input-driven prediction");
        }
        return resolved;
    }

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
                : ResolveRemoteInput(entity.EntityId, tick);
            clientPredictedEntity.OnProcessTick(tick, perEntityInput);
        });

        // T1 hysteresis pump. Run AFTER OnProcessTick so any per-tick contact
        // detection driven from a player's tick path (which lives in
        // OnPostPhysicsTick, but the hysteresis must outlive the contact tick
        // by N) has had a chance to refresh the upgrade. Counter decrements
        // back toward zero each tick; an entity continuously contacted by the
        // local player never falls below 1 because the player keeps re-arming
        // it. NoteTierIfChanged is what produces the [TIER-SWITCH] log line
        // and bumps TierSwitchCount/LastTierSwitchTick for diagnostics.
        EntitySpawner.Instance.ClientEntities.ForEach(entity =>
        {
            var cpe = entity.GetComponent<ClientPredictedEntity>();
            if (cpe == null) return;
            cpe.TickTierHysteresis();
            if (!_lastReportedTierByEid.TryGetValue(cpe.EntityId, out var prev))
                prev = cpe.BaseTier;
            cpe.NoteTierIfChanged(tick, ref prev);
            _lastReportedTierByEid[cpe.EntityId] = prev;
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
                : ResolveRemoteInput(entity.EntityId, tick);
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

            // Snap-on-overflow: snapshot tick is older than the oldest entry
            // still in _predictedStates. That means the matching predicted
            // entry was trimmed when the buffer hit MaxRollbackTicks — i.e.
            // the snapshot represents an authoritative state from too deep
            // in the past to resimulate forward affordably. If we just
            // dropped it (the previous behaviour), the client's body would
            // drift unbounded from the server's truth at C4 conditions
            // (observed 3.5m RMS / 6.6m P95 drift in cap=25 measurement run
            // 2026-05-25--23-34-50). Instead, teleport every entity body to
            // its authoritative state from the snapshot — accept a visible
            // 1-frame snap backward in exchange for keeping the client and
            // server pose+velocity in lockstep. The SpaceStep resim is
            // skipped entirely, so this path costs ~one body-pose write per
            // entity rather than ~N × full-scene SpaceStep.
            //
            // The smoother absorbs the teleport via AbsorbBodyTeleport
            // (called inside HandleReconciliation), and ResetBodyFtiAfterResim
            // pins SceneTreeFTI's local_prev = local_curr so the renderer
            // doesn't interpolate backward across the snap.
            //
            // Distinguish "trimmed by cap" (snap) from "never registered"
            // (genuine MissedLocalState, log only) by checking against the
            // oldest still-kept tick.
            int oldestKeptTick = _predictedStates.Count > 0
                ? _predictedStates[0].Tick
                : int.MaxValue;
            int newestKeptTick = _predictedStates.Count > 0
                ? _predictedStates[_predictedStates.Count - 1].Tick
                : int.MinValue;
            if (receivedSnapshot.Tick < oldestKeptTick)
            {
                MonkeLogger.Debug($"[PRED-SNAP-DECISION] tick={receivedSnapshot.Tick} bufSize={_predictedStates.Count} oldest={oldestKeptTick} newest={newestKeptTick} -> SNAP-OVERFLOW");
                SnapToAuthOverflow(receivedSnapshot);
                return;
            }

            _missedLocalState++;
            MonkeLogger.Debug($"[PRED-CHECK] tick={receivedSnapshot.Tick} MISSED-LOCAL-STATE (no matching predicted entry; total missed={_missedLocalState}) bufSize={_predictedStates.Count} oldest={oldestKeptTick} newest={newestKeptTick}");
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
        // current local tick via a targeted reconcile+resim. The resim's SpaceStep
        // is holistic and inevitably integrates already-initialized entities by
        // resimTicks of extra motion; CatchUpNewlySpawnedEntities snapshots their
        // pre-resim poses and restores them after the loop so subsequent PRED-REG
        // captures match the server. The restore invalidates persistent contact
        // manifolds for those bodies and the next physics step rebuilds them
        // (typically a sub-mm micro-bounce that PredictionVisualSmoothing3D
        // absorbs).
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

            // Interpolate tier: blend toward the snapshot pose on EVERY snapshot,
            // regardless of drift magnitude. The HasMisspredicted threshold gate
            // is a Resim-tier optimisation (skip expensive rollback for tiny
            // drift); it does NOT apply here because the blend is cheap and
            // gating it produces a visible bug — sub-threshold drift (under 0.2 m)
            // is left uncorrected, the local Jolt keeps integrating, and by the
            // time a cube comes to rest its pose can be 10–20 cm off the
            // authoritative pose. The subsequent SnapToRest then yanks the body
            // to server pose without smoother compensation, producing a visible
            // snap. Always-blend keeps the body within blend-lag (≤ snapshot
            // interval) of authoritative at all times, so SnapToRest is a no-op
            // when the cube finally settles. Contact-upgraded props flip to
            // Resim and skip this branch — the player still gets crisp local
            // physics on the bodies they're actually touching.
            //
            // _interpolateReconcileCount keeps its existing semantics — count
            // of blend writes — but it now scales with snapshot rate × prop
            // count, not with drift events. Tests that asserted "> 0" still
            // pass; tests that asserted a tight budget would need adjustment
            // (none currently exist).
            if (predictableEntity.EffectiveTier == PredictionTier.Interpolate)
            {
                _interpolateReconcileCount++;
                MonkeLogger.Debug($"[PRED-CHECK] tick={receivedSnapshot.Tick} eid={predictableEntity.EntityId} -> blend-reconcile (tier=Interpolate, |posDiff|={posDiff.Length():F4}m)");
                predictableEntity.HandleInterpolateReconciliation(authoritativeState);
                // ApplyAuthoritativeNonPoseState still runs so SyncSleepState
                // can mirror the server's sleep flag — but with the body
                // already at authoritative pose from the blend, SnapToRest
                // hits its sub-mm noop path and doesn't write the transform.
                predictableEntity.ApplyAuthoritativeNonPoseState(authoritativeState);
                continue;
            }

            // Resim tier: threshold-gated full-scene rollback. The 0.2 m / 1 m/s
            // gate exists because rollback re-simulates every body in the
            // physics space for _predictedStates.Count ticks — expensive enough
            // that sub-threshold drift is intentionally tolerated and absorbed
            // by PredictionVisualSmoothing3D on the visual mesh.
            if (predictableEntity.HasMisspredicted(receivedSnapshot.Tick, authoritativeState, predictedState))
            {
                _misspredictionsCount++;
                Vector3 authVel = predictableEntity.ExtractAuthoritativeVelocity(authoritativeState);
                Vector3 velDiff = authVel - predictedState.LinearVelocity;
                string cause = ClassifyMisprediction(velDiff, predictedState.LinearVelocity, networkDegraded);
                switch (cause)
                {
                    case "external-force":          _mispredictsExternalForce++; break;
                    case "physics-nondeterminism":  _mispredictsPhysicsNondeterminism++; break;
                    case "degraded-network":        _mispredictsDegradedNetwork++; break;
                }
                LogMispredictionThrottled(predictableEntity, predictedState, authoritativeState, receivedSnapshot.Tick, networkDegraded);
                MonkeLogger.Debug($"[PRED-CHECK] tick={receivedSnapshot.Tick} eid={predictableEntity.EntityId} MISPREDICTED -> rollback (tier=Resim, class={cause})");
                RollbackAndResimulate(receivedSnapshot.States, predictedStateData);
                return;
            }

            // Resim, below threshold — no body teleport needed; just sync
            // auxiliary state (sleep flag, etc.) from the snapshot.
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

        // Resolve newly-spawned ClientPredictedEntity objects once, up front. Used by
        // both the input replay and the post-step capture so a new entity that's not
        // yet a key in some older _predictedStates[i].Entities (because its
        // EntityEventMessage arrived after that tick's natural predict ran) still
        // gets its inputs replayed and its post-step state recorded. Iterating
        // remainingInput.Entities.Keys missed those ticks — the resim never replayed
        // their inputs and never wrote a post-step record, so a snapshot arriving
        // for those ticks would compare against a vacant slot.
        var newlySpawnedEntities = new List<ClientPredictedEntity>();
        foreach (ClientPredictedEntity predictableEntity in predictedStateData.Entities.Keys)
        {
            if (newlyInitializedIds.Contains(predictableEntity.EntityId))
                newlySpawnedEntities.Add(predictableEntity);
        }

        int otherCount = predictedStateData.Entities.Count - newlySpawnedEntities.Count;
        MonkeLogger.Debug($"[PRED-SPAWN-CATCHUP] tick={predictedStateData.Tick} newEntities={newlySpawnedEntities.Count} otherEntities={otherCount} resimTicks={_predictedStates.Count}");

        OfflineRigidbody3D.SnapshotAll();

        // Snapshot the live body state of every non-new predicted entity so we can
        // restore it after the catch-up resim. Jolt's SpaceStep is holistic — it
        // integrates every body in the space, including ones we don't want to
        // advance. Without this, each non-new entity emerges from the resim
        // resimTicks ahead of where natural prediction left it, and the next
        // PRED-REG captures that overshoot — producing a posDiff/velDiff against
        // the server's authoritative state for the same tick that trips
        // HasMisspredicted (tightly for owned entities, eventually for remote).
        // Restoring the pre-resim pose afterwards undoes the holistic integration.
        // Cost: bodies in sustained contact (stacked towers, players on a ramp)
        // have their persistent Jolt contact manifolds invalidated by the restore
        // and rebuild them on the next step — typically a sub-mm micro-bounce that
        // PredictionVisualSmoothing3D absorbs and that stays well under the
        // misprediction thresholds.
        Dictionary<ClientPredictedEntity, RigidbodyState> preResimPoses = null;
        foreach (ClientPredictedEntity predictableEntity in predictedStateData.Entities.Keys)
        {
            if (newlyInitializedIds.Contains(predictableEntity.EntityId)) continue;
            preResimPoses ??= new Dictionary<ClientPredictedEntity, RigidbodyState>();
            preResimPoses[predictableEntity] = predictableEntity.GetSnapshotState();
        }

        // Reconcile newly-spawned entities to their auth state at the rollback tick.
        foreach (ClientPredictedEntity predictableEntity in predictedStateData.Entities.Keys)
        {
            if (!newlyInitializedIds.Contains(predictableEntity.EntityId)) continue;
            var authoritativeState = FindStateForEntityId(predictableEntity.EntityId, authoritativeStates);
            MonkeLogger.Debug($"[PRED-RECONCILE] tick={predictedStateData.Tick} eid={predictableEntity.EntityId} -> auth={authoritativeState} (newly spawned)");
            predictableEntity.HandleReconciliation(authoritativeState);
        }

        int myId = ClientManager.Instance?.GetNetworkId() ?? 0;
        for (int i = 0; i < _predictedStates.Count; i++)
        {
            var remainingInput = _predictedStates[i];
            MonkeLogger.Debug($"[PRED-SPAWN-RESIM] resimTick={remainingInput.Tick} input={remainingInput.Input}");
            // Only re-apply inputs for the newly-spawned entities. Other entities were
            // already correctly stepped during natural predict; replaying their inputs
            // would double-apply them. Iterate the explicit newlySpawnedEntities list
            // (not remainingInput.Entities.Keys) so new entities that weren't yet a
            // key for this tick's natural predict still get resimulated.
            //
            // Per-entity input routing matches RollbackAndResimulate: the
            // locally-driven entity replays the local input recorded for this past
            // tick; any other newly-spawned entity (a remote player reconnecting
            // with the same EntityId, a server-owned passive prop) replays the
            // most recently-cached server input — or null, if none has arrived yet.
            // Without this, ResimulateTick on a remote player would receive the
            // LOCAL client's input and drive the remote body around with whatever
            // keys the local user happens to be holding.
            foreach (var predictableEntity in newlySpawnedEntities)
            {
                IPackableElement entityInput = (predictableEntity.Authority == myId)
                    ? remainingInput.Input
                    : ResolveRemoteInput(predictableEntity.EntityId, remainingInput.Tick);
                predictableEntity.ResimulateTick(entityInput);
            }

            PhysicsServer3D.SpaceStep(MonkeNetManager.Instance.PhysicsSpace, PhysicsUtils.DeltaTime);
            PhysicsServer3D.SpaceFlushQueries(MonkeNetManager.Instance.PhysicsSpace);

            // Capture post-step state for every newly-spawned entity into this tick's
            // record. The dictionary-index assignment creates the entry if missing,
            // so entities that weren't keys for this tick get backfilled — the next
            // live PRED-CHECK then compares against a populated slot instead of a
            // default(RigidbodyState). Non-new entities' recorded predicted states
            // stay at their original (correctly-predicted) values; their bodies are
            // restored to their pre-resim pose after the loop so subsequent PRED-REGs
            // match.
            foreach (var predictableEntity in newlySpawnedEntities)
            {
                var post = predictableEntity.GetSnapshotState();
                remainingInput.Entities[predictableEntity] = post;
                MonkeLogger.Debug($"[PRED-SPAWN-RESIM]   eid={predictableEntity.EntityId} postPos=({post.Position.X:F3},{post.Position.Y:F3},{post.Position.Z:F3}) postVel=({post.LinearVelocity.X:F3},{post.LinearVelocity.Y:F3},{post.LinearVelocity.Z:F3})");
            }
        }

        // Restore non-new entity body poses to their pre-resim state. The catch-up
        // resim's SpaceStep calls advanced these bodies by resimTicks of velocity-
        // driven motion they had no business undergoing twice; this puts them back.
        //
        // Gate on actual movement: if the resim didn't disturb the body (typical
        // for sleeping/resting bodies, which Jolt does not activate during a
        // SpaceStep), skip the restore. A "restore" to the current pose is a
        // no-op transform write that still invalidates Jolt's persistent contact
        // manifolds (see PredictionRigidbody3D.SnapToRest's docstring) — for a
        // body resting on the floor that produces the observed end-of-push
        // visual jitter on stacked bodies, with no benefit (the body is already
        // where we want it).
        if (preResimPoses != null)
        {
            const float RestorePosEpsilonSq = 1e-6f;    // (1 mm)²
            const float RestoreVelEpsilonSq = 1e-4f;    // (1 cm/s)²
            foreach (var (entity, savedState) in preResimPoses)
            {
                var postState = entity.GetSnapshotState();
                float posDeltaSq = (postState.Position - savedState.Position).LengthSquared();
                float velDeltaSq = (postState.LinearVelocity - savedState.LinearVelocity).LengthSquared();
                if (posDeltaSq < RestorePosEpsilonSq && velDeltaSq < RestoreVelEpsilonSq)
                {
                    MonkeLogger.Debug($"[PRED-SPAWN-RESTORE-SKIP] eid={entity.EntityId} body unchanged by resim (posΔ²={posDeltaSq:E2}m², velΔ²={velDeltaSq:E2}m²/s²)");
                    continue;
                }
                entity.RestoreBodyState(savedState);
                MonkeLogger.Debug($"[PRED-SPAWN-RESTORE] eid={entity.EntityId} pos=({savedState.Position.X:F3},{savedState.Position.Y:F3},{savedState.Position.Z:F3}) vel=({savedState.LinearVelocity.X:F3},{savedState.LinearVelocity.Y:F3},{savedState.LinearVelocity.Z:F3}) posΔ²={posDeltaSq:E2}m² velΔ²={velDeltaSq:E2}m²/s²");
            }
        }

        OfflineRigidbody3D.RestoreAll();
        MonkeLogger.Debug($"[PRED-SPAWN-CATCHUP] complete");
    }

    private void RollbackAndResimulate(IEntityStateData[] authoritativeStates, PredictedState predictedStateData)
    {
        bool isListenServer = MonkeNetManager.Instance != null && MonkeNetManager.Instance.IsServer;

        // Capture rollback depth before _predictedStates is mutated by the resim loop.
        // Depth = ticks of resim that the misprediction triggered = current local tick
        // − reconciled-from tick. Surfaced via LastRollbackDepth for quantitative tests
        // that build a P50/P95/P99 distribution across a scenario.
        _lastRollbackDepth = _predictedStates.Count;

        MonkeLogger.Debug($"[PRED-ROLLBACK] tick={predictedStateData.Tick} entities={predictedStateData.Entities.Count} resimTicks={_predictedStates.Count} listenServer={isListenServer}");

        // Snapshot non-networked rigidbodies so the resim's repeated SpaceStep calls
        // don't drift them. Restored after the loop. (Pure-client path only — the
        // listen-server branch short-circuits before SpaceStep, so no offline-body
        // restore is needed.)
        if (!isListenServer)
            OfflineRigidbody3D.SnapshotAll();

        // Snapshot each smoothed entity's PRE-reconcile visual POSITION so we
        // can re-anchor the smoother offset after the resim loop completes.
        // AbsorbBodyTeleport (called inside HandleReconciliation) sizes the
        // offset against the auth_pose, but the resim about to run moves the
        // body from auth_pose to post_resim_pose. Without the post-resim
        // fixup the offset is stale by (auth_pose − post_resim_pose) and the
        // visual teleports forward by exactly that gap on the next render.
        // See PredictionVisualSmoothing3D.FixupOffsetAfterResim for details
        // (including why only position — not rotation — gets re-anchored).
        Dictionary<ClientPredictedEntity, Vector3> preReconcileVisualPos = null;
        foreach (ClientPredictedEntity predictableEntity in predictedStateData.Entities.Keys)
        {
            var smoother = FindSmootherFor(predictableEntity);
            if (smoother?.Visual == null) continue;
            preReconcileVisualPos ??= new Dictionary<ClientPredictedEntity, Vector3>();
            preReconcileVisualPos[predictableEntity] = smoother.Visual.GlobalPosition;
        }

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

        // Advance simulation forward through every tick between the reconciled
        // tick and the current local tick. Iterating the TICK RANGE rather
        // than the list of registered PredictedState entries is load-bearing:
        // engine freezes (often caused by a previous reconcile's resim cost)
        // cause Godot to silently drop physics ticks via
        // `physics/common/max_physics_steps_per_frame`, after which
        // ClientNetworkClock jumps the local tick forward to align with
        // server time WITHOUT actually running the missed physics ticks.
        // That leaves `_predictedStates` with gaps — entries for ticks
        // [592, 612] with 593..611 absent, etc. If we iterated entries and
        // did one SpaceStep per entry, the resim would be short by the gap
        // size (15+ ticks worth of motion in observed C4 cases) and the
        // body would land 0.5–1.5m behind where the LIVE body had advanced
        // to by the same wall-clock moment. That stale post-resim pose then
        // becomes the body's render pose, while the smoother's
        // FixupOffsetAfterResim anchors the visual to the pre-reconcile
        // visual position — producing exactly the "visual stuck X metres
        // ahead of the rigidbody" artifact seen on the rigid-body player
        // during S7 C4.
        //
        // Iterating by tick lets us insert a SpaceStep for every missing
        // tick. For gap ticks we replay the local entity's most recent
        // input (best approximation of what the live tick would have used
        // had it not been dropped) and the server-cached input for remote
        // entities via the same ResolveRemoteInput path. Bodies for which
        // no PredictedState entry exists at the missing tick still get
        // their post-step pose written back into a fresh PredictedState
        // record so a later PRED-CHECK comparing that tick has a slot to
        // compare against.
        int myId = ClientManager.Instance?.GetNetworkId() ?? 0;
        int firstResimTick = predictedStateData.Tick + 1;
        int lastResimTick = _predictedStates.Count > 0
            ? _predictedStates[_predictedStates.Count - 1].Tick
            : firstResimTick - 1;
        var statesByTick = new Dictionary<int, PredictedState>(_predictedStates.Count);
        foreach (var ps in _predictedStates) statesByTick[ps.Tick] = ps;

        // Most-recent local input — used as the fall-back for the rare gap
        // tick that still slips through (ClientManager._PhysicsProcess now
        // back-fills engine-freeze-skipped ticks at predict time, so a
        // registered-state gap inside the rollback window should be vanishing-
        // rare; this synthesised-entry path is a safety net for that residual
        // case). Replays the last known held input — matches the GGPO /
        // QuakeWorld convention that "missing input == continue previous".
        IPackableElement lastLocalInput = null;
        if (_predictedStates.Count > 0) lastLocalInput = _predictedStates[0].Input;

        // Reference entity set: the union of all entities that ever appeared
        // in a registered state for this rollback. Used to populate freshly
        // synthesised PredictedState entries for the gap ticks.
        var referenceEntities = predictedStateData.Entities.Keys;

        for (int tick = firstResimTick; tick <= lastResimTick; tick++)
        {
            statesByTick.TryGetValue(tick, out var stateAtTick);
            if (stateAtTick == null)
            {
                // Gap tick — synthesise a PredictedState so the post-step
                // record gets captured (otherwise a future snapshot for this
                // tick would see no matching predicted entry and route to
                // MISSED-LOCAL-STATE instead of reconciling). Local input
                // replays the last known held input.
                stateAtTick = new PredictedState
                {
                    Tick = tick,
                    Input = lastLocalInput,
                    Entities = new Dictionary<ClientPredictedEntity, RigidbodyState>(),
                };
                statesByTick[tick] = stateAtTick;
                _predictedStates.Add(stateAtTick);
                MonkeLogger.Debug($"[PRED-RESIM-FILLGAP] tick={tick} synthesised entry for resim gap (replayed last local input={lastLocalInput})");
            }
            else if (stateAtTick.Input != null)
            {
                lastLocalInput = stateAtTick.Input;
            }

            MonkeLogger.Debug($"[PRED-RESIM] resimTick={tick} entities={referenceEntities.Count} input={stateAtTick.Input}");
            foreach (ClientPredictedEntity predictableEntity in referenceEntities)
            {
                // Per-entity input routing identical to the original loop —
                // local entity replays the LOCAL input we recorded for this
                // tick (or the last-known input for synthesised gap ticks);
                // other entities replay the server input the snapshot history
                // records for this specific tick. ResolveRemoteInput already
                // falls back to the closest preceding cached tick when an
                // exact match is unavailable, so passive props and remote
                // players get the same input the server would have applied.
                IPackableElement entityInput = (predictableEntity.Authority == myId)
                    ? stateAtTick.Input
                    : ResolveRemoteInput(predictableEntity.EntityId, tick);
                predictableEntity.ResimulateTick(entityInput);
            }

            PhysicsServer3D.SpaceStep(MonkeNetManager.Instance.PhysicsSpace, PhysicsUtils.DeltaTime);
            PhysicsServer3D.SpaceFlushQueries(MonkeNetManager.Instance.PhysicsSpace);

            foreach (ClientPredictedEntity predictableEntity in referenceEntities)
            {
                var post = predictableEntity.GetSnapshotState();
                stateAtTick.Entities[predictableEntity] = post;
                MonkeLogger.Debug($"[PRED-RESIM]   eid={predictableEntity.EntityId} postPos=({post.Position.X:F3},{post.Position.Y:F3},{post.Position.Z:F3}) postVel=({post.LinearVelocity.X:F3},{post.LinearVelocity.Y:F3},{post.LinearVelocity.Z:F3})");
            }
        }

        // _predictedStates may have grown out-of-order (we Add'd synthesised
        // entries at the end). Resort by Tick so a subsequent snapshot's
        // Find(tick) still hits the right index and any future iteration of
        // the list iterates in tick order. Stable sort isn't necessary —
        // (tick, input) pairs are unique by construction.
        _predictedStates.Sort((a, b) => a.Tick.CompareTo(b.Tick));

        OfflineRigidbody3D.RestoreAll();

        // Re-pump SceneTreeFTI prev=curr on every reconciled body, including
        // those without a wired smoother (e.g. LocalBall — no Visual node, so
        // no FixupOffsetAfterResim runs for it). Without this, the body's
        // local_transform_prev is still the auth_pose set by
        // PredictionRigidbody3D.Reconcile BEFORE the resim ran, while
        // local_transform_curr is the post_resim pose — so the renderer lerps
        // the body backward across child mesh chains during the render
        // frames following this tick. Visible on the balls' meshes as the
        // sliding-backwards artifact the user just reported, separate from
        // the rigid player one fixed by FixupOffsetAfterResim. Safe per
        // <see cref="PredictionRigidbody3D.ResetBodyFtiAfterResim"/> — pure
        // FTI tracking update, no PhysicsServer3D call, no contact-cache
        // invalidation.
        foreach (ClientPredictedEntity predictableEntity in predictedStateData.Entities.Keys)
        {
            var rb = FindRigidbodyFor(predictableEntity);
            rb?.ResetBodyFtiAfterResim();
        }

        // Re-anchor each smoother's offset against the post-resim body pose.
        // The offset captured by AbsorbBodyTeleport during HandleReconciliation
        // was sized against the auth_pose (rollback target); the resim loop
        // above moved the body to post_resim_pose. Without this re-anchor the
        // visual would teleport forward by (auth_pose − post_resim_pose) on
        // the next render frame — exactly the multi-metre visual jumps seen
        // around ticks 634/659/683/697/708/721/782/798 in S7-C4.
        if (preReconcileVisualPos != null)
        {
            foreach (var (predictableEntity, prePos) in preReconcileVisualPos)
            {
                var smoother = FindSmootherFor(predictableEntity);
                smoother?.FixupOffsetAfterResim(prePos);
            }
        }

        MonkeLogger.Debug($"[PRED-ROLLBACK] complete (offline bodies restored)");
    }

    /// <summary>Snap every locally-known predicted entity to its
    /// authoritative state from <paramref name="snapshot"/>, skipping the
    /// resim loop entirely. Called when the snapshot tick is older than the
    /// rollback cap allows — see the call site in
    /// <see cref="ProcessServerState"/> for the rationale.</summary>
    private void SnapToAuthOverflow(GameSnapshotMessage snapshot)
    {
        int snapped = 0;
        if (EntitySpawner.Instance != null)
        {
            foreach (var entity in EntitySpawner.Instance.ClientEntities)
            {
                var cpe = entity.GetComponent<ClientPredictedEntity>();
                if (cpe == null) continue;
                var authState = FindStateForEntityId(cpe.EntityId, snapshot.States);
                if (authState == null) continue;
                // Log per-entity auth pose so post-hoc analysis can plot the
                // server-truth trajectory alongside the client-side render.
                Vector3 authPos = cpe.ExtractAuthoritativePosition(authState);
                Vector3 authVel = cpe.ExtractAuthoritativeVelocity(authState);
                MonkeLogger.Debug($"[PRED-SNAP-OVERFLOW-ENTITY] tick={snapshot.Tick} eid={cpe.EntityId} authPos=({authPos.X:F3},{authPos.Y:F3},{authPos.Z:F3}) authVel=({authVel.X:F3},{authVel.Y:F3},{authVel.Z:F3})");
                // HandleReconciliation writes body pose+velocity to the auth
                // state AND calls AbsorbBodyTeleport on the smoother, so the
                // visual stays at its pre-snap pose and decays over DecayTime.
                cpe.HandleReconciliation(authState);
                // Pin FTI prev=curr to the new pose so the renderer doesn't
                // interpolate backward across the snap.
                var rb = FindRigidbodyFor(cpe);
                rb?.ResetBodyFtiAfterResim();
                snapped++;
            }
        }
        _snapOverflowCount++;
        MonkeLogger.Debug($"[PRED-SNAP-OVERFLOW] tick={snapshot.Tick} entities={snapped} (snapshot too old for resim; snapped to auth pose without forward resim, total snaps={_snapOverflowCount})");
    }

    private static PredictionRigidbody3D FindRigidbodyFor(ClientPredictedEntity entity)
    {
        if (entity == null) return null;
        Node root = EntitySpawner.Instance?.GetEntityRoot(entity);
        if (root == null) return null;
        return FindFirstRigidbody(root);
    }

    private static PredictionRigidbody3D FindFirstRigidbody(Node node)
    {
        if (node is PredictionRigidbody3D rb) return rb;
        foreach (Node child in node.GetChildren())
        {
            var found = FindFirstRigidbody(child);
            if (found != null) return found;
        }
        return null;
    }

    private static PredictionVisualSmoothing3D FindSmootherFor(ClientPredictedEntity entity)
    {
        if (entity == null) return null;
        Node root = EntitySpawner.Instance?.GetEntityRoot(entity);
        if (root == null) return null;
        return FindFirstSmoother(root);
    }

    private static PredictionVisualSmoothing3D FindFirstSmoother(Node node)
    {
        if (node is PredictionVisualSmoothing3D s) return s;
        foreach (Node child in node.GetChildren())
        {
            var found = FindFirstSmoother(child);
            if (found != null) return found;
        }
        return null;
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

    private void LogMispredictionThrottled(ClientPredictedEntity entity, RigidbodyState predictedState, IEntityStateData authoritativeState, int tick, bool networkDegraded)
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
        Vector3 authVel = entity.ExtractAuthoritativeVelocity(authoritativeState);
        Quaternion authRot = entity.ExtractAuthoritativeRotation(authoritativeState);
        Vector3 posDiff = authPos - predictedState.Position;
        Vector3 velDiff = authVel - predictedState.LinearVelocity;
        // Rotation diff as axis-angle (degrees). When neither |posDiff| nor
        // |velDiff| trips the documented threshold but a reconcile still fires,
        // it's the rotation threshold; without this in the log the reader has
        // to guess which threshold tripped.
        float rotDiffDeg = Mathf.RadToDeg(authRot.AngleTo(predictedState.Rotation));
        Quaternion rotDiffQuat = authRot * predictedState.Rotation.Inverse();
        // Axis from a unit quaternion: (x, y, z) / sin(angle/2). Guard against
        // a near-identity rotation where sin(angle/2)≈0 makes the axis
        // arbitrary — report the Y axis as a safe default.
        Vector3 rotAxis = Vector3.Up;
        float sinHalf = Mathf.Sqrt(Mathf.Max(0f, 1f - rotDiffQuat.W * rotDiffQuat.W));
        if (sinHalf > 0.0001f)
            rotAxis = new Vector3(rotDiffQuat.X, rotDiffQuat.Y, rotDiffQuat.Z) / sinHalf;
        // Tag which check triggered the reconcile. Useful for distinguishing
        // "position drifted past tolerance" from "rotation tumbled enough to
        // exceed the 5° budget while pose-and-vel still agree". The classifier
        // already produces a [cause] label for the physics-classification
        // dimension (external-force vs nondeterminism vs degraded-network);
        // this adds the orthogonal dimension of WHICH threshold check fired.
        // Delegated to the entity (rather than a hardcoded threshold table)
        // so entities with custom thresholds — notably the rigid-body player,
        // whose velocity threshold is tighter than the prop default — report
        // correctly. Hardcoded fallback would produce "below-thresholds" when
        // the player's tighter check actually fired.
        string trippedBy = entity.DescribeMispredictTrigger(authoritativeState, predictedState);
        string cause = ClassifyMisprediction(velDiff, predictedState.LinearVelocity, networkDegraded);
        MonkeLogger.Info($"Misprediction [{cause} trippedBy={trippedBy}]: entity {entity.EntityId} type {entity.EntityType} tick {tick} predicted {predictedState.Position} authoritative {authPos} diff {posDiff} |diff|={posDiff.Length():F3}m velDiff {velDiff} |velDiff|={velDiff.Length():F3}m/s rotDiff={rotDiffDeg:F2}° axis=({rotAxis.X:F2},{rotAxis.Y:F2},{rotAxis.Z:F2})");
    }

    // Hardcoded ClassifyTrippedThreshold removed — see
    // ClientPredictedEntity.DescribeMispredictTrigger. Each entity overrides
    // with its own actual threshold values so the trippedBy tag is correct
    // for entities with non-default thresholds (the rigid-body player has a
    // tighter velocity threshold than the prop default).

    // Bounce-timing guard threshold for the classifier — squared form (~2 m/s magnitude).
    // A vertical-velocity-dominated residual on a falling body with |velDiff| below this
    // cap is treated as cross-process Jolt contact-resolution timing skew, not an external
    // force. Above the cap the magnitude is large enough that a genuine vertical impulse
    // (jump pad, server-side knockback) is the more likely explanation.
    private const float BounceTimingVelMagSquaredCap = 4f;

    // Heuristic cause label for misprediction log messages, matching the known scenario table:
    //   degraded-network  — prediction buffer was trimmed before this snapshot arrived,
    //                       indicating snapshot packet-loss or an RTT spike (server used stale input).
    //   external-force    — velocity residual exceeds threshold AND the residual doesn't
    //                       match the bounce-timing signature; the server applied an
    //                       impulse the client didn't replay (remote-player collision,
    //                       server-side knockback). Position diff alone is ambiguous:
    //                       a 0.5 m Y-axis pos delta with matching velocity is just N
    //                       ticks of accumulated gravity drift, not a force the client
    //                       missed. The velocity residual is the load-bearing signal.
    //   physics-nondeterminism — either a small velocity diff (Jolt cross-process float
    //                       divergence accumulating over time, expected in solo or
    //                       low-contact play), OR a vertical-velocity-dominated residual
    //                       on a body the client still has in free-fall (bounce-timing
    //                       skew — server's contact normal fired one tick earlier than
    //                       the client's, producing a sub-2-m/s vertical velocity gap
    //                       that resolves on the next tick).
    private static string ClassifyMisprediction(Vector3 velDiff, Vector3 predictedVel, bool networkDegraded)
    {
        if (networkDegraded)
            return "degraded-network";

        // Bounce-timing guard. Without this, every bouncing ball trips external-force
        // on the snapshot tick where the server's contact normal fires before the
        // client's — the residual is purely on the gravity axis, ≤ 2 m/s, and the
        // predicted body is still in free-fall. Real impulses produce horizontal
        // residual components or magnitudes far above 2 m/s.
        float vertSq = velDiff.Y * velDiff.Y;
        float horizSq = velDiff.X * velDiff.X + velDiff.Z * velDiff.Z;
        bool verticalDominated = vertSq > 4f * horizSq;
        bool predictedFalling = predictedVel.Y < 0f;
        bool boundedMagnitude = velDiff.LengthSquared() < BounceTimingVelMagSquaredCap;
        if (verticalDominated && predictedFalling && boundedMagnitude)
            return "physics-nondeterminism";

        if (velDiff.LengthSquared() >= ExternalForceVelThresholdSquared)
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
