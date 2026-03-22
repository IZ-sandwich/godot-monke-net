using Godot;
using MonkeNet.Client;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace GameDemo;

public partial class LocalBallPrediction : ClientPredictedEntity
{
    [Export] private float _maxDeviationAllowedSquared = 0.001f;
    [Export] private RigidBody3D _rigidBody;

    public override void OnProcessTick(int tick, IPackableElement input) { }

    public override Vector3 GetPosition()
    {
        return _rigidBody.Position;
    }

    public override bool HasMisspredicted(int tick, IEntityStateData receivedState, Vector3 savedState)
    {
        EntityStateMessage state = (EntityStateMessage)receivedState;
        return (state.Position - savedState).LengthSquared() > _maxDeviationAllowedSquared;
    }

    public override void HandleReconciliation(IEntityStateData receivedState)
    {
        var state = (EntityStateMessage)receivedState;
        _rigidBody.Position = state.Position;
        _rigidBody.Rotation = state.Rotation;
        _rigidBody.LinearVelocity = state.Velocity;
        _rigidBody.AngularVelocity = state.AngularVelocity;
        _rigidBody.ForceUpdateTransform();
    }

    public override void ResimulateTick(IPackableElement input) { }
}
