using System.Collections.Generic;
using Godot;
using ImGuiNET;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace MonkeNet.Server;

public partial class ServerManager : Node
{
    [Signal] public delegate void ServerReadyEventHandler();
    [Signal] public delegate void ServerTickEventHandler(int currentTick);
    [Signal] public delegate void ServerNetworkTickEventHandler(int currentTick);
    [Signal] public delegate void ClientConnectedEventHandler(int clientId);
    [Signal] public delegate void ClientDisconnectedEventHandler(int clientId);

    public delegate void CommandReceivedEventHandler(int clientId, IPackableMessage command); // Using a C# signal here because the Godot signal wouldn't accept NetworkMessages.IPackableMessage
    public event CommandReceivedEventHandler CommandReceived;

    public static ServerManager Instance { get; private set; }

    private INetworkManager _networkManager;
    private ServerNetworkClock _serverClock;
    private ServerEntityManager _entityManager;
    private ServerInputReceiver _inputReceiver;
    private ServerConnectionMonitor _connectionMonitor;

    private int _currentTick = 0;

    public override void _EnterTree()
    {
        Instance = this;

        // Set the _Process() tickrate to be the same as the _PhysicsProcess() to not waste resources, we shouldn't be using _Process() anywhere
        // TODO: Update: Uncommenting this makes the network conditions shit. It seems like maybe it affects packet reading or something like that? Investigate further.
        //Engine.MaxFps = Engine.PhysicsTicksPerSecond; // This should be used
        Engine.MaxFps = 120; // Should be enough...
    }

    public override void _ExitTree()
    {
        if (Instance == this)
            Instance = null;

        if (_networkManager != null)
        {
            _networkManager.ClientConnected -= OnClientConnected;
            _networkManager.ClientDisconnected -= OnClientDisconnected;
            _networkManager.PacketReceived -= OnPacketReceived;
        }
        if (_serverClock != null)
            _serverClock.NetworkProcessTick -= OnNetworkProcess;
    }

    public override void _Ready()
    {
        _entityManager = GetNode<ServerEntityManager>("ServerEntityManager");
        _inputReceiver = GetNode<ServerInputReceiver>("ServerInputReceiver");
        _connectionMonitor = GetNode<ServerConnectionMonitor>("ServerConnectionMonitor");
    }

    public void Initialize(INetworkManager networkManager, int port)
    {
        _networkManager = networkManager;

        _serverClock = GetNode<ServerNetworkClock>("ServerNetworkClock");
        _serverClock.NetworkProcessTick += OnNetworkProcess;

        _networkManager.CreateServer(port);
        _networkManager.ClientConnected += OnClientConnected;
        _networkManager.ClientDisconnected += OnClientDisconnected;
        _networkManager.PacketReceived += OnPacketReceived;

        EmitSignal(SignalName.ServerReady);
        MonkeLogger.Info("Initialized Server Manager");
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
        if (_serverClock == null) return; // Not yet initialized via Initialize()
        _currentTick = _serverClock.ProcessTick();

        EmitSignal(SignalName.ServerTick, _currentTick);
        EntitiesCallProcessTick(_currentTick);

        _inputReceiver.DropOutdatedInputs(_currentTick); // Delete all inputs that we don't need anymore

        if (MonkeNetManager.Instance != null)
        {
            PhysicsServer3D.SpaceStep(MonkeNetManager.Instance.PhysicsSpace, PhysicsUtils.DeltaTime);
            PhysicsServer3D.SpaceFlushQueries(MonkeNetManager.Instance.PhysicsSpace);
        }

        _entityManager.SendSnapshotData(_currentTick);
    }

    private void EntitiesCallProcessTick(int currentTick)
    {
        if (MonkeNetManager.Instance?.EntitySpawner == null) return;
        foreach (var node in MonkeNetManager.Instance.EntitySpawner.Entities)
        {
            if (node is NetworkBehaviour serverEntity)
            {
                IPackableElement input = _inputReceiver.GetInputForEntityTick(serverEntity, currentTick);

                var serverStateSyncronizer = serverEntity.GetComponent<ServerStateSyncronizer>();
                if (input != null && serverStateSyncronizer != null)
                {
                    serverStateSyncronizer.OnProcessTick(currentTick, input);
                }
            }
        }
    }

    private void OnTimerTimeout()
    {
        MonkeLogger.Info($"Server Status: Tick {_currentTick}, Framerate {Engine.GetFramesPerSecond()}, Physics Tick {Engine.PhysicsTicksPerSecond}hz");
    }

    private void OnNetworkProcess(double delta)
    {
        EmitSignal(SignalName.ServerNetworkTick, _currentTick);
    }

    public void SendCommandToClient(int clientId, IPackableMessage command, INetworkManager.PacketModeEnum mode, int channel)
    {
        byte[] bin = MessageSerializer.Serialize(command);
        _networkManager.SendBytes(bin, clientId, channel, mode);
    }

    public int GetNetworkId()
    {
        return _networkManager.GetNetworkId();
    }

    public ServerEntityManager EntityManager => _entityManager;

    public T SpawnEntity<T>(byte entityType, int authority, string metadata = "") where T : Node3D
    {
        return _entityManager.SpawnEntity<T>(entityType, authority, metadata);
    }

    public void DestroyEntity(int entityId, int targetId)
    {
        _entityManager.DestroyEntity(entityId, targetId);
    }

    /// <summary>
    /// Reassigns ownership of an entity to a different peer (or back to the server with
    /// <paramref name="newAuthority"/> = 0). Old and new owner clients receive a Destroy +
    /// Create pair so their local view swaps between predicted and interpolated. Use this
    /// for vehicle entry/exit, picking up items, etc.
    /// </summary>
    public void ChangeAuthority(int entityId, int newAuthority)
    {
        _entityManager.ChangeAuthority(entityId, newAuthority);
    }

    public void DisconnectClient(int clientId, bool force = false)
    {
        _networkManager.DisconnectClient(clientId, force);
    }

    public IReadOnlyCollection<int> GetConnectedPeerIds() =>
        _networkManager.GetConnectedPeerIds();

    public int GetPeerRtt(int peerId) => _networkManager.GetPeerRtt(peerId);

    // Route received Input package to the correspondant Network ID
    private void OnPacketReceived(long id, byte[] bin)
    {
        var command = MessageSerializer.Deserialize(bin);
        CommandReceived?.Invoke((int)id, command);
    }

    private void OnClientConnected(long clientId)
    {
        EmitSignal(SignalName.ClientConnected, (int)clientId);
        MonkeLogger.Info($"Client {clientId} connected");
    }

    private void OnClientDisconnected(long clientId)
    {
        EmitSignal(SignalName.ClientDisconnected, (int)clientId);
        MonkeLogger.Info($"Client {clientId} disconnected");
    }

    private void DisplayDebugInformation()
    {
        ImGui.SetNextWindowPos(System.Numerics.Vector2.Zero);
        if (ImGui.Begin("Server Information",
            ImGuiWindowFlags.NoMove
                | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text($"Framerate {Engine.GetFramesPerSecond()}fps");
            ImGui.Text($"Physics Tick {Engine.PhysicsTicksPerSecond}hz");
            _serverClock.DisplayDebugInformation();
            _inputReceiver.DisplayDebugInformation();
            _entityManager.DisplayDebugInformation();
            _connectionMonitor?.DisplayDebugInformation(_inputReceiver);
            ImGui.End();
        }

    }
}
