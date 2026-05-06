using Godot;
using MonkeNet.Server;
using MonkeNet.Shared;

namespace GameDemo;

public partial class ServerBallStateSyncronizer : ServerStateSyncronizer
{
    [Export] private PredictionRigidbody3D _predictionRb;

    public override void OnEntitySpawned()
    {
        GetParent<Node3D>().Position = new Vector3(0, 10, 0);
    }

    public override IEntityStateData PackEntityState()
    {
        var state = _predictionRb.SnapshotState();
        return new EntityStateMessage
        {
            EntityId = this.EntityId,
            Yaw = 0,
            Position = state.Position,
            Rotation = state.Rotation.GetEuler(),
            Velocity = state.LinearVelocity,
            AngularVelocity = state.AngularVelocity,
        };
    }
}
