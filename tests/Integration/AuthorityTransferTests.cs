using System.Collections.Generic;
using System.Linq;
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
/// A-01..A-05: ChangeAuthority — runtime ownership transfer for entities (vehicle entry/exit,
/// item pickup, etc.) without destroy-respawn churn server-side.
///
/// The server mutates the entity's Authority in place and notifies just the old and new
/// owner clients with a Destroy + Create pair so their local representations swap between
/// LocalScene (predicted) and DummyScene (interpolated). Other clients are not notified —
/// their dummy view is correct regardless of which peer owns the entity.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class AuthorityTransferTests
{
    private const int ClientPeerId = 2;

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
        MonkeNetConfig.Instance = null;
        FakeNetworkBridge.Reset();
        ClientEntityManager.ClearSavedReclaimToken();
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

        _server!.Initialize(_serverNet, port: 7700);
        _client!.Initialize(_clientNet, "127.0.0.1", 7700);
        await _serverRunner.AwaitIdleFrame();
        await _clientRunner.AwaitIdleFrame();

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

    // A-01 ─────────────────────────────────────────────────────────────────────
    // ChangeAuthority sends Destroy+Create to BOTH the old and new owner so each
    // can swap its local view (Predicted ↔ Dummy). Other peers are not notified.
    [TestCase]
    public async Task ChangeAuthority_NotifiesBothOldAndNewOwner()
    {
        const int OtherPeerId = 3;
        _serverNet.FireClientConnected(OtherPeerId);
        await _serverRunner.AwaitIdleFrame();

        // Spawn entity owned by ClientPeerId.
        ServerManager.Instance.SpawnEntity<Godot.Node3D>(entityType: 0, authority: ClientPeerId);
        await _serverRunner.AwaitIdleFrame();
        int entityId = EntitySpawner.Instance.Entities[0].EntityId;

        int packetCountBefore = _serverNet.SentPackets.Count;
        ServerManager.Instance.ChangeAuthority(entityId, OtherPeerId);
        await _serverRunner.AwaitIdleFrame();

        var oldOwnerEvents = ExtractEntityEventsFor(entityId, ClientPeerId, packetCountBefore);
        var newOwnerEvents = ExtractEntityEventsFor(entityId, OtherPeerId, packetCountBefore);

        AssertThat(oldOwnerEvents.Count).IsGreaterEqual(2);
        AssertThat(oldOwnerEvents[0].Event).IsEqual(EntityEventEnum.Destroyed);
        AssertThat(oldOwnerEvents[1].Event).IsEqual(EntityEventEnum.Created);
        AssertThat(oldOwnerEvents[1].Authority).IsEqual(OtherPeerId);

        AssertThat(newOwnerEvents.Count).IsGreaterEqual(2);
        AssertThat(newOwnerEvents[0].Event).IsEqual(EntityEventEnum.Destroyed);
        AssertThat(newOwnerEvents[1].Event).IsEqual(EntityEventEnum.Created);
        AssertThat(newOwnerEvents[1].Authority).IsEqual(OtherPeerId);

        // Server's authoritative Authority field is updated.
        var serverEntity = EntitySpawner.Instance.Entities.First(e => e.EntityId == entityId);
        AssertThat(serverEntity.Authority).IsEqual(OtherPeerId);
    }

    // A-02 ─────────────────────────────────────────────────────────────────────
    // Server reclaims ownership (newAuthority = 0): only the previous owner is
    // notified. Server has no client-side view, so no second pair needed.
    [TestCase]
    public async Task ChangeAuthority_ServerReclaim_NotifiesOldOwnerOnly()
    {
        ServerManager.Instance.SpawnEntity<Godot.Node3D>(entityType: 0, authority: ClientPeerId);
        await _serverRunner.AwaitIdleFrame();
        int entityId = EntitySpawner.Instance.Entities[0].EntityId;

        int packetCountBefore = _serverNet.SentPackets.Count;
        ServerManager.Instance.ChangeAuthority(entityId, 0);
        await _serverRunner.AwaitIdleFrame();

        var oldOwnerEvents = ExtractEntityEventsFor(entityId, ClientPeerId, packetCountBefore);
        AssertThat(oldOwnerEvents.Count).IsGreaterEqual(2);
        AssertThat(oldOwnerEvents[0].Event).IsEqual(EntityEventEnum.Destroyed);
        AssertThat(oldOwnerEvents[1].Event).IsEqual(EntityEventEnum.Created);
        AssertThat(oldOwnerEvents[1].Authority).IsEqual(0);

        var serverEntity = EntitySpawner.Instance.Entities.First(e => e.EntityId == entityId);
        AssertThat(serverEntity.Authority).IsEqual(0);
    }

    // A-03 ─────────────────────────────────────────────────────────────────────
    // Calling ChangeAuthority with the same authority is a no-op — no entity
    // events are sent, and the entity's Authority field is unchanged.
    [TestCase]
    public async Task ChangeAuthority_SameAuthority_IsNoOp()
    {
        ServerManager.Instance.SpawnEntity<Godot.Node3D>(entityType: 0, authority: ClientPeerId);
        await _serverRunner.AwaitIdleFrame();
        int entityId = EntitySpawner.Instance.Entities[0].EntityId;

        int packetCountBefore = _serverNet.SentPackets.Count;
        ServerManager.Instance.ChangeAuthority(entityId, ClientPeerId);
        await _serverRunner.AwaitIdleFrame();

        var events = ExtractEntityEventsFor(entityId, ClientPeerId, packetCountBefore);
        AssertThat(events.Count).IsEqual(0);
    }

    // A-04 ─────────────────────────────────────────────────────────────────────
    // Client receiving authority swaps Dummy → Predicted scene. The new entity
    // has a ClientPredictedEntity component (LocalScene); the old one had a
    // ClientInterpolatedEntity (DummyScene).
    [TestCase]
    public async Task ChangeAuthority_ClientGainingAuthority_GetsPredictedScene()
    {
        // Spawn server-owned entity → client receives Dummy via SyncWorldState/Created.
        ServerManager.Instance.SpawnEntity<Godot.Node3D>(entityType: 0, authority: 0);
        await _serverRunner.AwaitIdleFrame();
        await _clientRunner.AwaitIdleFrame();
        int entityId = EntitySpawner.Instance.Entities[0].EntityId;

        var beforeClientEntity = EntitySpawner.Instance.ClientEntities
            .FirstOrDefault(e => e.EntityId == entityId);
        AssertThat(beforeClientEntity).IsNotNull();
        AssertThat(beforeClientEntity!.GetComponent<ClientInterpolatedEntity>())
            .OverrideFailureMessage("Pre-transfer client entity should be the dummy/interpolated scene")
            .IsNotNull();

        // Server transfers ownership to the connected client.
        ServerManager.Instance.ChangeAuthority(entityId, ClientPeerId);
        await _serverRunner.AwaitIdleFrame();
        await _clientRunner.AwaitIdleFrame();

        var afterClientEntity = EntitySpawner.Instance.ClientEntities
            .FirstOrDefault(e => e.EntityId == entityId);
        AssertThat(afterClientEntity).IsNotNull();
        AssertThat(afterClientEntity!.GetComponent<ClientPredictedEntity>())
            .OverrideFailureMessage("Post-transfer client entity should be the local/predicted scene")
            .IsNotNull();
    }

    // A-05 ─────────────────────────────────────────────────────────────────────
    // Client losing authority drops any predicted-state entries that referenced
    // the now-destroyed entity. Without this cleanup, RollbackAndResimulate would
    // iterate a freed Godot object and crash.
    [TestCase]
    public async Task ChangeAuthority_ClientLosingAuthority_DropsPredictedStateForEntity()
    {
        // Client owns the entity, then makes some predictions for it.
        ServerManager.Instance.SpawnEntity<Godot.Node3D>(entityType: 0, authority: ClientPeerId);
        await _serverRunner.AwaitIdleFrame();
        await _clientRunner.AwaitIdleFrame();
        int entityId = EntitySpawner.Instance.Entities[0].EntityId;

        var predictionManager = _client!.GetNode<ClientPredictionManager>("PredictionManager");
        // Drive a couple of physics ticks to populate _predictedStates with this entity.
        await _clientRunner.AwaitIdleFrame();
        await _clientRunner.AwaitIdleFrame();
        AssertThat(PredictedStateContainsEntity(predictionManager, entityId))
            .OverrideFailureMessage("Predicted entity should be tracked before authority change")
            .IsTrue();

        // Server reclaims authority — client gets Destroy+Create, the local entity is
        // freed, and OnEntityDestroyed strips the now-stale prediction entries.
        ServerManager.Instance.ChangeAuthority(entityId, 0);
        await _serverRunner.AwaitIdleFrame();
        await _clientRunner.AwaitIdleFrame();

        AssertThat(PredictedStateContainsEntity(predictionManager, entityId))
            .OverrideFailureMessage("Predicted state should not reference entity after authority transfer away from client")
            .IsFalse();
    }

    // A-06 ─────────────────────────────────────────────────────────────────────
    // Default ownership policy rejects: a client request with no approver registered
    // must NOT change authority and must produce an OwnershipChangeRejectedMessage
    // back to the requester. This is the safety default — an unconfigured server
    // doesn't let any peer claim any entity.
    [TestCase]
    public async Task RequestAuthority_DefaultPolicyRejects()
    {
        ServerManager.Instance.SpawnEntity<Godot.Node3D>(entityType: 0, authority: 0);
        await _serverRunner.AwaitIdleFrame();
        int entityId = EntitySpawner.Instance.Entities[0].EntityId;

        var serverEntityManager = _server.GetNode<ServerEntityManager>("ServerEntityManager");
        AssertThat(serverEntityManager.OwnershipApprover).IsNull();

        int packetCountBefore = _serverNet.SentPackets.Count;
        _serverNet.SimulateIncomingPacket(ClientPeerId,
            MessageSerializer.Serialize(new OwnershipChangeRequestMessage { EntityId = entityId }));
        await _serverRunner.AwaitIdleFrame();

        // No authority change.
        var serverEntity = EntitySpawner.Instance.Entities.First(e => e.EntityId == entityId);
        AssertThat(serverEntity.Authority).IsEqual(0);

        // Rejection sent back to the requester.
        var rejections = ExtractRejectionsFor(entityId, ClientPeerId, packetCountBefore);
        AssertThat(rejections.Count).IsEqual(1);
    }

    // A-07 ─────────────────────────────────────────────────────────────────────
    // When the approver returns true, ChangeAuthority runs and the client receives
    // the standard Destroy+Create swap (no separate "approved" message).
    [TestCase]
    public async Task RequestAuthority_ApprovedByValidator_TransfersOwnership()
    {
        ServerManager.Instance.SpawnEntity<Godot.Node3D>(entityType: 0, authority: 0);
        await _serverRunner.AwaitIdleFrame();
        int entityId = EntitySpawner.Instance.Entities[0].EntityId;

        var serverEntityManager = _server.GetNode<ServerEntityManager>("ServerEntityManager");
        int approverCalls = 0;
        int seenRequester = -1, seenEntityId = -1;
        serverEntityManager.OwnershipApprover = (req, eid) =>
        {
            approverCalls++;
            seenRequester = req;
            seenEntityId = eid;
            return true;
        };

        int packetCountBefore = _serverNet.SentPackets.Count;
        _serverNet.SimulateIncomingPacket(ClientPeerId,
            MessageSerializer.Serialize(new OwnershipChangeRequestMessage { EntityId = entityId }));
        await _serverRunner.AwaitIdleFrame();

        AssertThat(approverCalls).IsEqual(1);
        AssertThat(seenRequester).IsEqual(ClientPeerId);
        AssertThat(seenEntityId).IsEqual(entityId);

        var serverEntity = EntitySpawner.Instance.Entities.First(e => e.EntityId == entityId);
        AssertThat(serverEntity.Authority).IsEqual(ClientPeerId);

        // Client received Destroy+Create as the standard authority-swap path.
        var ev = ExtractEntityEventsFor(entityId, ClientPeerId, packetCountBefore);
        AssertThat(ev.Count).IsGreaterEqual(2);
        AssertThat(ev[0].Event).IsEqual(EntityEventEnum.Destroyed);
        AssertThat(ev[1].Event).IsEqual(EntityEventEnum.Created);
        AssertThat(ev[1].Authority).IsEqual(ClientPeerId);

        // No rejection on approve.
        var rejections = ExtractRejectionsFor(entityId, ClientPeerId, packetCountBefore);
        AssertThat(rejections.Count).IsEqual(0);
    }

    // A-08 ─────────────────────────────────────────────────────────────────────
    // Approver returns false: authority unchanged, single rejection sent back.
    [TestCase]
    public async Task RequestAuthority_RejectedByValidator_NoChange()
    {
        ServerManager.Instance.SpawnEntity<Godot.Node3D>(entityType: 0, authority: 0);
        await _serverRunner.AwaitIdleFrame();
        int entityId = EntitySpawner.Instance.Entities[0].EntityId;

        var serverEntityManager = _server.GetNode<ServerEntityManager>("ServerEntityManager");
        serverEntityManager.OwnershipApprover = (_, _) => false;

        int packetCountBefore = _serverNet.SentPackets.Count;
        _serverNet.SimulateIncomingPacket(ClientPeerId,
            MessageSerializer.Serialize(new OwnershipChangeRequestMessage { EntityId = entityId }));
        await _serverRunner.AwaitIdleFrame();

        var serverEntity = EntitySpawner.Instance.Entities.First(e => e.EntityId == entityId);
        AssertThat(serverEntity.Authority).IsEqual(0);

        var rejections = ExtractRejectionsFor(entityId, ClientPeerId, packetCountBefore);
        AssertThat(rejections.Count).IsEqual(1);

        // No Destroy+Create entity-swap traffic.
        var ev = ExtractEntityEventsFor(entityId, ClientPeerId, packetCountBefore);
        AssertThat(ev.Count).IsEqual(0);
    }

    // A-09 ─────────────────────────────────────────────────────────────────────
    // Request for an entity that doesn't exist (e.g. just destroyed) gets a
    // rejection rather than a server crash.
    [TestCase]
    public async Task RequestAuthority_UnknownEntity_RejectedSilently()
    {
        var serverEntityManager = _server.GetNode<ServerEntityManager>("ServerEntityManager");
        serverEntityManager.OwnershipApprover = (_, _) => true; // would approve if entity existed

        int packetCountBefore = _serverNet.SentPackets.Count;
        _serverNet.SimulateIncomingPacket(ClientPeerId,
            MessageSerializer.Serialize(new OwnershipChangeRequestMessage { EntityId = 9999 }));
        await _serverRunner.AwaitIdleFrame();

        var rejections = ExtractRejectionsFor(9999, ClientPeerId, packetCountBefore);
        AssertThat(rejections.Count).IsEqual(1);
    }

    // A-10 ─────────────────────────────────────────────────────────────────────
    // RequestAuthorityAnticipated: the local client entity flips from dummy to
    // predicted immediately. We break the bridge before sending so the synchronous
    // fake network can't deliver a rejection back inside the same call (real ENet is
    // never that fast). This way the assertion observes the post-flip pre-response state.
    [TestCase]
    public async Task AnticipatedRequest_LocalEntityFlipsImmediately()
    {
        ServerManager.Instance.SpawnEntity<Godot.Node3D>(entityType: 0, authority: 0);
        await _serverRunner.AwaitIdleFrame();
        await _clientRunner.AwaitIdleFrame();
        int entityId = EntitySpawner.Instance.Entities[0].EntityId;

        var beforeEntity = EntitySpawner.Instance.ClientEntities.FirstOrDefault(e => e.EntityId == entityId);
        AssertThat(beforeEntity).IsNotNull();
        AssertThat(beforeEntity!.GetComponent<ClientInterpolatedEntity>())
            .OverrideFailureMessage("Pre-flip should be dummy")
            .IsNotNull();

        // Disconnect the fake bridge so the request packet doesn't reach the server.
        // The local provisional flip still runs, and no rejection comes back to undo it.
        _clientNet.SetPeer(null);

        _client.RequestAuthorityAnticipated(entityId);

        var afterEntity = EntitySpawner.Instance.ClientEntities.FirstOrDefault(e => e.EntityId == entityId);
        AssertThat(afterEntity).IsNotNull();
        AssertThat(afterEntity!.GetComponent<ClientPredictedEntity>())
            .OverrideFailureMessage("Post-flip should be predicted (anticipated)")
            .IsNotNull();

        var clientEntityManager = _client.GetNode<ClientEntityManager>("ClientEntityManager");
        AssertThat(clientEntityManager.ProvisionalCount).IsEqual(1);
    }

    // A-11 ─────────────────────────────────────────────────────────────────────
    // Receiving OwnershipChangeRejectedMessage reverts the local entity to the dummy.
    [TestCase]
    public async Task AnticipatedRequest_RejectedByServer_RevertsToDummy()
    {
        ServerManager.Instance.SpawnEntity<Godot.Node3D>(entityType: 0, authority: 0);
        await _serverRunner.AwaitIdleFrame();
        await _clientRunner.AwaitIdleFrame();
        int entityId = EntitySpawner.Instance.Entities[0].EntityId;

        _client.RequestAuthorityAnticipated(entityId);
        await _clientRunner.AwaitIdleFrame();

        // Inject the rejection directly into the client (bypasses the server-side
        // approval flow so this test is independent of ServerEntityManager wiring).
        _clientNet.SimulateIncomingPacket(1, MessageSerializer.Serialize(
            new OwnershipChangeRejectedMessage { EntityId = entityId }));
        await _clientRunner.AwaitIdleFrame();

        var afterEntity = EntitySpawner.Instance.ClientEntities.FirstOrDefault(e => e.EntityId == entityId);
        AssertThat(afterEntity).IsNotNull();
        AssertThat(afterEntity!.GetComponent<ClientInterpolatedEntity>())
            .OverrideFailureMessage("After rejection should be back to dummy")
            .IsNotNull();
        AssertThat(afterEntity.GetComponent<ClientPredictedEntity>())
            .OverrideFailureMessage("Predicted scene should be gone")
            .IsNull();

        var clientEntityManager = _client.GetNode<ClientEntityManager>("ClientEntityManager");
        AssertThat(clientEntityManager.ProvisionalCount).IsEqual(0);
    }

    // A-12 ─────────────────────────────────────────────────────────────────────
    // No server response within the timeout window — client auto-reverts.
    [TestCase]
    public async Task AnticipatedRequest_TimeoutWithoutResponse_AutoReverts()
    {
        ServerManager.Instance.SpawnEntity<Godot.Node3D>(entityType: 0, authority: 0);
        await _serverRunner.AwaitIdleFrame();
        await _clientRunner.AwaitIdleFrame();
        int entityId = EntitySpawner.Instance.Entities[0].EntityId;

        var clientEntityManager = _client.GetNode<ClientEntityManager>("ClientEntityManager");
        clientEntityManager.AnticipationTimeoutSec = 0.05f; // tiny, so a few frames suffice

        // Break the bridge so the request packet can't reach the server and no
        // rejection can come back. The flip stays until the timeout elapses.
        _clientNet.SetPeer(null);

        _client.RequestAuthorityAnticipated(entityId);
        AssertThat(clientEntityManager.ProvisionalCount).IsEqual(1);

        // Drive enough idle frames to cross the timeout. ClientTick (which calls
        // OnProcessTick) fires from _PhysicsProcess, but AwaitIdleFrame interleaves
        // physics + idle so 30 frames is more than enough to cover 50ms.
        for (int i = 0; i < 30; i++) await _clientRunner.AwaitIdleFrame();

        AssertThat(clientEntityManager.ProvisionalCount).IsEqual(0);

        var afterEntity = EntitySpawner.Instance.ClientEntities.FirstOrDefault(e => e.EntityId == entityId);
        AssertThat(afterEntity).IsNotNull();
        AssertThat(afterEntity!.GetComponent<ClientInterpolatedEntity>())
            .OverrideFailureMessage("After timeout should be back to dummy")
            .IsNotNull();
    }

    // A-13 ─────────────────────────────────────────────────────────────────────
    // Approval path: the standard Destroy+Create swap arrives, the predicted entity
    // stays predicted, and the provisional dict is cleared so timeout doesn't fire.
    [TestCase]
    public async Task AnticipatedRequest_ApprovedByServer_ProvisionalCleanedUp()
    {
        ServerManager.Instance.SpawnEntity<Godot.Node3D>(entityType: 0, authority: 0);
        await _serverRunner.AwaitIdleFrame();
        await _clientRunner.AwaitIdleFrame();
        int entityId = EntitySpawner.Instance.Entities[0].EntityId;

        var clientEntityManager = _client.GetNode<ClientEntityManager>("ClientEntityManager");
        var serverEntityManager = _server.GetNode<ServerEntityManager>("ServerEntityManager");
        serverEntityManager.OwnershipApprover = (_, _) => true;

        _client.RequestAuthorityAnticipated(entityId);
        await _clientRunner.AwaitIdleFrame();
        await _serverRunner.AwaitIdleFrame();
        await _clientRunner.AwaitIdleFrame();

        // After the server's Destroy+Create flows back, provisional should be cleared.
        AssertThat(clientEntityManager.ProvisionalCount).IsEqual(0);

        var afterEntity = EntitySpawner.Instance.ClientEntities.FirstOrDefault(e => e.EntityId == entityId);
        AssertThat(afterEntity).IsNotNull();
        AssertThat(afterEntity!.GetComponent<ClientPredictedEntity>())
            .OverrideFailureMessage("After server approval should still be predicted")
            .IsNotNull();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private List<OwnershipChangeRejectedMessage> ExtractRejectionsFor(int entityId, int peerId, int sentPacketsStartIndex)
    {
        var result = new List<OwnershipChangeRejectedMessage>();
        for (int i = sentPacketsStartIndex; i < _serverNet.SentPackets.Count; i++)
        {
            var (data, id, _, _) = _serverNet.SentPackets[i];
            if (id != peerId) continue;
            try
            {
                if (MessageSerializer.Deserialize(data) is OwnershipChangeRejectedMessage rej && rej.EntityId == entityId)
                    result.Add(rej);
            }
            catch { }
        }
        return result;
    }

    private List<EntityEventMessage> ExtractEntityEventsFor(int entityId, int peerId, int sentPacketsStartIndex)
    {
        var result = new List<EntityEventMessage>();
        for (int i = sentPacketsStartIndex; i < _serverNet.SentPackets.Count; i++)
        {
            var (data, id, _, _) = _serverNet.SentPackets[i];
            if (id != peerId) continue;
            try
            {
                if (MessageSerializer.Deserialize(data) is EntityEventMessage ev && ev.EntityId == entityId)
                    result.Add(ev);
            }
            catch { }
        }
        return result;
    }

    private static bool PredictedStateContainsEntity(ClientPredictionManager pm, int entityId)
    {
        var field = typeof(ClientPredictionManager)
            .GetField("_predictedStates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var list = field?.GetValue(pm) as System.Collections.IList;
        if (list == null) return false;

        var stateType = typeof(ClientPredictionManager).GetNestedType("PredictedState",
            System.Reflection.BindingFlags.NonPublic);
        var entitiesField = stateType?.GetField("Entities");
        foreach (var item in list)
        {
            if (entitiesField?.GetValue(item) is System.Collections.IDictionary dict)
            {
                foreach (var key in dict.Keys)
                {
                    if (key is ClientPredictedEntity cpe
                        && Godot.GodotObject.IsInstanceValid(cpe)
                        && cpe.EntityId == entityId)
                        return true;
                }
            }
        }
        return false;
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
