using Godot;
using MonkeNet.Client;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace YourGame;

// Client-side physics prop prediction component.
// Because the game is heavily physics-based, props must simulate locally so
// the player's CharacterBody3D collides with them during client-side prediction.
// The RigidBody3D is stepped automatically alongside the player via SpaceStep.
//
// When the server snapshot arrives and the position has diverged, this component
// reconciles by snapping the rigid body to the authoritative state. The prediction
// loop then re-simulates subsequent ticks (SpaceStep handles the physics; no manual
// re-simulation code is needed here since props have no input).
//
// Scene structure (both ClientAuthorityScene and ClientDummyScene point to this):
//   [Node3D root]
//     ├── NetworkBehaviour
//     ├── ClientPropPrediction  ← this file
//     └── RigidBody3D           ← wired via export, CollisionShape3D MUST match server
//
// Note: configure both ClientAuthorityScene and ClientDummyScene in
// EntitySpawnConfiguration to point to the same scene so ALL clients simulate
// the prop locally, not just the owning client.
public partial class ClientPropPrediction : ClientPredictedEntity
{
    [Export] private float _mispredictionThresholdSquared = 0.25f; // ~0.5m
    [Export] private RigidBody3D _rigidBody;

    // Props have no player input — physics is driven by SpaceStep alone.
    public override void OnProcessTick(int tick, IPackableElement input) { }

    public override Vector3 GetPosition() => _rigidBody.Position;

    public override bool HasMisspredicted(int tick, IEntityStateData receivedState, Vector3 savedPosition)
    {
        var state = (PhysicsStateMessage)receivedState;
        return (state.Position - savedPosition).LengthSquared() > _mispredictionThresholdSquared;
    }

    public override void HandleReconciliation(IEntityStateData receivedState)
    {
        var state = (PhysicsStateMessage)receivedState;
        _rigidBody.Position        = state.Position;
        _rigidBody.Basis           = new Basis(state.Rotation);
        _rigidBody.LinearVelocity  = state.LinearVelocity;
        _rigidBody.AngularVelocity = state.AngularVelocity;
        _rigidBody.Sleeping        = !state.IsAwake;
        _rigidBody.ForceUpdateTransform();
    }

    // No ResimulateTick logic needed — SpaceStep re-simulates the rigid body automatically.
    public override void ResimulateTick(IPackableElement input) { }
}
