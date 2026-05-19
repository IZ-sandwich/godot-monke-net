using System.Linq;
using System.Threading.Tasks;
using GdUnit4;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Server;
using MonkeNet.Shared;
using MonkeNet.Tests.Infrastructure;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Integration;

/// <summary>
/// R-01..R-09: Entity retention and reclaim under the
/// <see cref="ClientPersistentIdentity"/> design.
///
/// New flow (replaces the previous server-issued one-shot session-token
/// design): on connect, the client sends a <see cref="ClientHelloMessage"/>
/// carrying its persistent client id (CLI override, file in user://, or
/// freshly generated GUID — see <see cref="ClientPersistentIdentity"/>).
/// The server's <see cref="ServerConnectionMonitor"/> stores the peer→id
/// mapping; on disconnect it parks the player's entities in a reclaim
/// entry keyed by that persistent id; on any later reconnect with the
/// same id it looks up the entry and reassigns Authority — no client-sent
/// reclaim message, no one-shot consume, just a lookup keyed by stable
/// identity that the same client can present any number of times.
///
/// These tests inject <c>ClientHelloMessage</c> directly through the
/// <see cref="FakeNetworkBridge"/> so we don't need to wait for the full
/// clock-sync handshake that drives <c>OnNetworkReady</c> on the client
/// side. The hello-send-on-NetworkReady path itself is exercised end to
/// end by the multi-process persistence suite.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class EntityRetentionTests
{
    private const int ClientPeerId = 2;
    private const string ClientAId = "client-a-persistent-id";

    private FakeNetworkEndpoint _serverNet;
    private FakeNetworkEndpoint _clientNet;
    private ISceneRunner _serverRunner;
    private ISceneRunner _clientRunner;
    private ISceneRunner _mainSceneRunner;
    private ServerManager _server;
    private MonkeNet.Client.ClientManager _client;
    private ServerConnectionMonitor _monitor;

    [BeforeTest]
    public async Task SetUp()
    {
        MonkeNetConfig.Instance = null;
        FakeNetworkBridge.Reset();
        MonkeNet.Client.ClientEntityManager.ClearAwaitingReconnect();
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
        await _clientRunner.AwaitIdleFrame();

        _monitor = _server.GetNode<ServerConnectionMonitor>("ServerConnectionMonitor");

        ClearSpawnedEntities();
    }

    [AfterTest]
    public void TearDown()
    {
        _serverRunner?.Dispose();
        _clientRunner?.Dispose();
        _mainSceneRunner?.Dispose();
        MonkeNetConfig.Instance = null;
    }

    // R-01 ─────────────────────────────────────────────────────────────────────
    // Hello carries the client's persistent id to the server, where it gets
    // mapped to the peer's netId. Replaces the server-issued-session-token
    // path entirely — there is no longer any server-to-client identity packet
    // on connect.
    [TestCase]
    public async Task ClientHello_MapsPeerIdToPersistentClientId()
    {
        SendClientHello(ClientPeerId, ClientAId);
        await _serverRunner.AwaitIdleFrame();

        AssertThat(_monitor.GetClientPersistentId(ClientPeerId))
            .OverrideFailureMessage(
                $"server should have recorded the hello mapping peer {ClientPeerId} → {ClientAId}; " +
                $"got {_monitor.GetClientPersistentId(ClientPeerId) ?? "<null>"}")
            .IsEqual(ClientAId);
    }

    // R-02 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task ManualDisconnect_RemoveEntityMode_DestroysEntities()
    {
        _monitor.ManualDisconnectMode = DisconnectEntityMode.RemoveEntity;

        SendClientHello(ClientPeerId, ClientAId);
        SpawnEntityForClient(ClientPeerId);
        await _serverRunner.AwaitIdleFrame();
        int countBefore = EntitySpawner.Instance.Entities.Count;

        SendDisconnectNotification(ClientPeerId);
        _serverNet.FireClientDisconnected(ClientPeerId);
        await _serverRunner.AwaitIdleFrame();

        int countAfter = EntitySpawner.Instance.Entities.Count;
        AssertThat(countAfter).IsLess(countBefore);
    }

    // R-03 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task ManualDisconnect_KeepEntityMode_OrphansEntities()
    {
        _monitor.ManualDisconnectMode = DisconnectEntityMode.KeepEntity;

        SendClientHello(ClientPeerId, ClientAId);
        SpawnEntityForClient(ClientPeerId);
        await _serverRunner.AwaitIdleFrame();
        int countBefore = EntitySpawner.Instance.Entities.Count;
        AssertThat(countBefore).IsGreaterEqual(1);

        SendDisconnectNotification(ClientPeerId);
        _serverNet.FireClientDisconnected(ClientPeerId);
        await _serverRunner.AwaitIdleFrame();

        // Entity count must not decrease — entity is kept alive
        int countAfter = EntitySpawner.Instance.Entities.Count;
        AssertThat(countAfter).IsEqual(countBefore);

        // All entities previously owned by the client must now be orphaned (Authority == 0)
        bool allOrphaned = EntitySpawner.Instance.Entities
            .All(e => e.Authority != ClientPeerId);
        AssertThat(allOrphaned).IsTrue();

        // And the reclaim entry must exist, keyed by the persistent id.
        AssertThat(_monitor.HasPendingReclaimFor(ClientAId))
            .OverrideFailureMessage($"server should hold a reclaim entry for [cid:{ClientPersistentIdentity.Tail(ClientAId)}] after KeepEntity disconnect")
            .IsTrue();
    }

    // R-04 ─────────────────────────────────────────────────────────────────────
    // The headline behaviour: a reconnect with the SAME persistent client id
    // recovers Authority of the previously-orphaned entity without the
    // client ever sending a separate reclaim message.
    [TestCase]
    public async Task Reclaim_OnRehello_RestoresAuthorityToReconnectingPeer()
    {
        _monitor.ManualDisconnectMode = DisconnectEntityMode.KeepEntity;

        SendClientHello(ClientPeerId, ClientAId);
        SpawnEntityForClient(ClientPeerId);
        await _serverRunner.AwaitIdleFrame();
        AssertThat(EntitySpawner.Instance.Entities.Count).IsGreaterEqual(1);
        int entityId = EntitySpawner.Instance.Entities[0].EntityId;

        // Disconnect — entity orphaned + parked under ClientAId.
        SendDisconnectNotification(ClientPeerId);
        _serverNet.FireClientDisconnected(ClientPeerId);
        await _serverRunner.AwaitIdleFrame();
        AssertThat(_monitor.HasPendingReclaimFor(ClientAId)).IsTrue();

        // Reconnect under a new peer id and re-hello with the SAME persistent
        // client id — server should reassign Authority automatically.
        const int newPeerId = 3;
        _serverNet.FireClientConnected(newPeerId);
        await _serverRunner.AwaitIdleFrame();
        SendClientHello(newPeerId, ClientAId);
        await _serverRunner.AwaitIdleFrame();

        var entity = EntitySpawner.Instance.Entities.FirstOrDefault(e => e.EntityId == entityId);
        AssertThat(entity).IsNotNull();
        AssertThat(entity!.Authority).IsEqual(newPeerId);

        // The entry was consumed by the lookup — server no longer holds it.
        AssertThat(_monitor.HasPendingReclaimFor(ClientAId))
            .OverrideFailureMessage("reclaim entry must be consumed once the hello reassigns Authority")
            .IsFalse();
    }

    // R-05 ─────────────────────────────────────────────────────────────────────
    // ENet (not the monitor) detects client timeouts and fires PeerDisconnected.
    // A timeout-induced disconnect arrives without a prior
    // DisconnectNotificationMessage, so the monitor applies TimeoutDisconnectMode.
    [TestCase]
    public async Task TimeoutDisconnect_KeepEntityMode_OrphansEntities()
    {
        _monitor.TimeoutDisconnectMode = DisconnectEntityMode.KeepEntity;

        SendClientHello(ClientPeerId, ClientAId);
        SpawnEntityForClient(ClientPeerId);
        await _serverRunner.AwaitIdleFrame();
        int countBefore = EntitySpawner.Instance.Entities.Count;
        AssertThat(countBefore).IsGreaterEqual(1);

        // ENet detects the timeout — no DisconnectNotificationMessage was sent.
        _serverNet.FireClientDisconnected(ClientPeerId);
        await _serverRunner.AwaitIdleFrame();

        int countAfter = EntitySpawner.Instance.Entities.Count;
        AssertThat(countAfter).IsEqual(countBefore);

        bool allOrphaned = EntitySpawner.Instance.Entities.All(e => e.Authority != ClientPeerId);
        AssertThat(allOrphaned).IsTrue();
        AssertThat(_monitor.HasPendingReclaimFor(ClientAId)).IsTrue();
    }

    // R-06 ─────────────────────────────────────────────────────────────────────
    // A hello with an unknown persistent client id MUST NOT silently
    // hijack any other client's entities; it just registers the mapping
    // (subsequent disconnect would park an empty reclaim entry, no harm).
    [TestCase]
    public async Task Hello_UnknownClientId_DoesNotTakeAnyEntity()
    {
        SendClientHello(ClientPeerId, ClientAId);
        SpawnEntityForClient(ClientPeerId);
        await _serverRunner.AwaitIdleFrame();
        int originalAuthority = EntitySpawner.Instance.Entities[0].Authority;

        const int innocentPeerId = 4;
        _serverNet.FireClientConnected(innocentPeerId);
        await _serverRunner.AwaitIdleFrame();
        SendClientHello(innocentPeerId, "never-seen-this-before");
        await _serverRunner.AwaitIdleFrame();

        AssertThat(EntitySpawner.Instance.Entities[0].Authority)
            .OverrideFailureMessage("a hello with a fresh client id must not touch any other entity's Authority")
            .IsEqual(originalAuthority);
    }

    // R-07 ─────────────────────────────────────────────────────────────────────
    // Reusable identity contract: the SAME persistent client id can
    // disconnect/reconnect multiple times in succession, reclaiming on
    // each cycle. The old design (one-shot server-issued token consumed
    // on first reclaim) is explicitly NOT this — the new lookup-keyed
    // design must support repeated reclaim from the same identity.
    [TestCase]
    public async Task Reclaim_LookupIsReusableAcrossManyReconnects()
    {
        _monitor.ManualDisconnectMode = DisconnectEntityMode.KeepEntity;

        SendClientHello(ClientPeerId, ClientAId);
        SpawnEntityForClient(ClientPeerId);
        await _serverRunner.AwaitIdleFrame();
        int entityId = EntitySpawner.Instance.Entities[0].EntityId;

        // Cycle 1: disconnect → reconnect under peer 3 → entity reassigned
        SendDisconnectNotification(ClientPeerId);
        _serverNet.FireClientDisconnected(ClientPeerId);
        await _serverRunner.AwaitIdleFrame();
        const int peer1 = 3;
        _serverNet.FireClientConnected(peer1);
        await _serverRunner.AwaitIdleFrame();
        SendClientHello(peer1, ClientAId);
        await _serverRunner.AwaitIdleFrame();
        AssertThat(EntitySpawner.Instance.Entities.First(e => e.EntityId == entityId).Authority)
            .IsEqual(peer1);

        // Cycle 2: same identity disconnects again and reconnects under
        // yet another peer id — reclaim must succeed a second time.
        SendDisconnectNotification(peer1);
        _serverNet.FireClientDisconnected(peer1);
        await _serverRunner.AwaitIdleFrame();
        const int peer2 = 4;
        _serverNet.FireClientConnected(peer2);
        await _serverRunner.AwaitIdleFrame();
        SendClientHello(peer2, ClientAId);
        await _serverRunner.AwaitIdleFrame();
        AssertThat(EntitySpawner.Instance.Entities.First(e => e.EntityId == entityId).Authority)
            .OverrideFailureMessage("same persistent client id must be able to reclaim across MANY reconnect cycles")
            .IsEqual(peer2);
    }

    // R-08 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task Reclaim_ExpiresAfterConfiguredDuration()
    {
        _monitor.ManualDisconnectMode = DisconnectEntityMode.KeepEntity;
        _monitor.ReclaimExpirySec = 30f;
        var fakeClock = new FakeTimestampProvider();
        _monitor.TimestampProvider = fakeClock;

        SendClientHello(ClientPeerId, ClientAId);
        SpawnEntityForClient(ClientPeerId);
        await _serverRunner.AwaitIdleFrame();
        int entityId = EntitySpawner.Instance.Entities[0].EntityId;

        SendDisconnectNotification(ClientPeerId);
        _serverNet.FireClientDisconnected(ClientPeerId);
        await _serverRunner.AwaitIdleFrame();
        AssertThat(_monitor.HasPendingReclaimFor(ClientAId)).IsTrue();

        // Just before expiry — entry still valid (sweep does nothing).
        fakeClock.AdvanceBy(29_000);
        _server!.EmitSignal(ServerManager.SignalName.ServerNetworkTick, 1);
        await _serverRunner.AwaitIdleFrame();

        bool entityStillExists = EntitySpawner.Instance.Entities.Any(e => e.EntityId == entityId);
        AssertThat(entityStillExists)
            .OverrideFailureMessage("entity destroyed before expiry").IsTrue();

        // Past expiry — sweep should drop the entry and destroy the orphaned entity.
        fakeClock.AdvanceBy(2_000); // total 31s, past the 30s threshold
        _server.EmitSignal(ServerManager.SignalName.ServerNetworkTick, 2);
        await _serverRunner.AwaitIdleFrame();

        AssertThat(_monitor.HasPendingReclaimFor(ClientAId))
            .OverrideFailureMessage("reclaim entry should have expired and been removed").IsFalse();
        bool entityRemoved = !EntitySpawner.Instance.Entities.Any(e => e.EntityId == entityId);
        AssertThat(entityRemoved)
            .OverrideFailureMessage("orphaned entity should have been destroyed by expiry sweep").IsTrue();
    }

    // R-09 ─────────────────────────────────────────────────────────────────────
    // Verifies that a reconnecting client receives entities spawned during its outage
    // via the standard OnClientConnected -> SyncWorldState path, not just its own
    // reclaimed entities. Regression guard against any future change that would
    // skip SyncWorldState when a reclaim is anticipated.
    [TestCase]
    public async Task Reclaim_PostReconnectResyncIncludesEntitiesSpawnedDuringOutage()
    {
        _monitor.ManualDisconnectMode = DisconnectEntityMode.KeepEntity;

        SendClientHello(ClientPeerId, ClientAId);
        SpawnEntityForClient(ClientPeerId);
        await _serverRunner.AwaitIdleFrame();
        int aEntityId = EntitySpawner.Instance.Entities[0].EntityId;

        // Client A disconnects with KeepEntity → entity orphaned, entry parked under ClientAId.
        SendDisconnectNotification(ClientPeerId);
        _serverNet.FireClientDisconnected(ClientPeerId);
        await _serverRunner.AwaitIdleFrame();

        // While A is gone, the server spawns another entity (e.g. a ball, a global prop)
        ServerManager.Instance.SpawnEntity<Godot.Node3D>(entityType: 1, authority: 0);
        await _serverRunner.AwaitIdleFrame();
        int newEntityId = EntitySpawner.Instance.Entities.Last().EntityId;
        AssertThat(newEntityId).IsNotEqual(aEntityId);

        // Client A reconnects under a new peer id
        const int newPeerId = 3;
        int packetCountBefore = _serverNet.SentPackets.Count;
        _serverNet.FireClientConnected(newPeerId);
        await _serverRunner.AwaitIdleFrame();

        // SyncWorldState must have sent Created events for BOTH entities to peer 3:
        //   - the orphaned entity (will later transition via reclaim)
        //   - the new entity that did not exist when A originally connected
        bool sawNewEntityCreated = false;
        bool sawOrphanedEntityCreated = false;
        for (int i = packetCountBefore; i < _serverNet.SentPackets.Count; i++)
        {
            var (data, id, _, _) = _serverNet.SentPackets[i];
            if (id != newPeerId) continue;
            try
            {
                if (MessageSerializer.Deserialize(data) is EntityEventMessage ev
                    && ev.Event == EntityEventEnum.Created)
                {
                    if (ev.EntityId == newEntityId) sawNewEntityCreated = true;
                    if (ev.EntityId == aEntityId) sawOrphanedEntityCreated = true;
                }
            }
            catch { }
        }
        AssertThat(sawNewEntityCreated)
            .OverrideFailureMessage("reconnecting client did not receive Created event for entity spawned during outage").IsTrue();
        AssertThat(sawOrphanedEntityCreated)
            .OverrideFailureMessage("reconnecting client did not receive Created event for its orphaned entity").IsTrue();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private void SendClientHello(int peerId, string persistentClientId)
    {
        var msg = new ClientHelloMessage { ClientId = persistentClientId };
        _serverNet.SimulateIncomingPacket(peerId, MessageSerializer.Serialize(msg));
    }

    private void SpawnEntityForClient(int clientId)
    {
        var req = new EntityRequestMessage { EntityType = 1 };
        _serverNet.SimulateIncomingPacket(clientId, MessageSerializer.Serialize(req));
    }

    private void SendDisconnectNotification(int clientId)
    {
        var msg = new DisconnectNotificationMessage();
        _serverNet.SimulateIncomingPacket(clientId, MessageSerializer.Serialize(msg));
    }

    private void ClearSpawnedEntities()
    {
        var spawner = EntitySpawner.Instance;
        if (spawner == null) return;
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
    }
}
