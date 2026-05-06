using Godot;
using MonkeNet.Client;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Server;
using MonkeNet.Shared;
using System.Collections.Generic;
using System.Linq;

namespace GameDemo;

public partial class MainScene : Node3D
{
    private static readonly string FLAG_DEDICATED_SERVER = "as_server";

    // Max distance at which a client may claim a vehicle. Both the server approver and
    // the client-side prompt enforce this; the server is the authoritative check.
    private const float MaxInteractionDistance = 4f;

    // Test-only knob: when false, the server approver skips the proximity check so a
    // client can claim a vehicle from anywhere via the "Claim Vehicle" UI button. Used
    // to repro misprediction scenarios without relying on the F-key proximity flow.
    [Export] private bool _requireProximityForVehicleClaim = true;

    // When true (default), claiming a vehicle teleports the player on top of it each
    // tick (rider mode). When false, claiming gives ownership but the player keeps its
    // own position — the vehicle still becomes the input target (player ignores WASD
    // while owning a vehicle), but no on-vehicle anchor. Lets you test pure vehicle
    // control without the player-on-top collision artifact.
    [Export] private bool _autoRideOnClaim = true;

    // Static mirror of _autoRideOnClaim for the player tick handlers (LocalPlayerPrediction
    // on the client, PlayerStateSyncronizer on the server) to consult without a NodePath
    // back to MainScene. Kept in sync with the [Export] in _Ready.
    public static bool AutoRideOnClaim { get; private set; } = true;

    // clientId → vehicleEntityId currently being driven. Populated when an authority
    // request is approved; cleared on ReleaseVehicleMessage or disconnect.
    private readonly Dictionary<int, int> _ridingByPlayer = new();

    private Label _connectingLabel;
    private Button _spawnButton;
    private Button _spawnBallButton;
    private Button _spawnVehicleButton;
    private Button _claimVehicleButton;
    private Button _disconnectButton;
    private Button _simulateTimeoutButton;
    private Button _cancelButton;
    private Button _stopServerButton;
    private Button _reconnectButton;
    private Label _highPingLabel;
    private Label _packetLossLabel;
    private Label _noResponseLabel;

    public override void _ExitTree()
    {
        var cm = ClientManager.Instance;
        if (cm == null || !IsInstanceValid(cm)) return;
        cm.NetworkReady -= OnNetworkReady;
        cm.ConnectionLost -= OnConnectionLost;
        cm.ConnectionFailed -= OnConnectionFailed;
        cm.ServerSilent -= OnServerSilent;
        cm.ServerResponded -= OnServerResponded;
        cm.LatencyCalculated -= OnLatencyCalculated;
    }

    public override void _Ready()
    {
        AutoRideOnClaim = _autoRideOnClaim;

        _connectingLabel = GetNode<Label>("Menu/ConnectingLabel");
        _spawnButton = GetNode<Button>("Menu/SpawnButton");
        _spawnBallButton = GetNode<Button>("Menu/SpawnBallButton");
        _spawnVehicleButton = GetNode<Button>("Menu/SpawnVehicleButton");
        _claimVehicleButton = GetNode<Button>("Menu/ClaimVehicleButton");
        _disconnectButton = GetNode<Button>("Menu/DisconnectButton");
        _simulateTimeoutButton = GetNode<Button>("Menu/SimulateTimeoutButton");
        _cancelButton = GetNode<Button>("Menu/CancelButton");
        _cancelButton.Hide();
        _stopServerButton = GetNode<Button>("Menu/StopServerButton");
        _stopServerButton.Hide();

        // ReconnectButton appears only when ClientEntityManager has a saved reclaim token
        // from a prior session — typical entry path is a timeout disconnect, after which
        // OnConnectionLost reloads this scene with the static token preserved.
        _reconnectButton = GetNode<Button>("Menu/ReconnectButton");
        _reconnectButton.Visible = ClientEntityManager.HasSavedReclaimToken;

        // Spawn buttons are useless until a client is connected and ready.
        // OnNetworkReady reveals them; OnConnectionLost / OnConnectionFailed re-hides.
        _spawnButton.Hide();
        _spawnBallButton.Hide();
        _spawnVehicleButton.Hide();
        _claimVehicleButton.Hide();
        _highPingLabel = GetNode<Label>("NetworkStatusPanel/HighPingLabel");
        _packetLossLabel = GetNode<Label>("NetworkStatusPanel/PacketLossLabel");
        _noResponseLabel = GetNode<Label>("NetworkStatusPanel/NoResponseLabel");

        if (OS.HasFeature(FLAG_DEDICATED_SERVER))
        {
            MonkeNetManager.Instance.CreateServer(9999);
            HookServerVehicleHandling();
        }
    }

