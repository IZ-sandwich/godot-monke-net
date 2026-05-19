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
    /// <summary>
    /// Game-defined approval policy for client-initiated authority requests. Returns
    /// true to grant ownership, false to reject. Default is reject — the game must
    /// opt-in by assigning this delegate so an unconfigured server doesn't let any
    /// peer claim any entity. Signature: <c>(requesterClientId, entityId) → approved</c>.
    /// </summary>
    public System.Func<int, int, bool> OwnershipApprover { get; set; }

    private EntitySpawner _entitySpawner;
    private int _entityIdCount = 0;
    private int _lastEntitiesPacked = 0;

    public override void _EnterTree()
    {
        _entitySpawner = MonkeNetManager.Instance?.EntitySpawner;
    }

    public void SendSnapshotData(int currentTick)
    {
        var snapshotCommand = PackSnapshot(currentTick);
        MonkeLogger.Debug($"[NET-SNAP-TX] tick={currentTick} entities={snapshotCommand.States.Length} -> broadcast");
        for (int i = 0; i < snapshotCommand.States.Length; i++)
        {
            var s = snapshotCommand.States[i];
            // Cast to GameDemo.EntityStateMessage shape via reflection-free interface check would
            // require the framework to know the demo type — instead, ToString the boxed struct.
            // Concrete field formatting comes from each IEntityStateData's own override; default
            // structs print field names which is good enough for replay/debug.
            MonkeLogger.Debug($"[NET-SNAP-TX]   eid={s.EntityId} state={s}");
        }
        SendCommandToClient(0, snapshotCommand, INetworkManager.PacketModeEnum.Unreliable, (int)ChannelEnum.Snapshot);
    }

    protected override void OnCommandReceived(int clientId, IPackableMessage command)
    {
        if (command is EntityRequestMessage entityRequest)
        {
            SpawnEntity<Node3D>(entityRequest.EntityType, clientId);
        }

        // Reclaim is no longer client-initiated: ServerConnectionMonitor now
        // owns the lookup, keyed by the client's persistent identity
        // (announced via ClientHelloMessage and surviving disconnect /
        // reconnect / process restart). On hello, the monitor reassigns
        // Authority directly through ChangeAuthority.

        if (command is OwnershipChangeRequestMessage ownershipReq)
        {
            HandleOwnershipRequest(clientId, ownershipReq.EntityId);
        }

        if (command is ReleaseAuthorityMessage releaseReq)
        {
            HandleReleaseRequest(clientId, releaseReq.EntityId);
        }
    }

    /// <summary>Look up a server-side entity by id, or null if it doesn't
    /// exist (e.g. it was just destroyed by another path). Used by
    /// <c>ServerConnectionMonitor</c>'s reclaim flow and by anything else
    /// that needs to inspect an entity without iterating the full list.</summary>
    public NetworkBehaviour FindEntityById(int entityId) =>
        _entitySpawner.Entities.Find(e => e.EntityId == entityId);

    private void HandleReleaseRequest(int requesterId, int entityId)
    {
        var entity = _entitySpawner.Entities.Find(e => e.EntityId == entityId);
        if (entity == null || entity.Authority != requesterId)
        {
            MonkeLogger.Debug($"HandleReleaseRequest: ignoring release from client {requesterId} for entity {entityId} (entity null or not owner)");
            return;
        }
        var policy = MonkeNetConfig.Instance?.GetSpawnConfigurationForEntityType(entity.EntityType)?.OwnershipPolicy;
        if (policy != null && !policy.AllowOwnerRelease)
        {
            MonkeLogger.Debug($"HandleReleaseRequest: policy denies owner-release for entity type {entity.EntityType}");
            return;
        }
        ChangeAuthority(entityId, 0);
    }

    private void HandleOwnershipRequest(int requesterId, int entityId)
    {
        var entity = _entitySpawner.Entities.Find(e => e.EntityId == entityId);
        // Reject silently if entity doesn't exist — a stale request from a client that
        // just saw the entity destroyed is normal, not a misbehavior to warn about.
        if (entity == null)
        {
            SendRejection(requesterId, entityId);
            return;
        }

        if (!EvaluatePolicy(entity, requesterId))
        {
            SendRejection(requesterId, entityId);
            return;
        }

        // Custom approver runs as a final gate after the declarative policy. Lets games
        // express predicates not expressible in OwnershipPolicy (e.g. "requester holds
        // a key"). A null approver passes through — pure policy-driven decisions don't
        // need it.
        if (OwnershipApprover != null && !OwnershipApprover(requesterId, entityId))
        {
            SendRejection(requesterId, entityId);
            return;
        }

        ChangeAuthority(entityId, requesterId);
    }

    private bool EvaluatePolicy(NetworkBehaviour entity, int requesterId)
    {
        var config = MonkeNetConfig.Instance?.GetSpawnConfigurationForEntityType(entity.EntityType);
        var policy = config?.OwnershipPolicy;
        if (policy == null)
        {
            // No policy configured = the entity type opts out of client-initiated claims.
            // Server-side ChangeAuthority calls (e.g. demo's vehicle-reclaim on disconnect)
            // still work; this only gates the request path.
            return false;
        }

        if (policy.RequireUnowned && entity.Authority != 0)
            return false;

        if (policy.MaxRequesterDistance > 0f)
        {
            var entityRoot = _entitySpawner.GetEntityRoot(entity);
            if (entityRoot == null) return false;
            Vector3 entityPos = entityRoot.GlobalPosition;

            float maxSq = policy.MaxRequesterDistance * policy.MaxRequesterDistance;
            bool anyInRange = false;
            foreach (int ownedId in _entitySpawner.GetAllEntitiesByAuthority(requesterId))
            {
                var ownedEntity = _entitySpawner.Entities.Find(e => e.EntityId == ownedId);
                var ownedRoot = ownedEntity != null ? _entitySpawner.GetEntityRoot(ownedEntity) : null;
                if (ownedRoot == null) continue;
                if (ownedRoot.GlobalPosition.DistanceSquaredTo(entityPos) <= maxSq)
                {
                    anyInRange = true;
                    break;
                }
            }
            if (!anyInRange) return false;
        }

        return true;
    }

    private static void SendRejection(int requesterId, int entityId)
    {
        SendCommandToClient(requesterId, new OwnershipChangeRejectedMessage { EntityId = entityId },
            INetworkManager.PacketModeEnum.Reliable, (int)ChannelEnum.GameReliable);
    }

    /// <summary>
    /// Reassigns ownership of a server-authoritative entity. Mutates the entity's Authority
    /// in place and broadcasts <see cref="AuthorityChangedMessage"/> so every client updates
    /// its local <c>entity.Authority</c> field. No scene swap, no rigid-body state loss —
    /// the same client-side scene instance keeps simulating; only input routing changes.
    /// <paramref name="newAuthority"/> = 0 means the server reclaims ownership.
    /// </summary>
    public void ChangeAuthority(int entityId, int newAuthority)
    {
        var entity = _entitySpawner.Entities.Find(e => e.EntityId == entityId);
        if (entity == null)
        {
            MonkeLogger.Warn($"ChangeAuthority: entity {entityId} not found on server");
            return;
        }

        int oldAuthority = entity.Authority;
        if (oldAuthority == newAuthority) return;

        entity.Authority = newAuthority;

        SendCommandToClient((int)NetworkManagerEnet.AudienceMode.Broadcast,
            new AuthorityChangedMessage { EntityId = entityId, NewAuthority = newAuthority },
            INetworkManager.PacketModeEnum.Reliable, (int)ChannelEnum.EntityEvent);

        MonkeLogger.Info($"ChangeAuthority: entity {entityId} authority {oldAuthority} -> {newAuthority}");
    }

    protected override void OnClientConnected(int clientId)
    {
        SyncWorldState(clientId);
    }

    public List<int> GetEntityIdsForClient(int clientId) =>
        _entitySpawner.GetAllEntitiesByAuthority(clientId);

    public void DestroyEntitiesForClient(int clientId)
    {
        var ids = _entitySpawner.GetAllEntitiesByAuthority(clientId);
        foreach (int id in ids)
            DestroyEntity(id, (int)NetworkManagerEnet.AudienceMode.Broadcast);
    }

    public void OrphanEntitiesForClient(int clientId)
    {
        foreach (var entity in _entitySpawner.Entities)
            if (entity.Authority == clientId)
                entity.Authority = 0;
    }

    /// <summary>
    /// Destroys an entity only if it is currently orphaned (Authority == 0).
    /// Used by the reclaim-expiry sweep to clean up entities whose owners never reconnected,
    /// without clobbering entities that were reclaimed in the meantime.
    /// </summary>
    public void DestroyOrphanedEntity(int entityId)
    {
        var entity = _entitySpawner.Entities.Find(e => e.EntityId == entityId);
        if (entity == null || entity.Authority != 0) return;
        DestroyEntity(entityId, (int)NetworkManagerEnet.AudienceMode.Broadcast);
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

        // Include per-entity inputs so observers can drive their local prediction
        // of entities they don't own with the same input the server applied. The
        // ServerInputReceiver lives as a sibling under ServerManager; look it up
        // once per snapshot rather than caching to keep restart paths simple.
        var inputReceiver = GetParent().GetNodeOrNull<ServerInputReceiver>("ServerInputReceiver");
        var inputs = new List<EntityInput>(entityCount);

        for (int i = 0; i < entityCount; i++)
        {
            snapshot.States[i] = includedEntities[i].PackEntityState();
            if (inputReceiver != null)
            {
                var lastInput = inputReceiver.GetLastInputFor(includedEntities[i]);
                if (lastInput != null)
                {
                    inputs.Add(new EntityInput { EntityId = includedEntities[i].EntityId, Input = lastInput });
                }
            }
        }
        snapshot.Inputs = inputs.ToArray();

        return snapshot;
    }

    /// <summary>
    /// Notifies all clients that an Entity has spawned
    /// </summary>
    /// <param name="entityId"></param>
    /// <param name="entityType"></param>
    /// <param name="targetId"></param>
    /// <param name="authority"></param>
    public T SpawnEntity<T>(byte entityType, int authority, Vector3? position = null, string metadata = "") where T : Node3D
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
        T instancedEntity = _entitySpawner.SpawnEntity(entityEvent, isServerSpawn: true) as T;
        // Caller-supplied position overrides whatever OnEntitySpawned set (some
        // entities have a hardcoded default like (0,10,0) for the ball/cube
        // drop-in). Apply BEFORE capturing entityEvent.Position so the broadcast
        // carries the final position — otherwise every client's DummyEntity
        // spawns at the default for one frame before the first snapshot
        // teleports it to the real location, which is a visible jump.
        if (position.HasValue) instancedEntity.GlobalPosition = position.Value;
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
            var entityRoot = _entitySpawner.GetEntityRoot(entity);
            var entityEvent = new EntityEventMessage
            {
                Event = EntityEventEnum.Created,
                EntityId = entity.EntityId,
                EntityType = entity.EntityType,
                Authority = entity.Authority,
                Position = entityRoot?.GlobalPosition ?? Vector3.Zero,
                Yaw = entityRoot?.GlobalRotation.Y ?? 0f,
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