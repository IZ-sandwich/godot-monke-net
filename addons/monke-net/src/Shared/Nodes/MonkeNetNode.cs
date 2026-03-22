using Godot;
using MonkeNet.Shared;

[GlobalClass, Icon("res://addons/monke-net/resources/circle_nodes_solid.png")]
public abstract partial class MonkeNetNode : Node
{
    public T GetComponent<T>() where T : MonkeNetNode
    {
        return MonkeNetComponents.GetComponent<T>(GetParent()); //FIXME: use lookup table instead of this
    }
}
