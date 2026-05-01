using Godot;
using MonkeNet.Serializer;

namespace MonkeNet.Shared;

public partial class MonkeNetManager : Node
{
    public static MonkeNetManager Instance { get; private set; }
    public bool IsServer { get; private set; } = false;
    public EntitySpawner EntitySpawner { get; private set; }
    public Rid PhysicsSpace { get; private set; }

    private INetworkManager _networkManager;
    private NetworkManagerEnet _clientNetworkManager;

    public override void _EnterTree()
    {
        Instance = this;
        PhysicsSpace = GetViewport().World3D.Space;
        PhysicsServer3D.SpaceSetActive(PhysicsSpace, false); // MonkeNet advances physics manually
        MessageSerializer.RegisterNetworkMessages();
    }

    public override void _Ready()
    {
        if (MonkeNetConfig.Instance == null)
            throw new MonkeNetException("Missing MonkeNet configuration node! Please add the MonkeNetConfig node to your main scene.");

        _networkManager = GetNode<INetworkManager>("NetworkManagerEnet");
        _clientNetworkManager = GetNode<NetworkManagerEnet>("NetworkManagerEnetClient");
        EntitySpawner = GetNode<EntitySpawner>("EntitySpawner");
    }

    public void CreateClient(string address, int port)
    {
        var clientManagerScene = GD.Load<PackedScene>("res://addons/monke-net/scenes/ClientManager.tscn");
        var clientManager = clientManagerScene.Instantiate() as Client.ClientManager;
        AddChild(clientManager);

        if (MonkeNetConfig.Instance.CustomClientScene != null)
        {
            MonkeNetConfig.Instance.AddChild(MonkeNetConfig.Instance.CustomClientScene.Instantiate());
        }

        // TODO: pass configurations as struct/.ini
        clientManager.Initialize(_networkManager, address, port);
    }

    public void CreateServer(int port)
    {
        var serverManagerScene = GD.Load<PackedScene>("res://addons/monke-net/scenes/ServerManager.tscn");
        var serverManager = serverManagerScene.Instantiate() as Server.ServerManager;
        AddChild(serverManager);

        if (MonkeNetConfig.Instance.CustomServerScene != null)
        {
            MonkeNetConfig.Instance.AddChild(MonkeNetConfig.Instance.CustomServerScene.Instantiate());
        }

        IsServer = true;

        // TODO: pass configurations as struct/.ini
        serverManager.Initialize(_networkManager, port);
    }

    public void CreateListenServer(int port)
    {
        // Server uses the primary network manager.
        CreateServer(port);

        // The client needs its own SceneMultiplayer so that calling CreateClient()
        // does not overwrite the server's ENet peer on the shared SceneMultiplayer,
        // which would put the server into CONNECTING state and break snapshot sends.
        // SetMultiplayer registers the custom api with the scene tree so Godot
        // polls the underlying ENet peer — without this the handshake never completes.
        var clientMultiplayer = new SceneMultiplayer();
        GetTree().SetMultiplayer(clientMultiplayer, _clientNetworkManager.GetPath());
        _clientNetworkManager.UseCustomMultiplayer(clientMultiplayer);

        var clientManagerScene = GD.Load<PackedScene>("res://addons/monke-net/scenes/ClientManager.tscn");
        var clientManager = clientManagerScene.Instantiate() as Client.ClientManager;
        AddChild(clientManager);

        if (MonkeNetConfig.Instance.CustomClientScene != null)
            MonkeNetConfig.Instance.AddChild(MonkeNetConfig.Instance.CustomClientScene.Instantiate());

        clientManager.Initialize(_clientNetworkManager, "localhost", port);
    }
}