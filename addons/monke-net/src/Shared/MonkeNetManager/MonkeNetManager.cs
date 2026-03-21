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
        EntitySpawner = GetNode<EntitySpawner>("EntitySpawner");
    }

    public void CreateClient(string address, int port)
    {
        IsServer = false;
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
        IsServer = true;
        var serverManagerScene = GD.Load<PackedScene>("res://addons/monke-net/scenes/ServerManager.tscn");
        var serverManager = serverManagerScene.Instantiate() as Server.ServerManager;
        AddChild(serverManager);

        if (MonkeNetConfig.Instance.CustomServerScene != null)
        {
            MonkeNetConfig.Instance.AddChild(MonkeNetConfig.Instance.CustomServerScene.Instantiate());
        }

        // TODO: pass configurations as struct/.ini
        serverManager.Initialize(_networkManager, port);
    }
}