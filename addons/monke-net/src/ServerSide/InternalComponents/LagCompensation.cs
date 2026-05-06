using Godot;
using ImGuiNET;
using MonkeNet.Shared;
using System.Collections.Generic;

namespace MonkeNet.Server;

/// <summary>
/// Server-side history of every networked entity's pose for the last <see cref="HistoryTicks"/>
/// physics ticks. Lets the server "rewind" the world to the moment a client fired a shot
/// (one round-trip-time ago) so a raycast lands where the client actually saw the target —
/// not where the entity was on the server when the packet arrived.
///
/// Without lag compensation, a player aiming at a moving target watches their shot land
/// behind the target by ~RTT × target velocity. This class restores the perceived hit fairness.
///
/// Usage:
/// 1. Wired automatically as a child of <c>ServerManager.tscn</c> (no setup needed there).
/// 2. From game code on the server, call:
///    <code>LagCompensation.Instance.RaycastAtTick(firedAtTick, origin, direction, length, out hit);</code>
///    where <c>firedAtTick</c> = client's currentTick − latencyTicks − jitterTicks (the hit
///    message should carry that tick stamp).
/// 3. Bodies the ray should ignore (e.g. the shooter themselves) can be passed via
///    <c>excludeEntityIds</c>.
///
/// Limitations:
/// - Only translation + rotation are rewound. Bone/animation poses are not (Fish-Net's
///   <c>HitShape</c> handles those — it's a follow-up if your game has skeletal hitboxes).
/// - Static bodies (StaticBody3D) are not rewound — they don't move, so their current
///   pose is correct at any past tick.
/// </summary>
[GlobalClass]
public partial class LagCompensation : Node
{
    public static LagCompensation Instance { get; private set; }

    /// <summary>
    /// Number of ticks to retain. 12 ticks ≈ 200ms @ 60Hz, which covers typical
    /// round-trip-time + jitter for online play. Larger windows tolerate worse networks
    /// but inflate memory linearly. Older ticks are evicted FIFO.
    /// </summary>
    [Export] public int HistoryTicks { get; set; } = 12;

    private struct Pose
    {
        public Vector3 Position;
        public Quaternion Rotation;
    }

    // Each ring entry maps EntityId → captured pose at that tick.
    private struct TickSnapshot
    {
        public int Tick;
        public Dictionary<int, Pose> Poses;
    }

    private readonly LinkedList<TickSnapshot> _history = new();
    private int _queryCount;
    private int _missingTickCount;

    public override void _EnterTree()
    {
        Instance = this;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        _history.Clear();
    }

    public override void _Ready()
    {
        var server = ServerManager.Instance;
        if (server != null)
            server.ServerTick += OnServerTick;
    }

    private void OnServerTick(int currentTick)
    {
        var spawner = MonkeNetManager.Instance?.EntitySpawner;
        if (spawner == null) return;

        // Build a snapshot of every server entity's current pose. Reuse a fresh
        // dict per entry so older buffer slots stay independent.
        var snap = new TickSnapshot
        {
            Tick = currentTick,
            Poses = new Dictionary<int, Pose>(spawner.Entities.Count),
        };
        foreach (var entity in spawner.Entities)
        {
            var root = spawner.GetEntityRoot(entity);
            if (root == null) continue;
            snap.Poses[entity.EntityId] = new Pose
            {
                Position = root.GlobalPosition,
                Rotation = root.Quaternion,
            };
        }

        _history.AddLast(snap);
        while (_history.Count > HistoryTicks)
            _history.RemoveFirst();
    }

