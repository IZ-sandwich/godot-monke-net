
using Godot;

namespace MonkeNet.Shared;

[GlobalClass, Icon("res://addons/monke-net/resources/circle_nodes_solid.png")]

public partial class NetworkBehaviour : MonkeNetNode
{
    public int EntityId { get; set; }
    public byte EntityType { get; set; }
    public int Authority { get; set; }
    public string Metadata { get; set; }
    public Node Parent { get; set; }

    public virtual void OnEntityRemoved() { }
    public virtual void OnEntitySpawned() { }

    public override void _EnterTree()
    {
        Parent = GetParent();
    }
}
