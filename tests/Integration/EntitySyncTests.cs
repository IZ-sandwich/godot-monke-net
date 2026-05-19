using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using GdUnit4;
using MonkeNet.Client;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Server;
using MonkeNet.Shared;
using MonkeNet.Tests.Infrastructure;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Integration;

/// <summary>
/// G-01..G-07: Entity synchronisation tests.
///
/// These tests exercise the server entity manager's broadcast behaviour and the
/// client entity manager's reaction to EntityEventMessages.
///
/// Prerequisite: a full MonkeNet scene must be available at
///   res://addons/monke-net/scenes/MonkeNet.tscn
/// so that MonkeNetManager.Instance, EntitySpawner.Instance, and MonkeNetConfig.Instance
/// are all wired up before the tests run.
///
/// For snapshot / interpolator tests (G-06, G-07), only the snapshot message parsing
/// logic is verified — no full scene is required beyond ClientManager.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class EntitySyncTests
{
    private FakeNetworkEndpoint _serverNet;
    private FakeNetworkEndpoint _clientNet;
    private ISceneRunner _serverRunner;
    private ISceneRunner _clientRunner;
    private ISceneRunner _mainSceneRunner;
    private ServerManager _server;
    private MonkeNet.Client.ClientManager _client;

    [BeforeTest]
    public async Task SetUp()
    {
        MonkeNet.Shared.MonkeNetConfig.Instance = null;
        FakeNetworkBridge.Reset();
        MessageSerializer.RegisterNetworkMessages();
        (_serverNet, _clientNet) = FakeNetworkBridge.CreatePair();

        _mainSceneRunner = ISceneRunner.Load("res://demo/MainScene.tscn", autoFree: true);
        await _mainSceneRunner.AwaitIdleFrame();

        _serverRunner = ISceneRunner.Load("res://addons/monke-net/scenes/ServerManager.tscn", autoFree: true);
        await _serverRunner.AwaitIdleFrame();
        _server = _serverRunner.Scene() as ServerManager;

        _clientRunner = ISceneRunner.Load("res://addons/monke-net/scenes/ClientManager.tscn", autoFree: true);
        await _clientRunner.AwaitIdleFrame();
        _client = _clientRunner.Scene() as MonkeNet.Client.ClientManager;

        _server!.Initialize(_serverNet, port: 7600);
        _client!.Initialize(_clientNet, "127.0.0.1", 7600);
        await _serverRunner.AwaitIdleFrame();

        var spawner = MonkeNet.Shared.EntitySpawner.Instance;
        if (spawner != null)
        {
            foreach (var e in spawner.Entities.ToList())
            {
                if (Godot.GodotObject.IsInstanceValid(e))
                {
                    Godot.Node current = e;
                    while (current.GetParent() != spawner && current.GetParent() != null)
                        current = current.GetParent();
                    current.QueueFree();
                }
            }
            spawner.Entities.Clear();
            spawner.ClearClientEntities();
            // Flush QueueFree'd entities from previous test before new test body runs.
            // Without this, a freed entity's _ExitTree signal teardown races with the new
            // test's first AwaitIdleFrame and triggers CastHelpers.Unbox in the Godot bridge.
            await _clientRunner.AwaitIdleFrame();
        }
    }

    [AfterTest]
    public void TearDown()
    {
        // Free entities immediately (synchronous) BEFORE disposing runners.
        // Disposing runners uses QueueFree (deferred), so in the next frame the
        // old ServerManager still runs _PhysicsProcess and emits ServerTick(int)
        // via Godot's native bridge.  If entities are still in the tree at that
        // point, the signal dispatch lands on a partially-freed C# instance and
        // causes CastHelpers.Unbox in CSharpInstanceBridge.Call.
        // Freeing entities here — while managers are fully valid — lets their
        // _ExitTree disconnect signals cleanly, so the next frame has nothing to
        // dispatch to.
        var spawner = MonkeNet.Shared.EntitySpawner.Instance;
        if (spawner != null)
        {
            foreach (var e in spawner.Entities.Concat(spawner.ClientEntities).ToList())
            {
                if (!Godot.GodotObject.IsInstanceValid(e)) continue;
                Godot.Node current = e;
                while (current.GetParent() != spawner && current.GetParent() != null)
                    current = current.GetParent();
                current.Free();
            }
            spawner.Entities.Clear();
            spawner.ClientEntities.Clear();
        }

        _serverRunner?.Dispose();
        _clientRunner?.Dispose();
        _mainSceneRunner?.Dispose();
        MonkeNet.Shared.MonkeNetConfig.Instance = null;
    }

    // G-01 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task Server_SpawnEntity_BroadcastsEntityEventCreated()
    {
        // Capture packets that the server sends (broadcast = id 0)
        var sent = new List<byte[]>();
        _serverNet.PacketReceived += (_, bin) => { /* inbound on server — ignore */ };
        // Hook the peer (client) endpoint to capture what was delivered to it
        _clientNet.PacketReceived += (_, bin) => sent.Add(bin);

        // Deliver a fake EntityRequestMessage from the client so the server spawns an entity
        var req = new EntityRequestMessage { EntityType = 1 };
        _serverNet.SimulateIncomingPacket(2, MessageSerializer.Serialize(req));
        await _serverRunner.AwaitIdleFrame();

        // Verify at least one EntityEventMessage(Created) was delivered to the client
        bool foundCreated = sent.Any(bytes =>
        {
            try
            {
                var msg = MessageSerializer.Deserialize(bytes);
                return msg is EntityEventMessage evt && evt.Event == EntityEventEnum.Created;
            }
            catch { return false; }
        });

        AssertThat(foundCreated).IsTrue();
    }

    // G-02 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task Client_ReceivesEntityEventCreated_SpawnsClientEntity()
    {
        // Deliver EntityEventMessage(Created) directly to the client
        var createMsg = new EntityEventMessage
        {
            Event = EntityEventEnum.Created,
            EntityId = 99,
            EntityType = 1,
            Authority = _client.GetNetworkId(),
            Position = Godot.Vector3.Zero,
            Yaw = 0f,
            Metadata = ""
        };

        _clientNet.SimulateIncomingPacket(1, MessageSerializer.Serialize(createMsg));
        await _clientRunner.AwaitIdleFrame();

        // EntitySpawner.EntitySpawned signal should have fired
        if (MonkeNet.Shared.EntitySpawner.Instance != null)
        {
            AssertThat(MonkeNet.Shared.EntitySpawner.Instance.ClientEntities.Count).IsGreaterEqual(1);
        }
    }

    // G-03 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task Client_ReceivesEntityEventDestroyed_RemovesEntity()
    {
        // First spawn an entity
        var createMsg = new EntityEventMessage
        {
            Event = EntityEventEnum.Created,
            EntityId = 88,
            EntityType = 1,
            Authority = _client.GetNetworkId(),
            Position = Godot.Vector3.Zero,
            Yaw = 0f,
            Metadata = ""
        };
        _clientNet.SimulateIncomingPacket(1, MessageSerializer.Serialize(createMsg));
        await _clientRunner.AwaitIdleFrame();

        int countAfterSpawn = MonkeNet.Shared.EntitySpawner.Instance?.ClientEntities.Count ?? 0;

        // Now destroy it
        var destroyMsg = new EntityEventMessage
        {
            Event = EntityEventEnum.Destroyed,
            EntityId = 88,
            EntityType = 1,
            Authority = 0,
            Metadata = ""
        };
        _clientNet.SimulateIncomingPacket(1, MessageSerializer.Serialize(destroyMsg));
        await _clientRunner.AwaitIdleFrame();
        // Extra frame so the deferred QueueFree fully completes before the test ends.
        await _clientRunner.AwaitIdleFrame();

        int countAfterDestroy = MonkeNet.Shared.EntitySpawner.Instance?.ClientEntities.Count ?? 0;
        AssertThat(countAfterDestroy).IsLess(countAfterSpawn);
    }

    // G-04 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task Server_NewClientConnects_ReceivesWorldState()
    {
        // Spawn an entity owned by peer 2 first so there is world state to sync
        var spawnReq = new EntityRequestMessage { EntityType = 1 };
        _serverNet.SimulateIncomingPacket(2, MessageSerializer.Serialize(spawnReq));
        await _serverRunner.AwaitIdleFrame();

        var spawner = MonkeNet.Shared.EntitySpawner.Instance;
        AssertThat(spawner.Entities.Count).IsGreaterEqual(1);
        int spawnedEntityId = spawner.Entities[0].EntityId;

        // Discard everything the server sent during the spawn so we only inspect
        // packets emitted in response to the new connection
        _serverNet.ClearSentPackets();

        // Simulate a second peer connecting
        _serverNet.FireClientConnected(peerId: 3);
        await _serverRunner.AwaitIdleFrame();

        // SyncWorldState should have unicast an EntityEvent(Created) to peer 3
        // describing the existing entity
        bool foundCreatedForPeer3 = _serverNet.SentPackets.Any(p =>
        {
            if (p.Id != 3) return false;
            try
            {
                return MessageSerializer.Deserialize(p.Data) is EntityEventMessage evt
                    && evt.Event == EntityEventEnum.Created
                    && evt.EntityId == spawnedEntityId;
            }
            catch { return false; }
        });

        AssertThat(foundCreatedForPeer3).IsTrue();
    }

    // G-05 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public void Server_SendsSnapshot_ContainsCorrectTick()
    {
        const int Tick = 42;
        var entityManager = _server.GetNode<ServerEntityManager>("ServerEntityManager");
        _serverNet.ClearSentPackets();

        // Direct invocation with a known tick — bypasses physics frame timing
        entityManager.SendSnapshotData(Tick);

        var snapshots = new List<GameSnapshotMessage>();
        foreach (var p in _serverNet.SentPackets)
        {
            try
            {
                if (MessageSerializer.Deserialize(p.Data) is GameSnapshotMessage snap)
                    snapshots.Add(snap);
            }
            catch { }
        }

        AssertThat(snapshots.Count).IsEqual(1);
        AssertThat(snapshots[0].Tick).IsEqual(Tick);
    }

    // G-06 deleted with the ClientSnapshotInterpolator removal — out-of-order
    // snapshots are now handled by ClientPredictionManager's per-tick matching
    // (older ticks are pruned in ProcessServerState's _predictedStates loop).

    // G-07 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task Snapshot_RoundTrip_PreservesTick()
    {
        var original = new GameSnapshotMessage
        {
            Tick = 777,
            States = System.Array.Empty<IEntityStateData>()
        };

        byte[] serialized = MessageSerializer.Serialize(original);
        var result = (GameSnapshotMessage)MessageSerializer.Deserialize(serialized);

        AssertThat(result.Tick).IsEqual(777);
    }
}
