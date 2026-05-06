using Godot;
using System.Collections.Generic;

namespace MonkeNet.Shared;

/// <summary>
/// Marks a non-networked <see cref="RigidBody3D"/> as "offline" — its transform and
/// velocities are snapshotted before the client's resimulation loop and restored after,
/// so it doesn't drift each rollback. Without this, every dynamic body sharing the
/// physics space gets re-stepped N times during rollback, including scenery the game
/// doesn't replicate (loose props, debris, etc.), causing visible drift under
/// sustained mispredictions.
///
/// Usage:
/// 1. Add as a child of any non-networked <see cref="RigidBody3D"/>.
/// 2. Wire <see cref="Body"/> in the inspector (or leave null to auto-detect from the parent).
/// 3. Nothing else — registration with the resim loop is automatic via static registry.
///
/// Networked entities (anything with a <see cref="ClientPredictedEntity"/> upstream)
/// already get reconciled to authoritative state and should NOT use this node — it
/// would fight the prediction system.
/// </summary>
[GlobalClass, Icon("res://addons/monke-net/resources/circle_nodes_solid.png")]
public partial class OfflineRigidbody3D : Node
{
    [Export] public RigidBody3D Body { get; set; }

    private static readonly List<OfflineRigidbody3D> _instances = new();

    private Vector3 _snapshotPosition;
    private Quaternion _snapshotRotation;
    private Vector3 _snapshotLinearVel;
    private Vector3 _snapshotAngularVel;

    public override void _EnterTree()
    {
        if (Body == null && GetParent() is RigidBody3D parent)
            Body = parent;
        _instances.Add(this);
    }

    public override void _ExitTree()
    {
        _instances.Remove(this);
    }

    /// <summary>
    /// Records the current state of every registered offline body. Called by
    /// <c>ClientPredictionManager</c> immediately before the resimulation loop.
    /// </summary>
    public static void SnapshotAll()
    {
        foreach (var inst in _instances)
        {
            if (inst.Body == null) continue;
            inst._snapshotPosition = inst.Body.GlobalPosition;
            inst._snapshotRotation = inst.Body.Quaternion;
            inst._snapshotLinearVel = inst.Body.LinearVelocity;
            inst._snapshotAngularVel = inst.Body.AngularVelocity;
        }
    }

    /// <summary>
    /// Restores every registered offline body to the state captured by
    /// <see cref="SnapshotAll"/>. Called by <c>ClientPredictionManager</c> at the end
    /// of the resimulation loop. Calls <c>ResetPhysicsInterpolation</c> so the visual
    /// mesh doesn't lerp between the resim-final pose and the restored pose.
    /// </summary>
    public static void RestoreAll()
    {
        foreach (var inst in _instances)
        {
            if (inst.Body == null) continue;
            inst.Body.GlobalPosition = inst._snapshotPosition;
            inst.Body.Quaternion = inst._snapshotRotation;
            inst.Body.LinearVelocity = inst._snapshotLinearVel;
            inst.Body.AngularVelocity = inst._snapshotAngularVel;
            inst.Body.ForceUpdateTransform();
            inst.Body.ResetPhysicsInterpolation();
        }
    }

    public static int InstanceCount => _instances.Count;
}
