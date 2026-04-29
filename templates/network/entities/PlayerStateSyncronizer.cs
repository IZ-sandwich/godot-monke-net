using Godot;
using MonkeNet.Server;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace YourGame;

// Server-side player networking component.
// Add as a child of your server player scene alongside a NetworkBehaviour node.
//
// Scene structure:
//   [CharacterBody3D root]
//     ├── NetworkBehaviour      ← required by EntitySpawner
//     ├── PlayerStateSyncronizer  ← this file
//     └── SharedPlayerMovement  ← your movement logic node (wired via export)
public partial class PlayerStateSyncronizer : ServerStateSyncronizer
{
    [Export] private CharacterBody3D _characterBody;
    [Export] private Node _playerMovement; // Wire to your shared movement logic node.

    private float _yaw;
    private float _pitch;

    public override void OnProcessTick(int tick, IPackableElement genericInput)
    {
        var input = (PlayerInputMessage)genericInput;
        _yaw   = input.CameraYaw;
        _pitch = input.CameraPitch;

        // TODO: call your movement logic, e.g.:
        // _playerMovement.AdvancePhysics(input);

        EmitSignal(SignalName.TickProcessed, _characterBody.Position, _characterBody.Velocity, _yaw, _pitch);
    }

    public override IEntityStateData PackEntityState()
    {
        return new PlayerStateMessage
        {
            EntityId = EntityId,
            Position = _characterBody.Position,
            Velocity = _characterBody.Velocity,
            Yaw      = _yaw,
            Pitch    = _pitch,
        };
    }

    // Connect from GDScript children to react to each tick (animations, sounds, etc.)
    [Signal] public delegate void TickProcessedEventHandler(Vector3 position, Vector3 velocity, float yaw, float pitch);
}
