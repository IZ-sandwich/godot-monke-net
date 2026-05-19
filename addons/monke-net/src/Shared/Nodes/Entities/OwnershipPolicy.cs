using Godot;

namespace MonkeNet.Shared;

/// <summary>
/// Declarative per-entity-type policy for authority-request approval. Attached to an
/// <see cref="EntitySpawnConfiguration"/>. The server consults this policy when a client
/// sends <c>OwnershipChangeRequestMessage</c> or <c>ReleaseAuthorityMessage</c>; a null
/// policy on the config means "reject all requests" (the secure default — games opt in
/// per entity type by assigning a policy resource).
/// </summary>
[GlobalClass, Icon("res://addons/monke-net/resources/circle_nodes_solid.png")]
public partial class OwnershipPolicy : Resource
{
    /// <summary>
    /// Approve a claim only if the entity is currently unowned (Authority == 0).
    /// Default true. Set false to allow stealing ownership from another client (rare).
    /// </summary>
    [Export] public bool RequireUnowned { get; set; } = true;

    /// <summary>
    /// Max distance in meters from any of the requesting client's owned entities to the
    /// requested entity. A request is approved only if at least one of the requester's
    /// owned entities is within this distance of the target. -1 disables the check
    /// (any requester can claim regardless of distance). Default -1.
    /// </summary>
    [Export] public float MaxRequesterDistance { get; set; } = -1f;

    /// <summary>
    /// If true, the entity's current owner can release authority back to the server
    /// (Authority=0) by sending <c>ReleaseAuthorityMessage</c>. Default true. Set false
    /// to make ownership sticky (server must explicitly reclaim).
    /// </summary>
    [Export] public bool AllowOwnerRelease { get; set; } = true;
}
