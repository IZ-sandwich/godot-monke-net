using Godot;

namespace MonkeNet.Shared;

/// <summary>
/// Hides reconciliation snaps from the player by lerping a visual root toward the
/// physics body over a short window (default 100ms ≈ 6 ticks at 60Hz). The body
/// teleports to the authoritative state immediately for collision correctness;
/// the visible mesh appears to "catch up" smoothly.
///
/// Usage in a scene:
/// 1. Add a <see cref="Node3D"/> child of the body, set <c>top_level = true</c> so it
///    keeps the absolute world transform (we set it explicitly each frame).
/// 2. Move all visible nodes (MeshInstance3D, etc.) under that visual root.
/// 3. Drop a <see cref="PredictionVisualSmoothing3D"/> alongside, wire <see cref="Body"/>
///    and <see cref="Visual"/>, then point your <see cref="PredictionRigidbody3D"/>
///    (or your custom reconciliation code) at this node so it gets called on reconcile.
///
/// When no smoothing is in flight, the visual stays locked to the body's pose each
/// <c>_Process</c> frame — this is what makes <c>top_level = true</c> safe to use.
/// </summary>
[GlobalClass, Icon("res://addons/monke-net/resources/circle_nodes_solid.png")]
public partial class PredictionVisualSmoothing3D : Node3D
{
    [Export] public Node3D Body { get; set; }
    [Export] public Node3D Visual { get; set; }

    /// <summary>How long the visual takes to catch up after a reconciliation snap.</summary>
    [Export] public float DurationSec { get; set; } = 0.1f;

    /// <summary>
    /// If the captured pre-reconcile offset is larger than this, the smoother snaps the
    /// visual to the body instead of lerping. Smoothing a multi-meter correction looks
    /// worse than a teleport because the visible mesh trails far behind collision for a
    /// noticeable window. Set to 0 to disable the threshold and always smooth.
    /// </summary>
    [Export] public float TeleportDistance { get; set; } = 5f;

    private Vector3 _posOffset;
    private Quaternion _rotOffset = Quaternion.Identity;
    private float _remaining;

    // OnReconciled fires inside the resim loop, when the body has just been teleported
    // to the authoritative pose but resim hasn't run yet. The offset captured here is
    // (preVisual - bodyAuthoritative). After resim, body advances to its post-resim
    // pose; the smoother then renders Visual = body_postresim + offset, which makes
    // Visual *overshoot* its pre-reconcile pose by (body_postresim - bodyAuthoritative).
    // To fix: stash the inputs and recompute the offset on the next _Process, when
    // the body is at its post-resim pose. The initial offset is left as a best-guess
    // estimate so IsSmoothing/CurrentOffset are sensible if anyone queries them
    // before _Process runs.
    private bool _pendingReconcile;
    private Vector3 _pendingPreVisualPos;
    private Quaternion _pendingPreVisualRot;

    /// <summary>
    /// Records that a reconciliation just happened. Pass the visual's pose captured
    /// **before** the body was teleported. The remaining offset between that pose and
    /// the body's new pose decays linearly to zero over <see cref="DurationSec"/>.
    /// Calling repeatedly is safe — each call resets the smoothing window.
    /// </summary>
    public void OnReconciled(Vector3 preVisualPosition, Quaternion preVisualRotation)
    {
        if (Body == null || Visual == null || DurationSec <= 0f)
        {
            _remaining = 0f;
            _pendingReconcile = false;
            return;
        }
        _posOffset = preVisualPosition - Body.GlobalPosition;
        _rotOffset = Body.Quaternion.Inverse() * preVisualRotation;

        if (TeleportDistance > 0f && _posOffset.Length() > TeleportDistance)
        {
            _posOffset = Vector3.Zero;
            _rotOffset = Quaternion.Identity;
            _remaining = 0f;
            _pendingReconcile = false;
            return;
        }
        _remaining = DurationSec;
        _pendingPreVisualPos = preVisualPosition;
        _pendingPreVisualRot = preVisualRotation;
        _pendingReconcile = true;
    }

    public override void _Process(double delta)
    {
        if (Body == null || Visual == null) return;

        if (_pendingReconcile)
        {
            _pendingReconcile = false;
            // Body has now settled at its post-resim pose. Recompute the offset against
            // it so Visual.GlobalPosition = body + offset starts at preVisualPos exactly.
            _posOffset = _pendingPreVisualPos - Body.GlobalPosition;
            _rotOffset = Body.Quaternion.Inverse() * _pendingPreVisualRot;
            // Re-check teleport threshold — post-resim offset can be much smaller (or larger)
            // than the just-teleported one, and we want the threshold applied to the value
            // that actually drives the lerp.
            if (TeleportDistance > 0f && _posOffset.Length() > TeleportDistance)
            {
                _posOffset = Vector3.Zero;
                _rotOffset = Quaternion.Identity;
                _remaining = 0f;
            }
        }

        if (_remaining > 0f)
        {
            _remaining = Mathf.Max(0f, _remaining - (float)delta);
            float t = DurationSec > 0f ? _remaining / DurationSec : 0f;
            Visual.GlobalPosition = Body.GlobalPosition + _posOffset * t;
            // Slerp the body-relative rotation offset toward identity as t decays.
            Visual.Quaternion = Body.Quaternion * Quaternion.Identity.Slerp(_rotOffset, t);
        }
        else
        {
            // No smoothing in flight — visual is locked to the body each frame.
            Visual.GlobalPosition = Body.GlobalPosition;
            Visual.Quaternion = Body.Quaternion;
        }
    }

    /// <summary>True while the visual is still catching up to the body.</summary>
    public bool IsSmoothing => _remaining > 0f;

    /// <summary>Current world-space position offset between visual and body. Zero when not smoothing.</summary>
    public Vector3 CurrentOffset => _remaining > 0f && DurationSec > 0f
        ? _posOffset * (_remaining / DurationSec)
        : Vector3.Zero;
}
