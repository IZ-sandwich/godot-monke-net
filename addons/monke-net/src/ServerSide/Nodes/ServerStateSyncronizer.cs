using Godot;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace MonkeNet.Server;

[GlobalClass, Icon("res://addons/monke-net/resources/circle_nodes_solid.png")]
public partial class ServerStateSyncronizer : MonkeNetNode
{
    public NetworkBehaviour NetworkBehaviour { get; set; }

    public virtual IEntityStateData PackEntityState() { return null; }
    public virtual void OnProcessTick(int tick, IPackableElement genericInput) { }

    public override void _Ready()
    {
        NetworkBehaviour = GetComponent<NetworkBehaviour>();
    }
}
