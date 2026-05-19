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
    // Linear-velocity divergence threshold (squared, m²/s²). Without this the vehicle can
    // share the server's position to within tolerance while carrying noticeably different
    // momentum — the wrong velocity writes new wrong positions every tick until the
    // position threshold trips and a hard snap fires.
    [Export] private float _maxVelocityDeviationSquared = 0.25f;
    // Rotation divergence threshold (degrees). See LocalRigidPropPrediction for rationale.
    [Export] private float _maxRotationDeviationDegrees = 5f;
    [Export] private PredictionRigidbody3D _predictionRb;

    public override void OnProcessTick(int tick, IPackableElement input)
    {
        // ClientPredictionManager.Predict() routes the correct input per entity:
        // the driver gets fresh local input; observers get the input the server
        // last forwarded for this vehicle. So we can apply vehicle physics for
        // any client and the resulting motion matches the server modulo cross-
        // process Jolt drift.
        if (input is CharacterInputMessage cmd)
            VehiclePhysics.AdvancePhysics(_predictionRb, cmd);
    }

    public override Vector3 GetPosition() => _predictionRb.Body.GlobalPosition;

    public override RigidbodyState GetSnapshotState() => _predictionRb.SnapshotState();

    public override Vector3 ExtractAuthoritativePosition(IEntityStateData state) =>
        ((EntityStateMessage)state).Position;

    public override bool HasMisspredicted(int tick, IEntityStateData receivedState, RigidbodyState savedState)
    {
        EntityStateMessage state = (EntityStateMessage)receivedState;
        if ((state.Position - savedState.Position).LengthSquared() > _maxDeviationAllowedSquared)
            return true;
        if ((state.Velocity - savedState.LinearVelocity).LengthSquared() > _maxVelocityDeviationSquared)
            return true;
        if (state.Rotation.AngleTo(savedState.Rotation) > Mathf.DegToRad(_maxRotationDeviationDegrees))
            return true;
        return false;
    }

    public override void HandleReconciliation(IEntityStateData receivedState)
    {
        var state = (EntityStateMessage)receivedState;
        MonkeLogger.Debug($"[ENTITY-RECONCILE] LocalVehicle eid={EntityId} auth={state}");
        _predictionRb.Reconcile(new RigidbodyState
        {
            Position = state.Position,
            Rotation = state.Rotation,
            LinearVelocity = state.Velocity,
            AngularVelocity = state.AngularVelocity,
        });
    }

    public override void ResimulateTick(IPackableElement input)
    {
        // Rollback resim receives per-entity input from ClientPredictionManager;
        // apply it for both driver and observers so the resim trajectory matches.
        if (input is CharacterInputMessage cmd)
            VehiclePhysics.AdvancePhysics(_predictionRb, cmd);
    }

    public override void RestoreBodyState(RigidbodyState state) => _predictionRb.Reconcile(state);
}
