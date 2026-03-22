using Godot;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace MonkeNet.Client;

[GlobalClass, Icon("res://addons/monke-net/resources/link-solid.png")]
public abstract partial class InternalClientComponent : Node
{
    protected virtual void OnCommandReceived(IPackableMessage command) { }
    protected virtual void OnLatencyCalculated(int latencyAverageTicks, int jitterAverageTicks) { }
    protected virtual void OnProcessTick(int currentTick, IPackableElement input) { }

    private bool _networkReady = false;

    public override void _Ready()
    {
        ClientManager.Instance.ClientTick += OnProcessTick;
        ClientManager.Instance.NetworkReady += OnNetworkReady;
        ClientManager.Instance.CommandReceived += OnCommandReceived;
        ClientManager.Instance.LatencyCalculated += OnLatencyCalculated;
    }

    protected static void SendCommandToServer(IPackableMessage command, INetworkManager.PacketModeEnum mode, int channel)
    {
        ClientManager.Instance.SendCommandToServer(command, mode, channel);
    }

    private void OnNetworkReady()
    {
        _networkReady = true;
    }

    protected static int NetworkId
    {
        get { return ClientManager.Instance.GetNetworkId(); }
    }

    protected bool NetworkReady
    {
        get { return _networkReady; }
    }
}