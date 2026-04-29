using Godot;
using MonkeNet.Client;
using MonkeNet.Shared;

namespace YourGame;

// Client-side interpolation component for remote (non-owned) players.
// Add as a child of your dummy player scene alongside a NetworkBehaviour node.
//
// Scene structure:
//   [Node3D root]
//     ├── NetworkBehaviour
//     ├── DummyPlayerInterpolation  ← this file
//     └── [mesh, AnimationTree, etc.]
public partial class DummyPlayerInterpolation : ClientInterpolatedEntity
{
    [Export] private Node3D _body;       // Root node to reposition (or assign GetParent<Node3D>() in _Ready).
    [Export] private AnimationTree _animTree; // Optional — wire for locomotion animations.

    public override void HandleStateInterpolation(IEntityStateData past, IEntityStateData future, float interpolationFactor)
    {
        var p = (PlayerStateMessage)past;
        var f = (PlayerStateMessage)future;

        _body.Position = p.Position.Lerp(f.Position, interpolationFactor);

        float yaw = Mathf.LerpAngle(p.Yaw, f.Yaw, interpolationFactor);
        _body.Rotation = Vector3.Up * yaw;

        // TODO: interpolate pitch and apply to a head/aim bone if needed.

        Vector3 velocity = p.Velocity.Lerp(f.Velocity, interpolationFactor);
        EmitSignal(SignalName.StateInterpolated, _body.Position, velocity, yaw);

        if (_animTree != null)
            UpdateAnimationTree(velocity);
    }

    // GDScript children connect here to drive audio, VFX, etc. without any C#.
    [Signal] public delegate void StateInterpolatedEventHandler(Vector3 position, Vector3 velocity, float yaw);

    private void UpdateAnimationTree(Vector3 velocity)
    {
        bool moving = !(velocity * new Vector3(1, 0, 1)).IsZeroApprox();
        _animTree.Set("parameters/conditions/idle", !moving);
        _animTree.Set("parameters/conditions/run",  moving);
    }
}
