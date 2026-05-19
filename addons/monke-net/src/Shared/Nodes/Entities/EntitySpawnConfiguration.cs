using Godot;
using MonkeNet.Shared;

[GlobalClass, Icon("res://addons/monke-net/resources/circle_nodes_solid.png")]
public partial class EntitySpawnConfiguration : Resource
{
    [Export] public byte EntityType { get; set; }

    /// <summary>
    /// Scene instantiated on every client regardless of ownership. The unified-prediction
    /// refactor collapsed the old ClientAuthorityScene / ClientDummyScene pair into this
    /// single scene — every client now predicts the entity locally (Jolt sim + snapshot
    /// reconcile + PredictionVisualSmoothing3D); authority only governs whose input
    /// drives the entity.
    /// </summary>
    [Export] public PackedScene ClientScene { get; set; }

    [Export] public PackedScene ServerScene { get; set; }

    /// <summary>
    /// Server-side authority-approval policy. Null = reject all ownership requests for
    /// this entity type. Configure to allow clients to claim/release ownership via
    /// <c>RequestAuthority</c> / <c>ReleaseAuthority</c>. The framework evaluates the
    /// policy before falling through to the custom
    /// <c>ServerEntityManager.OwnershipApprover</c> escape hatch.
    /// </summary>
    [Export] public OwnershipPolicy OwnershipPolicy { get; set; }
}
