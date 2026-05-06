using Godot;

namespace MonkeNet.Shared;

/// <summary>
/// Immutable snapshot of a RigidBody3D's simulation state at a tick boundary.
/// Used by <see cref="PredictionRigidbody3D"/> to capture and restore body state
/// during reconciliation. Network state messages typically pack a subset of these
/// fields (e.g. converting Quaternion to compact yaw for player entities).
/// </summary>
public struct RigidbodyState
{
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 LinearVelocity;
    public Vector3 AngularVelocity;
}
