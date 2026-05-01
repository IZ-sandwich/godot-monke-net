using Godot;
using MonkeNet.Client;
using MonkeNet.Shared;

namespace GameDemo;

public partial class MainScene : Node3D
{
    private static readonly string FLAG_DEDICATED_SERVER = "as_server";

    private Label _connectingLabel;

    public override void _Ready()
    {
        _connectingLabel = GetNode<Label>("Menu/ConnectingLabel");

        if (OS.HasFeature(FLAG_DEDICATED_SERVER))
        {
            MonkeNetManager.Instance.CreateServer(9999);
        }
    }

    // When the client clicks "Spawn" we request the server to spawn a Player entity for us
    private void OnSpawnButtonPressed()
    {
        ClientManager.Instance.MakeEntityRequest(0);
        GetNode("Menu/SpawnButton").QueueFree();
    }

    // When the client clicks "Spawn Ball" we request the server to spawn a Ball entity for us
    private void OnSpawnBallButtonPressed()
    {
        ClientManager.Instance.MakeEntityRequest(1);
        GetNode("Menu/SpawnBallButton").QueueFree();
    }

    // Creates game server
    private void OnHostButtonPressed()
    {
        MonkeNetManager.Instance.CreateServer(9999);
        GetNode("Menu").QueueFree();
    }

    // Creates Client and connects to localhost
    private void OnConnectButtonPressed()
    {
        GD.Print("Connecting...");
        MonkeNetManager.Instance.CreateClient("localhost", 9999);
        GetNode("Menu/HostButton").QueueFree();
        GetNode("Menu/ConnectButton").QueueFree();
        _connectingLabel.Show();
        ClientManager.Instance.NetworkReady += OnNetworkReady;
    }

    // Host and connect to self
    private void OnHostAndConnectButtonPressed()
    {
        GD.Print("Connecting...");
        MonkeNetManager.Instance.CreateListenServer(9999);
        GetNode("Menu/HostButton").QueueFree();
        GetNode("Menu/ConnectButton").QueueFree();
        _connectingLabel.Show();
        ClientManager.Instance.NetworkReady += OnNetworkReady;
    }

    private void OnNetworkReady()
    {
        GD.Print("Connection established.");
        _connectingLabel.Hide();
        ClientManager.Instance.NetworkReady -= OnNetworkReady;
    }
}
