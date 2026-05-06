using Godot;
using MonkeNet.Client;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace GameDemo;

public partial class LocalBallPrediction : ClientPredictedEntity
{
    [Export] private float _maxDeviationAllowedSquared = 0.001f;
    [Export] private PredictionRigidbody3D _predictionRb;

    public override void OnProcessTick(int tick, IPackableElement input) { }

    public override Vector3 GetPosition()
    {
        return _predictionRb.Body.GlobalPosition;
    }

    public override bool HasMisspredicted(int tick, IEntityStateData receivedState, Vector3 savedState)
    {
        EntityStateMessage state = (EntityStateMessage)receivedState;
        return (state.Position - savedState).LengthSquared() > _maxDeviationAllowedSquared;
    }

    public override void HandleReconciliation(IEntityStateData receivedState)
    {
        var state = (EntityStateMessage)receivedState;
        _predictionRb.Reconcile(new RigidbodyState
        {
            Position = state.Position,
            Rotation = Quaternion.FromEuler(state.Rotation),
            LinearVelocity = state.Velocity,
            AngularVelocity = state.AngularVelocity,
        });
    }

    public override void ResimulateTick(IPackableElement input) { }
}
