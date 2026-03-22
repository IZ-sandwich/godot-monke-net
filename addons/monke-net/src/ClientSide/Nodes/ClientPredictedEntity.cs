using Godot;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace MonkeNet.Client;

[GlobalClass, Icon("res://addons/monke-net/resources/circle_nodes_solid.png")]
public partial class ClientPredictedEntity : MonkeNetNode
{
    public NetworkBehaviour NetworkBehaviour { get; set; }

    public virtual void OnProcessTick(int tick, IPackableElement input) { }
    public virtual bool HasMisspredicted(IEntityStateData receivedState, Vector3 savedState) { return false; }
    public virtual void HandleReconciliation(IEntityStateData receivedState) { }
    public virtual void ResimulateTick(IPackableElement input) { }
    public virtual Vector3 GetPosition() { return Vector3.Zero; }

    public override void _Ready()
    {
        NetworkBehaviour = GetComponent<NetworkBehaviour>()
            ?? throw new MonkeNetException($"Could not find {typeof(NetworkBehaviour).Name}!");
    }

}
