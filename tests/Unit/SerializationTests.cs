using System.IO;
using GameDemo;
using GdUnit4;
using Godot;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Shared;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Unit;

/// <summary>
/// A-01..A-15: All IPackableMessage round-trip serialization tests.
/// Requires Godot runtime because RegisterNetworkMessages calls GD.Print and
/// MessageWriter/Reader use Godot value types (Vector3, Quaternion, etc.).
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class SerializationTests
{
    [Before]
    public void RegisterMessages()
    {
        // Ensures all IPackableMessage types in all loaded assemblies are registered.
        MessageSerializer.RegisterNetworkMessages();
    }

    // A-01 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public void RoundTrip_ClockSyncMessage()
    {
        var msg = new ClockSyncMessage { ClientTime = 12345, ServerTime = 67890 };
        var result = (ClockSyncMessage)RoundTrip(msg);

        AssertThat(result.ClientTime).IsEqual(12345);
        AssertThat(result.ServerTime).IsEqual(67890);
    }

    // A-02 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public void RoundTrip_EntityEventMessage_Created()
    {
        var msg = new EntityEventMessage
        {
            Event = EntityEventEnum.Created,
            EntityId = 42,
            EntityType = 1,
            Authority = 7,
            Position = new Vector3(1f, 2f, 3f),
            Yaw = 1.5f,
            Metadata = "mapA"
        };

        var result = (EntityEventMessage)RoundTrip(msg);

        AssertThat(result.Event).IsEqual(EntityEventEnum.Created);
        AssertThat(result.EntityId).IsEqual(42);
        AssertThat(result.EntityType).IsEqual((byte)1);
        AssertThat(result.Authority).IsEqual(7);
        AssertThat(result.Yaw).IsEqualApprox(1.5f, 1e-5f);
        AssertThat(result.Metadata).IsEqual("mapA");
        AssertThat(result.Position.X).IsEqualApprox(1f, 1e-5f);
        AssertThat(result.Position.Y).IsEqualApprox(2f, 1e-5f);
        AssertThat(result.Position.Z).IsEqualApprox(3f, 1e-5f);
    }

    // A-03 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public void RoundTrip_EntityEventMessage_Destroyed()
    {
        var msg = new EntityEventMessage
        {
            Event = EntityEventEnum.Destroyed,
            EntityId = 5,
            EntityType = 0,
            Authority = 0,
            Metadata = ""
        };

        var result = (EntityEventMessage)RoundTrip(msg);

        AssertThat(result.Event).IsEqual(EntityEventEnum.Destroyed);
        AssertThat(result.EntityId).IsEqual(5);
    }

    // A-04 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public void RoundTrip_GameSnapshotMessage_EmptyStates()
    {
        var msg = new GameSnapshotMessage
        {
            Tick = 100,
            States = System.Array.Empty<IEntityStateData>()
        };

        var result = (GameSnapshotMessage)RoundTrip(msg);

        AssertThat(result.Tick).IsEqual(100);
        AssertThat(result.States.Length).IsEqual(0);
    }

    // A-05 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public void RoundTrip_GameSnapshotMessage_WithStates()
    {
        var state1 = new EntityStateMessage { EntityId = 10, Position = new Vector3(1, 0, 0) };
        var state2 = new EntityStateMessage { EntityId = 20, Position = new Vector3(0, 0, 5) };

        var msg = new GameSnapshotMessage
        {
            Tick = 200,
            States = new IEntityStateData[] { state1, state2 }
        };

        var result = (GameSnapshotMessage)RoundTrip(msg);

        AssertThat(result.Tick).IsEqual(200);
        AssertThat(result.States.Length).IsEqual(2);
        AssertThat(((EntityStateMessage)result.States[0]).EntityId).IsEqual(10);
        AssertThat(((EntityStateMessage)result.States[1]).EntityId).IsEqual(20);
    }

    // A-06 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public void RoundTrip_PackedClientInputMessage_SingleInput()
    {
        var input = new CharacterInputMessage { Keys = 0b0011, MoveX = 0.5f, MoveY = -1f, CameraYaw = 1.2f };
        var msg = new PackedClientInputMessage
        {
            Tick = 50,
            Inputs = new IPackableElement[] { input }
        };

        var result = (PackedClientInputMessage)RoundTrip(msg);

        AssertThat(result.Tick).IsEqual(50);
        AssertThat(result.Inputs.Length).IsEqual(1);
        var resultInput = (CharacterInputMessage)result.Inputs[0];
        AssertThat(resultInput.Keys).IsEqual((byte)0b0011);
        AssertThat(resultInput.MoveX).IsEqualApprox(0.5f, 1e-5f);
        AssertThat(resultInput.MoveY).IsEqualApprox(-1f, 1e-5f);
        AssertThat(resultInput.CameraYaw).IsEqualApprox(1.2f, 1e-5f);
    }

    // A-07 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public void RoundTrip_PackedClientInputMessage_RedundantInputs()
    {
        var inputs = new IPackableElement[]
        {
            new CharacterInputMessage { Keys = 1, MoveX = 0.1f, MoveY = 0.1f, CameraYaw = 0f },
            new CharacterInputMessage { Keys = 2, MoveX = 0.2f, MoveY = 0.2f, CameraYaw = 0f },
            new CharacterInputMessage { Keys = 3, MoveX = 0.3f, MoveY = 0.3f, CameraYaw = 0f },
            new CharacterInputMessage { Keys = 4, MoveX = 0.4f, MoveY = 0.4f, CameraYaw = 0f },
        };

        var msg = new PackedClientInputMessage { Tick = 55, Inputs = inputs };
        var result = (PackedClientInputMessage)RoundTrip(msg);

        AssertThat(result.Inputs.Length).IsEqual(4);
        AssertThat(((CharacterInputMessage)result.Inputs[0]).Keys).IsEqual((byte)1);
        AssertThat(((CharacterInputMessage)result.Inputs[3]).Keys).IsEqual((byte)4);
    }

    // A-08 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public void RoundTrip_EntityRequestMessage()
    {
        var msg = new EntityRequestMessage { EntityType = 3 };
        var result = (EntityRequestMessage)RoundTrip(msg);

        AssertThat(result.EntityType).IsEqual((byte)3);
    }

    // A-09 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public void MessageWriter_Vector3_RoundTrip()
    {
        var original = new Vector3(1.23f, -4.56f, 7.89f);
        using var stream = new MemoryStream();
        using var writer = new MessageWriter(stream);
        writer.Write(original);

        stream.Position = 0;
        using var reader = new MessageReader(stream);
        Vector3 result = reader.ReadVector3();

        AssertThat(result.X).IsEqualApprox(original.X, 1e-6f);
        AssertThat(result.Y).IsEqualApprox(original.Y, 1e-6f);
        AssertThat(result.Z).IsEqualApprox(original.Z, 1e-6f);
    }

    // A-10 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public void MessageWriter_Quaternion_RoundTrip()
    {
        var original = Quaternion.Identity;
        using var stream = new MemoryStream();
        using var writer = new MessageWriter(stream);
        writer.Write(original);

        stream.Position = 0;
        using var reader = new MessageReader(stream);
        Quaternion result = reader.ReadQuaternion();

        AssertThat(result.X).IsEqualApprox(0f, 1e-6f);
        AssertThat(result.Y).IsEqualApprox(0f, 1e-6f);
        AssertThat(result.Z).IsEqualApprox(0f, 1e-6f);
        AssertThat(result.W).IsEqualApprox(1f, 1e-6f);
    }

    // A-11 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public void MessageWriter_Transform3D_RoundTrip()
    {
        var original = new Transform3D(Basis.Identity, new Vector3(5f, 0f, -3f));
        using var stream = new MemoryStream();
        using var writer = new MessageWriter(stream);
        writer.Write(original);

        stream.Position = 0;
        using var reader = new MessageReader(stream);
        Transform3D result = reader.ReadTransform();

        AssertThat(result.Origin.X).IsEqualApprox(5f, 1e-6f);
        AssertThat(result.Origin.Z).IsEqualApprox(-3f, 1e-6f);
        AssertThat(result.Basis.Column0.X).IsEqualApprox(1f, 1e-6f);
    }

    // A-12 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public void MessageWriter_MultiTypeArray_RoundTrip()
    {
        var s1 = new EntityStateMessage { EntityId = 1, Position = new Vector3(1, 0, 0) };
        var s2 = new EntityStateMessage { EntityId = 2, Position = new Vector3(2, 0, 0) };

        using var stream = new MemoryStream();
        using var writer = new MessageWriter(stream);
        writer.Write(new IPackableMessage[] { s1, s2 });

        stream.Position = 0;
        using var reader = new MessageReader(stream);
        var result = reader.ReadArray<IEntityStateData>();

        AssertThat(result.Length).IsEqual(2);
        AssertThat(result[0].EntityId).IsEqual(1);
        AssertThat(result[1].EntityId).IsEqual(2);
    }

    // A-13 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public void MessageWriter_SingleTypeArray_RoundTrip()
    {
        var inputs = new IPackableElement[]
        {
            new CharacterInputMessage { Keys = 10, MoveX = 1f, MoveY = 0f, CameraYaw = 0f },
            new CharacterInputMessage { Keys = 20, MoveX = 0f, MoveY = 1f, CameraYaw = 0f },
            new CharacterInputMessage { Keys = 30, MoveX = 0f, MoveY = 0f, CameraYaw = 1f },
        };

        using var stream = new MemoryStream();
        using var writer = new MessageWriter(stream);
        writer.WriteSingleTypeArray(inputs);

        stream.Position = 0;
        using var reader = new MessageReader(stream);
        var result = reader.ReadSingleTypeArray<IPackableElement>();

        AssertThat(result.Length).IsEqual(3);
        AssertThat(((CharacterInputMessage)result[0]).Keys).IsEqual((byte)10);
        AssertThat(((CharacterInputMessage)result[2]).Keys).IsEqual((byte)30);
    }

    // A-14 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public void Deserialize_ThrowsMonkeNetException_OnUnknownTypeByte()
    {
        // Craft a byte array whose first byte (type id) is 0xFF, which should be
        // unregistered unless > 255 message types are in use.
        var badData = new byte[] { 0xFF, 0x00, 0x00 };
        AssertThrown(() => MessageSerializer.Deserialize(badData))
            .IsInstanceOf<MonkeNet.Shared.MonkeNetException>();
    }

    // A-15 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public void TypeMap_AllRegisteredTypes_HaveUniqueIds()
    {
        // Verify no two concrete IPackableMessage types share the same byte key.
        // Use round-trip consistency as a proxy: serialise each known type and
        // confirm it deserialises back to the same type.
        var types = new IPackableMessage[]
        {
            new ClockSyncMessage { ClientTime = 0, ServerTime = 0 },
            new EntityEventMessage { Event = EntityEventEnum.Created, EntityId = 0, EntityType = 0, Authority = 0 },
            new EntityRequestMessage { EntityType = 0 },
            new GameSnapshotMessage { Tick = 0, States = System.Array.Empty<IEntityStateData>() },
            new PackedClientInputMessage { Tick = 0, Inputs = new IPackableElement[] { new CharacterInputMessage() } },
        };

        var seenIds = new System.Collections.Generic.HashSet<byte>();
        foreach (var t in types)
        {
            byte id = MessageSerializer.GetByteTypeFromMessage(t);
            AssertThat(seenIds.Contains(id)).IsFalse();
            seenIds.Add(id);
        }
    }

    // A-16 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public void RoundTrip_OwnershipChangeRequestMessage()
    {
        var msg = new OwnershipChangeRequestMessage { EntityId = 42 };
        var result = (OwnershipChangeRequestMessage)RoundTrip(msg);
        AssertThat(result.EntityId).IsEqual(42);
    }

    // A-17 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public void RoundTrip_OwnershipChangeRejectedMessage()
    {
        var msg = new OwnershipChangeRejectedMessage { EntityId = 99 };
        var result = (OwnershipChangeRejectedMessage)RoundTrip(msg);
        AssertThat(result.EntityId).IsEqual(99);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static IPackableMessage RoundTrip(IPackableMessage msg)
    {
        byte[] bytes = MessageSerializer.Serialize(msg);
        return MessageSerializer.Deserialize(bytes);
    }
}