    /// <summary>
    /// Casts a ray against the physics world rewound to <paramref name="rewindTick"/>.
    /// Bodies move to their captured poses for the duration of the query, then revert.
    /// Returns true if the ray hit a body. Sets <paramref name="hit"/> with the entity ID
    /// (or 0 for non-networked geometry), the world-space hit point, and the normal.
    /// If the requested tick has been evicted from history, falls back to a current-time
    /// raycast and increments a counter visible in the debug overlay.
    /// </summary>
    public bool RaycastAtTick(int rewindTick, Vector3 origin, Vector3 direction, float length,
        out HitInfo hit, IReadOnlyCollection<int> excludeEntityIds = null)
    {
        _queryCount++;
        var snap = FindSnapshot(rewindTick);
        if (snap.Poses == null)
        {
            _missingTickCount++;
            return Raycast(origin, direction, length, out hit, excludeEntityIds);
        }

        var spawner = MonkeNetManager.Instance?.EntitySpawner;
        if (spawner == null)
        {
            hit = default;
            return false;
        }

        // Save current poses so we can restore after the query. Faster than re-snapshotting
        // because we already have the entity list.
        var currentPoses = new Dictionary<int, Pose>(spawner.Entities.Count);
        foreach (var entity in spawner.Entities)
        {
            var root = spawner.GetEntityRoot(entity);
            if (root == null) continue;
            currentPoses[entity.EntityId] = new Pose
            {
                Position = root.GlobalPosition,
                Rotation = root.Quaternion,
            };

            if (snap.Poses.TryGetValue(entity.EntityId, out var past))
            {
                root.GlobalPosition = past.Position;
                root.Quaternion = past.Rotation;
                if (root is CollisionObject3D co) co.ForceUpdateTransform();
            }
        }
        // Flush so the physics server picks up the rewound transforms before the query.
        if (MonkeNetManager.Instance != null)
            PhysicsServer3D.SpaceFlushQueries(MonkeNetManager.Instance.PhysicsSpace);

        bool hitResult = Raycast(origin, direction, length, out hit, excludeEntityIds);

        // Restore.
        foreach (var entity in spawner.Entities)
        {
            var root = spawner.GetEntityRoot(entity);
            if (root == null) continue;
            if (currentPoses.TryGetValue(entity.EntityId, out var current))
            {
                root.GlobalPosition = current.Position;
                root.Quaternion = current.Rotation;
                if (root is CollisionObject3D co) co.ForceUpdateTransform();
            }
        }
        if (MonkeNetManager.Instance != null)
            PhysicsServer3D.SpaceFlushQueries(MonkeNetManager.Instance.PhysicsSpace);

        return hitResult;
    }

    private TickSnapshot FindSnapshot(int tick)
    {
        // Linear scan; HistoryTicks is small (default 12). Exact match preferred; if
        // the requested tick is older than our oldest, return default (caller falls back).
        for (var node = _history.Last; node != null; node = node.Previous)
        {
            if (node.Value.Tick == tick) return node.Value;
            if (node.Value.Tick < tick) return node.Value; // closest older snapshot
        }
        return default;
    }

    private static bool Raycast(Vector3 origin, Vector3 direction, float length,
        out HitInfo hit, IReadOnlyCollection<int> excludeEntityIds)
    {
        hit = default;
        if (MonkeNetManager.Instance == null) return false;

        var spaceState = PhysicsServer3D.SpaceGetDirectState(MonkeNetManager.Instance.PhysicsSpace);
        if (spaceState == null) return false;

        var query = PhysicsRayQueryParameters3D.Create(origin, origin + direction.Normalized() * length);

        if (excludeEntityIds != null && excludeEntityIds.Count > 0)
        {
            var rids = new Godot.Collections.Array<Rid>();
            var spawner = MonkeNetManager.Instance.EntitySpawner;
            if (spawner != null)
            {
                foreach (int id in excludeEntityIds)
                {
                    var entity = spawner.TryGetEntityById(id);
                    if (entity == null) continue;
                    var root = spawner.GetEntityRoot(entity);
                    if (root is CollisionObject3D co) rids.Add(co.GetRid());
                }
            }
            query.Exclude = rids;
        }

        var result = spaceState.IntersectRay(query);
        if (result.Count == 0) return false;

        hit = new HitInfo
        {
            Point = (Vector3)result["position"],
            Normal = (Vector3)result["normal"],
            EntityId = ResolveEntityIdFromCollider(result),
        };
        return true;
    }

    private static int ResolveEntityIdFromCollider(Godot.Collections.Dictionary result)
    {
        if (!result.ContainsKey("collider")) return 0;
        if (result["collider"].As<Node>() is not Node node) return 0;
        var spawner = MonkeNetManager.Instance?.EntitySpawner;
        if (spawner == null) return 0;
        // Walk up to find the root entity (the one registered in Entities).
        Node current = node;
        while (current != null)
        {
            foreach (var entity in spawner.Entities)
                if (spawner.GetEntityRoot(entity) == current) return entity.EntityId;
            current = current.GetParent();
        }
        return 0;
    }

    public int HistoryDepth => _history.Count;
    public int OldestTick => _history.First?.Value.Tick ?? -1;
    public int NewestTick => _history.Last?.Value.Tick ?? -1;
    public int QueryCount => _queryCount;
    public int MissingTickCount => _missingTickCount;

    public void DisplayDebugInformation()
    {
        if (ImGui.CollapsingHeader("Lag Compensation"))
        {
            ImGui.Text($"History {_history.Count}/{HistoryTicks} ticks");
            ImGui.Text($"Range {OldestTick} .. {NewestTick}");
            ImGui.Text($"Queries {_queryCount} (missing-tick: {_missingTickCount})");
        }
    }

    public struct HitInfo
    {
        public Vector3 Point;
        public Vector3 Normal;
        /// <summary>EntityId of the hit body, or 0 if the ray hit non-networked geometry.</summary>
        public int EntityId;
    }
}
