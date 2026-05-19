using Godot;
using ImGuiNET;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Shared;
using System.Collections.Generic;

namespace MonkeNet.Server;

/// <summary>
/// Tracks each connected peer's <see cref="ClientPersistentIdentity"/>
/// (announced by the client via <see cref="ClientHelloMessage"/> on connect),
/// applies the configured entity-retention mode (RemoveEntity / KeepEntity)
/// on disconnect, and parks entities of timed-out / disconnected clients in
/// a reclaim table keyed by that persistent id so any subsequent reconnect
/// from the same identity automatically recovers ownership.
///
/// The reclaim table is a <i>lookup</i>, not a one-shot consume: the same
/// <c>ClientPersistentId</c> can disconnect+reconnect many times, picking up
/// its entities each time. Entries are removed only when (a) the matching
/// client reconnects and Authority is reassigned away from 0, or (b) the
/// <see cref="ReclaimExpirySec"/> timeout fires.
///
/// ENet remains authoritative for detecting dropped clients:
/// <c>enet_peer_timeout</c> fires <c>EVENT_DISCONNECT</c>, which
/// <c>ENetMultiplayerPeer</c> turns into <c>peer_disconnected</c>.
/// </summary>
[GlobalClass]
public partial class ServerConnectionMonitor : InternalServerComponent
{
    // KeepEntity by default so a transient ENet timeout doesn't destroy the player's
    // entities — the reclaim entry indexed by ClientPersistentId lets a reconnecting
    // client recover them.
    [Export] public DisconnectEntityMode TimeoutDisconnectMode { get; set; } = DisconnectEntityMode.KeepEntity;
    [Export] public DisconnectEntityMode ManualDisconnectMode { get; set; } = DisconnectEntityMode.KeepEntity;

    // After this many seconds, an unclaimed reclaim entry is dropped and its orphaned
    // entities are destroyed. Without this, _reclaimableEntities leaks indefinitely on
    // long-running servers when clients never reconnect to claim their entities.
    [Export] public float ReclaimExpirySec { get; set; } = 180f;

    // Soft warning threshold for a connected client going silent. Purely informational —
    // ENet remains the disconnect authority. Mirrors ClientConnectionMonitor.SilenceThresholdSec.
    [Export] public float ClientSilenceThresholdSec { get; set; } = 3f;

    public ITimestampProvider TimestampProvider { get; set; } = new RealTimestampProvider();

    private readonly HashSet<int> _voluntaryDisconnects = new();
    // Map ENet peer netId → client's persistent identity (sent in ClientHelloMessage).
    // Populated when the hello message arrives; consulted on disconnect to choose the
    // reclaim entry key.
    private readonly Dictionary<int, string> _clientIdByPeer = new();
    // Reclaim entries indexed by ClientPersistentId. While a client is connected they
    // have no entry; on disconnect an entry is created with the entity ids that were
    // orphaned. On the same identity's next reconnect we look up the entry, reassign
    // those entities' Authority back to the new peer netId, then remove the entry.
    private readonly Dictionary<string, ReclaimEntry> _reclaimableEntities = new();
    private readonly Dictionary<int, ulong> _lastClientMessageMsec = new();
    private readonly HashSet<int> _silenceWarnedClients = new();

    private class ReclaimEntry
    {
        public List<int> EntityIds;
        public ulong CreatedAtMsec;
    }

    protected override void OnClientConnected(int clientId)
    {
        // No server-issued identity any more. We log a placeholder and wait
        // for the client's ClientHelloMessage to arrive on the reliable
        // channel; once it does, OnCommandReceived stores the mapping and
        // performs the reclaim lookup.
        _lastClientMessageMsec[clientId] = TimestampProvider.GetTicksMsec();
        MonkeLogger.Info($"ServerConnectionMonitor: peer {clientId} connected — awaiting ClientHelloMessage");
    }

