using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using GameDemo;
using GdUnit4;
using Godot;
using MonkeNet.Client;
using MonkeNet.Server;
using MonkeNet.Shared;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Integration;

/// <summary>
/// NRB-01..NRB-07: 3D rigidbody behaviour over a real localhost network connection.
///
/// Unlike the rest of the suite (which substitutes <see cref="Infrastructure.FakeNetworkBridge"/>
/// for the transport and delivers packets synchronously), these tests call
/// <see cref="MonkeNetManager.CreateListenServer"/>. That bootstraps a real
/// <see cref="NetworkManagerEnet"/> server and a real <see cref="NetworkManagerEnet"/>
/// client, each with its own <see cref="SceneMultiplayer"/>, both bound to a localhost
/// UDP port. Every snapshot, EntityEvent, and ClockSync travels through ENet's send/receive
/// poll loop the same way it would between two machines.
///
/// Server and client share the single Godot SceneTree and physics space (listen-server
/// layout), but their entities are split onto different collision layers so they never
/// interfere with each other. Tests inspect state via <see cref="EntitySpawner.Entities"/>
/// (server side) and <see cref="EntitySpawner.ClientEntities"/> (client side).
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class NetworkedRigidbodyTests
{
    // Each test uses a fresh UDP port so a previous test's not-yet-released socket
    // cannot collide with the next CreateServer bind. Starts well above the demo's
    // hard-coded 9999 and any other test suite's range to avoid cross-suite clashes.
    private static int _portCounter = 9300;

    private ISceneRunner _runner;
    private int _port;

    [BeforeTest]
    public async Task SetUp()
    {
        // The MonkeNet scene is a Godot autoload — MonkeNetManager.Instance, EntitySpawner.Instance,
        // and the two NetworkManagerEnet nodes survive across tests. We can't recreate them, so
        // defensively clear any state a previous test left behind before starting fresh.
        await ResetAutoloadedMonkeNetState();

        // PredictionRigidbodyTests sets MonkeNetConfig.Instance = null in TearDown so the
        // singleton from a previously-loaded MainScene doesn't survive into this test's
        // MainScene.Instance assignment. Mirror that defensive reset.
        MonkeNetConfig.Instance = null;
        _port = System.Threading.Interlocked.Increment(ref _portCounter);

        _runner = ISceneRunner.Load("res://demo/MainScene.tscn", autoFree: true);
        await _runner.AwaitIdleFrame();
    }

    [AfterTest]
    public async Task TearDown()
    {
        // Order matters: disconnect client first so the server doesn't try to send to a dead
        // peer, then stop the server (which clears server-side entities), then explicitly clear
        // client entities (no equivalent of StopServer for the client side), then queue-free the
        // surviving ClientManager subtree.
        await ResetAutoloadedMonkeNetState();

        _runner?.Dispose();
        MonkeNetConfig.Instance = null;
    }

    /// <summary>
    /// Tears down anything the previous test left attached to the autoloaded MonkeNet tree:
    /// closes ENet hosts, frees both manager scenes, clears entity lists. Safe to call when
    /// nothing is set up (e.g., very first test, or a test that bailed in BeforeTest).
    /// </summary>
    private async Task ResetAutoloadedMonkeNetState()
    {
        try
        {
            if (ClientManager.Instance != null && Godot.GodotObject.IsInstanceValid(ClientManager.Instance))
                ClientManager.Instance.DisconnectUngraceful();
        }
        catch { /* best effort */ }

        try
        {
            if (MonkeNetManager.Instance != null && MonkeNetManager.Instance.IsServer)
                MonkeNetManager.Instance.StopServer();
        }
        catch { /* best effort */ }

        // ClearServerEntities already runs inside StopServer, but call it again so a test that
        // populated entities WITHOUT going through StopServer (or where StopServer threw mid-way)
        // still cleans up. Idempotent.
        try { EntitySpawner.Instance?.ClearServerEntities(); } catch { }
        try { EntitySpawner.Instance?.ClearClientEntities(); } catch { }

        // Force-remove the ClientManager autoload child if it survived. CreateListenServer
        // calls RemoveExistingManager("ClientManager") on the next setup, so this is belt-and-
        // suspenders — but a stale ClientManager.Instance pointer can mislead null checks
        // in the next test's StartAndWaitForReady.
        try
        {
            var lingeringClient = MonkeNetManager.Instance?.GetNodeOrNull("ClientManager");
            if (lingeringClient != null)
            {
                MonkeNetManager.Instance.RemoveChild(lingeringClient);
                lingeringClient.QueueFree();
            }
        }
        catch { }

        // Give Godot a couple of frames for: deferred Disconnect callable, ENet host close,
        // QueueFree to actually free, ClientManager._ExitTree to null Instance.
        if (_runner != null)
        {
            try { for (int i = 0; i < 3; i++) await _runner.AwaitIdleFrame(); }
            catch { /* runner may already be gone */ }
        }
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Boots the listen-server, lowers the clock sample size to 1 so NetworkReady fires
    /// after the first round-trip, and waits for the handshake to complete. Returns once
    /// <see cref="ClientManager.IsNetworkReady"/> is true.
    /// </summary>
    private async Task StartAndWaitForReady(int timeoutMs = 8000)
    {
        MonkeNetManager.Instance.CreateListenServer(_port);
        await _runner.AwaitIdleFrame();

        // ClientNetworkClock waits for _sampleSize round-trip ClockSync exchanges before
        // emitting LatencyCalculated (which gates NetworkReady). Default sample size is 3
        // and sample rate is 1Hz → ~3s of wall time per test. Drop to 1 so a single
        // ClockSync exchange flips us ready, cutting setup to ~1s.
        var clock = ClientManager.Instance.GetNode<ClientNetworkClock>("ClientNetworkClock");
        typeof(ClientNetworkClock)
            .GetField("_sampleSize", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.SetValue(clock, 1);

        var sa = AssertSignal(ClientManager.Instance);
        await sa.IsEmitted(ClientManager.SignalName.NetworkReady).WithTimeout(timeoutMs);
        AssertThat(ClientManager.Instance.IsNetworkReady).IsTrue();
    }

    /// <summary>Pumps n idle frames so deferred work (snapshot dispatch, ENet poll, scene attach) catches up.</summary>
    private async Task PumpFrames(int n)
    {
        for (int i = 0; i < n; i++)
            await _runner.AwaitIdleFrame();
    }

    /// <summary>Pumps frames until <paramref name="condition"/> returns true or <paramref name="maxFrames"/> elapses.</summary>
    private async Task<bool> WaitUntil(System.Func<bool> condition, int maxFrames = 120)
    {
        for (int i = 0; i < maxFrames; i++)
        {
            if (condition()) return true;
            await _runner.AwaitIdleFrame();
        }
        return condition();
    }

    private static NetworkBehaviour FindServerEntity(int entityId) =>
        EntitySpawner.Instance?.Entities.FirstOrDefault(e => e.EntityId == entityId);

    private static NetworkBehaviour FindClientEntity(int entityId) =>
        EntitySpawner.Instance?.ClientEntities.FirstOrDefault(e => e.EntityId == entityId);

    /// <summary>
    /// Returns the entity's RigidBody3D by walking up to the spawned scene root (the direct
    /// child of EntitySpawner) and then down through its tree. The demo's ball/cube/vehicle
    /// scenes have the RigidBody3D AS the root, so the up-walk lands directly on it.
    /// </summary>
    private static RigidBody3D GetRigidBodyOfEntity(NetworkBehaviour entity)
    {
        if (entity == null) return null;
        Node root = entity;
        while (root.GetParent() != null && root.GetParent() is not EntitySpawner)
            root = root.GetParent();
        return FindFirstChildOfType<RigidBody3D>(root);
    }

    /// <summary>Same up-walk + down-search but for the PredictionRigidbody3D wrapper.</summary>
    private static PredictionRigidbody3D GetPredictionRbOfEntity(NetworkBehaviour entity)
    {
        if (entity == null) return null;
        Node root = entity;
        while (root.GetParent() != null && root.GetParent() is not EntitySpawner)
            root = root.GetParent();
        return FindFirstChildOfType<PredictionRigidbody3D>(root);
    }

    private static T FindFirstChildOfType<T>(Node node) where T : Node
    {
        if (node == null) return null;
        if (node is T match) return match;
        foreach (Node child in node.GetChildren())
        {
            var found = FindFirstChildOfType<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// Dumps the live entity lists with their tree paths — used in IsNotNull failure messages
    /// so a flaky lookup tells us whether the entity was missing from the list, missing a
    /// rigidbody, or the test infrastructure got into a wrong scene state.
    /// </summary>
    private static string DumpEntities()
    {
        var spawner = EntitySpawner.Instance;
        if (spawner == null) return "EntitySpawner.Instance is NULL";
        string serverList = string.Join(",", spawner.Entities.Select(e =>
            $"id={e.EntityId} type={e.EntityType} root={(e.GetParent()?.Name.ToString() ?? "?")}"));
        string clientList = string.Join(",", spawner.ClientEntities.Select(e =>
            $"id={e.EntityId} type={e.EntityType} root={(e.GetParent()?.Name.ToString() ?? "?")}"));
        return $"server=[{serverList}] client=[{clientList}]";
    }

    // ─── NRB-01: real ENet handshake ──────────────────────────────────────────
    // Sanity that the listen-server boot path actually completes a handshake over a real
    // SceneMultiplayer/ENet pair (no Fake* substitutes anywhere). Every other test in this
    // file is meaningless if this one doesn't pass.
    [TestCase(Timeout = 12000)]
    public async Task NRB01_ListenServerHandshake_ClientReadiesOverRealEnet()
    {
        await StartAndWaitForReady();

        AssertThat(ClientManager.Instance).IsNotNull();
        AssertThat(ServerManager.Instance).IsNotNull();
        AssertThat(MonkeNetManager.Instance.IsServer).IsTrue();
        // The client picked up a non-server peer ID from ENet (server is always 1).
        AssertThat(ClientManager.Instance.GetNetworkId()).IsNotEqual(0);
        AssertThat(ClientManager.Instance.GetNetworkId()).IsNotEqual(1);
    }

    // ─── NRB-02: spawn replication ────────────────────────────────────────────
    // Spawning a ball server-side broadcasts EntityEventMessage(Created) on the reliable
    // EntityEvent channel. The client's NetworkManagerEnet receives the bytes through real
    // ENet poll and instantiates the DummyBall scene. Both sides should now see one entity
    // each: the server's ServerBall in EntitySpawner.Entities, the client's DummyBall in
    // EntitySpawner.ClientEntities, both with the same EntityId.
    [TestCase(Timeout = 15000)]
    public async Task NRB02_ServerSpawnsBall_ClientReceivesDummyBallOverWire()
    {
        await StartAndWaitForReady();

        var serverBall = ServerManager.Instance.SpawnEntity<Node3D>(entityType: 1, authority: 0);
        AssertThat(serverBall).IsNotNull();
        // The framework names the spawned root after the EntityId (see EntitySpawner.InitializeEntity),
        // but reading from the tracked NetworkBehaviour list is the authoritative source.
        var serverEntity = EntitySpawner.Instance.Entities.LastOrDefault();
        AssertThat(serverEntity)
            .OverrideFailureMessage($"No server entity in EntitySpawner.Entities after Spawn. {DumpEntities()}")
            .IsNotNull();
        int entityId = serverEntity.EntityId;

        // Wait for the reliable Created event to round-trip and the DummyBall scene to attach.
        bool clientGotIt = await WaitUntil(() => FindClientEntity(entityId) != null, maxFrames: 120);
        AssertThat(clientGotIt)
            .OverrideFailureMessage($"Client never received the EntityEventMessage(Created) for entity {entityId}. " +
                                    $"ClientEntities count = {EntitySpawner.Instance.ClientEntities.Count}.")
            .IsTrue();

        // Both sides see exactly one ball with matching entity id and entity type 1.
        // GdUnit4's AssertThat rejects primitives — cast to int explicitly.
        AssertThat((int)FindServerEntity(entityId).EntityType).IsEqual(1);
        AssertThat((int)FindClientEntity(entityId).EntityType).IsEqual(1);
    }

    // ─── NRB-03: gravity falls and snapshots replicate position ───────────────
    // ServerBall._body is a RigidBody3D with non-zero gravity. The server's PhysicsProcess
    // steps physics each tick and broadcasts a GameSnapshotMessage on the unreliable Snapshot
    // channel. The client decodes the snapshot, routes the EntityStateMessage to the
    // DummyBall via ClientSnapshotInterpolator, and the dummy lerps toward the authoritative
    // pose. After ~1s the dummy's Y position should track the server's within a few units
    // (interpolation buffer adds a small render-tick lag, hence the 2m tolerance).
    [TestCase(Timeout = 20000)]
    public async Task NRB03_BallFallsUnderGravity_ClientPositionTracksServerOverWire()
    {
        await StartAndWaitForReady();

        ServerManager.Instance.SpawnEntity<Node3D>(entityType: 1, authority: 0);
        var serverEntity = EntitySpawner.Instance.Entities.Last();
        int entityId = serverEntity.EntityId;

        bool clientGotIt = await WaitUntil(() => FindClientEntity(entityId) != null, maxFrames: 120);
        AssertThat(clientGotIt).IsTrue();

        // Capture starting Y on the server (ball spawns at y=10 per ServerBallStateSyncronizer.OnEntitySpawned).
        var serverBody = GetRigidBodyOfEntity(FindServerEntity(entityId));
        var clientBody = GetRigidBodyOfEntity(FindClientEntity(entityId));
        AssertThat(serverBody)
            .OverrideFailureMessage($"Server-side RigidBody3D not found for entity {entityId}. {DumpEntities()}")
            .IsNotNull();
        AssertThat(clientBody)
            .OverrideFailureMessage($"Client-side RigidBody3D not found for entity {entityId}. {DumpEntities()}")
            .IsNotNull();

        float startServerY = serverBody.GlobalPosition.Y;

        // Let physics run and snapshots flow for ~1 second (60 frames @ 60Hz).
        await PumpFrames(60);

        // Server ball should have fallen meaningfully under gravity.
        float endServerY = serverBody.GlobalPosition.Y;
        AssertThat(startServerY - endServerY)
            .OverrideFailureMessage($"Server ball did not fall: startY={startServerY:F3} endY={endServerY:F3}")
            .IsGreater(0.5f);

        // Client dummy should track within a couple of units (interpolation lag + small delivery jitter).
        float yDelta = Mathf.Abs(serverBody.GlobalPosition.Y - clientBody.GlobalPosition.Y);
        AssertThat(yDelta)
            .OverrideFailureMessage($"Client dummy Y={clientBody.GlobalPosition.Y:F3} does not track server Y={serverBody.GlobalPosition.Y:F3} (delta {yDelta:F3} > 2m)")
            .IsLess(2f);
    }

    // ─── NRB-04: server impulse propagates as a position change ───────────────
    // Apply a horizontal impulse to the server ball. Under the unified-prediction
    // model the client predicts the ball locally (no input), so the impulse propagates
    // through the snapshot-reconcile path: each snapshot the client compares its
    // predicted state against the server's and pulls position+velocity back when they
    // diverge beyond LocalRigidPropPrediction's threshold. Position is the more
    // stable signal across reconcile timing — velocity may briefly read 0 between
    // reconciles even when the body has been actively repositioned.
    [TestCase(Timeout = 20000)]
    public async Task NRB04_ServerAppliesImpulse_ClientPositionReflectsServerOverWire()
    {
        await StartAndWaitForReady();

        ServerManager.Instance.SpawnEntity<Node3D>(entityType: 1, authority: 0);
        var serverEntity = EntitySpawner.Instance.Entities.Last();
        int entityId = serverEntity.EntityId;
        bool clientGotIt = await WaitUntil(() => FindClientEntity(entityId) != null, maxFrames: 120);
        AssertThat(clientGotIt).IsTrue();

        var serverBody = GetRigidBodyOfEntity(serverEntity);
        AssertThat(serverBody)
            .OverrideFailureMessage($"Server-side RigidBody3D not found for entity {entityId}. {DumpEntities()}")
            .IsNotNull();

        float startServerX = serverBody.GlobalPosition.X;
        var kick = new Vector3(8f, 0f, 0f);
        serverBody.ApplyCentralImpulse(kick);

        await PumpFrames(30);

        var clientBody = GetRigidBodyOfEntity(FindClientEntity(entityId));
        AssertThat(clientBody)
            .OverrideFailureMessage($"Client RigidBody3D not found for entity {entityId}. {DumpEntities()}")
            .IsNotNull();

        // After 30 ticks the server ball should have moved several meters along +X.
        // The client's predicted body, reconciled to server state, should track within
        // a generous tolerance.
        float serverDeltaX = serverBody.GlobalPosition.X - startServerX;
        AssertThat(serverDeltaX)
            .OverrideFailureMessage($"Server body did not move under impulse (deltaX={serverDeltaX:F3})")
            .IsGreater(0.5f);
        float clientServerXDelta = System.MathF.Abs(clientBody.GlobalPosition.X - serverBody.GlobalPosition.X);
        AssertThat(clientServerXDelta)
            .OverrideFailureMessage($"Client X={clientBody.GlobalPosition.X:F3} diverges from server X={serverBody.GlobalPosition.X:F3} beyond reconcile envelope")
            .IsLess(1.0f);
    }

    // ─── NRB-05: two-ball collision replicates both post-collision states ─────
    // Spawn two server balls in a head-on configuration, give them opposing velocities,
    // and let the server simulate the impact. After the collision the client should see
    // both dummies with non-trivial velocities pointing roughly back the way they came.
    // This exercises multi-entity GameSnapshotMessage packing and per-entity routing
    // through ClientSnapshotInterpolator.
    [TestCase(Timeout = 20000)]
    public async Task NRB05_TwoBallsCollideOnServer_ClientObservesPostCollisionStateOverWire()
    {
        await StartAndWaitForReady();

        ServerManager.Instance.SpawnEntity<Node3D>(entityType: 1, authority: 0);
        ServerManager.Instance.SpawnEntity<Node3D>(entityType: 1, authority: 0);

        bool ready = await WaitUntil(
            () => EntitySpawner.Instance.ClientEntities.Count(e => e.EntityType == 1) >= 2,
            maxFrames: 120);
        AssertThat(ready)
            .OverrideFailureMessage($"Both balls did not replicate to client: client ball count = {EntitySpawner.Instance.ClientEntities.Count(e => e.EntityType == 1)}")
            .IsTrue();

        var serverBalls = EntitySpawner.Instance.Entities.Where(e => e.EntityType == 1).Take(2).ToArray();
        var ballA = serverBalls[0];
        var ballB = serverBalls[1];

        var bodyA = GetRigidBodyOfEntity(ballA);
        var bodyB = GetRigidBodyOfEntity(ballB);
        AssertThat(bodyA)
            .OverrideFailureMessage($"RigidBody3D not found on ballA (id={ballA.EntityId}). {DumpEntities()}")
            .IsNotNull();
        AssertThat(bodyB)
            .OverrideFailureMessage($"RigidBody3D not found on ballB (id={ballB.EntityId}). {DumpEntities()}")
            .IsNotNull();

        // Position them in a head-on configuration. Override OnEntitySpawned's (0,10,0)
        // default by placing them close together on the X axis (above the floor so gravity
        // doesn't drop them into a static surface before they collide).
        bodyA.GlobalPosition = new Vector3(-0.6f, 5f, 0f);
        bodyB.GlobalPosition = new Vector3(+0.6f, 5f, 0f);
        // Direct velocity assignment, NOT via PredictionRigidbody3D.SetLinearVelocity — the
        // ball has no per-tick handler that would call Simulate() to flush the queue, so a
        // queued op would never execute. See NRB-04's comment.
        bodyA.LinearVelocity = new Vector3(+6f, 0f, 0f);
        bodyB.LinearVelocity = new Vector3(-6f, 0f, 0f);

        // Capture pre-collision Xs so the assertion can compare against the actual starting
        // values (ServerManager._PhysicsProcess may run a frame before we get here).
        float startAX = bodyA.GlobalPosition.X;
        float startBX = bodyB.GlobalPosition.X;

        // Run long enough for collision to occur (~0.1s for 6 m/s closing over ~1.2m gap)
        // plus a few extra ticks for the post-collision snapshots to propagate.
        await PumpFrames(30);

        // After the collision balls should have BOTH non-trivial X velocities AND have
        // separated (each pushed away from the impact point). A no-op (no collision) would
        // leave both X velocities ≈ 0 (gravity zeroes nothing on X) and both bodies would
        // have continued past each other — neither outcome should look like a real collision.
        // The clearest signal is: ballA's X velocity is now LESS than its starting +6, and
        // ballB's is now GREATER than its starting -6 (i.e., both decelerated/reversed).
        AssertThat(bodyA.LinearVelocity.X)
            .OverrideFailureMessage($"BallA still moving forward — collision did not decelerate it: vel={bodyA.LinearVelocity}")
            .IsLess(5f);
        AssertThat(bodyB.LinearVelocity.X)
            .OverrideFailureMessage($"BallB still moving forward — collision did not decelerate it: vel={bodyB.LinearVelocity}")
            .IsGreater(-5f);
        // And both bodies should have actually MOVED on the X axis (not stuck — would mean physics never ran).
        AssertThat(Mathf.Abs(bodyA.GlobalPosition.X - startAX) + Mathf.Abs(bodyB.GlobalPosition.X - startBX))
            .OverrideFailureMessage($"Bodies barely moved over 30 ticks: ballA.X {startAX:F3}->{bodyA.GlobalPosition.X:F3}, ballB.X {startBX:F3}->{bodyB.GlobalPosition.X:F3}")
            .IsGreater(0.2f);

        // Client dummies should reflect the post-collision X positions within tolerance of the server.
        var dummyA = GetRigidBodyOfEntity(FindClientEntity(ballA.EntityId));
        var dummyB = GetRigidBodyOfEntity(FindClientEntity(ballB.EntityId));
        AssertThat(dummyA)
            .OverrideFailureMessage($"DummyA RigidBody3D not found for entity {ballA.EntityId}. {DumpEntities()}")
            .IsNotNull();
        AssertThat(dummyB)
            .OverrideFailureMessage($"DummyB RigidBody3D not found for entity {ballB.EntityId}. {DumpEntities()}")
            .IsNotNull();

        AssertThat(Mathf.Abs(dummyA.GlobalPosition.X - bodyA.GlobalPosition.X))
            .OverrideFailureMessage($"DummyA X={dummyA.GlobalPosition.X:F3} does not track ServerA X={bodyA.GlobalPosition.X:F3}")
            .IsLess(2f);
        AssertThat(Mathf.Abs(dummyB.GlobalPosition.X - bodyB.GlobalPosition.X))
            .OverrideFailureMessage($"DummyB X={dummyB.GlobalPosition.X:F3} does not track ServerB X={bodyB.GlobalPosition.X:F3}")
            .IsLess(2f);
    }

    // ─── NRB-06: despawn replication ──────────────────────────────────────────
    // The mirror of NRB-02 — destroying server-side dispatches EntityEventMessage(Destroyed)
    // reliably on EntityEvent channel. The client's ClientEntities list should drop the
    // matching entry once the message is processed.
    [TestCase(Timeout = 15000)]
    public async Task NRB06_ServerDestroysBall_ClientRemovesDummyOverWire()
    {
        await StartAndWaitForReady();

        ServerManager.Instance.SpawnEntity<Node3D>(entityType: 1, authority: 0);
        var serverEntity = EntitySpawner.Instance.Entities.Last();
        int entityId = serverEntity.EntityId;

        bool clientGotIt = await WaitUntil(() => FindClientEntity(entityId) != null, maxFrames: 120);
        AssertThat(clientGotIt).IsTrue();
        AssertThat(EntitySpawner.Instance.ClientEntities.Count(e => e.EntityId == entityId)).IsEqual(1);

        // Broadcast destroy to all peers (target = AudienceMode.Broadcast).
        ServerManager.Instance.DestroyEntity(entityId, (int)NetworkManagerEnet.AudienceMode.Broadcast);

        bool removed = await WaitUntil(() => FindClientEntity(entityId) == null, maxFrames: 120);
        AssertThat(removed)
            .OverrideFailureMessage($"Client never removed entity {entityId} after server-side DestroyEntity. " +
                                    $"ClientEntities count = {EntitySpawner.Instance.ClientEntities.Count}.")
            .IsTrue();
    }

    // ─── NRB-07: real bytes are flying on the wire ────────────────────────────
    // Sanity that the snapshot stream actually generated traffic on the ENet host. If
    // PopStatistic returns 0 after a second of running snapshots, either the listen-server
    // wired itself to a fake transport (regression) or no entities were syncing — both
    // would invalidate every other test in this suite.
    [TestCase(Timeout = 15000)]
    public async Task NRB07_SnapshotStreamGeneratesRealEnetTraffic()
    {
        await StartAndWaitForReady();

        // Spawn one ball so the snapshot has content to send each tick.
        ServerManager.Instance.SpawnEntity<Node3D>(entityType: 1, authority: 0);
        await PumpFrames(10);

        // Reach into MonkeNetManager for the server's NetworkManagerEnet (the listen-server's
        // primary transport). PopStatistic returns total bytes since the last call AND resets
        // the counter, so we drain once first to start counting from a known baseline.
        var serverNet = MonkeNetManager.Instance.GetNode<NetworkManagerEnet>("NetworkManagerEnet");
        AssertThat(serverNet).IsNotNull();
        serverNet.PopStatistic(INetworkManager.NetworkStatisticEnum.SentBytes);

        // Run for ~1s of physics to accumulate snapshot traffic.
        await PumpFrames(60);

        int sentBytes = serverNet.PopStatistic(INetworkManager.NetworkStatisticEnum.SentBytes);
        AssertThat(sentBytes)
            .OverrideFailureMessage($"Server's ENet host reported zero bytes sent after 60 frames of snapshot streaming. " +
                                    $"Either the listen-server is not wired to real ENet, or no snapshots are being broadcast.")
            .IsGreater(0);
    }
}
