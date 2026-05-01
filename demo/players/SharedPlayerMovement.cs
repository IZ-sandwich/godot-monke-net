using Godot;
using ImGuiNET;
using MonkeNet.Shared;

namespace GameDemo;

public enum InputFlags
{
    Space = 0b_0000_0001,
    Shift = 0b_0000_0010,
}

/// <summary>
/// Shared player movement code, used to move both client and server players.
/// </summary>
public partial class SharedPlayerMovement : Node
{
    [Export] private CharacterBody3D _characterBody;
    public static readonly float MaxRunSpeed = 5;
    public static readonly float MaxWalkSpeed = 2;
    public static readonly float Gravity = 9.8f;
    public static readonly float JumpVelocity = 6.0f;

    public void AdvancePhysics(CharacterInputMessage input)
    {
        Vector3 newVelocity = CalculateVelocity(_characterBody, input);
        _characterBody.Velocity = newVelocity;
        PhysicsUtils.MoveAndSlide(_characterBody);
        if (input.MoveX != 0 || input.MoveY != 0 || ReadInput(input.Keys, InputFlags.Space))
            GD.Print($"[Movement] IsOnFloor={_characterBody.IsOnFloor()} Vel=({newVelocity.X:0.0},{newVelocity.Y:0.0},{newVelocity.Z:0.0}) Pos=({_characterBody.GlobalPosition.X:0.0},{_characterBody.GlobalPosition.Y:0.0},{_characterBody.GlobalPosition.Z:0.0})");
    }

    public static Vector3 CalculateVelocity(CharacterBody3D body, CharacterInputMessage input)
    {
        // MoveX/MoveY are analog: -1..1 from either keyboard or controller stick.
        // Clamp the 2D magnitude to 1 so keyboard diagonals don't exceed max speed,
        // while partial stick tilts produce proportionally reduced speed.
        var move2D = new Vector2(input.MoveX, input.MoveY);
        float inputMagnitude = Mathf.Min(move2D.Length(), 1f);

        bool isWalking = ReadInput(input.Keys, InputFlags.Shift);
        bool isJumping = ReadInput(input.Keys, InputFlags.Space);
        Vector3 velocity = body.Velocity;

        bool isOnFloor = body.IsOnFloor();
        Vector3 direction = move2D.IsZeroApprox()
            ? Vector3.Zero
            : new Vector3(move2D.X, 0, move2D.Y).Normalized();
        direction = direction.Rotated(Vector3.Up, input.CameraYaw);

        if (!direction.IsZeroApprox())
        {
            float speed = (isWalking ? MaxWalkSpeed : MaxRunSpeed) * inputMagnitude;
            velocity.X = direction.X * speed;
            velocity.Z = direction.Z * speed;
        }
        else
        {
            velocity.X = 0;
            velocity.Z = 0;
        }

        if (!isOnFloor)
            velocity.Y -= Gravity * PhysicsUtils.DeltaTime;

        if (isJumping && isOnFloor)
            velocity.Y = JumpVelocity;

        return velocity;
    }

    public static bool ReadInput(byte input, InputFlags flag)
    {
        return (input & (byte)flag) > 0;
    }

    public void DisplayDebugInformation()
    {
        if (ImGui.CollapsingHeader("Movement"))
        {
            ImGui.Text($"Position ({_characterBody.GlobalPosition.X:0.00}, {_characterBody.GlobalPosition.Y:0.00}, {_characterBody.GlobalPosition.Z:0.00})");
            ImGui.Text($"Velocity ({_characterBody.Velocity.X:0.00}, {_characterBody.Velocity.Y:0.00}, {_characterBody.Velocity.Z:0.00})");
        }
    }
}
