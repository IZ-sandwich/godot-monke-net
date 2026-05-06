using Godot;
using MonkeNet.Client;
using MonkeNet.NetworkMessages;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MonkeNet.Shared;

public partial class EntitySpawner : Node
{
    public const int AuthorityServer = 0;

    // Collision layers — must match the names set in Project Settings → Layer Names → 3D Physics.
    private const uint LayerEnvironment  = 1;        // layer  1 — static world geometry
    private const uint LayerClientPlayers = 2;       // layer  2 — LocalPlayer, DummyPlayer
    private const uint LayerServerPlayers = 1 << 15; // layer 16 — server entities in listen-server mode

    [Signal] public delegate void EntitySpawnedEventHandler(Node3D entity);
    [Signal] public delegate void EntityDestroyedEventHandler(int entityId);

    public static EntitySpawner Instance { get; private set; }
    public List<NetworkBehaviour> Entities { get; private set; } = [];       // Server entities only
    public List<NetworkBehaviour> ClientEntities { get; private set; } = []; // Client entities only (LocalPlayer, DummyPlayer)

    public override void _Ready()
    {
        Instance = this;
    }

    public override void _ExitTree()
    {
        if (Instance == this)
            Instance = null;
    }

    //TODO: do not cast, make Entities a list of INetworkedEntity directly
    public NetworkBehaviour GetEntityById(int entityId)
    {
        // Prefer client entities so that in listen-server mode the LocalPlayer/DummyPlayer
        // is returned instead of the ServerPlayer sharing the same EntityId.
        foreach (var e in ClientEntities)
            if (e.EntityId == entityId) return e;
        foreach (var e in Entities)
            if (e.EntityId == entityId) return e;

        throw new MonkeNetException($"Couldn't find entity by id {entityId}");
    }

    // Returns null when the entity doesn't exist yet (e.g. snapshot arrived before EntityEventMessage.Created).
    public NetworkBehaviour TryGetEntityById(int entityId)
    {
        foreach (var e in ClientEntities)
            if (e.EntityId == entityId) return e;
        foreach (var e in Entities)
            if (e.EntityId == entityId) return e;
        return null;
    }

    // Can be called from both the server or a client, so it needs to handle both scenarios.
    // Pass isServerSpawn: true when called from a server-side component so that the server
    // scene is selected even in listen-server mode where IsServer is true for both contexts.
    public Node SpawnEntity(EntityEventMessage @event, bool isServerSpawn = false)
    {
        var config = MonkeNetConfig.Instance
            .GetSpawnConfigurationForEntityType(@event.EntityType);

        var scene = SolveWhatEntitySceneToSpawn(config, @event, isServerSpawn);

        var instance = scene?.Instantiate()
            ?? throw new MonkeNetException($"Couldn't instance entity {@event.EntityType}");

        NetworkBehaviour networkBehaviour = MonkeNetComponents.GetComponent<NetworkBehaviour>(instance)
            ?? throw new MonkeNetException($"Can't spawn entity that doesn't have a {nameof(NetworkBehaviour)} node!");

        InitializeEntity(instance, networkBehaviour, @event);
        AddChild(instance);
        if (isServerSpawn)
            Entities.Add(networkBehaviour);
        else
            ClientEntities.Add(networkBehaviour);

        // In listen-server mode both a server entity and a client entity exist in the
        // same physics space. Without adjustment they share the default collision layer
        // (1) and block each other, causing stuck movement and a visible server mesh.
        // Server entities move to layer 16 (detected by nothing on the client side) so
        // they can still collide with the environment but never block client entities.
        // Client entities move to layer 2 so they detect environment (mask bit 1) and
        // each other (mask bit 2) but not server entities (layer 16).
        if (MonkeNetManager.Instance.IsServer && ClientManager.Instance != null)
        {
            if (isServerSpawn)
            {
                SetCollisionLayerRecursive(instance, layer: LayerServerPlayers, mask: LayerEnvironment | LayerServerPlayers);
                HideMeshesRecursive(instance);
            }
            else
            {
                SetCollisionLayerRecursive(instance, layer: LayerClientPlayers, mask: LayerEnvironment | LayerClientPlayers);
            }
        }

        EmitSignal(SignalName.EntitySpawned, instance);
        networkBehaviour.OnEntitySpawned();

        string layerInfo = instance is CollisionObject3D co
            ? $" Layer={co.CollisionLayer} Mask={co.CollisionMask}"
            : "";
        MonkeLogger.Info($"Spawned entity:{@event.EntityId} ({@event.EntityType}) Auth:{@event.Authority} ServerSpawn:{isServerSpawn}{layerInfo}");
        return instance;
    }

