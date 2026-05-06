using System.Collections.Generic;
using Godot;

namespace MonkeNet.Shared;

/// <summary>
/// Client/Server Network communication via Godot Enet
/// </summary>
public partial class NetworkManagerEnet : Node, INetworkManager
{
    public enum AudienceMode : int
    {
        Broadcast = 0,
        Server = 1
    }

    // Per-peer ENet timeout applied in OnPeerConnected. Defaults intentionally exceed
    // ENet's built-in floor/ceiling (5s/30s) so a few-second loss burst doesn't kill
    // an otherwise healthy session. Same values apply on both sides, so the server's
    // view of a peer and the peer's view of the server time out at the same scale.
    public static int PeerTimeoutLimit { get; set; } = 32;
    public static int PeerTimeoutMinMs { get; set; } = 10_000;
    public static int PeerTimeoutMaxMs { get; set; } = 60_000;

    private int _networkId = 0;
    private SceneMultiplayer _multiplayer;
    private readonly HashSet<int> _connectedPeers = new();

    public event INetworkManager.ClientConnectedEventHandler ClientConnected;
    public event INetworkManager.ClientDisconnectedEventHandler ClientDisconnected;
    public event INetworkManager.PacketReceivedEventHandler PacketReceived;

    public override void _Ready()
    {
        SubscribeToMultiplayer(Multiplayer as SceneMultiplayer);
    }

    // Used by CreateListenServer to give this node its own SceneMultiplayer so it
    // does not share a peer with the server-side NetworkManagerEnet.
    public void UseCustomMultiplayer(SceneMultiplayer multiplayer)
    {
        // Do NOT close `_multiplayer.MultiplayerPeer` here. At listen-server startup,
        // _multiplayer is the global SceneMultiplayer that the server-side
        // NetworkManagerEnet shares — its peer is the server's ENet peer that was
        // just bound by CreateServer. Closing it would kill the server before the
        // first handshake. SubscribeToMultiplayer just rewires our subscriptions
        // to the new (client-only) multiplayer; no peer cleanup is needed because
        // the client hasn't created its own peer yet.
        SubscribeToMultiplayer(multiplayer);
    }

    private void SubscribeToMultiplayer(SceneMultiplayer multiplayer)
    {
        if (_multiplayer != null)
        {
            _multiplayer.PeerConnected -= OnPeerConnected;
            _multiplayer.PeerDisconnected -= OnPeerDisconnected;
            _multiplayer.PeerPacket -= OnPacketReceived;
        }
        _multiplayer = multiplayer;
        _multiplayer.PeerConnected += OnPeerConnected;
        _multiplayer.PeerDisconnected += OnPeerDisconnected;
        _multiplayer.PeerPacket += OnPacketReceived;
    }

    public void CreateServer(int port, int maxClients = 32)
    {
        // Null out the old peer so SceneMultiplayer releases its reference before we bind the
        // port again. Keeping the closed peer assigned can prevent the OS socket from being
        // reused on the next CreateServer call on the same port.
        (_multiplayer.MultiplayerPeer as ENetMultiplayerPeer)?.Close();
        _multiplayer.MultiplayerPeer = null;
        _connectedPeers.Clear();
        ENetMultiplayerPeer enet = new();
        var err = enet.CreateServer(port, maxClients);
        if (err != Error.Ok)
        {
            MonkeLogger.Error($"Failed to create server on port {port}: {err}");
            return;
        }
        _multiplayer.MultiplayerPeer = enet;
        _networkId = _multiplayer.GetUniqueId();
        MonkeLogger.Info($"Created server, Port:{port} Max Clients:{maxClients}");
    }

    public void CreateClient(string address, int port)
    {
        (_multiplayer.MultiplayerPeer as ENetMultiplayerPeer)?.Close();
        _multiplayer.MultiplayerPeer = null;
        _connectedPeers.Clear();
        ENetMultiplayerPeer enet = new();
        var err = enet.CreateClient(address, port);
        if (err != Error.Ok)
        {
            MonkeLogger.Error($"Failed to connect to {address}:{port}: {err}");
            return;
        }
        _multiplayer.MultiplayerPeer = enet;
        _networkId = _multiplayer.GetUniqueId();
        MonkeLogger.Info($"Connecting to {address}:{port}");
    }

