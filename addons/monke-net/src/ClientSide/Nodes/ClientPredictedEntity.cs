using Godot;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace MonkeNet.Client;

[GlobalClass, Icon("res://addons/monke-net/resources/circle_nodes_solid.png")]
public partial class ClientPredictedEntity : ClientNetworkBehaviour
{
    public virtual void OnProcessTick(int tick, IPackableElement input) { }
    public virtual bool HasMisspredicted(int tick, IEntityStateData receivedState, Vector3 savedState) { return false; }
    public virtual void HandleReconciliation(IEntityStateData receivedState) { }
    public virtual void ResimulateTick(IPackableElement input) { }
    public virtual Vector3 GetPosition() { return Vector3.Zero; }

    /// <summary>
    /// Optional silent-correction hook. Called every snapshot when <see cref="HasMisspredicted"/>
    /// returns false — i.e. divergence is below the hard reconcile threshold but may still be
    /// non-zero from accumulated physics nondeterminism (Jolt collision response, friction
    /// caches, etc.). Pulls the body gently toward authoritative without triggering rollback,
    /// so small drifts converge silently instead of accumulating until they cross the
    /// reconcile threshold and snap. Mirrors Fish-Net's LocalReconcileCorrectionType=Smooth.
    /// Default no-op — opt in per entity by overriding.
    /// </summary>
    public virtual void ApplySoftCorrection(IEntityStateData receivedState, Vector3 savedPositionAtTick) { }
}
