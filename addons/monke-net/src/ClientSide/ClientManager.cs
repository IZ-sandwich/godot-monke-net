using Godot;
using ImGuiNET;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace MonkeNet.Client;

/// <summary>
/// Main Client-side node, communicates with the server and other components of the client
/// </summary>
public partial class ClientManager : Node
{
    [Signal] public delegate void LatencyCalculatedEventHandler(int latencyAverageTicks, int jitterAverageTicks);
    [Signal] public delegate void NetworkReadyEventHandler();
    [Signal] public delegate void ServerDisconnectedEventHandler();
    [Signal] public delegate void ConnectionLostEventHandler();
    [Signal] public delegate void ConnectionFailedEventHandler();
    [Signal] public delegate void ServerSilentEventHandler();
    [Signal] public delegate void ServerRespondedEventHandler();

    public delegate void CommandReceivedEventHandler(IPackableMessage command); // Using a C# signal here because the Godot signal wouldn't accept NetworkMessages.IPackableMessage
    public event CommandReceivedEventHandler CommandReceived;

    public delegate void ClientTickEventHandler(int tick, IPackableElement command); // Using a C# signal here because the Godot signal wouldn't accept NetworkMessages.IPackableMessage
    public event ClientTickEventHandler ClientTick;

    public delegate void ServerDisconnectedInternalHandler();
    public event ServerDisconnectedInternalHandler ServerDisconnectedInternal;

    public static ClientManager Instance { get; private set; }

    private INetworkManager _networkManager;
    private ClientSnapshotInterpolator _snapshotInterpolator;
    private ClientNetworkClock _clock;
    private NetworkDebug _networkDebug;
    private ClientEntityManager _entityManager;
    private ClientInputManager _inputManager;
    private ClientPredictionManager _predictionManager;

    private bool _networkReady = false;
    public bool IsNetworkReady => _networkReady;

    // Tracks whether we currently have an established server connection.
    // Used to suppress duplicate ServerDisconnected events during reconnect attempts,
    // since each failed ENet connection attempt fires PeerDisconnected(1).
    private bool _serverConnected = false;

    // Set to true between Initialize() and the first PeerConnected(1) so that a failed
    // connection attempt (ENet fires PeerDisconnected(1) on timeout) emits ConnectionFailed.
    private bool _connecting = false;

    // Set to true when we initiate a voluntary disconnect so that the resulting
    // PeerDisconnected(1) does not trigger the ConnectionLost path.
    private bool _disconnecting = false;

    public override void _EnterTree()
    {
        Instance = this;
    }

    public override void _ExitTree()
    {
        if (Instance == this)
            Instance = null;

        if (_networkManager != null)
        {
            _networkManager.PacketReceived -= OnPacketReceived;
            _networkManager.ClientConnected -= OnPeerConnected;
            _networkManager.ClientDisconnected -= OnPeerDisconnected;
        }
        if (_clock != null)
            _clock.LatencyCalculated -= OnLatencyCalculated;
    }

    public override void _Ready()
    {
        _networkDebug = GetNode<NetworkDebug>("NetworkDebug");
        _clock = GetNode<ClientNetworkClock>("ClientNetworkClock");
        _snapshotInterpolator = GetNode<ClientSnapshotInterpolator>("SnapshotInterpolator");
        _entityManager = GetNode<ClientEntityManager>("ClientEntityManager");
        _inputManager = GetNode<ClientInputManager>("ClientInputManager");
        _predictionManager = GetNode<ClientPredictionManager>("PredictionManager");
    }

    public override void _Process(double delta)
    {
        // ImGuiRoot autoload is only present in the main project, not in headless/test mode.
        if (GetTree().Root.HasNode("ImGuiRoot"))
            DisplayDebugInformation();
    }

    // TODO: I don't know if manually stepping physics inside _PhysicsProcess is a good idea,
    // as internally _PhysicsProcess will call _step() and _flush_queries() the same way I'm doing right now...
    // causing multiple calls to the same PhysicsServer methods
    public override void _PhysicsProcess(double delta)
    {
        // Advance Clock
        _clock.ProcessTick();
        int currentTick = _clock.GetCurrentTick();

        // Read and send produced input to the server
        var input = _inputManager.GenerateAndTransmitInputs(currentTick);

        // Call OnProcessTick on all entities, pass current input so they can simulate
        _predictionManager.Predict(currentTick, input);
        ClientTick?.Invoke(currentTick, input);

        // In listen-server mode the ServerManager already stepped physics this frame;
        // stepping it again here would advance it twice and cause double-speed movement.
        if (MonkeNetManager.Instance != null && !MonkeNetManager.Instance.IsServer)
        {
            PhysicsServer3D.SpaceStep(MonkeNetManager.Instance.PhysicsSpace, PhysicsUtils.DeltaTime);
            PhysicsServer3D.SpaceFlushQueries(MonkeNetManager.Instance.PhysicsSpace);
        }

        // Register all local predictions
        _predictionManager.RegisterPrediction(currentTick, input);
    }

    public void Initialize(INetworkManager networkManager, string address, int port)
    {
        _networkManager = networkManager;
        _networkDebug.NetworkManager = _networkManager;

        _clock.LatencyCalculated += OnLatencyCalculated;

        _networkManager.PacketReceived += OnPacketReceived;
        _networkManager.ClientConnected += OnPeerConnected;
        _networkManager.ClientDisconnected += OnPeerDisconnected;
        _networkManager.CreateClient(address, port);
        _connecting = true;

        MonkeLogger.Info("Client Manager Initialized");
    }

