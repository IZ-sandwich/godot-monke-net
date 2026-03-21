using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace MonkeNet.Client;

public interface IClientEntity : INetworkedEntity
{
    public abstract void OnProcessTick(int tick, IPackableElement input);
}
