using Godot;
using MonkeNet.Server;
using MonkeNet.Shared;

namespace YourGame;

// Server-side physics prop networking component.
// Jolt steps the RigidBody3D automatically via SpaceStep each tick;
// this component just reads the result and packages it for broadcast.
//
// Scene structure:
//   [Node3D root]
//     ├── NetworkBehaviour
//     ├── PropStateSyncronizer  ← this file
//     └── RigidBody3D           ← wired via export, with CollisionShape3D child
public partial class PropStateSyncronizer : ServerStateSyncronizer
{
    [Export] private RigidBody3D _rigidBody;

    // No OnProcessTick override needed — props have no player input.

    public override IEntityStateData PackEntityState()
    {
        return new PhysicsStateMessage
        {
            EntityId        = EntityId,
            Position        = _rigidBody.Position,
            Rotation        = _rigidBody.Basis.GetRotationQuaternion(),
            LinearVelocity  = _rigidBody.LinearVelocity,
            AngularVelocity = _rigidBody.AngularVelocity,
            IsAwake         = !PhysicsServer3D.BodyGetDirectState(_rigidBody.GetRid()).IsSleeping(),
        };
    }
}
