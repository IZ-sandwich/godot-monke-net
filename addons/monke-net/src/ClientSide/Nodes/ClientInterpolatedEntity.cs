using Godot;
using MonkeNet.Shared;

namespace MonkeNet.Client;

[GlobalClass, Icon("res://addons/monke-net/resources/circle_nodes_solid.png")]
public partial class ClientInterpolatedEntity : MonkeNetNode
{
    public NetworkBehaviour NetworkBehaviour { get; set; }

    public virtual void HandleStateInterpolation(IEntityStateData past, IEntityStateData future, float interpolationFactor) { }

    public override void _Ready()
    {
        NetworkBehaviour = GetComponent<NetworkBehaviour>() ?? throw new MonkeNetException($"Could not find {typeof(NetworkBehaviour).Name}!");
    }

}
