using Godot;
using MonkeNet.Client;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace GameDemo;

public partial class LocalPlayerPrediction : ClientPredictedEntity
{
    [Export] private float _maxDeviationAllowedSquared = 0.001f;
    [Export] private SharedPlayerMovement _playerMovement;
    [Export] private CharacterBody3D _characterBody;

    // Vertical offset above the vehicle while riding. Matches the server-side anchor in
    // MainScene so prediction and the authoritative state agree, avoiding rollbacks.
    private static readonly Vector3 RideOffset = new(0, 1.5f, 0);

    // Called every physics tick (but synced to network clock)
    public override void OnProcessTick(int tick, IPackableElement input)
    {
        if (TryAnchorToOwnedVehicle()) return;
        _playerMovement.AdvancePhysics((CharacterInputMessage)input);
    }

    // We have misspredicted, return player back to authoritative position
    public override void HandleReconciliation(IEntityStateData receivedState)
    {
        EntityStateMessage state = (EntityStateMessage)receivedState;
        _characterBody.Position = state.Position;
        _characterBody.Velocity = state.Velocity;
    }

    // Check if we have misspredicted
    public override bool HasMisspredicted(int tick, IEntityStateData receivedState, Vector3 savedPosition)
    {
        EntityStateMessage state = (EntityStateMessage)receivedState;
        return (state.Position - savedPosition).LengthSquared() > _maxDeviationAllowedSquared;
    }

    // When the client is re-simulating inputs, what should we do with it? usually the same we do on process tick
    public override void ResimulateTick(IPackableElement input)
    {
        if (TryAnchorToOwnedVehicle()) return;
        _playerMovement.AdvancePhysics((CharacterInputMessage)input);
    }

    public override Vector3 GetPosition()
    {
        return _characterBody.Position;
    }

    // While the local client owns a vehicle, skip MoveAndSlide on the player so WASD
    // drives the vehicle instead. By default also pin the player on top of the vehicle
    // (rider mode); when MainScene.AutoRideOnClaim is false the player keeps its own
    // position — useful for testing pure vehicle control without the player-on-top
    // collision artifact.
    private bool TryAnchorToOwnedVehicle()
    {
        foreach (var entity in EntitySpawner.Instance.ClientEntities)
        {
            if (entity is LocalVehiclePrediction vehiclePred)
            {
                if (MainScene.AutoRideOnClaim)
                {
                    var vehicleRoot = EntitySpawner.Instance.GetEntityRoot(vehiclePred);
                    if (vehicleRoot == null) return false;
                    _characterBody.GlobalPosition = vehicleRoot.GlobalPosition + RideOffset;
                }
                _characterBody.Velocity = Vector3.Zero;
                return true;
            }
        }
        return false;
    }
}
