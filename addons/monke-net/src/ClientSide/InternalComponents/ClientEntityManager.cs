using Godot;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace MonkeNet.Client;

[GlobalClass]
public partial class ClientEntityManager : InternalClientComponent
{
    private EntitySpawner _entitySpawner;

    public override void _EnterTree()
    {
        _entitySpawner = MonkeNetManager.Instance.EntitySpawner;
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
            switch (entityEvent.Event)
            {
                case EntityEventEnum.Created:
                    _entitySpawner.SpawnEntity(entityEvent);
                    break;
                case EntityEventEnum.Destroyed:
                    _entitySpawner.DestroyEntity(entityEvent);
                    break;
                default:
                    break;
            }
        }
    }
}
