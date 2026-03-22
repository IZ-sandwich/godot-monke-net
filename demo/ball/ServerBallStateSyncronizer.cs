using Godot;
using MonkeNet.Server;
using MonkeNet.Shared;

namespace GameDemo;

public partial class ServerBallStateSyncronizer : ServerStateSyncronizer
{
    [Export] private RigidBody3D _rigidBody;

    public override IEntityStateData PackEntityState()
    {
        return new EntityStateMessage
        {
            EntityId = this.NetworkBehaviour.EntityId,
            Yaw = 0,
            Position = _rigidBody.Position,
            Rotation = _rigidBody.Rotation,
            Velocity = _rigidBody.LinearVelocity,
            AngularVelocity = _rigidBody.AngularVelocity,
        };
    }
}
