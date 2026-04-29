using Godot;
using MonkeNet.Server;
using MonkeNet.Shared;
using MonkeNet.Serializer;


namespace YourGame;

// Server-side vehicle networking component.
// Receives the driver's VehicleInputMessage each tick, advances vehicle physics,
// then packages full rigid-body state for broadcast.
//
// Scene structure:
//   [CharacterBody3D (or VehicleBody3D) root]
//     ├── NetworkBehaviour
//     ├── VehicleStateSyncronizer  ← this file
//     └── SharedVehiclePhysics     ← your vehicle physics node (wired via export)
//                                     Must be identical to the LocalVehicle scene's node.
public partial class VehicleStateSyncronizer : ServerStateSyncronizer
{
    [Export] private CharacterBody3D _vehicleBody;
    [Export] private Node _vehiclePhysics; // Wire to your shared vehicle physics node.

    public override void OnProcessTick(int tick, IPackableElement genericInput)
    {
        var input = (VehicleInputMessage)genericInput;

        // TODO: call your vehicle physics node, e.g.:
        // _vehiclePhysics.AdvancePhysics(input);

        EmitSignal(SignalName.TickProcessed,
            _vehicleBody.GlobalPosition,
            _vehicleBody.GlobalBasis.GetRotationQuaternion(),
            _vehicleBody.Velocity);
    }

    public override IEntityStateData PackEntityState()
    {
        return new PhysicsStateMessage
        {
            EntityId        = EntityId,
            Position        = _vehicleBody.GlobalPosition,
            Rotation        = _vehicleBody.GlobalBasis.GetRotationQuaternion(),
            LinearVelocity  = _vehicleBody.Velocity,
            AngularVelocity = Vector3.Zero, // Update if your physics node tracks angular velocity.
            IsAwake         = true,
        };
    }

    [Signal] public delegate void TickProcessedEventHandler(Vector3 position, Quaternion rotation, Vector3 velocity);
}
