using Godot;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace MonkeNet.Client;

[GlobalClass]
public partial class ClientEntityManager : InternalClientComponent
{
    private EntitySpawner _entitySpawner;

    // Sticky "we got disconnected and haven't reconnected yet" flag, set on
    // OnServerDisconnected and cleared on the next OnNetworkReady. The demo
    // UI (Reconnect button visibility, FirstPersonCameraController's saved
    // yaw/pitch) gates on this so the same client process can tell whether
    // it's in a clean-launch state vs. a "you were just disconnected" state
    // — independently of identity, which is now permanent and always known.
    private static bool _isAwaitingReconnect;

    /// <summary>True between the moment we observe a server disconnect and
    /// the moment our reconnect handshake completes (NetworkReady fires).
    /// Survives ClientManager teardown so the demo's reload-on-disconnect
    /// scene flow can read it back. Cleared by
    /// <see cref="ClearAwaitingReconnect"/> for UI flows that want to opt
    /// out of reclaim ("start fresh as the same identity").</summary>
    public static bool IsAwaitingReconnect => _isAwaitingReconnect;

    /// <summary>Clears the awaiting-reconnect flag. Used by tests for
    /// isolation, and by UI flows that want "I know I was disconnected,
    /// but don't carry pose state forward".</summary>
    public static void ClearAwaitingReconnect() => _isAwaitingReconnect = false;

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

    protected override void OnCommandReceived(IPackableMessage command)
    {
        if (command is EntityEventMessage entityEvent)
        {
            MonkeLogger.Debug($"[NET-ENTITY-RX] event={entityEvent.Event} eid={entityEvent.EntityId} type={entityEvent.EntityType} authority={entityEvent.Authority} pos=({entityEvent.Position.X:F3},{entityEvent.Position.Y:F3},{entityEvent.Position.Z:F3}) yaw={entityEvent.Yaw:F3}");
            switch (entityEvent.Event)
            {
                case EntityEventEnum.Created:
                    _entitySpawner.SpawnEntity(entityEvent);
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
            // No local state to revert under the unified-prediction model — the client
            // never mutates Authority speculatively. Game-side code may listen for this
            // (e.g. flash a "denied" UI hint) by subscribing to ClientManager.CommandReceived.
            MonkeLogger.Debug($"[NET-OWN-REJ] eid={rejected.EntityId}");
            return;
        }

        if (command is AuthorityChangedMessage authChanged)
        {
            // Locate the entity by id; if not found, log and skip (rare race with concurrent
            // destroy). The entity stays in place; only its Authority field changes — input
            // routing reacts to the new value next tick.
            var entity = _entitySpawner.ClientEntities.Find(e => e.EntityId == authChanged.EntityId);
            if (entity == null)
            {
                MonkeLogger.Debug($"[NET-AUTH-RX] eid={authChanged.EntityId} unknown (already destroyed?)");
                return;
            }
            int oldAuth = entity.Authority;
            entity.Authority = authChanged.NewAuthority;
            MonkeLogger.Info($"[NET-AUTH-RX] eid={authChanged.EntityId} authority {oldAuth} -> {authChanged.NewAuthority}");
            return;
        }
    }

    protected override void OnServerDisconnected()
    {
        _isAwaitingReconnect = true;
        _entitySpawner.ClearClientEntities();
    }

    protected override void OnNetworkReady()
    {
        base.OnNetworkReady();

        // Announce our persistent identity to the server. This is the
        // FIRST application-level message we send post-handshake; the
        // server uses it both to identify the peer in logs and to look
        // up any parked reclaim entry left over from a previous session
        // of this same identity. Sending unconditionally on every
        // NetworkReady (fresh connect AND reconnect) means there's no
        // separate "reclaim" code path — same hello, same lookup, every
        // time.
        string id = ClientPersistentIdentity.Get();
        MonkeLogger.CurrentToken = id;
        MonkeLogger.Info($"ClientEntityManager: sending ClientHelloMessage [cid:{ClientPersistentIdentity.Tail(id)}] (source: {ClientPersistentIdentity.SourceDescription})");
        SendCommandToServer(new ClientHelloMessage { ClientId = id },
            INetworkManager.PacketModeEnum.Reliable, (int)ChannelEnum.GameReliable);

        _isAwaitingReconnect = false;
    }
}
