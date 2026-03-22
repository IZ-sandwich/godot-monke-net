using Godot;
using MonkeNet.Client;
using MonkeNet.Shared;

namespace GameDemo;

public partial class DummyStateInterpolation : ClientInterpolatedEntity
{
    [Export] private Node3D _parent;
    [Export] private Node3D _skeleton;
    [Export] private AnimationTree _animTree;

    public override void HandleStateInterpolation(IEntityStateData past, IEntityStateData future, float interpolationFactor)
    {
        var pastState = (EntityStateMessage)past;
        var futureState = (EntityStateMessage)future;

        // Interpolate position
        _parent.Position = pastState.Position.Lerp(futureState.Position, interpolationFactor);

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
