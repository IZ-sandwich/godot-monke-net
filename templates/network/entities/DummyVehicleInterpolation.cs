using Godot;
using MonkeNet.Client;
using MonkeNet.Shared;

namespace YourGame;

// Client-side interpolation component for vehicles driven by other players.
// Purely visual — no physics body. Position and rotation slerp between snapshots.
//
// Scene structure:
//   [Node3D root]
//     ├── NetworkBehaviour
//     ├── DummyVehicleInterpolation  ← this file
//     └── [vehicle mesh, wheel nodes, etc.]
public partial class DummyVehicleInterpolation : ClientInterpolatedEntity
{
    [Export] private Node3D _body; // Root or visual node to reposition.

    public override void HandleStateInterpolation(IEntityStateData past, IEntityStateData future, float interpolationFactor)
    {
        var p = (PhysicsStateMessage)past;
        var f = (PhysicsStateMessage)future;

        _body.GlobalPosition = p.Position.Lerp(f.Position, interpolationFactor);
        _body.GlobalBasis    = new Basis(p.Rotation.Slerp(f.Rotation, interpolationFactor));

        Vector3 velocity = p.LinearVelocity.Lerp(f.LinearVelocity, interpolationFactor);
        EmitSignal(SignalName.StateInterpolated,
            _body.GlobalPosition,
            _body.GlobalBasis.GetRotationQuaternion(),
            velocity);
    }

    // Connect from GDScript to animate wheels, play engine sound, trigger exhaust VFX, etc.
    [Signal] public delegate void StateInterpolatedEventHandler(Vector3 position, Quaternion rotation, Vector3 velocity);
}