    private void HookServerVehicleHandling()
    {
        var server = ServerManager.Instance;
        if (server == null) return;

        var entityManager = server.GetNode<ServerEntityManager>("ServerEntityManager");
        entityManager.OwnershipApprover = OnVehicleAuthorityRequested;

        server.CommandReceived += OnServerCommandReceived;
        server.ClientDisconnected += OnServerClientDisconnected;
    }

    // Server-side approver: only vehicles can be claimed, only when free, and only by a
    // player that is physically near the vehicle. Other entity types fall through to the
    // default reject. Called synchronously from HandleOwnershipRequest, so writes here
    // (recording the rider) commit before ChangeAuthority dispatches its destroy/create.
    private bool OnVehicleAuthorityRequested(int requesterId, int entityId)
    {
        var entity = EntitySpawner.Instance.Entities.Find(e => e.EntityId == entityId);
        if (entity == null || entity.EntityType != 2)
        {
            MonkeLogger.Warn($"VehicleClaim rejected (client {requesterId}, entity {entityId}): not a vehicle");
            return false;
        }
        if (entity.Authority != 0)
        {
            MonkeLogger.Warn($"VehicleClaim rejected (client {requesterId}, entity {entityId}): already owned by {entity.Authority}");
            return false;
        }

        var player = EntitySpawner.Instance.Entities
            .FirstOrDefault(e => e.EntityType == 0 && e.Authority == requesterId);
        if (player == null)
        {
            MonkeLogger.Warn($"VehicleClaim rejected (client {requesterId}, entity {entityId}): no player entity for client");
            return false;
        }

        var pRoot = EntitySpawner.Instance.GetEntityRoot(player);
        var vRoot = EntitySpawner.Instance.GetEntityRoot(entity);
        if (pRoot == null || vRoot == null)
        {
            MonkeLogger.Warn($"VehicleClaim rejected (client {requesterId}, entity {entityId}): missing root node");
            return false;
        }
        if (_requireProximityForVehicleClaim)
        {
            float dist = pRoot.GlobalPosition.DistanceTo(vRoot.GlobalPosition);
            if (dist > MaxInteractionDistance)
            {
                MonkeLogger.Warn($"VehicleClaim rejected (client {requesterId}, entity {entityId}): player {dist:0.0}m from vehicle (max {MaxInteractionDistance}m). Set MainScene._requireProximityForVehicleClaim=false in the editor to bypass for testing.");
                return false;
            }
        }

        _ridingByPlayer[requesterId] = entityId;
        MonkeLogger.Info($"VehicleClaim approved (client {requesterId}, entity {entityId})");
        return true;
    }

    private void OnServerCommandReceived(int clientId, IPackableMessage command)
    {
        if (command is ReleaseVehicleMessage release)
        {
            var entity = EntitySpawner.Instance.Entities.Find(e => e.EntityId == release.EntityId);
            if (entity == null || entity.Authority != clientId) return;

            ServerManager.Instance.ChangeAuthority(release.EntityId, 0);
            _ridingByPlayer.Remove(clientId);
            return;
        }

        if (command is SpawnVehicleRequestMessage)
        {
            // authority = 0 → server-owned; clients see a DummyVehicle and must press F
            // near it to claim ownership (validated by OnVehicleAuthorityRequested).
            ServerManager.Instance.SpawnEntity<Node3D>(entityType: 2, authority: 0);
            return;
        }
    }

