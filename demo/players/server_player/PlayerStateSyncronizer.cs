using Godot;
using MonkeNet.Serializer;
using MonkeNet.Server;
using MonkeNet.Shared;

namespace GameDemo;

public partial class PlayerStateSyncronizer : ServerStateSyncronizer
{
    [Export] private CharacterBody3D _characterBody;
    [Export] private SharedPlayerMovement _playerMovement;

    public float Yaw { get; set; }

    public override void OnProcessTick(int tick, IPackableElement genericInput)
    {
        CharacterInputMessage input = (CharacterInputMessage)genericInput;
        Yaw = input.CameraYaw;
        _playerMovement.AdvancePhysics(input);
    }

    //Capture current entity state, sent by the Server Entity Manager to all clients
    public override IEntityStateData PackEntityState()
    {
        return new EntityStateMessage
        {
            EntityId = this.NetworkBehaviour.EntityId,
            Yaw = this.Yaw,
            Position = _characterBody.Position,
            Velocity = _characterBody.Velocity
        };
    }
}
