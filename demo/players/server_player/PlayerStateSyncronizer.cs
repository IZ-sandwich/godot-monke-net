using Godot;
using MonkeNet.Serializer;
using MonkeNet.Server;
using MonkeNet.Shared;

namespace GameDemo;

public partial class PlayerStateSyncronizer : ServerStateSyncronizer
{
    [Export] private CharacterBody3D _characterBody;
    [Export] private SharedPlayerMovement _playerMovement;

    // Vertical offset above the vehicle while riding. Must match LocalPlayerPrediction
    // and MainScene so prediction and the authoritative state agree.
    private static readonly Vector3 RideOffset = new(0, 1.5f, 0);

    public float Yaw { get; set; }

    public override void OnProcessTick(int tick, IPackableElement genericInput)
    {
        CharacterInputMessage input = (CharacterInputMessage)genericInput;
        Yaw = input.CameraYaw;

        // Mirror LocalPlayerPrediction: while this player owns a vehicle, pin them on top
        // and skip MoveAndSlide. Without this, the server's player would walk off the
        // vehicle each tick (the start-of-tick anchor in MainScene.OnServerTickAnchorRiders
        // is overwritten by MoveAndSlide here) and PushRigidBodies would apply spurious
        // collision impulses to the server's vehicle — making the server's vehicle drift
        // away from the client's predicted vehicle on every snapshot.
        if (TryAnchorToOwnedVehicle()) return;

        _playerMovement.AdvancePhysics(input);
    }

    private bool TryAnchorToOwnedVehicle()
    {
        foreach (var entity in EntitySpawner.Instance.Entities)
        {
            if (entity.EntityType == 2 && entity.Authority == this.Authority)
            {
                if (MainScene.AutoRideOnClaim)
                {
                    var vRoot = EntitySpawner.Instance.GetEntityRoot(entity);
                    if (vRoot == null) return false;
                    _characterBody.GlobalPosition = vRoot.GlobalPosition + RideOffset;
                }
                _characterBody.Velocity = Vector3.Zero;
                return true;
            }
        }
        return false;
    }

    //Capture current entity state, sent by the Server Entity Manager to all clients
    public override IEntityStateData PackEntityState()
    {
        return new EntityStateMessage
        {
            EntityId = this.EntityId,
            Yaw = this.Yaw,
            Position = _characterBody.Position,
            Velocity = _characterBody.Velocity
        };
    }
}