    private void OnServerClientDisconnected(int clientId)
    {
        // If the client was driving a vehicle, return that vehicle to the server so a
        // future joiner can claim it.
        if (!_ridingByPlayer.TryGetValue(clientId, out int vehicleId)) return;
        _ridingByPlayer.Remove(clientId);
        var entity = EntitySpawner.Instance.Entities.Find(e => e.EntityId == vehicleId);
        if (entity != null && entity.Authority == clientId)
            ServerManager.Instance.ChangeAuthority(vehicleId, 0);
    }


    private void OnSpawnButtonPressed()
    {
        if (ClientManager.Instance == null) return;
        ClientManager.Instance.MakeEntityRequest(0);
        _spawnButton.Hide();
    }

    private void OnSpawnBallButtonPressed()
    {
        if (ClientManager.Instance == null) return;
        ClientManager.Instance.MakeEntityRequest(1);
        _spawnBallButton.Hide();
    }

    // Spawn a vehicle owned by the server (not by the requesting client). The framework's
    // MakeEntityRequest assigns authority = sender, which would instantly mount this
    // player on the new vehicle. Use the demo's SpawnVehicleRequestMessage instead so the
    // vehicle starts unowned and players claim it via the F-key proximity prompt.
    private void OnSpawnVehicleButtonPressed()
    {
        if (ClientManager.Instance == null) return;
        ClientManager.Instance.SendCommandToServer(
            new SpawnVehicleRequestMessage(),
            INetworkManager.PacketModeEnum.Reliable, (int)ChannelEnum.GameReliable);
        _spawnVehicleButton.Hide();
    }

    // Test-only shortcut. Sends RequestAuthority for the first unowned vehicle in the
    // local entity list. Bypasses the F-key proximity flow so the user can repro
    // misprediction scenarios without driving the player to the vehicle first. Server
    // approver still runs — set _requireProximityForVehicleClaim = false in the editor
    // to claim from anywhere.
    private void OnClaimVehicleButtonPressed()
    {
        if (ClientManager.Instance == null) return;
        var spawner = EntitySpawner.Instance;
        if (spawner == null) return;
        // Iterate ClientEntities, not Entities — Entities holds the server's tree and is
        // empty on a pure client. ClientEntities holds the client-side scene instances
        // (LocalAuthority / Dummy variants), each with the synced Authority field.
        foreach (var entity in spawner.ClientEntities)
        {
            if (entity.EntityType == 2 && entity.Authority == 0)
            {
                ClientManager.Instance.RequestAuthority(entity.EntityId);
                return;
            }
        }
        MonkeLogger.Warn("ClaimVehicle: no unowned vehicle (EntityType=2, Authority=0) found.");
    }

    private void OnHostButtonPressed()
    {
        MonkeLogger.Info("Starting server...");
        MonkeNetManager.Instance.CreateServer(9999);
        HookServerVehicleHandling();
        GetNode<Button>("Menu/HostButton").Hide();
        GetNode<Button>("Menu/ConnectButton").Hide();
        GetNode<Button>("Menu/HostAndConnectButton").Hide();
        _stopServerButton.Show();
    }

    private void OnStopServerButtonPressed()
    {
        MonkeLogger.Info("Stopping server...");
        MonkeNetManager.Instance.StopServer();
        GetTree().CallDeferred(SceneTree.MethodName.ReloadCurrentScene);
    }

    private void OnConnectButtonPressed()
    {
        MonkeLogger.Info("Connecting...");
        MonkeNetManager.Instance.CreateClient("localhost", 9999);
        GetNode("Menu/HostButton").QueueFree();
        GetNode("Menu/ConnectButton").QueueFree();
        _connectingLabel.Text = "Connecting...";
        _connectingLabel.Show();
        _cancelButton.Show();
        SubscribeClientSignals();
    }