    public void DestroyEntity(EntityEventMessage @event)
    {
        var serverEntity = Entities.Find(e => e.EntityId == @event.EntityId);
        if (serverEntity != null)
        {
            Entities.Remove(serverEntity);
            FreeEntityRoot(serverEntity);
            EmitSignal(SignalName.EntityDestroyed, @event.EntityId);
            MonkeLogger.Info($"Destroyed server entity {serverEntity.EntityId}");
            return;
        }

        var clientEntity = ClientEntities.Find(e => e.EntityId == @event.EntityId);
        if (clientEntity != null)
        {
            ClientEntities.Remove(clientEntity);
            FreeEntityRoot(clientEntity);
            EmitSignal(SignalName.EntityDestroyed, @event.EntityId);
            MonkeLogger.Info($"Destroyed client entity {clientEntity.EntityId}");
            return;
        }

        MonkeLogger.Error($"DestroyEntity: entity {@event.EntityId} not found");
    }

    public void DestroyClientEntity(EntityEventMessage @event)
    {
        var clientEntity = ClientEntities.Find(e => e.EntityId == @event.EntityId);
        if (clientEntity != null)
        {
            ClientEntities.Remove(clientEntity);
            FreeEntityRoot(clientEntity);
            EmitSignal(SignalName.EntityDestroyed, @event.EntityId);
            MonkeLogger.Info($"Destroyed client entity {clientEntity.EntityId}");
            return;
        }
        MonkeLogger.Error($"DestroyClientEntity: entity {@event.EntityId} not found in ClientEntities");
    }

    public void ClearClientEntities()
    {
        foreach (var entity in ClientEntities.ToList())
            FreeEntityRoot(entity);
        ClientEntities.Clear();
    }

    public void ClearServerEntities()
    {
        foreach (var entity in Entities.ToList())
            FreeEntityRoot(entity);
        Entities.Clear();
    }

    public Node3D GetEntityRoot(NetworkBehaviour entity)
    {
        Node current = entity;
        while (current.GetParent() != this && current.GetParent() != null)
            current = current.GetParent();
        return current as Node3D;
    }

    private void FreeEntityRoot(NetworkBehaviour entity)
    {
        // Walk up until we find the direct child of EntitySpawner, then free it
        Node current = entity;
        while (current.GetParent() != this && current.GetParent() != null)
            current = current.GetParent();
        current.QueueFree();
    }

    public List<int> GetAllEntitiesByAuthority(int authority)
    {
        List<int> entitiesGeneratedByAuthority = [];

        for (int i = 0; i < Entities.Count; i++)
        {
            if (Entities[i].Authority == authority)
            {
                entitiesGeneratedByAuthority.Add(Entities[i].EntityId);
            }
        }

        return entitiesGeneratedByAuthority;
    }

    private static void InitializeEntity(Node node, NetworkBehaviour entity, EntityEventMessage @event)
    {
        node.Name = @event.EntityId.ToString();
        entity.EntityId = @event.EntityId;
        entity.EntityType = @event.EntityType;
        entity.Authority = @event.Authority;
        entity.Metadata = @event.Metadata;

        // Apply the spawn pose carried in the EntityEventMessage. For initial spawns this is
        // Vector3.Zero / 0f (the default), so nothing visible changes. For reclaim spawns the
        // server fills these from the orphaned entity's last known transform — without this
        // the reclaimed body would respawn at scene-origin instead of where it was when the
        // owner disconnected.
        if (node is Node3D node3D)
        {
            node3D.Position = @event.Position;
            var rotation = node3D.Rotation;
            rotation.Y = @event.Yaw;
            node3D.Rotation = rotation;
        }
    }

    private PackedScene SolveWhatEntitySceneToSpawn(EntitySpawnConfiguration entitySpawnConfig, EntityEventMessage @event, bool isServerSpawn)
    {
        if (isServerSpawn)
            return entitySpawnConfig.ServerScene;

        bool isAuthority = @event.Authority == ClientManager.Instance.GetNetworkId();

        if (isAuthority)
            return entitySpawnConfig.ClientAuthorityScene;

        return entitySpawnConfig.ClientDummyScene;
    }

    private static void SetCollisionLayerRecursive(Node node, uint layer, uint mask)
    {
        if (node is CollisionObject3D body)
        {
            body.CollisionLayer = layer;
            body.CollisionMask = mask;
        }
        foreach (Node child in node.GetChildren())
            SetCollisionLayerRecursive(child, layer, mask);
    }

    private static void HideMeshesRecursive(Node node)
    {
        if (node is MeshInstance3D mesh)
            mesh.Visible = false;
        foreach (Node child in node.GetChildren())
            HideMeshesRecursive(child);
    }
}