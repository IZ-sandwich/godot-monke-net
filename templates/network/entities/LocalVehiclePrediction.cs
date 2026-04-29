using Godot;
using MonkeNet.Client;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace YourGame;

// Client-side prediction component for the vehicle driven by this client.
// Must use identical physics code to VehicleStateSyncronizer.
//
// Scene structure:
//   [CharacterBody3D root]
//     ├── NetworkBehaviour
//     ├── LocalVehiclePrediction  ← this file
//     └── SharedVehiclePhysics    ← same node type as on the server scene
public partial class LocalVehiclePrediction : ClientPredictedEntity
{
    [Export] private float _mispredictionThresholdSquared = 0.01f;
    [Export] private CharacterBody3D _vehicleBody;
    [Export] private Node _vehiclePhysics; // Wire to your shared vehicle physics node.

    public override void OnProcessTick(int tick, IPackableElement input)
    {
        // TODO: call your vehicle physics node, e.g.:
        // _vehiclePhysics.AdvancePhysics((VehicleInputMessage)input);
    }

    public override bool HasMisspredicted(int tick, IEntityStateData receivedState, Vector3 savedPosition)
    {
        var state = (PhysicsStateMessage)receivedState;
        return (state.Position - savedPosition).LengthSquared() > _mispredictionThresholdSquared;
    }

    public override void HandleReconciliation(IEntityStateData receivedState)
    {
        var state = (PhysicsStateMessage)receivedState;
        _vehicleBody.GlobalPosition = state.Position;
        _vehicleBody.GlobalBasis    = new Basis(state.Rotation);
        _vehicleBody.Velocity       = state.LinearVelocity;
    }

    public override void ResimulateTick(IPackableElement input)
    {
        // Must be identical to OnProcessTick.
        // TODO: call your vehicle physics node, e.g.:
        // _vehiclePhysics.AdvancePhysics((VehicleInputMessage)input);
    }

    public override Vector3 GetPosition() => _vehicleBody.GlobalPosition;
}
