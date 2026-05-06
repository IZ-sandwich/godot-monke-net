using Godot;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Shared;
using System.Collections.Generic;

namespace MonkeNet.Client;

[GlobalClass]
public partial class ClientEntityManager : InternalClientComponent
{
    /// <summary>
    /// How long an anticipated authority request can stay outstanding before the client
    /// auto-reverts the provisional flip. Picked large enough to absorb a normal RTT
    /// (≤200ms typical) plus jitter, but short enough that a dropped response doesn't
    /// leave the client driving an entity the server doesn't think they own.
    /// </summary>
    [Export] public float AnticipationTimeoutSec { get; set; } = 2f;

    private EntitySpawner _entitySpawner;
    private string _sessionToken = null;  // token for the current connection (saved for next reclaim)

    // Step 3b: outstanding anticipated authority flips. Key = entityId; value contains
    // the pre-flip pose so we can revert cleanly if the server rejects or never replies.
    private struct ProvisionalRequest
    {
        public int EntityId;
        public byte EntityType;
        public int OriginalAuthority;
        public Vector3 PrePos;
        public float PreYaw;
        public ulong RequestTimeMsec;
    }
    private readonly Dictionary<int, ProvisionalRequest> _provisional = new();
    public int ProvisionalCount => _provisional.Count;

    // Static so the reclaim token survives ClientManager teardown — MonkeNetManager.CreateClient
    // disposes the previous ClientManager (and this component) before instantiating a fresh one,
    // and OnConnectionLost reloads the scene. An instance field would be lost in either case.
    // Cleared after the reclaim message is sent on the next NetworkReady.
    private static string _persistentReclaimToken = null;

    /// <summary>True if a session-token from a prior connection is awaiting reclaim.</summary>
    public static bool HasSavedReclaimToken => _persistentReclaimToken != null;

    /// <summary>Discards any saved reclaim token. Used by tests for isolation and by UI flows
    /// that want to opt out of reclaim (e.g. "start fresh" instead of "rejoin as same player").</summary>
    public static void ClearSavedReclaimToken() => _persistentReclaimToken = null;

    public override void _EnterTree()
    {
        _entitySpawner = MonkeNetManager.Instance?.EntitySpawner;
    }

    /// <summary>
    /// Requests the server to spawn an entity
    /// </summary>
    /// <param name="entityType"></param>
    public void MakeEntityRequest(byte entityType)
    {
        var req = new EntityRequestMessage
        {
            EntityType = entityType
        };

        SendCommandToServer(req, INetworkManager.PacketModeEnum.Reliable, (int)ChannelEnum.EntityEvent);
    }

    /// <summary>
    /// Anticipated authority request (Step 3b): flips the local entity from dummy to
    /// predicted immediately, then sends the request packet. On server approval the
    /// normal Destroy+Create swap arrives and the provisional state cleans up; on
    /// rejection (<see cref="OwnershipChangeRejectedMessage"/>) or timeout the local
    /// entity reverts to the dummy at the captured pose. Use this when the responsiveness
    /// of "click to drive" / "click to pick up" matters more than waiting one RTT for
    /// confirmation.
    /// </summary>
    public void RequestAuthorityAnticipated(int entityId)
    {
        var entity = _entitySpawner.ClientEntities.Find(e => e.EntityId == entityId);
        if (entity == null)
        {
            // No local representation to flip — fall back to the plain request.
            SendCommandToServer(new OwnershipChangeRequestMessage { EntityId = entityId },
                INetworkManager.PacketModeEnum.Reliable, (int)ChannelEnum.GameReliable);
            return;
        }

        if (_provisional.ContainsKey(entityId))
        {
            // Already mid-request — just resend, don't re-flip.
            SendCommandToServer(new OwnershipChangeRequestMessage { EntityId = entityId },
                INetworkManager.PacketModeEnum.Reliable, (int)ChannelEnum.GameReliable);
            return;
        }

        var root = _entitySpawner.GetEntityRoot(entity);
        Vector3 prePos = root?.GlobalPosition ?? Vector3.Zero;
        float preYaw = root?.GlobalRotation.Y ?? 0f;
        byte entityType = entity.EntityType;
        int originalAuthority = entity.Authority;

        // Tear down the dummy and instantiate the predicted scene at the same pose,
        // claiming ourselves as authority. The prediction system picks it up automatically
        // because EntitySpawner now sees a ClientPredictedEntity in ClientEntities.
        _entitySpawner.DestroyClientEntity(new EntityEventMessage
        {
            Event = EntityEventEnum.Destroyed,
            EntityId = entityId,
            EntityType = entityType,
            Authority = 0,
        });

        _entitySpawner.SpawnEntity(new EntityEventMessage
        {
            Event = EntityEventEnum.Created,
            EntityId = entityId,
            EntityType = entityType,
            Authority = ClientManager.Instance.GetNetworkId(),
            Position = prePos,
            Yaw = preYaw,
        });

        _provisional[entityId] = new ProvisionalRequest
        {
            EntityId = entityId,
            EntityType = entityType,
            OriginalAuthority = originalAuthority,
            PrePos = prePos,
            PreYaw = preYaw,
            RequestTimeMsec = Time.GetTicksMsec(),
        };

        SendCommandToServer(new OwnershipChangeRequestMessage { EntityId = entityId },
            INetworkManager.PacketModeEnum.Reliable, (int)ChannelEnum.GameReliable);
    }