    protected override void OnCommandReceived(int clientId, IPackableMessage command)
    {
        _lastClientMessageMsec[clientId] = TimestampProvider.GetTicksMsec();
        if (_silenceWarnedClients.Remove(clientId))
        {
            string tok = _clientIdByPeer.TryGetValue(clientId, out var t) ? Tok(t) : "????";
            MonkeLogger.Info($"ServerConnectionMonitor: client {clientId} responded after silence [cid:{tok}]");
        }

        if (command is ClientHelloMessage hello)
        {
            HandleClientHello(clientId, hello.ClientId);
            return;
        }

        if (command is DisconnectNotificationMessage)
        {
            _voluntaryDisconnects.Add(clientId);
            string tok = _clientIdByPeer.TryGetValue(clientId, out var t) ? Tok(t) : "????";
            MonkeLogger.Info($"ServerConnectionMonitor: client {clientId} sent voluntary disconnect notification [cid:{tok}]");
        }
    }

    private void HandleClientHello(int peerId, string persistentId)
    {
        if (string.IsNullOrWhiteSpace(persistentId))
        {
            MonkeLogger.Warn($"ServerConnectionMonitor: peer {peerId} sent empty ClientPersistentId — ignoring hello");
            return;
        }

        // Defend against the same persistent id being held by two simultaneously
        // connected peers (manual cheat, dev running two clients with the same
        // CLI override, …). If we see the id already mapped to a different
        // peer, the more recent peer wins and the previous mapping is dropped —
        // but we leave the actual entities at their current Authority. Logged
        // loudly so it can't be silent.
        string existingPeerForId = null;
        foreach (var (peer, id) in _clientIdByPeer)
        {
            if (peer != peerId && id == persistentId)
            {
                existingPeerForId = $"peer {peer}";
                break;
            }
        }
        if (existingPeerForId != null)
        {
            MonkeLogger.Warn($"ServerConnectionMonitor: ClientPersistentId ...{Tok(persistentId)} is already held by {existingPeerForId}; " +
                             $"peer {peerId}'s hello overrides the mapping (the older peer keeps its current entities until it disconnects)");
        }

        _clientIdByPeer[peerId] = persistentId;
        MonkeLogger.Info($"ServerConnectionMonitor: peer {peerId} identified as [cid:{Tok(persistentId)}]");

        // Look up any reclaim entry for this identity. If we find one, hand
        // back the entities by reassigning Authority. The entry is removed
        // once consumed — but the persistent id remains valid, so a future
        // disconnect of this same client creates a new entry under the same
        // key, and a subsequent reconnect can reclaim again.
        if (_reclaimableEntities.TryGetValue(persistentId, out var entry))
        {
            var entityManager = ServerManager.Instance.EntityManager;
            int reclaimed = 0;
            foreach (int entityId in entry.EntityIds)
            {
                var ent = entityManager.FindEntityById(entityId);
                if (ent == null) continue;
                if (ent.Authority != 0)
                {
                    // Someone else (or a stale orphan path) already owns it; skip
                    // rather than yank ownership from a live client.
                    MonkeLogger.Warn($"ServerConnectionMonitor: skipping reclaim of entity {entityId} for [cid:{Tok(persistentId)}] — currently owned by {ent.Authority}");
                    continue;
                }
                entityManager.ChangeAuthority(entityId, peerId);
                reclaimed++;
            }
            _reclaimableEntities.Remove(persistentId);
            MonkeLogger.Info($"ServerConnectionMonitor: reclaimed {reclaimed}/{entry.EntityIds.Count} entities for peer {peerId} [cid:{Tok(persistentId)}]");
        }
    }

    protected override void OnClientDisconnected(int clientId)
    {
        bool wasVoluntary = _voluntaryDisconnects.Remove(clientId);
        DisconnectEntityMode mode = wasVoluntary ? ManualDisconnectMode : TimeoutDisconnectMode;
        var entityManager = ServerManager.Instance.EntityManager;

        if (mode == DisconnectEntityMode.KeepEntity
            && _clientIdByPeer.TryGetValue(clientId, out string persistentId))
        {
            var entityIds = entityManager.GetEntityIdsForClient(clientId);
            if (entityIds.Count > 0)
            {
                _reclaimableEntities[persistentId] = new ReclaimEntry
                {
                    EntityIds = entityIds,
                    CreatedAtMsec = TimestampProvider.GetTicksMsec(),
                };
            }
            entityManager.OrphanEntitiesForClient(clientId);
            MonkeLogger.Info($"ServerConnectionMonitor: peer {clientId} entities orphaned, parked under [cid:{Tok(persistentId)}] ({entityIds.Count} entities)");
        }
        else
        {
            string tok = _clientIdByPeer.TryGetValue(clientId, out var t) ? Tok(t) : "????";
            entityManager.DestroyEntitiesForClient(clientId);
            MonkeLogger.Info($"ServerConnectionMonitor: peer {clientId} entities destroyed [cid:{tok}] (mode={mode}, voluntary={wasVoluntary})");
        }

        _clientIdByPeer.Remove(clientId);
        _lastClientMessageMsec.Remove(clientId);
        _silenceWarnedClients.Remove(clientId);
    }

