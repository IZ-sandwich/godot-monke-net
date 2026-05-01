using Godot;
using MonkeNet.Client;
using MonkeNet.Shared;

namespace GameDemo;

public partial class DummyStateInterpolation : ClientInterpolatedEntity
{
    [Export] private Node3D _parent;
    [Export] private Node3D _skeleton;
    [Export] private AnimationTree _animTree;
    [Export] private CharacterBody3D _collisionBody;

    public override void HandleStateInterpolation(IEntityStateData past, IEntityStateData future, float interpolationFactor)
    {
        var pastState = (EntityStateMessage)past;
        var futureState = (EntityStateMessage)future;

        var targetPosition = pastState.Position.Lerp(futureState.Position, interpolationFactor);

        // Move the collision body toward the target using MoveAndCollide so it stops at the
        // surface of any other player's collision body instead of passing through it.
        // This fixes both:
        //   - The local player clipping through this dummy (collision body present for MoveAndSlide).
        //   - This dummy visually interpolating through another dummy on observer clients.
        var motion = targetPosition - _parent.GlobalPosition;
        if (!motion.IsZeroApprox())
            _collisionBody.MoveAndCollide(motion);

        // Drive the visual root to the collision-resolved position.
        // _collisionBody is a child of _parent: moving the parent double-shifts the child,
        // so reset the child's local offset to zero after the parent move.
        var resolved = _collisionBody.GlobalPosition;
        _parent.GlobalPosition = resolved;
        _collisionBody.Position = Vector3.Zero;

        // Interpolate Yaw
        var rotation = Mathf.LerpAngle(pastState.Yaw, futureState.Yaw, interpolationFactor);
        _skeleton.Rotation = Vector3.Up * (rotation + Mathf.Pi);

        // Interpolate velocity
        Vector3 velocity = pastState.Velocity.Lerp(futureState.Velocity, interpolationFactor);

        UpdateAnimationTree(velocity);
    }

    private void UpdateAnimationTree(Vector3 velocity)
    {
        Vector3 lateralVelocity = velocity * new Vector3(1, 0, 1);

        if (lateralVelocity.IsZeroApprox())
        {
            _animTree.Set("parameters/conditions/idle", true);
            _animTree.Set("parameters/conditions/run", false);
        }
        else
        {
            _animTree.Set("parameters/conditions/idle", false);
            _animTree.Set("parameters/conditions/run", true);
        }
    }
}
