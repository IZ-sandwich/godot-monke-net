using Godot;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace YourGame;

// ── Input messages (client → server) ────────────────────────────────────────

public struct PlayerInputMessage : IPackableElement
{
    // Bit flags for binary actions. Add your own values to PlayerAction below.
    public byte Actions { get; set; }

    // Analog movement axes: -1..1. Keyboard produces -1/0/1; controller stick produces analog values.
    public float MoveX { get; set; }
    public float MoveY { get; set; }

    // Camera orientation sent each tick so the server knows where the player is looking.
    public float CameraYaw { get; set; }
    public float CameraPitch { get; set; }

    public readonly void WriteBytes(MessageWriter writer)
    {
        writer.Write(Actions);
        writer.Write(MoveX);
        writer.Write(MoveY);
        writer.Write(CameraYaw);
        writer.Write(CameraPitch);
    }

    public void ReadBytes(MessageReader reader)
    {
        Actions = reader.ReadByte();
        MoveX = reader.ReadSingle();
        MoveY = reader.ReadSingle();
        CameraYaw = reader.ReadSingle();
        CameraPitch = reader.ReadSingle();
    }

    public readonly IPackableElement GetCopy() => this;
}

public struct VehicleInputMessage : IPackableElement
{
    public float Steering { get; set; }  // -1..1
    public float Throttle { get; set; }  // 0..1
    public float Brake    { get; set; }  // 0..1
    public bool Handbrake { get; set; }

    public readonly void WriteBytes(MessageWriter writer)
    {
        writer.Write(Steering);
        writer.Write(Throttle);
        writer.Write(Brake);
        writer.Write(Handbrake);
    }

    public void ReadBytes(MessageReader reader)
    {
        Steering  = reader.ReadSingle();
        Throttle  = reader.ReadSingle();
        Brake     = reader.ReadSingle();
        Handbrake = reader.ReadBoolean();
    }

    public readonly IPackableElement GetCopy() => this;
}

// ── State messages (server → all clients, packed inside each snapshot) ───────

public struct PlayerStateMessage : IEntityStateData
{
    public int     EntityId { get; set; }
    public Vector3 Position { get; set; }
    public Vector3 Velocity { get; set; }
    public float   Yaw      { get; set; }
    public float   Pitch    { get; set; }

    public readonly void WriteBytes(MessageWriter writer)
    {
        writer.Write(EntityId);
        writer.Write(Position);
        writer.Write(Velocity);
        writer.Write(Yaw);
        writer.Write(Pitch);
    }

    public void ReadBytes(MessageReader reader)
    {
        EntityId = reader.ReadInt32();
        Position = reader.ReadVector3();
        Velocity = reader.ReadVector3();
        Yaw      = reader.ReadSingle();
        Pitch    = reader.ReadSingle();
    }

    public readonly IPackableElement GetCopy() => this;
}

// Shared by physics props and vehicles — anything that needs full 3D rigid-body state.
public struct PhysicsStateMessage : IEntityStateData
{
    public int        EntityId        { get; set; }
    public Vector3    Position        { get; set; }
    public Quaternion Rotation        { get; set; }
    public Vector3    LinearVelocity  { get; set; }
    public Vector3    AngularVelocity { get; set; }
    public bool       IsAwake         { get; set; }

    public readonly void WriteBytes(MessageWriter writer)
    {
        writer.Write(EntityId);
        writer.Write(Position);
        writer.Write(Rotation);
        writer.Write(LinearVelocity);
        writer.Write(AngularVelocity);
        writer.Write(IsAwake);
    }

    public void ReadBytes(MessageReader reader)
    {
        EntityId        = reader.ReadInt32();
        Position        = reader.ReadVector3();
        Rotation        = reader.ReadQuaternion();
        LinearVelocity  = reader.ReadVector3();
        AngularVelocity = reader.ReadVector3();
        IsAwake         = reader.ReadBoolean();
    }

    public readonly IPackableElement GetCopy() => this;
}

// ── Action bit-flag helpers ──────────────────────────────────────────────────

public enum PlayerAction : byte
{
    Jump     = 0b_0000_0001,
    Crouch   = 0b_0000_0010,
    Interact = 0b_0000_0100,
    Sprint   = 0b_0000_1000,
    // Up to 8 total in a byte — add more as needed.
}

public static class InputHelper
{
    public static bool IsPressed(byte actions, PlayerAction flag) =>
        (actions & (byte)flag) != 0;

    public static byte SetPressed(byte actions, PlayerAction flag) =>
        (byte)(actions | (byte)flag);
}
