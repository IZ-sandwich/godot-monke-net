using Godot;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace GameDemo;

// Entity state sent by the server to all clients every time a snapshot is produced
public struct EntityStateMessage : IEntityStateData
{
    public int EntityId { get; set; } // Entity Id
    public Vector3 Position { get; set; } // Entity Position
    public Vector3 Velocity { get; set; } // Entity velocity
    public Vector3 AngularVelocity { get; set; } // Entity velocity
    public Vector3 Rotation { get; set; }
    public float Yaw { get; set; } // Looking angle

    public void ReadBytes(MessageReader reader)
    {
        EntityId = reader.ReadInt32();
        Position = reader.ReadVector3();
        Velocity = reader.ReadVector3();
        AngularVelocity = reader.ReadVector3();
        Rotation = reader.ReadVector3();
        Yaw = reader.ReadSingle();
    }

    public readonly void WriteBytes(MessageWriter writer)
    {
        writer.Write(EntityId);
        writer.Write(Position);
        writer.Write(Velocity);
        writer.Write(AngularVelocity);
        writer.Write(Rotation);
        writer.Write(Yaw);
    }

    public readonly IPackableElement GetCopy() => this;
}

// Character inputs sent to the server by a local player every tick.
// MoveX/MoveY carry analog values (-1..1) so controller sticks work correctly.
// Keyboard produces exactly -1/0/1; a controller stick produces values in between.
public struct CharacterInputMessage : IPackableElement
{
    public byte Keys { get; set; }   // Bit flags for binary actions (see InputFlags).
    public float MoveX { get; set; } // -1..1: negative = left, positive = right.
    public float MoveY { get; set; } // -1..1: negative = forward, positive = backward.
    public float CameraYaw { get; set; }

    public readonly void WriteBytes(MessageWriter writer)
    {
        writer.Write(Keys);
        writer.Write(MoveX);
        writer.Write(MoveY);
        writer.Write(CameraYaw);
    }

    public void ReadBytes(MessageReader reader)
    {
        Keys = reader.ReadByte();
        MoveX = reader.ReadSingle();
        MoveY = reader.ReadSingle();
        CameraYaw = reader.ReadSingle();
    }

    public readonly IPackableElement GetCopy() => this;
}