using Godot;
using ImGuiNET;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Shared;
using System.Collections.Generic;

namespace MonkeNet.Server;

/// <summary>
/// Handles creation/deletion of entities
/// </summary>
[GlobalClass]
public partial class ServerEntityManager : InternalServerComponent
{
    private EntitySpawner _entitySpawner;
    private int _entityIdCount = 0;
    private int _lastEntitiesPacked = 0;

    public override void _EnterTree()
    {
        _entitySpawner = MonkeNetManager.Instance.EntitySpawner;
    }

    public void SendSnapshotData(int currentTick)
    {
        var snapshotCommand = PackSnapshot(currentTick);
        SendCommandToClient(0, snapshotCommand, INetworkManager.PacketModeEnum.Unreliable, (int)ChannelEnum.Snapshot);
    }

    protected override void OnCommandReceived(int clientId, IPackableMessage command)
    {
        if (command is EntityRequestMessage entityRequest)
        {
            SpawnEntity<Node3D>(entityRequest.EntityType, clientId);
        }
    }

    protected override void OnClientConnected(int clientId)
    {
        SyncWorldState(clientId);
    }

    protected override void OnClientDisconnected(int clientId)
    {
        //TODO: this will send 1 packet for each entity, do in bulk, same as sync should be done
        List<int> entitiesGeneratedByAuthority = _entitySpawner.GetAllEntitiesByAuthority(clientId);
        foreach (int entityId in entitiesGeneratedByAuthority)
        {
            DestroyEntity(entityId, (int)NetworkManagerEnet.AudienceMode.Broadcast);
        }
    }

    /// <summary>
    /// Packs the current game state for a tick (Snapshot)
    /// </summary>
    /// <param name="currentTick"></param>
    private GameSnapshotMessage PackSnapshot(int currentTick)
    {
        // Solve which entities we should include in this snapshot
        List<ServerStateSyncronizer> includedEntities = [];
        foreach (NetworkBehaviour entity in _entitySpawner.Entities)
        {
            var serverStateSyncronizer = entity.GetComponent<ServerStateSyncronizer>();
            if (serverStateSyncronizer != null)
            {
                includedEntities.Add(serverStateSyncronizer);
            }
        }

        // Pack entity data into snapshot
        var entityCount = includedEntities.Count;
        _lastEntitiesPacked = entityCount;

        var snapshot = new GameSnapshotMessage
        {
            Tick = currentTick,
            States = new IEntityStateData[entityCount]
        };

        for (int i = 0; i < entityCount; i++)
        {
            snapshot.States[i] = includedEntities[i].PackEntityState();
        }

        return snapshot;
    }

    /// <summary>
    /// Notifies all clients that an Entity has spawned
    /// </summary>
    /// <param name="entityId"></param>
    /// <param name="entityType"></param>
    /// <param name="targetId"></param>
    /// <param name="authority"></param>
    public T SpawnEntity<T>(byte entityType, int authority, string metadata = "") where T : Node3D
    {
        var entityEvent = new EntityEventMessage
        {
            Event = EntityEventEnum.Created,
            EntityId = ++_entityIdCount,
            EntityType = entityType,
            Authority = authority,
            Metadata = metadata
        };

        // TODO: this should be inside metadata
        // Execute event locally and retrieve position and rotation data
        T instancedEntity = _entitySpawner.SpawnEntity(entityEvent) as T;
        entityEvent.Position = instancedEntity.Position;
        entityEvent.Yaw = instancedEntity.Rotation.Y;

        SendCommandToClient((int)NetworkManagerEnet.AudienceMode.Broadcast, entityEvent, INetworkManager.PacketModeEnum.Reliable, (int)ChannelEnum.EntityEvent);
        return instancedEntity;
    }

    /// <summary>
    /// Notifies all clients that an Entity has been destroyed
    /// </summary>
    /// <param name="entityId"></param>
    /// <param name="targetId"></param>
    public void DestroyEntity(int entityId, int targetId)
    {
        var entityEvent = new EntityEventMessage
        {
            Event = EntityEventEnum.Destroyed,
            EntityId = entityId,
            EntityType = 0,
            Authority = 0,
            Metadata = ""
        };

        _entitySpawner.DestroyEntity(entityEvent);  // Execute event locally

        SendCommandToClient(targetId, entityEvent, INetworkManager.PacketModeEnum.Reliable, (int)ChannelEnum.EntityEvent);
    }

    /// <summary>
    /// Sends the whole game state to a specific clientId, used when the client connects to replicate world state
    /// </summary>
    /// <param name="clientId"></param>
    private void SyncWorldState(int clientId)
    {
        foreach (NetworkBehaviour entity in _entitySpawner.Entities)
        {
            var entityEvent = new EntityEventMessage
            {
                Event = EntityEventEnum.Created,
                EntityId = entity.EntityId,
                EntityType = entity.EntityType,
                Authority = entity.Authority,
                Metadata = entity.Metadata
            };

            SendCommandToClient(clientId, entityEvent, INetworkManager.PacketModeEnum.Reliable, (int)ChannelEnum.EntityEvent);
        }

    }

    public void DisplayDebugInformation()
    {
        if (ImGui.CollapsingHeader("Entity Manager"))
        {
            ImGui.Text($"Entity Count {_entityIdCount}");
            ImGui.Text($"Entities Packed {_lastEntitiesPacked}");
        }
    }
}