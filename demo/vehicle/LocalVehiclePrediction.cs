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
    //
    // Threshold = 1.0 m/s (squared = 1.0). Observers reconcile against a server
    // state that was integrated using input_at_T while the observer's prediction
    // for that same tick was integrated using input_at_{T-1} (snapshot.Inputs[]
    // only carries the latest input the server applied — there's no per-tick
    // input history on the wire). At a vehicle's accel during the first tick
    // of a new throttle direction, that one-tick input lag alone produces
    // ≈ 0.5–0.7 m/s of velocity divergence. Anything tighter than 1.0 m/s
    // makes every direction change a reconcile event on every observer, even
    // when the server and observer's prediction are otherwise in lockstep.
    [Export] private float _maxVelocityDeviationSquared = 1.0f;
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

    public override Vector3 ExtractAuthoritativeVelocity(IEntityStateData state) =>
        ((EntityStateMessage)state).Velocity;

    public override Quaternion ExtractAuthoritativeRotation(IEntityStateData state) =>
        ((EntityStateMessage)state).Rotation;

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

    public override void OnPostPhysicsTick(int tick, IPackableElement input)
    {
        // T1 contact-upgrade for vehicle-vs-prop interactions. The driver's
        // process upgrades cubes its vehicle touches so the contact response
        // stays crisp; an observer process ALSO runs this for the same reason
        // — its locally-simulated copy of the (remote-driven) vehicle is
        // predicting the same contact, and if the contacted cube stays in
        // Interpolate tier the always-blend path tugs the cube toward server
        // pose every snapshot, perturbing the vehicle's contact resolution
        // and tripping vehicle rollbacks (the Resim threshold on the vehicle
        // is 20 cm, easily crossed when the cube is repositioned mid-contact).
        // Running the upgrade on BOTH driver and observer keeps the cubes
        // synchronised with whichever side is locally simulating contact with
        // them. The observer's local vehicle sim WILL diverge from the
        // server's, and contact-upgraded cubes will sometimes rollback —
        // that's the correct cost, far less disruptive than per-snapshot
        // cube body writes mid-contact.
        var body = _predictionRb?.Body;
        if (body == null) return;
        var contactBodies = RigidPlayerPhysics.QueryContactBodies(body);
        if (contactBodies.Count == 0) return;
        foreach (var cb in contactBodies)
        {
            var cpe = LocalRigidPlayerPrediction.FindOwningPredictedEntity(cb);
            if (cpe != null && cpe != this)
                cpe.RequestResimUpgrade();
        }
    }
}
