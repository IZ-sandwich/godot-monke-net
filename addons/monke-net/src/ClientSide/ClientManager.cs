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
    private ClientNetworkClock _clock;
    private NetworkDebug _networkDebug;
    private ClientEntityManager _entityManager;
    private ClientInputManager _inputManager;
    private ClientPredictionManager _predictionManager;

    private bool _networkReady = false;
    public bool IsNetworkReady => _networkReady;

    // Tick of the last fully-processed (Predict + SpaceStep + RegisterPrediction)
    // physics frame. Used by _PhysicsProcess to detect engine-freeze-induced tick
    // gaps and back-fill them in-place so live body integration stays in lock-
    // step with the client tick clock — see _PhysicsProcess for the rationale.
    // -1 sentinel = no tick processed yet; the first _PhysicsProcess call only
    // runs the current tick (no back-fill on cold start).
    private int _lastProcessedTick = -1;

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
        // In listen-server mode this manager and ServerManager are siblings under
        // MonkeNetManager. We must run our _PhysicsProcess (which calls Predict and
        // queues client-side forces) BEFORE ServerManager's _PhysicsProcess (which runs
        // the single shared SpaceStep). With the default order ServerManager runs first,
        // its SpaceStep integrates this frame's server forces but NOT the client's not-
        // yet-queued ones — the client's impulses lag by a tick and trigger continuous
        // mispredictions. Lower process_priority = runs earlier.
        ProcessPriority = -1;
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

        // Client-side entities (LocalAuthorityScene / DummyScene instances) live
        // under EntitySpawner (an autoload), so they survive ClientManager going
        // away unless we explicitly free them. Clear here so reconnect or scene-
        // reload doesn't carry over stale entities into the next session.
        // OnServerDisconnected already does this for the disconnect path; this
        // covers tree-exit paths (test dispose, scene reload) that bypass it.
        MonkeNetManager.Instance?.EntitySpawner?.ClearClientEntities();
    }

    public override void _Ready()
    {
        _networkDebug = GetNode<NetworkDebug>("NetworkDebug");
        _clock = GetNode<ClientNetworkClock>("ClientNetworkClock");
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
        MonkeLogger.Debug($"[CLIENT-TICK] tick={currentTick} dt={PhysicsUtils.DeltaTime:F4}");

        // Read and send produced input to the server
        var input = _inputManager.GenerateAndTransmitInputs(currentTick);

        bool isListenServer = MonkeNetManager.Instance != null && MonkeNetManager.Instance.IsServer;

        // Back-fill any client ticks the engine dropped since the previous
        // _PhysicsProcess call. Godot's physics catch-up is capped by
        // `physics/common/max_physics_steps_per_frame` (default 8), so a slow
        // render frame — typically caused by a previous reconcile's resim cost
        // — means _PhysicsProcess may be skipped while ClientNetworkClock
        // continues to advance with wall-clock time. Without back-fill, the
        // client tick counter outruns local physics integration: live body
        // state accumulates fewer ticks of motion than the network tick
        // suggests, snapshots for the skipped ticks arrive with no matching
        // PredictedState (logged as MISSED-LOCAL-STATE), and when a reconcile
        // does fire the post-resim body lands behind where it should be (the
        // tick=634 "visual ahead of rigidbody" artifact in S7 C4).
        //
        // The catch-up runs the full Predict + SpaceStep + RegisterPrediction
        // sequence for each missed tick, using the SAME local input we just
        // generated for the current tick. This is the GGPO / QuakeWorld
        // repeat-last-input convention — the held-keys state at the moment
        // physics caught up is the best estimate of what the player was
        // pressing during the dropped-tick window (60-Hz physics + human
        // reaction time means most ticks within a sub-second freeze share
        // the same input as the surrounding live ticks). Inputs are NOT
        // re-transmitted to the server: the server has already advanced past
        // those ticks and won't apply retroactively-dated inputs.
        //
        // Listen-server mode skips back-fill: client and server share the same
        // tick clock there, so there's no drift to repair, and any extra
        // SpaceStep would double-integrate the shared World3D.Space.
        if (!isListenServer && _lastProcessedTick >= 0 && currentTick > _lastProcessedTick + 1)
        {
            int catchupStart = _lastProcessedTick + 1;
            int catchupEnd = currentTick - 1;
            MonkeLogger.Debug($"[CLIENT-TICK-CATCHUP] back-filling ticks {catchupStart}..{catchupEnd} ({catchupEnd - catchupStart + 1} dropped by engine freeze)");
            for (int catchupTick = catchupStart; catchupTick <= catchupEnd; catchupTick++)
            {
                _predictionManager.Predict(catchupTick, input);
                ClientTick?.Invoke(catchupTick, input);
                PhysicsServer3D.SpaceStep(MonkeNetManager.Instance.PhysicsSpace, PhysicsUtils.DeltaTime);
                PhysicsServer3D.SpaceFlushQueries(MonkeNetManager.Instance.PhysicsSpace);
                _predictionManager.RunPostPhysicsTick(catchupTick, input);
                _predictionManager.RegisterPrediction(catchupTick, input);
            }
        }

        // Call OnProcessTick on all entities, pass current input so they can simulate.
        // This queues forces on rigid bodies (and runs MoveAndSlide on the player, which
        // applies push impulses to networked rigid bodies via SharedPlayerMovement).
        _predictionManager.Predict(currentTick, input);
        ClientTick?.Invoke(currentTick, input);

        if (!isListenServer)
        {
            // Pure-client mode: this manager owns the SpaceStep. Run it now so forces
            // queued in Predict integrate, then RegisterPrediction reads post-step state.
            MonkeLogger.Debug($"[CLIENT-TICK] tick={currentTick} SpaceStep (pure-client)");
            PhysicsServer3D.SpaceStep(MonkeNetManager.Instance.PhysicsSpace, PhysicsUtils.DeltaTime);
            PhysicsServer3D.SpaceFlushQueries(MonkeNetManager.Instance.PhysicsSpace);
            // Post-step entity hook. Runs after every body's post-step pose is
            // visible on the scene tree. Used by a kinematic rider to re-anchor
            // to the vehicle's just-integrated pose so its FTI lerp window
            // matches the vehicle's — without this, OnProcessTick's pre-step
            // anchor lags vehicle motion by exactly one tick, which makes the
            // first-person camera (a child of the rider body) render the world
            // from a viewpoint 1 tick behind the vehicle mesh, perceived as
            // oscillation between mesh and camera every render frame.
            _predictionManager.RunPostPhysicsTick(currentTick, input);
            _predictionManager.RegisterPrediction(currentTick, input);
        }
        else
        {
            // Listen-server: the SpaceStep happens in ServerManager._PhysicsProcess,
            // which runs after this method (we set ProcessPriority = -1 in _EnterTree).
            // RegisterPrediction must wait for that step to complete so it records
            // post-step body state — defer it via the PostPhysicsTick signal that
            // ServerManager fires after its SpaceStep. The stash here makes the deferred
            // call use the input/tick from THIS frame.
            _predictionManager.StashForLatePrediction(currentTick, input);
        }

        _lastProcessedTick = currentTick;
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
    /// Server evaluates the per-entity <c>OwnershipPolicy</c> (and the optional custom
    /// <c>ServerEntityManager.OwnershipApprover</c>) — on approval it broadcasts
    /// <see cref="AuthorityChangedMessage"/>, on rejection the requester receives
    /// <see cref="OwnershipChangeRejectedMessage"/>. The client never mutates local
    /// state until the server's response arrives.
    /// </summary>
    public void RequestAuthority(int entityId)
    {
        SendCommandToServer(new OwnershipChangeRequestMessage { EntityId = entityId },
            INetworkManager.PacketModeEnum.Reliable, (int)ChannelEnum.GameReliable);
    }

    /// <summary>
    /// Asks the server to release authority over <paramref name="entityId"/> back to the
    /// server (Authority=0). Server validates the sender is the current owner and that the
    /// entity's <c>OwnershipPolicy.AllowOwnerRelease</c> is true, then broadcasts
    /// <see cref="AuthorityChangedMessage"/>. No local mutation until confirmation.
    /// </summary>
    public void ReleaseAuthority(int entityId)
    {
        SendCommandToServer(new ReleaseAuthorityMessage { EntityId = entityId },
            INetworkManager.PacketModeEnum.Reliable, (int)ChannelEnum.GameReliable);
    }

    public int GetNetworkId()
    {
        // Null-safe: _networkManager is only set by Initialize, but _PhysicsProcess
        // runs every physics frame from the moment the scene is loaded. Tests that
        // load the ClientManager scene and let one idle frame elapse before calling
        // Initialize would NRE through Predict() → ClientManager.GetNetworkId() →
        // _networkManager.GetNetworkId() without this guard. 0 is the conventional
        // "no peer id" value already returned elsewhere (see DisplayDebugInformation).
        return _networkManager?.GetNetworkId() ?? 0;
    }

    /// <summary>Pop a network statistic (ENet host counter) from the underlying
    /// network manager. Reads are destructive (the counter resets); intended
    /// for telemetry / tests that sample bandwidth over a known interval.</summary>
    public int PopNetworkStatistic(INetworkManager.NetworkStatisticEnum stat) =>
        _networkManager?.PopStatistic(stat) ?? 0;

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
            if (ImGui.Button("Mark log"))
                MonkeLogger.Mark(_clock?.GetCurrentTick() ?? 0, "client");
            ImGui.Text($"Network ID {_networkManager?.GetNetworkId() ?? 0}");
            string tok = MonkeLogger.CurrentToken?.Length >= 4 ? MonkeLogger.CurrentToken[^4..] : "----";
            ImGui.Text($"Session Token  ...{tok}");
            ImGui.Text($"Framerate {Engine.GetFramesPerSecond()}fps");
            ImGui.Text($"Physics Tick {Engine.PhysicsTicksPerSecond}hz");
            _clock.DisplayDebugInformation();
            _networkDebug.DisplayDebugInformation();
            _inputManager.DisplayDebugInformation();
            _predictionManager.DisplayDebugInformation();
            ImGui.End();
        }
    }
}