    public void Disconnect()
    {
        (_multiplayer.MultiplayerPeer as ENetMultiplayerPeer)?.Close();
        _multiplayer.MultiplayerPeer = null;
        _connectedPeers.Clear();
    }

    public void SendBytes(byte[] bin, int id, int channel, INetworkManager.PacketModeEnum mode)
    {
        if (_multiplayer.MultiplayerPeer?.GetConnectionStatus() != MultiplayerPeer.ConnectionStatus.Connected)
            return;
        var m = mode == INetworkManager.PacketModeEnum.Reliable
            ? MultiplayerPeer.TransferModeEnum.Reliable
            : MultiplayerPeer.TransferModeEnum.Unreliable;

        // When broadcasting (id=0) over ENet, iterate tracked peers individually and skip any
        // that are not in Connected state. ENet resets channelCount before Godot fires
        // PeerDisconnected, so a raw broadcast to id=0 can hit a zombie peer and log an error.
        if (id == 0 && _multiplayer.MultiplayerPeer is ENetMultiplayerPeer enetPeer)
        {
            foreach (var peerId in _connectedPeers)
            {
                if (enetPeer.GetPeer(peerId)?.GetState() == ENetPacketPeer.PeerState.Connected)
                    _multiplayer.SendBytes(bin, peerId, m, channel);
            }
        }
        else
        {
            if (!_connectedPeers.Contains(id))
                return;
            _multiplayer.SendBytes(bin, id, m, channel);
        }
    }

    public int PopStatistic(INetworkManager.NetworkStatisticEnum statistic)
    {
        // The ENet host is torn down on disconnect. Accessing .Host on an inactive peer
        // prints a native error and returns null, so bail out early in that case.
        if (_multiplayer.MultiplayerPeer is not ENetMultiplayerPeer enetPeer)
            return 0;

        if (enetPeer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Disconnected)
            return 0;

        var enetHost = enetPeer.Host;
        if (enetHost == null)
            return 0;

        return statistic switch
        {
            INetworkManager.NetworkStatisticEnum.SentBytes => (int)enetHost.PopStatistic(ENetConnection.HostStatistic.SentData),
            INetworkManager.NetworkStatisticEnum.ReceivedBytes => (int)enetHost.PopStatistic(ENetConnection.HostStatistic.ReceivedData),
            INetworkManager.NetworkStatisticEnum.SentPackets => (int)enetHost.PopStatistic(ENetConnection.HostStatistic.SentPackets),
            INetworkManager.NetworkStatisticEnum.ReceivedPackets => (int)enetHost.PopStatistic(ENetConnection.HostStatistic.ReceivedPackets),
            _ => throw new MonkeNetException("Undefined statistic"),
        };
    }

    public void DisconnectClient(int clientId, bool force = false)
    {
        (_multiplayer.MultiplayerPeer as ENetMultiplayerPeer)?.DisconnectPeer(clientId, force);
    }

    public int GetNetworkId() => _networkId;

    public IReadOnlyCollection<int> GetConnectedPeerIds() => _connectedPeers;

    public int GetPeerRtt(int peerId)
    {
        if (_multiplayer.MultiplayerPeer is not ENetMultiplayerPeer enetPeer) return 0;
        return (int)(enetPeer.GetPeer(peerId)
            ?.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTime) ?? 0);
    }

    private void OnPeerConnected(long id)
    {
        _connectedPeers.Add((int)id);
        // Only direct ENet peers live in ENetMultiplayerPeer's peers map. On a server
        // (local id 1) every peer_connected is direct. On a client (local id != 1) only
        // id 1 (the server) is direct — other ids are server_relay notifications about
        // peer-to-peer connections, and GetPeer would error with "!peers.has(p_id)".
        bool isDirectPeer = _multiplayer.GetUniqueId() == 1 || (int)id == 1;
        if (isDirectPeer && _multiplayer.MultiplayerPeer is ENetMultiplayerPeer enetPeer)
            enetPeer.GetPeer((int)id)?.SetTimeout(PeerTimeoutLimit, PeerTimeoutMinMs, PeerTimeoutMaxMs);
        ClientConnected?.Invoke(id);
    }

    private void OnPeerDisconnected(long id)
    {
        _connectedPeers.Remove((int)id);
        ClientDisconnected?.Invoke(id);
    }

    private void OnPacketReceived(long id, byte[] bin)
    {
        PacketReceived?.Invoke(id, bin);
    }
}
