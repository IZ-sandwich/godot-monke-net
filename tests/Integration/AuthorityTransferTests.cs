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
/// A-01..A-09: ChangeAuthority — runtime ownership transfer for entities (vehicle entry/exit,
/// item pickup, etc.) using the unified-prediction model. The server mutates the entity's
/// Authority field in place and broadcasts <see cref="AuthorityChangedMessage"/>; every
/// client mutates its local entity.Authority — the same scene instance keeps simulating.
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
        ClientEntityManager.ClearAwaitingReconnect();
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
    // ChangeAuthority broadcasts AuthorityChangedMessage to all peers. The server
    // mutates entity.Authority in place; no Destroy+Create pair is emitted under
    // the unified-prediction model.
    [TestCase]
    public async Task ChangeAuthority_BroadcastsAuthorityChangedMessage()
    {
        const int OtherPeerId = 3;
        _serverNet.FireClientConnected(OtherPeerId);
        await _serverRunner.AwaitIdleFrame();

        // EntityType=1 is the Ball (a still-existing entity type after the
        // CharacterPlayer demo was removed).
        ServerManager.Instance.SpawnEntity<Godot.Node3D>(entityType: 1, authority: ClientPeerId);
        await _serverRunner.AwaitIdleFrame();
        int entityId = EntitySpawner.Instance.Entities[0].EntityId;

        int packetCountBefore = _serverNet.SentPackets.Count;
        ServerManager.Instance.ChangeAuthority(entityId, OtherPeerId);
        await _serverRunner.AwaitIdleFrame();

        // Broadcast carries new authority — at least one packet to each peer.
        var oldOwnerAuth = ExtractAuthorityChangesFor(entityId, ClientPeerId, packetCountBefore);
        var newOwnerAuth = ExtractAuthorityChangesFor(entityId, OtherPeerId, packetCountBefore);
        AssertThat(oldOwnerAuth.Count).IsGreaterEqual(1);
        AssertThat(oldOwnerAuth[0].NewAuthority).IsEqual(OtherPeerId);
        AssertThat(newOwnerAuth.Count).IsGreaterEqual(1);
        AssertThat(newOwnerAuth[0].NewAuthority).IsEqual(OtherPeerId);

        // Server's authoritative Authority field is updated in place.
        var serverEntity = EntitySpawner.Instance.Entities.First(e => e.EntityId == entityId);
        AssertThat(serverEntity.Authority).IsEqual(OtherPeerId);

        // No Destroy+Create entity-event traffic was emitted for the swap.
        var oldOwnerEntityEvents = ExtractEntityEventsFor(entityId, ClientPeerId, packetCountBefore);
        AssertThat(oldOwnerEntityEvents.Count)
            .OverrideFailureMessage("Authority transfer must not emit Destroy/Create events")
            .IsEqual(0);
    }

    // A-02 ─────────────────────────────────────────────────────────────────────
    // Server reclaim (newAuthority = 0): broadcasts AuthorityChangedMessage with
    // NewAuthority=0 to all peers (including the previous owner).
    [TestCase]
    public async Task ChangeAuthority_ServerReclaim_BroadcastsNewAuthorityZero()
    {
        ServerManager.Instance.SpawnEntity<Godot.Node3D>(entityType: 1, authority: ClientPeerId);
        await _serverRunner.AwaitIdleFrame();
        int entityId = EntitySpawner.Instance.Entities[0].EntityId;

        int packetCountBefore = _serverNet.SentPackets.Count;
        ServerManager.Instance.ChangeAuthority(entityId, 0);
        await _serverRunner.AwaitIdleFrame();

        var auth = ExtractAuthorityChangesFor(entityId, ClientPeerId, packetCountBefore);
        AssertThat(auth.Count).IsGreaterEqual(1);
        AssertThat(auth[0].NewAuthority).IsEqual(0);

        var serverEntity = EntitySpawner.Instance.Entities.First(e => e.EntityId == entityId);
        AssertThat(serverEntity.Authority).IsEqual(0);
    }

    // A-03 ─────────────────────────────────────────────────────────────────────
    // Calling ChangeAuthority with the same authority is a no-op — no broadcast,
    // no entity events.
    [TestCase]
    public async Task ChangeAuthority_SameAuthority_IsNoOp()
    {
        ServerManager.Instance.SpawnEntity<Godot.Node3D>(entityType: 1, authority: ClientPeerId);
        await _serverRunner.AwaitIdleFrame();
        int entityId = EntitySpawner.Instance.Entities[0].EntityId;

        int packetCountBefore = _serverNet.SentPackets.Count;
        ServerManager.Instance.ChangeAuthority(entityId, ClientPeerId);
        await _serverRunner.AwaitIdleFrame();

        var auth = ExtractAuthorityChangesFor(entityId, ClientPeerId, packetCountBefore);
        AssertThat(auth.Count).IsEqual(0);
        var events = ExtractEntityEventsFor(entityId, ClientPeerId, packetCountBefore);
        AssertThat(events.Count).IsEqual(0);
    }

    // A-04 ─────────────────────────────────────────────────────────────────────
    // Client receiving authority mutates the same entity instance in place. No
    // destroy/respawn — the rigid body, prediction history, and visual smoother
    // all keep going. Only the Authority field changes.
    [TestCase]
    public async Task ChangeAuthority_ClientGainingAuthority_KeepsSameInstance()
    {
        ServerManager.Instance.SpawnEntity<Godot.Node3D>(entityType: 1, authority: 0);
        await _serverRunner.AwaitIdleFrame();
        await _clientRunner.AwaitIdleFrame();
        int entityId = EntitySpawner.Instance.Entities[0].EntityId;

        var beforeClientEntity = EntitySpawner.Instance.ClientEntities
            .FirstOrDefault(e => e.EntityId == entityId);
        AssertThat(beforeClientEntity).IsNotNull();
        AssertThat(beforeClientEntity!.Authority).IsEqual(0);
        ulong beforeId = beforeClientEntity.GetInstanceId();

        ServerManager.Instance.ChangeAuthority(entityId, ClientPeerId);
        await _serverRunner.AwaitIdleFrame();
        await _clientRunner.AwaitIdleFrame();

        var afterClientEntity = EntitySpawner.Instance.ClientEntities
            .FirstOrDefault(e => e.EntityId == entityId);
        AssertThat(afterClientEntity).IsNotNull();
        AssertThat(afterClientEntity!.Authority).IsEqual(ClientPeerId);
        AssertThat(afterClientEntity.GetInstanceId())
            .OverrideFailureMessage("Client entity instance must be preserved across authority change (no scene swap)")
            .IsEqual(beforeId);
    }

    // A-05 ─────────────────────────────────────────────────────────────────────
    // Under the unified-prediction model the entity stays in _predictedStates
    // regardless of who owns it — every client predicts every entity. Authority
    // transfer only changes input routing; the prediction-history entries remain.
    [TestCase]
    public async Task ChangeAuthority_ClientLosingAuthority_KeepsPredictedStateForEntity()
    {
        ServerManager.Instance.SpawnEntity<Godot.Node3D>(entityType: 1, authority: ClientPeerId);
        await _serverRunner.AwaitIdleFrame();
        await _clientRunner.AwaitIdleFrame();
        int entityId = EntitySpawner.Instance.Entities[0].EntityId;

        var predictionManager = _client!.GetNode<ClientPredictionManager>("PredictionManager");
        await _clientRunner.AwaitIdleFrame();
        await _clientRunner.AwaitIdleFrame();
        AssertThat(PredictedStateContainsEntity(predictionManager, entityId))
            .OverrideFailureMessage("Predicted entity should be tracked before authority change")
            .IsTrue();

        ServerManager.Instance.ChangeAuthority(entityId, 0);
        await _serverRunner.AwaitIdleFrame();
        await _clientRunner.AwaitIdleFrame();
        await _clientRunner.AwaitIdleFrame();

        AssertThat(PredictedStateContainsEntity(predictionManager, entityId))
            .OverrideFailureMessage("Entity must remain in prediction history after authority transfer — every client predicts every entity")
            .IsTrue();
    }

    // A-06 ─────────────────────────────────────────────────────────────────────
    // Default ownership policy rejects: with no OwnershipPolicy set on the entity's
    // EntitySpawnConfiguration, the request is rejected before the custom approver
    // is even consulted. This is the safety default — opt-in per entity type.
    [TestCase]
    public async Task RequestAuthority_DefaultPolicyRejects()
    {
        // Ball spawn config has no OwnershipPolicy in MainScene.tscn — that's the
        // "default" path under test here.
        ServerManager.Instance.SpawnEntity<Godot.Node3D>(entityType: 1, authority: 0);
        await _serverRunner.AwaitIdleFrame();
        int entityId = EntitySpawner.Instance.Entities[0].EntityId;

        var serverEntityManager = _server.GetNode<ServerEntityManager>("ServerEntityManager");
        AssertThat(serverEntityManager.OwnershipApprover).IsNull();

        int packetCountBefore = _serverNet.SentPackets.Count;
        _serverNet.SimulateIncomingPacket(ClientPeerId,
            MessageSerializer.Serialize(new OwnershipChangeRequestMessage { EntityId = entityId }));
        await _serverRunner.AwaitIdleFrame();

        var serverEntity = EntitySpawner.Instance.Entities.First(e => e.EntityId == entityId);
        AssertThat(serverEntity.Authority).IsEqual(0);

        var rejections = ExtractRejectionsFor(entityId, ClientPeerId, packetCountBefore);
        AssertThat(rejections.Count).IsEqual(1);
    }

    // A-07 ─────────────────────────────────────────────────────────────────────
    // With an OwnershipPolicy attached and the custom approver returning true,
    // ChangeAuthority runs and broadcasts AuthorityChangedMessage.
    [TestCase]
    public async Task RequestAuthority_ApprovedByValidator_TransfersOwnership()
    {
        // Vehicle (EntityType=2) has an OwnershipPolicy in MainScene.tscn:
        // RequireUnowned=true, MaxRequesterDistance=4.0. With no requester-owned
        // entities the proximity check would fail, so we disable it for the unit
        // test by overriding the policy in code.
        AttachPermissivePolicyForEntityType(2);

        ServerManager.Instance.SpawnEntity<Godot.Node3D>(entityType: 2, authority: 0);
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

        var auth = ExtractAuthorityChangesFor(entityId, ClientPeerId, packetCountBefore);
        AssertThat(auth.Count).IsGreaterEqual(1);
        AssertThat(auth[0].NewAuthority).IsEqual(ClientPeerId);

        var rejections = ExtractRejectionsFor(entityId, ClientPeerId, packetCountBefore);
        AssertThat(rejections.Count).IsEqual(0);
    }

    // A-08 ─────────────────────────────────────────────────────────────────────
    // OwnershipPolicy passes, custom approver returns false: authority unchanged,
    // single rejection sent back.
    [TestCase]
    public async Task RequestAuthority_RejectedByValidator_NoChange()
    {
        AttachPermissivePolicyForEntityType(2);
        ServerManager.Instance.SpawnEntity<Godot.Node3D>(entityType: 2, authority: 0);
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

        var auth = ExtractAuthorityChangesFor(entityId, ClientPeerId, packetCountBefore);
        AssertThat(auth.Count).IsEqual(0);
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

    // A-10..A-13 (anticipated-flip tests) deleted: the unified-prediction refactor
    // removed the speculative local flip path. Coverage of "request → approve" lives
    // in A-06/A-09; "request → reject" lives in A-08.

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

    private List<AuthorityChangedMessage> ExtractAuthorityChangesFor(int entityId, int peerId, int sentPacketsStartIndex)
    {
        var result = new List<AuthorityChangedMessage>();
        for (int i = sentPacketsStartIndex; i < _serverNet.SentPackets.Count; i++)
        {
            var (data, id, _, _) = _serverNet.SentPackets[i];
            // AuthorityChangedMessage is sent via broadcast (peerId 0), so accept both.
            if (id != peerId && id != 0) continue;
            try
            {
                if (MessageSerializer.Deserialize(data) is AuthorityChangedMessage msg && msg.EntityId == entityId)
                    result.Add(msg);
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

    // Test helper: attach a permissive OwnershipPolicy (no proximity, allow when free)
    // to the given entity type's spawn config. Used by request tests that need to drive
    // the policy-evaluation path without setting up player entities for the proximity
    // check.
    private static void AttachPermissivePolicyForEntityType(byte entityType)
    {
        var config = MonkeNetConfig.Instance.GetSpawnConfigurationForEntityType(entityType);
        config.OwnershipPolicy = new OwnershipPolicy
        {
            RequireUnowned = true,
            MaxRequesterDistance = -1f,
            AllowOwnerRelease = true,
        };
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