    private static string Tok(string token) =>
        ClientPersistentIdentity.Tail(token);

    /// <summary>Test / introspection helper — returns the persistent
    /// client id currently mapped to a peer netId, or null if the peer
    /// hasn't sent its hello yet (or has disconnected).</summary>
    public string GetClientPersistentId(int peerId) =>
        _clientIdByPeer.TryGetValue(peerId, out var id) ? id : null;

    /// <summary>Test / introspection helper — returns true if there's a
    /// parked reclaim entry waiting for the given persistent id (i.e. the
    /// client disconnected and hasn't reconnected yet).</summary>
    public bool HasPendingReclaimFor(string persistentId) =>
        persistentId != null && _reclaimableEntities.ContainsKey(persistentId);

    // Runs every server network tick: sweeps expired reclaim entries and emits soft
    // warnings for silent clients before ENet drops them.
    protected override void OnNetworkProcessTick(int currentTick)
    {
        ulong now = TimestampProvider.GetTicksMsec();
        SweepExpiredReclaimEntries(now);
        CheckClientSilence(now);
    }

    private void SweepExpiredReclaimEntries(ulong now)
    {
        if (_reclaimableEntities.Count == 0) return;

        ulong expiryMs = (ulong)(ReclaimExpirySec * 1000f);

        List<string> expired = null;
        foreach (var (key, entry) in _reclaimableEntities)
        {
            if (now - entry.CreatedAtMsec > expiryMs)
            {
                expired ??= new List<string>();
                expired.Add(key);
            }
        }

        if (expired == null) return;

        var entityManager = ServerManager.Instance.EntityManager;
        foreach (string key in expired)
        {
            var entry = _reclaimableEntities[key];
            foreach (int entityId in entry.EntityIds)
                entityManager.DestroyOrphanedEntity(entityId);
            _reclaimableEntities.Remove(key);
            MonkeLogger.Info($"ServerConnectionMonitor: reclaim entry expired [cid:{Tok(key)}], orphaned entities destroyed");
        }
    }

    private void CheckClientSilence(ulong now)
    {
        if (_lastClientMessageMsec.Count == 0) return;
        ulong thresholdMs = (ulong)(ClientSilenceThresholdSec * 1000f);
        foreach (var (clientId, lastMsec) in _lastClientMessageMsec)
        {
            if (now - lastMsec <= thresholdMs) continue;
            if (!_silenceWarnedClients.Add(clientId)) continue;
            string tok = _clientIdByPeer.TryGetValue(clientId, out var t) ? Tok(t) : "????";
            MonkeLogger.Warn($"ServerConnectionMonitor: client {clientId} silent for {(now - lastMsec) / 1000f:F1}s [cid:{tok}]");
        }
    }

    public void DisplayDebugInformation(ServerInputReceiver inputReceiver)
    {
        if (!ImGui.CollapsingHeader("Connected Players")) return;
        var peers = ServerManager.Instance.GetConnectedPeerIds();
        bool any = false;
        foreach (int peerId in peers)
        {
            any = true;
            int rtt = ServerManager.Instance.GetPeerRtt(peerId);
            int missed = inputReceiver.GetMissedInputTotal(peerId);
            float rate = inputReceiver.GetMissedInputRate(peerId) * 100f;
            string tok = _clientIdByPeer.TryGetValue(peerId, out var t) ? Tok(t) : "----";
            ImGui.TextUnformatted($"Peer {peerId} [cid:{tok}]:  RTT {rtt} ms   Missed {missed} total  ({rate:0.0}% last 64)");
        }
        if (!any) ImGui.Text("No players connected");
    }
}
