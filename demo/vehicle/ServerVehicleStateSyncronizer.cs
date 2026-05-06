using Godot;
using MonkeNet.Server;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace GameDemo;

public partial class ServerVehicleStateSyncronizer : ServerStateSyncronizer
{
    [Export] private PredictionRigidbody3D _predictionRb;

    public override void OnEntitySpawned()
    {
        // Spawn slightly off-axis from the ball so a fresh client gets visual variety.
        GetParent<Node3D>().Position = new Vector3(5, 1, 0);
    }

    public override void OnProcessTick(int tick, IPackableElement input)
    {
        if (input is CharacterInputMessage cmd)
            VehiclePhysics.AdvancePhysics(_predictionRb, cmd);
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
