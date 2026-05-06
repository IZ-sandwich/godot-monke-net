using Godot;
using ImGuiNET;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Shared;
using System.Collections.Generic;

namespace MonkeNet.Server;

/// <summary>
/// Issues a server-generated session token to each client on connect, applies the
/// configured entity-retention mode (RemoveEntity or KeepEntity) on disconnect,
/// and stores reclaim tokens so reconnecting clients can recover their entities.
///
/// ENet itself is authoritative for detecting dropped clients: enet_peer_timeout
/// fires EVENT_DISCONNECT, which ENetMultiplayerPeer turns into peer_disconnected.
/// </summary>
[GlobalClass]
public partial class ServerConnectionMonitor : InternalServerComponent
{
    // KeepEntity by default so a transient ENet timeout doesn't destroy the player's
    // entities — the reclaim token issued at connect lets a reconnecting client recover them.
    [Export] public DisconnectEntityMode TimeoutDisconnectMode { get; set; } = DisconnectEntityMode.KeepEntity;
    [Export] public DisconnectEntityMode ManualDisconnectMode { get; set; } = DisconnectEntityMode.KeepEntity;

    // After this many seconds, an unclaimed reclaim token is dropped and its orphaned
    // entities are destroyed. Without this, _reclaimableEntities leaks indefinitely on
    // long-running servers when clients never reconnect to claim their entities.
    [Export] public float ReclaimExpirySec { get; set; } = 180f;

    // Soft warning threshold for a connected client going silent. Purely informational —
    // ENet remains the disconnect authority. Mirrors ClientConnectionMonitor.SilenceThresholdSec.
    [Export] public float ClientSilenceThresholdSec { get; set; } = 3f;

    public ITimestampProvider TimestampProvider { get; set; } = new RealTimestampProvider();

    private readonly HashSet<int> _voluntaryDisconnects = new();
    private readonly Dictionary<int, string> _sessionTokenByClient = new();
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
        string token = System.Guid.NewGuid().ToString();
        _sessionTokenByClient[clientId] = token;
        _lastClientMessageMsec[clientId] = TimestampProvider.GetTicksMsec();
        SendCommandToClient(clientId, new SessionTokenMessage { Token = token },
            INetworkManager.PacketModeEnum.Reliable, (int)ChannelEnum.GameReliable);
        MonkeLogger.Info($"ServerConnectionMonitor: issued session token to client {clientId} [tok:{Tok(token)}]");
    }

    protected override void OnCommandReceived(int clientId, IPackableMessage command)
    {
        _lastClientMessageMsec[clientId] = TimestampProvider.GetTicksMsec();
        if (_silenceWarnedClients.Remove(clientId))
        {
            string tok = _sessionTokenByClient.TryGetValue(clientId, out var t) ? Tok(t) : "????";
            MonkeLogger.Info($"ServerConnectionMonitor: client {clientId} responded after silence [tok:{tok}]");
        }

        if (command is DisconnectNotificationMessage)
        {
            _voluntaryDisconnects.Add(clientId);
            string tok = _sessionTokenByClient.TryGetValue(clientId, out var t) ? Tok(t) : "????";
            MonkeLogger.Info($"ServerConnectionMonitor: client {clientId} sent voluntary disconnect notification [tok:{tok}]");
        }
    }

    protected override void OnClientDisconnected(int clientId)
    {
        bool wasVoluntary = _voluntaryDisconnects.Remove(clientId);

        DisconnectEntityMode mode = wasVoluntary ? ManualDisconnectMode : TimeoutDisconnectMode;
        var entityManager = ServerManager.Instance.EntityManager;

        if (mode == DisconnectEntityMode.KeepEntity
            && _sessionTokenByClient.TryGetValue(clientId, out string token))
        {
            var entityIds = entityManager.GetEntityIdsForClient(clientId);
            if (entityIds.Count > 0)
                _reclaimableEntities[token] = new ReclaimEntry
                {
                    EntityIds = entityIds,
                    CreatedAtMsec = TimestampProvider.GetTicksMsec(),
                };
            entityManager.OrphanEntitiesForClient(clientId);
            MonkeLogger.Info($"ServerConnectionMonitor: client {clientId} entities orphaned [tok:{Tok(token)}]");
        }
        else
        {
            string tok = _sessionTokenByClient.TryGetValue(clientId, out var t) ? Tok(t) : "????";
            entityManager.DestroyEntitiesForClient(clientId);
            MonkeLogger.Info($"ServerConnectionMonitor: client {clientId} entities destroyed [tok:{tok}]");
        }

        _sessionTokenByClient.Remove(clientId);
        _lastClientMessageMsec.Remove(clientId);
        _silenceWarnedClients.Remove(clientId);
    }

    private static string Tok(string token) =>
        token?.Length >= 4 ? token[^4..] : (token ?? "????");

    /// <summary>
    /// Validates a reclaim token and returns the entity IDs it maps to, consuming the token.
    /// Returns null if the token is invalid or already used.
    /// </summary>
    public List<int> ConsumeReclaimToken(string token)
    {
        if (!_reclaimableEntities.TryGetValue(token, out var entry))
            return null;
        _reclaimableEntities.Remove(token);
        return entry.EntityIds;
    }

    // Runs every server network tick: sweeps expired reclaim entries and emits soft
    // warnings for silent clients before ENet drops them.
    protected override void OnNetworkProcessTick(int currentTick)
    {
        ulong now = TimestampProvider.GetTicksMsec();
        SweepExpiredReclaimTokens(now);
        CheckClientSilence(now);
    }

    private void SweepExpiredReclaimTokens(ulong now)
    {
        if (_reclaimableEntities.Count == 0) return;

        ulong expiryMs = (ulong)(ReclaimExpirySec * 1000f);

        List<string> expired = null;
        foreach (var (token, entry) in _reclaimableEntities)
        {
            if (now - entry.CreatedAtMsec > expiryMs)
            {
                expired ??= new List<string>();
                expired.Add(token);
            }
        }

        if (expired == null) return;

        var entityManager = ServerManager.Instance.EntityManager;
        foreach (string token in expired)
        {
            var entry = _reclaimableEntities[token];
            foreach (int entityId in entry.EntityIds)
                entityManager.DestroyOrphanedEntity(entityId);
            _reclaimableEntities.Remove(token);
            MonkeLogger.Info($"ServerConnectionMonitor: reclaim token expired [tok:{Tok(token)}], orphaned entities destroyed");
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
            string tok = _sessionTokenByClient.TryGetValue(clientId, out var t) ? Tok(t) : "????";
            MonkeLogger.Warn($"ServerConnectionMonitor: client {clientId} silent for {(now - lastMsec) / 1000f:F1}s [tok:{tok}]");
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
            string tok = _sessionTokenByClient.TryGetValue(peerId, out var t) ? Tok(t) : "----";
            ImGui.TextUnformatted($"Peer {peerId} [tok:{tok}]:  RTT {rtt} ms   Missed {missed} total  ({rate:0.0}% last 64)");
        }
        if (!any) ImGui.Text("No players connected");
    }
}
