using Godot;
using MonkeNet.Client;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace YourGame;

// Client-side prediction component for the locally-owned player.
// Add as a child of your local player scene alongside a NetworkBehaviour node.
// Must use identical physics code to PlayerStateSyncronizer so simulations stay in sync.
//
// Scene structure:
//   [CharacterBody3D root]
//     ├── NetworkBehaviour
//     ├── LocalPlayerPrediction  ← this file
//     └── SharedPlayerMovement   ← same node type as on the server scene
public partial class LocalPlayerPrediction : ClientPredictedEntity
{
    [Export] private float _mispredictionThresholdSquared = 0.001f;
    [Export] private CharacterBody3D _characterBody;
    [Export] private Node _playerMovement; // Wire to your shared movement logic node.

    public override void OnProcessTick(int tick, IPackableElement input)
    {
        // TODO: call your movement logic, e.g.:
        // _playerMovement.AdvancePhysics((PlayerInputMessage)input);
    }

    public override bool HasMisspredicted(int tick, IEntityStateData receivedState, Vector3 savedPosition)
    {
        var state = (PlayerStateMessage)receivedState;
        return (state.Position - savedPosition).LengthSquared() > _mispredictionThresholdSquared;
    }

    public override void HandleReconciliation(IEntityStateData receivedState)
    {
        var state = (PlayerStateMessage)receivedState;
        _characterBody.Position = state.Position;
        _characterBody.Velocity = state.Velocity;
    }

    public override void ResimulateTick(IPackableElement input)
    {
        // Must be identical to OnProcessTick — called for every stored tick during rollback.
        // TODO: call your movement logic, e.g.:
        // _playerMovement.AdvancePhysics((PlayerInputMessage)input);
    }

    public override Vector3 GetPosition() => _characterBody.Position;
}
