using GameDemo;
using Godot;
using MonkeNet.Client;
using MonkeNet.Serializer;
using MonkeNet.Shared;

public partial class LocalPlayerPrediction : ClientPredictedEntity
{
    [Export] private float _maxDeviationAllowedSquared = 0.001f;
    [Export] private SharedPlayerMovement _playerMovement;
    [Export] private CharacterBody3D _characterBody;

    // Called every physics tick (but synced to network clock)
    public override void OnProcessTick(int tick, IPackableElement input)
    {
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
    public override bool HasMisspredicted(IEntityStateData receivedState, Vector3 savedPosition)
    {
        EntityStateMessage state = (EntityStateMessage)receivedState;
        return (state.Position - savedPosition).LengthSquared() > _maxDeviationAllowedSquared;
    }

    // When the client is re-simulating inputs, what should we do with it? usually the same we do on process tick
    public override void ResimulateTick(IPackableElement input)
    {
        _playerMovement.AdvancePhysics((CharacterInputMessage)input);
    }

    public override Vector3 GetPosition()
    {
        return _characterBody.Position;
    }
}
