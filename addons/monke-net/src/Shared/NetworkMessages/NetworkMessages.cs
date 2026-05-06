using Godot;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace MonkeNet.NetworkMessages;

public enum EntityEventEnum : byte //TODO: move somewhere else
{
    Created,
    Destroyed

}
public enum ChannelEnum : int
{
    Snapshot,
    Clock,
    EntityEvent,
    ClientInput,
    GameReliable,
    GameUnreliable
}

public struct EntityRequestMessage : IPackableMessage
{
    public required byte EntityType { get; set; }

    public void ReadBytes(MessageReader reader)
    {
        EntityType = reader.ReadByte();
    }

    public readonly void WriteBytes(MessageWriter writer)
    {
        writer.Write(EntityType);
    }
}

public struct ClockSyncMessage : IPackableMessage
{
    public required int ClientTime { get; set; }
    public required int ServerTime { get; set; }

    public void ReadBytes(MessageReader reader)
    {
        ClientTime = reader.ReadInt32();
        ServerTime = reader.ReadInt32();
    }

    public readonly void WriteBytes(MessageWriter writer)
    {
        writer.Write(ClientTime);
        writer.Write(ServerTime);
    }
}

public struct EntityEventMessage : IPackableMessage
{
    public required EntityEventEnum Event { get; set; }
    public required int EntityId { get; set; }
    public required byte EntityType { get; set; }
    public required int Authority { get; set; }
    public Vector3 Position { get; set; }
    public float Yaw { get; set; }
    public string Metadata { get; set; } //TODO: his should contain Position, Yaw, etc all the other specific stuff

    public void ReadBytes(MessageReader reader)
    {
        Event = (EntityEventEnum)reader.ReadByte();
        EntityId = reader.ReadInt32();
        EntityType = reader.ReadByte();
        Authority = reader.ReadInt32();
        Position = reader.ReadVector3();
        Yaw = reader.ReadSingle();
        Metadata = reader.ReadString();
    }

    public readonly void WriteBytes(MessageWriter writer)
    {
        writer.Write((byte)Event);
        writer.Write(EntityId);
        writer.Write(EntityType);
        writer.Write(Authority);
        writer.Write(Position);
        writer.Write(Yaw);
        writer.Write(Metadata);
    }

}

public struct GameSnapshotMessage : IPackableMessage
{
    public required int Tick { get; set; }
    public IEntityStateData[] States { get; set; }

    public readonly void WriteBytes(MessageWriter writer)
    {
        writer.Write(Tick);
        writer.Write(States);
    }

    public void ReadBytes(MessageReader reader)
    {
        Tick = reader.ReadInt32();
        States = reader.ReadArray<IEntityStateData>();
    }
}

public struct PackedClientInputMessage : IPackableMessage
{
    public required int Tick { get; set; } // This is the Tick stamp for the latest generated input (Inputs[Inputs.Length]), all other Ticks are (Tick - index)
    public IPackableElement[] Inputs { get; set; }

    public readonly void WriteBytes(MessageWriter writer)
    {
        writer.Write(Tick);
        writer.WriteSingleTypeArray(Inputs);
    }

    public void ReadBytes(MessageReader reader)
    {
        Tick = reader.ReadInt32();
        Inputs = reader.ReadSingleTypeArray<IPackableElement>();
    }
}

public enum DisconnectEntityMode { RemoveEntity, KeepEntity }

public struct SessionTokenMessage : IPackableMessage
{
    public required string Token { get; set; }

    public void ReadBytes(MessageReader reader) { Token = reader.ReadString(); }
    public readonly void WriteBytes(MessageWriter writer) { writer.Write(Token); }
}

public struct DisconnectNotificationMessage : IPackableMessage
{
    public void ReadBytes(MessageReader reader) { }
    public readonly void WriteBytes(MessageWriter writer) { }
}

public struct ReclaimEntityMessage : IPackableMessage
{
    public required string Token { get; set; }

    public void ReadBytes(MessageReader reader) { Token = reader.ReadString(); }
    public readonly void WriteBytes(MessageWriter writer) { writer.Write(Token); }
}

/// <summary>
/// Client → server: "I'd like to take ownership of this entity." The server runs the
/// game-defined approval policy (see <c>ServerEntityManager.OwnershipApprover</c>) and
/// either calls <c>ChangeAuthority</c> (which sends the Destroy+Create pair) on approval
/// or replies with <see cref="OwnershipChangeRejectedMessage"/>.
/// </summary>
public struct OwnershipChangeRequestMessage : IPackableMessage
{
    public required int EntityId { get; set; }

    public void ReadBytes(MessageReader reader) { EntityId = reader.ReadInt32(); }
    public readonly void WriteBytes(MessageWriter writer) { writer.Write(EntityId); }
}

/// <summary>
/// Server → requester: explicit rejection of an <see cref="OwnershipChangeRequestMessage"/>.
/// Used by Step-3b's anticipated client-side flip to revert without waiting for a
/// timeout. On a default Step-3a setup (no anticipated flip), receiving this is a no-op
/// the client can ignore — there's no provisional state to undo.
/// </summary>
public struct OwnershipChangeRejectedMessage : IPackableMessage
{
    public required int EntityId { get; set; }

    public void ReadBytes(MessageReader reader) { EntityId = reader.ReadInt32(); }
    public readonly void WriteBytes(MessageWriter writer) { writer.Write(EntityId); }
}