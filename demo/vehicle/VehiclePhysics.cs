using Godot;
using MonkeNet.Shared;

namespace GameDemo;

/// <summary>
/// Shared, deterministic vehicle simulation. Both <see cref="ServerVehicleStateSyncronizer"/>
/// and <see cref="LocalVehiclePrediction"/> route through this so the server's authoritative
/// tick and the client's predicted tick produce identical body trajectories given identical
/// input — the property the rollback resimulation loop relies on.
///
/// Forces are queued on a <see cref="PredictionRigidbody3D"/>; <see cref="Simulate"/> flushes
/// them to the underlying RigidBody3D. The framework calls SpaceStep afterward to integrate.
/// Reuses <see cref="CharacterInputMessage"/> so a player who owns a vehicle drives it with
/// the same input stream they use for their character — MoveY = throttle, MoveX = steering.
/// </summary>
public static class VehiclePhysics
{
    // Tuned for ~30 m/s top speed (6× player run), ~2 rad/s turn rate (360 in ~3s),
    // and ~1s acceleration time constant. Math:
    //   terminal speed = ForwardThrust / LinearDrag
    //   terminal turn  = TurnTorque    / AngularDrag
    //   accel τ        = mass          / LinearDrag       (default mass = 1)
    public const float ForwardThrust = 30f;
    public const float TurnTorque = 4f;
    public const float LinearDrag = 1f;
    public const float AngularDrag = 2f;

    public static void AdvancePhysics(PredictionRigidbody3D predictionRb, CharacterInputMessage input)
    {
        var body = predictionRb?.Body;
        if (body == null) return;

        Vector3 forward = -body.GlobalTransform.Basis.Z; // Godot convention: -Z is forward.
        float throttle = -input.MoveY;                   // W maps to MoveY = -1, treat as forward.
        float steering = -input.MoveX;                   // A maps to MoveX = -1, treat as left turn.

        if (Mathf.Abs(throttle) > 0.01f)
            predictionRb.AddForce(forward * throttle * ForwardThrust);

        if (Mathf.Abs(steering) > 0.01f)
        {
            // When the vehicle is moving backward, the rear is the leading edge — pressing
            // "left" should still swing the rear of the body to the left, which rotates the
            // body in the *opposite* direction around Y compared to forward motion. The
            // 0.1 m/s dead zone prevents the steering sign from flickering at near-zero
            // velocity (physics jitter, brief contact bumps, etc.).
            float forwardSpeed = body.LinearVelocity.Dot(forward);
            float steeringSign = forwardSpeed < -0.1f ? -1f : 1f;
            predictionRb.AddTorque(Vector3.Up * steering * steeringSign * TurnTorque);
        }

        // Manual drag instead of body.LinearDamp/AngularDamp so the magnitude shows up
        // in the queue and is reproduced bit-identically on resim.
        predictionRb.AddForce(-body.LinearVelocity * LinearDrag);
        predictionRb.AddTorque(-body.AngularVelocity * AngularDrag);

        predictionRb.Simulate();
    }
}
