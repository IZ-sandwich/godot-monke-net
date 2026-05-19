using Godot;

namespace GameDemo;

/// <summary>
/// Drives the Knight model's <see cref="AnimationTree"/> state-machine conditions
/// (<c>idle</c>, <c>run</c>, <c>jump</c>) from the rigid player's body velocity each
/// frame. Mirrors the animation-driving logic the old CharacterBody3D DummyPlayer
/// used so the knight visual reads the same in the new rigid-player flow.
///
/// Reads body velocity rather than input because (a) the same script drives both
/// owned and observed players — neither has direct access to the input stream — and
/// (b) the body velocity is the authoritative motion signal: it's what produces the
/// movement the player actually sees, so the animation always matches the visible
/// motion regardless of network conditions.
/// </summary>
[GlobalClass]
public partial class RigidPlayerAnimationDriver : Node
{
    [Export] public RigidBody3D Body { get; set; }
    [Export] public AnimationTree AnimTree { get; set; }

    /// <summary>Velocity magnitude (m/s, horizontal) above which the rig switches to
    /// the run animation. Below it the rig idles. Default ~0.5 m/s avoids flapping
    /// between idle/run during the deceleration ramp at the end of a run.</summary>
    [Export] public float RunThreshold { get; set; } = 0.5f;

    public override void _Process(double delta)
    {
        if (Body == null || AnimTree == null) return;
        // While the player is riding a vehicle the body is Freeze=Kinematic and its
        // transform is anchored to the vehicle pose every tick. Jolt then derives an
        // implicit linear velocity from the transform delta — equal to the vehicle's
        // velocity — even though we explicitly set LinearVelocity=Zero. Reading
        // LinearVelocity here would therefore drive the rig into the run animation
        // while the player is just sitting on the vehicle. Force idle in that case.
        bool running = !Body.Freeze
                       && new Vector3(Body.LinearVelocity.X, 0, Body.LinearVelocity.Z).Length() > RunThreshold;
        AnimTree.Set("parameters/conditions/idle", !running);
        AnimTree.Set("parameters/conditions/run",  running);
        AnimTree.Set("parameters/conditions/jump", false);
    }
}
