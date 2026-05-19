using Godot;
using ImGuiNET;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Shared;
using System.Collections.Generic;

namespace MonkeNet.Server;

[GlobalClass]
public partial class ServerInputReceiver : InternalServerComponent
{
    // After a client stops sending input, the last input is repeated for this many seconds
    // (so brief packet loss is masked) and then default-valued inputs are used so a
    // disconnected client doesn't keep moving forever.
    [Export] public float StaleInputTimeoutSec { get; set; } = 1.0f;

    /// <summary>
    /// Hard cap on how many ticks of unconsumed input may be queued per entity. Above
    /// this, the oldest queued ticks are evicted. Protects the server from a client
    /// (malicious or buggy) that floods far-future inputs and would otherwise grow the
    /// pending dictionary without bound. Default ≈ 60 ticks (1s @ 60Hz) — well past any
    /// reasonable client lookahead, but still bounded.
    /// </summary>
    [Export] public int MaximumServerReplicates { get; set; } = 60;

    private readonly Dictionary<int, Dictionary<NetworkBehaviour, IPackableElement>> _pendingInputs = [];
    private readonly Dictionary<NetworkBehaviour, IPackableElement> _lastInputStored = [];
    private readonly Dictionary<NetworkBehaviour, int> _lastReceivedTick = [];
    private readonly Dictionary<NetworkBehaviour, IPackableElement> _defaultInputCache = [];

    // Side index of which ticks each entity currently has queued, for O(log n) eviction
    // of the oldest tick when MaximumServerReplicates is exceeded.
    private readonly Dictionary<NetworkBehaviour, SortedSet<int>> _pendingTicksPerEntity = [];

    private int _trimmedInputTotal = 0;

    private int _missedInput = 0;
    private readonly Dictionary<int, int> _missedInputTotal = [];
    private readonly Dictionary<int, Queue<bool>> _missedInputWindow = [];
    private const int MissedInputWindowSize = 64;

    public IPackableElement GetInputForEntityTick(NetworkBehaviour serverEntity, int tick)
    {
        bool received;
        IPackableElement result;
        string source;

        if (_pendingInputs.TryGetValue(tick, out var tickInputs)
            && tickInputs.TryGetValue(serverEntity, out result))
        {
            _lastInputStored[serverEntity] = result;
            _lastReceivedTick[serverEntity] = tick;
            if (!_defaultInputCache.ContainsKey(serverEntity))
                _defaultInputCache[serverEntity] = (IPackableElement)System.Activator.CreateInstance(result.GetType());
            received = true;
            source = "received";
        }
        else
        {
            _missedInput++;
            received = false;

            int maxStaleTicks = (int)(StaleInputTimeoutSec * Engine.PhysicsTicksPerSecond);
            int staleTicks = _lastReceivedTick.TryGetValue(serverEntity, out int last)
                ? tick - last
                : int.MaxValue;

            if (staleTicks <= maxStaleTicks
                && _lastInputStored.TryGetValue(serverEntity, out IPackableElement repeat))
            {
                result = repeat;
                source = $"repeat(stale={staleTicks})";
            }
            else
            {
                _defaultInputCache.TryGetValue(serverEntity, out result);
                source = "default";
            }
            // Critical: keep _lastInputStored in sync with the input ACTUALLY
            // APPLIED this tick — not just the last received one. PackSnapshot
            // calls GetLastInputFor() to fill GameSnapshotMessage.Inputs[],
            // and observers cache that and re-apply it for their local
            // prediction of the entity. Without this assignment, when an
            // owner stops sending input and the server falls back to "default"
            // after the stale timeout, the server applies (move=0) locally
            // but the snapshot keeps reporting the LAST RECEIVED input
            // (e.g. move=(1,0)) forever — so every observer's prediction
            // accelerates the body each resim tick while the server keeps
            // it stopped, producing a constant stream of mispredictions
            // whenever the snapshot arrives (observed as "constant mispredicts
            // when a player changes direction" — i.e. when they stop pressing
            // keys after a directional change).
            _lastInputStored[serverEntity] = result;
        }
        MonkeLogger.Debug($"[NET-INPUT-CONSUME] tick={tick} eid={serverEntity.EntityId} authority={serverEntity.Authority} source={source} input={result?.ToString() ?? "null"}");

        int authority = serverEntity.Authority;
        _missedInputTotal.TryAdd(authority, 0);
        _missedInputWindow.TryAdd(authority, new Queue<bool>());
        if (!received) _missedInputTotal[authority]++;
        var window = _missedInputWindow[authority];
        window.Enqueue(received);
        if (window.Count > MissedInputWindowSize) window.Dequeue();

        return result;
    }

