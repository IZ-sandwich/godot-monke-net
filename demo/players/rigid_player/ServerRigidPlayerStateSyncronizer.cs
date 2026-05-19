using Godot;
using MonkeNet.Server;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace GameDemo;

public partial class ServerRigidPlayerStateSyncronizer : ServerStateSyncronizer
{
    [Export] private PredictionRigidbody3D _predictionRb;

    // Vertical offset above the vehicle while riding. Mirrors LocalRigidPlayerPrediction
    // and PlayerStateSyncronizer so the authoritative anchor matches the client predicted
    // anchor — divergence here would cost a reconcile every snapshot.
    private static readonly Vector3 RideOffset = new(0, 1.5f, 0);

    // Track the vehicle we last rode so we can read its LinearVelocity at dismount
    // and inherit it. Mirrors the client-side field in LocalRigidPlayerPrediction.
    private int _lastRiddenVehicleEntityId;

    public override void OnProcessTick(int tick, IPackableElement input)
    {
        // Mirror LocalRigidPlayerPrediction: while this player owns a vehicle, pin the
        // body on top and skip RigidPlayerPhysics. Without this the server's rigid
        // player keeps walking each tick and PushRigidBodies-style collision impulses
        // get routed into the server's vehicle, drifting it away from the client's
        // predicted vehicle every snapshot.
        UpdateRideFreezeState();
        if (TryAnchorToOwnedVehicle()) return;

        if (input is CharacterInputMessage cmd)
            RigidPlayerPhysics.AdvancePhysics(_predictionRb, cmd);
    }

    private NetworkBehaviour FindRiddenVehicleEntity()
    {
        foreach (var entity in EntitySpawner.Instance.Entities)
        {
            if (entity.EntityType == 2 && entity.Authority == this.Authority)
                return entity;
        }
        return null;
    }

    // Toggle Body.Freeze on ride entry/exit. On dismount, inherit the vehicle's
    // linear velocity so jumping off a moving vehicle preserves momentum on the
    // authoritative side as well — keeps the post-release snapshot consistent
    // with the client's locally-inherited velocity.
    private void UpdateRideFreezeState()
    {
        var body = _predictionRb?.Body;
        if (body == null) return;
        var ridden = FindRiddenVehicleEntity();
        bool ridingNow = ridden != null;
        if (ridingNow && !body.Freeze)
        {
            body.FreezeMode = RigidBody3D.FreezeModeEnum.Kinematic;
            body.Freeze = true;
        }
        else if (!ridingNow && body.Freeze)
        {
            Vector3 inheritedVel = Vector3.Zero;
            if (_lastRiddenVehicleEntityId != 0)
            {
                foreach (var entity in EntitySpawner.Instance.Entities)
                {
                    if (entity.EntityId == _lastRiddenVehicleEntityId)
                    {
                        if (EntitySpawner.Instance.GetEntityRoot(entity) is RigidBody3D vehBody)
                            inheritedVel = vehBody.LinearVelocity;
                        break;
                    }
                }
            }
            body.Freeze = false;
            body.LinearVelocity = inheritedVel;
            body.AngularVelocity = Vector3.Zero;
        }
        _lastRiddenVehicleEntityId = ridingNow ? ridden.EntityId : 0;
    }

    private bool TryAnchorToOwnedVehicle()
    {
        var body = _predictionRb?.Body;
        if (body == null) return false;
        var ridden = FindRiddenVehicleEntity();
        if (ridden == null) return false;
        // Always snap the rider on top of the vehicle. The client mirror in
        // LocalRigidPlayerPrediction.TryAnchorToOwnedVehicle does the same so
        // both sides' rider poses stay in lockstep with the vehicle pose, and
        // the snapshot stream doesn't ship a divergent rider trajectory.
        var vRoot = EntitySpawner.Instance.GetEntityRoot(ridden);
        if (vRoot == null) return false;
        AnchorBodyToVehicle(body, vRoot.GlobalPosition + RideOffset);
        return true;
    }

    // Anchor a rigid body to a teleport target using the same atomic-transform-write
    // pattern PredictionRigidbody3D.Reconcile uses. Setting GlobalPosition alone on a
    // RigidBody3D doesn't reliably propagate to Jolt's internal state — the next
    // SpaceStep can integrate from the stale physics-server-side transform and the
    // visible body stays at its pre-teleport pose. ForceUpdateTransform commits the
    // C# transform write through to Jolt.
    //
    // NOTE: we deliberately do NOT call ResetPhysicsInterpolation here. The rider
    // body is anchored EVERY physics tick to (vehicle.GlobalPosition + RideOffset),
    // i.e. it moves smoothly with the vehicle. ResetPhysicsInterpolation tells
    // SceneTreeFTI "this was a teleport, collapse prev=curr, don't lerp" — which
    // is correct on a reconcile snap but wrong on a per-tick smooth-motion anchor.
    // Calling it here pinned the rider body's render pose to the un-interpolated
    // current-tick value, so the first-person camera attached to the rider body
    // rendered the world from a stair-stepped viewpoint while the vehicle mesh
    // (FTI-lerped) slid smoothly between ticks — the rider perceived the vehicle
    // oscillating ~3 cm against the camera every render frame even with
    // debug_collisions disabled. Leaving FTI's normal pump+lerp alone keeps the
    // rider's render pose on the same lerp window as the vehicle.
    internal static void AnchorBodyToVehicle(RigidBody3D body, Vector3 targetPos)
    {
        body.GlobalTransform = new Transform3D(body.GlobalTransform.Basis, targetPos);
        body.LinearVelocity = Vector3.Zero;
        body.AngularVelocity = Vector3.Zero;
        body.ConstantForce = Vector3.Zero;
        body.ConstantTorque = Vector3.Zero;
        body.ForceUpdateTransform();
    }

    public override IEntityStateData PackEntityState()
    {
        var state = _predictionRb.SnapshotState();
        return new EntityStateMessage
        {
            EntityId = this.EntityId,
            Yaw = 0,
            Position = state.Position,
            Rotation = state.Rotation,
            Velocity = state.LinearVelocity,
            AngularVelocity = state.AngularVelocity,
            ServerSleeping = _predictionRb.Body != null && _predictionRb.Body.Sleeping,
        };
    }
}