    private void OnPeerConnected(long id)
    {
        if (id != 1) return;
        _connecting = false;
        _serverConnected = true;
        MonkeLogger.Info("Connected to server");
    }

    private void OnPeerDisconnected(long id)
    {
        if (id != 1) return; // 1 is always the server's peer ID on the client

        if (!_serverConnected)
        {
            if (_connecting) { _connecting = false; EmitSignal(SignalName.ConnectionFailed); }
            _disconnecting = false;
            return;
        }

        bool voluntary = _disconnecting;
        _disconnecting = false;
        _serverConnected = false;
        _networkReady = false;
        ServerDisconnectedInternal?.Invoke();

        if (voluntary)
        {
            MonkeLogger.Info("Disconnected from server");
            EmitSignal(SignalName.ServerDisconnected);
        }
        else
        {
            MonkeLogger.Info("Connection to server lost");
            EmitSignal(SignalName.ConnectionLost);
        }
    }

    // Closes the connection without sending DisconnectNotificationMessage.
    // The server receives no notification and applies TimeoutDisconnectMode entity retention.
    public void DisconnectUngraceful()
    {
        _connecting = false;
        Callable.From(_networkManager.Disconnect).CallDeferred();
    }

    public void Disconnect()
    {
        _connecting = false;
        _disconnecting = true;

        if (_serverConnected)
        {
            _serverConnected = false;
            _networkReady = false;
            MonkeLogger.Info("Disconnecting from server");
            ServerDisconnectedInternal?.Invoke();
            EmitSignal(SignalName.ServerDisconnected);
        }

        // Send disconnect notification while the ENet peer is still open, then close it.
        // Cleanup above is done synchronously because Close() only fires PeerDisconnected on
        // the next ENet poll cycle — by which time the scene may already be reloading.
        SendCommandToServer(new DisconnectNotificationMessage(),
            INetworkManager.PacketModeEnum.Reliable, (int)ChannelEnum.GameReliable);
        Callable.From(_networkManager.Disconnect).CallDeferred();
    }

    public void SendCommandToServer(IPackableMessage command, INetworkManager.PacketModeEnum mode, int channel)
    {
        byte[] bin = MessageSerializer.Serialize(command);
        _networkManager.SendBytes(bin, 1, channel, mode);
    }

    private void OnPacketReceived(long id, byte[] bin)
    {
        var command = MessageSerializer.Deserialize(bin);
        CommandReceived?.Invoke(command);
    }

    public void MakeEntityRequest(byte entityType) //TODO: This should NOT be here
    {
        _entityManager.MakeEntityRequest(entityType);
    }

    /// <summary>
    /// Asks the server to transfer authority of <paramref name="entityId"/> to this client.
    /// Server runs <c>ServerEntityManager.OwnershipApprover</c>; on approve, the normal
    /// Destroy+Create authority-swap path runs, on reject the client receives an
    /// <see cref="OwnershipChangeRejectedMessage"/>. Default policy is reject — the game
    /// must register an approver server-side or no request will ever succeed.
    /// </summary>
    public void RequestAuthority(int entityId)
    {
        SendCommandToServer(new OwnershipChangeRequestMessage { EntityId = entityId },
            INetworkManager.PacketModeEnum.Reliable, (int)ChannelEnum.GameReliable);
    }

    /// <summary>
    /// Anticipated variant of <see cref="RequestAuthority"/>: flips the local entity's
    /// scene from dummy to predicted immediately so the player can drive it without
    /// waiting one RTT for server confirmation. On reject (or timeout) the client
    /// reverts to the dummy at the original pose. Use when responsiveness matters
    /// (vehicle entry, item pickup) and a brief revert-flicker on rejection is
    /// acceptable.
    /// </summary>
    public void RequestAuthorityAnticipated(int entityId)
    {
        _entityManager.RequestAuthorityAnticipated(entityId);
    }

    public int GetNetworkId()
    {
        return _networkManager.GetNetworkId();
    }

    private void OnLatencyCalculated(int currentTick, int latencyAverageTicks, int jitterAverageTicks, int averageClockOffset)
    {
        EmitSignal(SignalName.LatencyCalculated, latencyAverageTicks, jitterAverageTicks);

        // NetworkReady fires exactly once per connection lifecycle. _networkReady is cleared
        // in OnPeerDisconnected and Disconnect, so the next connection will emit again.
        if (!_networkReady)
        {
            _networkReady = true;
            EmitSignal(SignalName.NetworkReady);
        }
    }

    private void DisplayDebugInformation()
    {
        ImGui.SetNextWindowPos(System.Numerics.Vector2.Zero);
        if (ImGui.Begin("Client Information",
                ImGuiWindowFlags.NoMove
                | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text($"Network ID {_networkManager?.GetNetworkId() ?? 0}");
            string tok = MonkeLogger.CurrentToken?.Length >= 4 ? MonkeLogger.CurrentToken[^4..] : "----";
            ImGui.Text($"Session Token  ...{tok}");
            ImGui.Text($"Framerate {Engine.GetFramesPerSecond()}fps");
            ImGui.Text($"Physics Tick {Engine.PhysicsTicksPerSecond}hz");
            _clock.DisplayDebugInformation();
            _networkDebug.DisplayDebugInformation();
            _snapshotInterpolator.DisplayDebugInformation();
            _inputManager.DisplayDebugInformation();
            _predictionManager.DisplayDebugInformation();
            ImGui.End();
        }
    }
}