    /// <summary>
    /// Returns the most recent input the server processed for <paramref name="serverEntity"/>,
    /// or null if no input has ever been consumed (e.g. server-authoritative passive prop).
    /// Used by <see cref="ServerEntityManager"/> when packing the snapshot so observers
    /// receive each entity's owner-supplied input and can apply it to their local
    /// prediction — without it, observers can only coast at last-known velocity and
    /// reconcile against every snapshot.
    /// </summary>
    public IPackableElement GetLastInputFor(NetworkBehaviour serverEntity)
    {
        return _lastInputStored.TryGetValue(serverEntity, out var input) ? input : null;
    }

    public int GetMissedInputTotal(int clientId) =>
        _missedInputTotal.TryGetValue(clientId, out int v) ? v : 0;

    public float GetMissedInputRate(int clientId)
    {
        if (!_missedInputWindow.TryGetValue(clientId, out var w) || w.Count == 0) return 0f;
        int missed = 0;
        foreach (bool received in w) if (!received) missed++;
        return missed / (float)w.Count;
    }

    protected override void OnCommandReceived(int clientId, IPackableMessage command)
    {
        if (command is not PackedClientInputMessage inputCommand)
            return;

        // Find the ServerEntity target for this input command
        foreach (var entity in MonkeNetManager.Instance.EntitySpawner.Entities)
        {
            if (entity is NetworkBehaviour serverEntity && clientId == serverEntity.Authority)
            {
                RegisterCommand(serverEntity, inputCommand);
            }
        }
    }

    private void RegisterCommand(NetworkBehaviour serverEntity, PackedClientInputMessage inputCommand)
    {
        MonkeLogger.Debug($"[NET-INPUT-RX] eid={serverEntity.EntityId} authority={serverEntity.Authority} latestTick={inputCommand.Tick} batch={inputCommand.Inputs.Length}");
        int offset = inputCommand.Inputs.Length - 1;
        foreach (IPackableElement input in inputCommand.Inputs)
        {
            int tick = inputCommand.Tick - (offset--);

            // Check if we have an entry for this tick
            if (!_pendingInputs.TryGetValue(tick, out Dictionary<NetworkBehaviour, IPackableElement> value))
            {
                value = ([]);
                _pendingInputs.Add(tick, value);
            }

            if (value.TryAdd(serverEntity, input))
            {
                MonkeLogger.Debug($"[NET-INPUT-RX]   tick={tick} input={input}");
                if (!_pendingTicksPerEntity.TryGetValue(serverEntity, out var ticks))
                {
                    ticks = new SortedSet<int>();
                    _pendingTicksPerEntity[serverEntity] = ticks;
                }
                ticks.Add(tick);
            }
        }

        EnforceCap(serverEntity);
    }

    private void EnforceCap(NetworkBehaviour serverEntity)
    {
        if (!_pendingTicksPerEntity.TryGetValue(serverEntity, out var ticks)) return;
        while (ticks.Count > MaximumServerReplicates)
        {
            int oldest = ticks.Min;
            ticks.Remove(oldest);
            if (_pendingInputs.TryGetValue(oldest, out var perTick))
            {
                perTick.Remove(serverEntity);
                if (perTick.Count == 0) _pendingInputs.Remove(oldest);
            }
            _trimmedInputTotal++;
        }
    }

    public void DropOutdatedInputs(int currentTick)
    {
        // Materialise the keys list first — modifying _pendingInputs while enumerating
        // its Keys collection throws InvalidOperationException.
        var stale = new List<int>();
        foreach (int key in _pendingInputs.Keys)
        {
            if (key <= currentTick) stale.Add(key);
        }
        foreach (int key in stale)
        {
            _pendingInputs.Remove(key);
        }

        // Trim the per-entity tick index too so EnforceCap doesn't think long-consumed
        // ticks are still in flight.
        foreach (var ticks in _pendingTicksPerEntity.Values)
        {
            while (ticks.Count > 0 && ticks.Min <= currentTick) ticks.Remove(ticks.Min);
        }
    }

    public int GetTrimmedInputTotal() => _trimmedInputTotal;

    public void DisplayDebugInformation()
    {
        if (ImGui.CollapsingHeader("Input Receiver"))
        {
            ImGui.Text($"Input Queue {_pendingInputs.Count}");
            ImGui.Text($"Missed Inputs {_missedInput}");
            ImGui.Text($"Trimmed (over cap) {_trimmedInputTotal}");
        }
    }
}
