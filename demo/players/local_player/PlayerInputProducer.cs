using GameDemo;
using Godot;
using MonkeNet.Client;
using MonkeNet.Serializer;

public partial class PlayerInputProducer : InputProducerComponent
{
    [Export] private FirstPersonCameraController _cameraController;

    public override void _Ready()
    {
        base._Ready(); // Important! This will notify the Client Manager of this input producer!
    }

    public override IPackableElement GenerateCurrentInput()
    {
        // Hold at zero movement until the clock is synced; movement before that causes a
        // snap-back to spawn position when the first authoritative snapshot is reconciled.
        if (!ClientManager.Instance.IsNetworkReady)
            return new CharacterInputMessage { CameraYaw = _cameraController.GetLateralRotationAngle() };

        float moveX = Input.GetAxis("left", "right");
        float moveY = Input.GetAxis("forward", "backward");
        byte keys = GetCurrentPressedKeys();
        if (moveX != 0 || moveY != 0 || keys != 0)
            GD.Print($"[InputProducer] MoveX={moveX:0.00} MoveY={moveY:0.00} Keys={keys} Yaw={_cameraController.GetLateralRotationAngle():0.00}");
        return new CharacterInputMessage
        {
            MoveX = moveX,
            MoveY = moveY,
            Keys = keys,
            CameraYaw = _cameraController.GetLateralRotationAngle()
        };
    }

    private static byte GetCurrentPressedKeys()
    {
        byte keys = 0;
        if (Input.IsActionPressed("space")) keys |= (byte)InputFlags.Space;
        if (Input.IsActionPressed("shift")) keys |= (byte)InputFlags.Shift;
        return keys;
    }
}
