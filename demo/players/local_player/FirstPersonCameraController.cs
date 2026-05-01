using Godot;
using MonkeNet.Client;

namespace GameDemo;

public partial class FirstPersonCameraController : Node3D
{
    [Export] private float _mouseSensitivity = 0.05f;
    [Export] private float _maxVerticalAngle = 90;

    private Node3D _rotationHelperY;

    public override void _Ready()
    {
        _rotationHelperY = GetParent<Node3D>();
        if (ClientManager.Instance?.IsNetworkReady == true)
            Input.MouseMode = Input.MouseModeEnum.Captured;
        else if (ClientManager.Instance != null)
            ClientManager.Instance.NetworkReady += OnNetworkReady;
    }

    private void OnNetworkReady()
    {
        Input.MouseMode = Input.MouseModeEnum.Captured;
        ClientManager.Instance.NetworkReady -= OnNetworkReady;
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