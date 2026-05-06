using Godot;
using MonkeNet.Client;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace GameDemo;

public partial class LocalVehiclePrediction : ClientPredictedEntity
{
    // Tolerance is ~20 cm because Jolt collision response is not bit-deterministic across
    // processes — contact normals, friction, and persistent-contact caches can each diverge
    // by a few cm per impact, and 3 cm (the player threshold) makes every wall hit trigger
    // a reconcile. 20 cm hides those without being visibly off authoritative; bigger drifts
    // still snap. This mirrors Fish-Net's LocalReconcileCorrectionType=None path.
    [Export] private float _maxDeviationAllowedSquared = 0.04f;
    // Per-snapshot fraction of accumulated drift to absorb when below the hard reconcile
    // threshold. 0 disables soft correction; 1 would full-snap every snapshot. 0.1 gives a
    // ~7-snapshot half-life: at 30 Hz snapshots, drift converges to zero in ~230 ms without
    // a visible jump. Position is corrected by relative shift (preserves momentum); velocity
    // and rotation are slerped toward authoritative.
    [Export] private float _softCorrectionBlend = 0.1f;
    [Export] private PredictionRigidbody3D _predictionRb;

    public override void OnProcessTick(int tick, IPackableElement input)
    {
        if (input is CharacterInputMessage cmd)
            VehiclePhysics.AdvancePhysics(_predictionRb, cmd);
    }

    public override Vector3 GetPosition() => _predictionRb.Body.GlobalPosition;

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

    public override void ResimulateTick(IPackableElement input)
    {
        if (input is CharacterInputMessage cmd)
            VehiclePhysics.AdvancePhysics(_predictionRb, cmd);
    }

    public override void ApplySoftCorrection(IEntityStateData receivedState, Vector3 savedPositionAtTick)
    {
        if (_softCorrectionBlend <= 0f) return;
        var state = (EntityStateMessage)receivedState;
        var body = _predictionRb.Body;

        // Position-only correction: shift body's CURRENT pos backward by a fraction of the
        // at-tick-T error. This preserves the body's motion since tick T while gradually
        // undoing the misprediction error itself.
        //
        // We deliberately do NOT correct velocity, angular velocity, or rotation here even
        // though the snapshot carries them. Those values are the body's state at tick T
        // (in the past); the body's current values are several ticks ahead on a different
        // part of the dynamics curve (especially during throttle reversal or turning).
        // Lerping current toward stale tick-T values pulls the body backward in time,
        // disrupts the natural progression, and causes the NEXT tick to predict from a
        // wrong starting state — drift then grows until the hard reconcile threshold
        // fires. Position has no such issue because we use the error-DIFF, not the value.
        Vector3 posError = savedPositionAtTick - state.Position;
        body.GlobalPosition -= posError * _softCorrectionBlend;
    }
}