    private void OnReconnectButtonPressed()
    {
        MonkeLogger.Info($"Reconnecting to reclaim previous session...");
        MonkeNetManager.Instance.CreateClient("localhost", 9999);
        GetNode("Menu/HostButton").QueueFree();
        GetNode("Menu/ConnectButton").QueueFree();
        GetNode("Menu/HostAndConnectButton").QueueFree();
        _reconnectButton.Hide();
        _connectingLabel.Text = "Reconnecting...";
        _connectingLabel.Show();
        _cancelButton.Show();
        SubscribeClientSignals();
    }

    private void OnHostAndConnectButtonPressed()
    {
        MonkeLogger.Info("Connecting...");
        MonkeNetManager.Instance.CreateListenServer(9999);
        HookServerVehicleHandling();
        GetNode("Menu/HostButton").QueueFree();
        GetNode("Menu/ConnectButton").QueueFree();
        _connectingLabel.Text = "Connecting...";
        _connectingLabel.Show();
        _cancelButton.Show();
        SubscribeClientSignals();
    }

    private void SubscribeClientSignals()
    {
        ClientManager.Instance.NetworkReady += OnNetworkReady;
        ClientManager.Instance.ConnectionLost += OnConnectionLost;
        ClientManager.Instance.ConnectionFailed += OnConnectionFailed;
        ClientManager.Instance.ServerSilent += OnServerSilent;
        ClientManager.Instance.ServerResponded += OnServerResponded;
        ClientManager.Instance.LatencyCalculated += OnLatencyCalculated;
    }

    private void OnDisconnectButtonPressed()
    {
        MonkeLogger.Info("Disconnect manually requested");
        _disconnectButton.Hide();
        ClientManager.Instance.Disconnect();
        GetTree().CallDeferred(SceneTree.MethodName.ReloadCurrentScene);
    }

    private void OnCancelButtonPressed()
    {
        MonkeLogger.Info("Canceling connection attempt...");
        _cancelButton.Hide();
        ClientManager.Instance.Disconnect();
        GetTree().CallDeferred(SceneTree.MethodName.ReloadCurrentScene);
    }

    private void OnSimulateTimeoutButtonPressed()
    {
        MonkeLogger.Info("Simulating timeout disconnect");
        _disconnectButton.Hide();
        _simulateTimeoutButton.Hide();
        ClientManager.Instance.DisconnectUngraceful();
    }

    private void OnNetworkReady()
    {
        _connectingLabel.Hide();
        _cancelButton.Hide();
        _disconnectButton.Show();
        _simulateTimeoutButton.Show();
        _spawnButton.Show();
        _spawnBallButton.Show();
        _spawnVehicleButton.Show();
        _claimVehicleButton.Show();
    }

    private void OnConnectionLost()
    {
        _disconnectButton.Hide();
        _simulateTimeoutButton.Hide();
        _cancelButton.Hide();
        _spawnButton.Hide();
        _spawnBallButton.Hide();
        _spawnVehicleButton.Hide();
        _claimVehicleButton.Hide();
        MonkeLogger.Info("Connection lost. Returning to main menu.");
        _connectingLabel.Text = "Connection lost.";
        _connectingLabel.Show();
        GetTree().CreateTimer(2.0f).Timeout += () =>
            GetTree().CallDeferred(SceneTree.MethodName.ReloadCurrentScene);
    }

    private void OnConnectionFailed()
    {
        _cancelButton.Hide();
        _spawnButton.Hide();
        _spawnBallButton.Hide();
        _spawnVehicleButton.Hide();
        _claimVehicleButton.Hide();
        _connectingLabel.Text = "Failed to connect.";
        GetTree().CreateTimer(2.0f).Timeout += () =>
            GetTree().CallDeferred(SceneTree.MethodName.ReloadCurrentScene);
    }

    private void OnServerSilent()
    {
        _noResponseLabel.Show();
    }

    private void OnServerResponded()
    {
        _noResponseLabel.Hide();
    }

    private void OnLatencyCalculated(int latencyTicks, int jitterTicks)
    {
        float tickMs = 1000f / Engine.PhysicsTicksPerSecond;
        _highPingLabel.Visible = latencyTicks * tickMs > 150f;
        _packetLossLabel.Visible = jitterTicks * tickMs > 30f;
    }
}
