using Godot;

using MonkeNet.Shared;

public partial class ServerBallNetworkBehaviour : NetworkBehaviour
{
    public override void OnEntitySpawned()
    {
        GetParent<Node3D>().Position = new Vector3(0, 10, 0);
    }
}
