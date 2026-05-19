using Godot;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace MonkeNet.Client;

[GlobalClass, Icon("res://addons/monke-net/resources/circle_nodes_solid.png")]
public partial class ClientPredictedEntity : ClientNetworkBehaviour
{
    public virtual void OnProcessTick(int tick, IPackableElement input) { }

    /// <summary>
    /// Per-snapshot hook for syncing AUXILIARY state (sleep flags, custom
    /// per-entity coherence data) from the authoritative snapshot when the
    /// body pose did NOT exceed the hard reconcile threshold. Does NOT
    /// touch the body's transform — that path is the
    /// <see cref="HandleReconciliation"/>/rollback flow. Fires every
    /// snapshot regardless of misprediction outcome (when below threshold),
    /// so cumulative-drift state (sleep coherence across stacked rigid
    /// bodies, for example) stays in lockstep without forcing a full
    /// pose snap. Default no-op.
    /// </summary>
    public virtual void ApplyAuthoritativeNonPoseState(IEntityStateData receivedState) { }

    /// <summary>
    /// Called after the per-tick SpaceStep completes, before
    /// <see cref="ClientPredictionManager"/> records the post-step
    /// snapshot. Use for state that must read this tick's post-step body pose of
    /// OTHER entities — e.g. anchoring a kinematic rider to the just-integrated
    /// vehicle pose. <see cref="OnProcessTick"/> runs BEFORE SpaceStep so any
    /// position it reads from a peer body is the previous tick's result; reading
    /// here gets the current tick's result.
    /// </summary>
    public virtual void OnPostPhysicsTick(int tick, IPackableElement input) { }

    public virtual bool HasMisspredicted(int tick, IEntityStateData receivedState, RigidbodyState savedState) { return false; }
    public virtual void HandleReconciliation(IEntityStateData receivedState) { }
    public virtual void ResimulateTick(IPackableElement input) { }

    /// <summary>
    /// Position of the entity at the moment of registration. Used by the prediction
    /// manager only as a quick accessor for diagnostic logging.
    /// </summary>
    public virtual Vector3 GetPosition() { return Vector3.Zero; }

    /// <summary>
    /// Snapshot of the entity's full simulation state at the current tick. Stored by
    /// the prediction manager for each registered tick and passed back to
    /// <see cref="HasMisspredicted"/> when a server snapshot for that tick arrives.
    /// Override on each predicted entity that wants velocity-aware misprediction
    /// detection — default is zeroed state.
    /// </summary>
    public virtual RigidbodyState GetSnapshotState() { return default; }

    /// <summary>
    /// Extracts the authoritative position from an <see cref="IEntityStateData"/>.
    /// The framework uses this only for diagnostic logging — concrete entity types
    /// know which message struct they use and can cast to it. Default returns
    /// <see cref="Vector3.Zero"/>; override on each predicted entity that wants
    /// useful misprediction logs.
    /// </summary>
    public virtual Vector3 ExtractAuthoritativePosition(IEntityStateData state) { return Vector3.Zero; }

    /// <summary>
    /// Restores the body's transform + velocities to a previously captured snapshot
    /// state, without any of the authority-reconcile side effects
    /// (<see cref="HandleReconciliation"/> may call SyncSleepState, zero residual
    /// forces, etc.). Called by the prediction manager's spawn-tick-alignment path
    /// to put non-newly-spawned entities back at their pre-resim pose after the
    /// catch-up resim's <c>SpaceStep</c> calls have over-stepped every body in the
    /// physics space. Default no-op — override on entities that wrap a
    /// <see cref="PredictionRigidbody3D"/>.
    /// </summary>
    public virtual void RestoreBodyState(RigidbodyState state) { }
}