    protected override void OnCommandReceived(IPackableMessage command)
    {
        if (command is SessionTokenMessage tokenMsg)
        {
            _sessionToken = tokenMsg.Token;
            MonkeLogger.CurrentToken = _sessionToken;
            MonkeLogger.Info("ClientEntityManager: received session token");
            return;
        }

        if (command is EntityEventMessage entityEvent)
        {
            switch (entityEvent.Event)
            {
                case EntityEventEnum.Created:
                    _entitySpawner.SpawnEntity(entityEvent);
                    // Approval path: any Created for an entity we provisionally flipped means
                    // the server has spoken. The standard handler already spawned the right
                    // scene (predicted if Authority matches us, dummy otherwise) — we just
                    // need to drop the provisional bookkeeping so the timeout watchdog
                    // doesn't revert a now-confirmed swap.
                    _provisional.Remove(entityEvent.EntityId);
                    break;
                case EntityEventEnum.Destroyed:
                    _entitySpawner.DestroyClientEntity(entityEvent);
                    break;
                default:
                    break;
            }
            return;
        }

        if (command is OwnershipChangeRejectedMessage rejected)
        {
            if (_provisional.TryGetValue(rejected.EntityId, out var prov))
            {
                _provisional.Remove(rejected.EntityId);
                RevertProvisional(prov);
            }
            return;
        }
    }

    private void RevertProvisional(ProvisionalRequest prov)
    {
        // Tear down the predicted entity we speculatively spawned.
        _entitySpawner.DestroyClientEntity(new EntityEventMessage
        {
            Event = EntityEventEnum.Destroyed,
            EntityId = prov.EntityId,
            EntityType = prov.EntityType,
            Authority = 0,
        });

        // Re-spawn the dummy at the captured pre-flip pose. Authority is whatever the
        // entity had before we flipped — usually 0 (server) or another peer; either
        // way it's not us, so SolveWhatEntitySceneToSpawn picks the dummy scene.
        _entitySpawner.SpawnEntity(new EntityEventMessage
        {
            Event = EntityEventEnum.Created,
            EntityId = prov.EntityId,
            EntityType = prov.EntityType,
            Authority = prov.OriginalAuthority,
            Position = prov.PrePos,
            Yaw = prov.PreYaw,
        });

        MonkeLogger.Info($"ClientEntityManager: reverted provisional authority on entity {prov.EntityId}");
    }

    protected override void OnProcessTick(int currentTick, IPackableElement input)
    {
        if (_provisional.Count == 0) return;

        ulong now = Time.GetTicksMsec();
        ulong timeoutMsec = (ulong)(AnticipationTimeoutSec * 1000f);
        // Materialise to avoid mutating the dictionary while enumerating.
        List<ProvisionalRequest> expired = null;
        foreach (var kv in _provisional)
        {
            if (now - kv.Value.RequestTimeMsec >= timeoutMsec)
            {
                (expired ??= new List<ProvisionalRequest>()).Add(kv.Value);
            }
        }
        if (expired == null) return;
        foreach (var prov in expired)
        {
            _provisional.Remove(prov.EntityId);
            MonkeLogger.Warn($"ClientEntityManager: provisional authority on entity {prov.EntityId} timed out, reverting");
            RevertProvisional(prov);
        }
    }

    protected override void OnNetworkReady()
    {
        base.OnNetworkReady();
        if (_persistentReclaimToken == null) return;
        MonkeLogger.Info("ClientEntityManager: sending reclaim token");
        SendCommandToServer(new ReclaimEntityMessage { Token = _persistentReclaimToken },
            INetworkManager.PacketModeEnum.Reliable, (int)ChannelEnum.GameReliable);
        _persistentReclaimToken = null;
    }

    protected override void OnServerDisconnected()
    {
        _persistentReclaimToken = _sessionToken; // save for reclaim on next reconnect
        _sessionToken = null;          // will be replaced when server issues new token on reconnect
        MonkeLogger.CurrentToken = null;
        _provisional.Clear();
        _entitySpawner.ClearClientEntities();
    }
}
