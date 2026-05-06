using Godot;
using MonkeNet.Client;

namespace GameDemo;

public partial class FirstPersonCameraController : Node3D
{
    [Export] private float _mouseSensitivity = 0.05f;
    [Export] private float _maxVerticalAngle = 90;

    private Node3D _rotationHelperY;
    private ClientManager _subscribedManager;
    private Callable _networkReadyCallable;
    private bool _networkReadySubscribed;

    // Camera yaw and pitch are held on RotationHelperY/Camera and are not part of any
    // synced entity state, so a fresh LocalPlayer spawn after a reclaim would otherwise
    // start facing the default direction. Saved on _ExitTree only when a reclaim is
    // pending (so an ordinary scene close doesn't pollute the next session) and applied
    // one-shot in _Ready of the next LocalPlayer instance.
    private static float _savedYaw;
    private static float _savedPitch;
    private static bool _hasSavedRotation;

    public override void _Ready()
    {
        _rotationHelperY = GetParent<Node3D>();

        if (_hasSavedRotation)
        {
            _hasSavedRotation = false;
            var helperRot = _rotationHelperY.Rotation;
            helperRot.Y = _savedYaw;
            _rotationHelperY.Rotation = helperRot;

            var cameraRot = Rotation;
            cameraRot.X = _savedPitch;
            Rotation = cameraRot;
        }

        var cm = ClientManager.Instance;
        if (cm?.IsNetworkReady == true)
        {
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }
        else if (cm != null)
        {
            _networkReadyCallable = Callable.From(OnNetworkReady);
            _networkReadySubscribed = true;
            _subscribedManager = cm;
            cm.Connect(ClientManager.SignalName.NetworkReady, _networkReadyCallable);
        }
    }

    public override void _ExitTree()
    {
        // Capture camera state only when a reclaim is pending — i.e. the user was disconnected
        // and the saved session token is sitting in ClientEntityManager waiting for the next
        // NetworkReady. Saving unconditionally would also fire on app shutdown / fresh-game
        // restarts and pollute the next session's initial facing direction.
        if (ClientEntityManager.HasSavedReclaimToken
            && _rotationHelperY != null && IsInstanceValid(_rotationHelperY))
        {
            _savedYaw = _rotationHelperY.Rotation.Y;
            _savedPitch = Rotation.X;
            _hasSavedRotation = true;
        }

        Input.MouseMode = Input.MouseModeEnum.Visible;
        if (_networkReadySubscribed && _subscribedManager != null && IsInstanceValid(_subscribedManager))
        {
            _subscribedManager.Disconnect(ClientManager.SignalName.NetworkReady, _networkReadyCallable);
            _networkReadySubscribed = false;
        }
        _subscribedManager = null;
    }

    private void OnNetworkReady()
    {
        Input.MouseMode = Input.MouseModeEnum.Captured;
        if (_networkReadySubscribed && _subscribedManager != null)
        {
            _subscribedManager.Disconnect(ClientManager.SignalName.NetworkReady, _networkReadyCallable);
            _networkReadySubscribed = false;
            _subscribedManager = null;
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (Input.MouseMode == Input.MouseModeEnum.Captured && @event is InputEventMouseMotion mouseMotionEvent)
        {
            RotateX(-Mathf.DegToRad(mouseMotionEvent.Relative.Y * _mouseSensitivity));
            _rotationHelperY.RotateY(Mathf.DegToRad(-mouseMotionEvent.Relative.X * _mouseSensitivity));

            Vector3 cameraRot = RotationDegrees;
            cameraRot.X = Mathf.Clamp(cameraRot.X, -_maxVerticalAngle, _maxVerticalAngle);
            RotationDegrees = cameraRot;

            // Camera is transformed outside _PhysicsProcess; reset so Godot's built-in
            // physics interpolation doesn't lerp back to the previous physics-frame pose.
            ResetPhysicsInterpolation();
            _rotationHelperY.ResetPhysicsInterpolation();
        }

        if (@event is InputEventKey keyEvent)
        {
            if (keyEvent.Keycode == Key.C && keyEvent.Pressed && ClientManager.Instance?.IsNetworkReady == true)
            {
                Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Visible ?
                    Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
            }
        }

    }

    public float GetLateralRotationAngle()
    {
        return _rotationHelperY.Rotation.Y;
    }

    public void RotateCameraYaw(float amount)
    {
        _rotationHelperY.RotateY(amount);
    }
}