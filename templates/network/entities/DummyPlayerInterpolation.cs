using Godot;
using MonkeNet.Client;
using MonkeNet.Shared;

namespace YourGame;

// Client-side interpolation component for remote (non-owned) players.
// Add as a child of your dummy player scene alongside a NetworkBehaviour node.
//
// Scene structure:
//   [Node3D root]              ← visual root (mesh/rig)
//     ├── NetworkBehaviour
//     ├── DummyPlayerInterpolation  ← this file
//     ├── CollisionBody (CharacterBody3D)
//     │     └── CollisionShape3D (CapsuleShape3D)
//     └── [AnimationTree, etc.]
//
// The CollisionBody must mirror the capsule shape and offset of your local player
// scene so that MoveAndSlide() on the local player sees this dummy as solid.
public partial class DummyPlayerInterpolation : ClientInterpolatedEntity
{
    [Export] private Node3D _body;                   // Visual root node to reposition.
    [Export] private CharacterBody3D _collisionBody; // Physics stand-in — keeps local player from walking through.
    [Export] private AnimationTree _animTree;        // Optional — wire for locomotion animations.

    public override void HandleStateInterpolation(IEntityStateData past, IEntityStateData future, float interpolationFactor)
    {
        var p = (PlayerStateMessage)past;
        var f = (PlayerStateMessage)future;

        var targetPosition = p.Position.Lerp(f.Position, interpolationFactor);

        // Move the collision body toward the target using MoveAndCollide so it stops at
        // the surface of any other player's collision body instead of passing through it.
        // This fixes both:
        //   - The local player clipping through this dummy (collision body present for MoveAndSlide).
        //   - This dummy visually interpolating through another dummy on observer clients.
        var motion = targetPosition - _body.GlobalPosition;
        if (!motion.IsZeroApprox())
            _collisionBody.MoveAndCollide(motion);

        // Drive the visual root to the collision-resolved position.
        // _collisionBody is a child of _body: moving the parent double-shifts the child,
        // so reset the child's local offset to zero after the parent move.
        var resolved = _collisionBody.GlobalPosition;
        _body.GlobalPosition = resolved;
        _collisionBody.Position = Vector3.Zero;

        float yaw = Mathf.LerpAngle(p.Yaw, f.Yaw, interpolationFactor);
        _body.Rotation = Vector3.Up * yaw;

        // TODO: interpolate pitch and apply to a head/aim bone if needed.

        Vector3 velocity = p.Velocity.Lerp(f.Velocity, interpolationFactor);
        EmitSignal(SignalName.StateInterpolated, resolved, velocity, yaw);

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
