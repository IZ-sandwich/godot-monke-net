using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using GdUnit4;
using Godot;
using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Artifacts;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.MultiProcess;

/// <summary>
/// MP-PERSIST-01 — two-player session-persistence-and-reclaim round-trip.
///
/// The scenario, end to end across two real child Godot client processes +
/// a real server process:
///
/// 1. Both clients connect. The server spawns a rigid player per client at
///    distinct positions (Player A on −X, Player B on +X) and rotates each
///    body to face the other (yaw set via teleport-entity right after spawn,
///    using the rigid-player's lock_rotation=true setting to make the facing
///    stable under physics). The recorded videos show the two knights
///    standing across from each other on the floor.
/// 2. <b>Sub-phase 1A</b>: A's input schedule drives moveX oscillation; B's
///    schedule is held at zero. Only A moves. We assert that A's owned view
///    of player A moves through the full ±X excursion, that B's snapshot
///    view of player A tracks the server's authoritative pose within
///    tolerance, AND that both clients' view of player B stays effectively
///    static (no input → rigid body sleeps).
/// 3. <b>Sub-phase 1B</b>: symmetric — B moves, A is still. Same three
///    assertions, swapped.
/// 4. Quiet rest, both clients voluntarily disconnect
///    (ClientManager.Disconnect → server receives
///    DisconnectNotificationMessage → ManualDisconnectMode = KeepEntity by
///    default → server orphans entities under reclaim tokens rather than
///    destroying them). Server peer count drops to zero, but both entities
///    remain on the server with Authority = 0. We sample the SERVER
///    continuously through this window so the timeline plot shows the
///    authority line cleanly stepping down to 0 across the orphan period
///    rather than drawing a diagonal between the pre- and post-reconnect
///    panels.
/// 5. Both clients reconnect against the same server. ENet hands each side
///    a fresh NetworkId (typically different from the pre-disconnect one),
///    and ClientEntityManager.OnNetworkReady sends the saved reclaim token
///    so the server reassigns each previously-orphaned entity's Authority
///    back to the reconnecting client. We assert:
///      a. Server peer count is 2 again.
///      b. Each entity's post-reclaim Authority equals the reconnecting
///         client's NEW NetworkId — same EntityId, same owner-process,
///         even though the underlying NetworkId is fresh.
///      c. Each entity's position immediately post-reconnect is within
///         tolerance of its pre-disconnect resting position (no respawn
///         to the world origin).
///      d. Approximately-same body yaw, i.e. the rigid body wasn't
///         re-oriented during the orphan window.
/// 6. Sub-phases 2A and 2B repeat the alternating-movement assertions to
///    prove the reclaimed entities respond to input from their original
///    owner-process the same way they did before the disconnect.
///
/// Note on session tokens: the server's ServerConnectionMonitor issues a
/// fresh GUID token on every accepted connect. The CLIENT-side identity
/// across the disconnect/reconnect cycle is the saved RECLAIM token
/// (ClientEntityManager._persistentReclaimToken — the previous session's
/// token, sent back to the server in ReclaimEntityMessage to recover
/// ownership). Both the pre- and post-reconnect session tokens are
/// recorded in the CSV's sessionToken column so the trace ties to the
/// per-process MonkeLogger files via the same suffix the server logs use.
/// The test deliberately does NOT assert "tokens must change" — token
/// continuity is verified end-to-end by the entity-authority reclaim chain.
///
/// Artefacts written under <c>TestResults/PlayerPersistence/</c>:
///   - two_players.csv          per-tick, per-process, per-entity timeline
///                              (tick, t_s, phase, role, networkId,
///                              sessionToken, eid, authority, x, y, z, vx,
///                              vy, vz, yaw).
///   - two_players.svg          multi-panel position + authority plot.
///                              Client polylines are split per phase so the
///                              disconnect interval shows as a gap rather
///                              than a diagonal interpolation; server line
///                              is continuous across the orphan window.
///   - two_players.playerA.mp4  client A's in-engine viewport recording.
///   - two_players.playerB.mp4  client B's in-engine viewport recording.
///   - two_players.steps.log    StepLogger trace of phase transitions.
///   - per-process MonkeLogger files for srv, c1, c2.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class MultiProcessPlayerPersistenceTests : MultiProcessTestBase
{
    protected override string ArtifactSubdir => "PlayerPersistence";

    [BeforeTest] public void SetUp() => SetUpInternal();
    [AfterTest]  public void TearDown() => TearDownInternal();

    private const byte EntityTypeRigidPlayer = 3;

    // ── scenario geometry ────────────────────────────────────────────────────
    // Players spawn on the X axis, equidistant from the world origin so the
    // harness's fixed observer camera at (10, 1.5, -1) frames both. They're
    // rotated post-spawn to face each other along ±X (see SpawnFacingYaw*).
    // Y=0 lets the rigid body free-fall onto the floor (Y≈-2 in MainScene)
    // before any movement input is applied — matches the spawn protocol used
    // by every other multi-process rigid-player scenario.
    private const float PlayerAStartX = -1.75f;
    private const float PlayerBStartX = +1.75f;
    private const float PlayerStartY = 0f;
    private const float PlayerStartZ = -1.0f;
    // Yaw chosen to point each Knight.glb model's forward (-Z by default for
    // GLTF) toward the OTHER player.
    //   Vector3(0,0,-1).Rotated(Up, +π/2) = (-1, 0, 0) ⇒ faces -X
    //   Vector3(0,0,-1).Rotated(Up, -π/2) = (+1, 0, 0) ⇒ faces +X
    // Player A at -X faces +X (toward B): yaw = -π/2.
    // Player B at +X faces -X (toward A): yaw = +π/2.
    // RigidBody3D.lock_rotation=true on the rigid-player scenes pins these
    // values under physics integration so the bodies don't drift off facing.
    private static readonly float SpawnFacingYawA = -Mathf.Pi / 2f;
    private static readonly float SpawnFacingYawB = +Mathf.Pi / 2f;

    // ── timing ───────────────────────────────────────────────────────────────
    // Pre-spawn clock-sync warmup; matches every other MP-* test so the first
    // tick of input lands after sync has converged.
    private const int SnapshotArmTicks = 60;
    // Generous free-fall + ground-settle budget before input starts. The
    // rigid player drops ~2 m at gravity 9.8 m/s²; 90 ticks (1.5 s) clears
    // the bounce so velocity is ~zero before we start oscillating.
    private const int PlayerFallTicks = 90;
    // Sub-phase layout: each sub-phase has an active "this side moves"
    // window followed by a settle window where ALL inputs are zero. The
    // settle window exists so the previously-moving body decelerates back
    // to rest BEFORE the OTHER side starts moving — otherwise residual
    // velocity from the end of one sub-phase carries 5 m/s × ~0.1 s damp
    // window ≈ 0.5 m of coast into the next sub-phase, which would falsely
    // fail the "still side didn't move" assertion. The settle window also
    // makes the recorded video read cleanly as "A moves, both stop, B
    // moves" rather than "A trails off as B starts."
    private const int ActiveTicks = 60;
    private const int SettleTicks = 30;
    private const int SubPhaseTotalTicks = ActiveTicks + SettleTicks; // 90
    private const int OscillationPeriodTicks = 30;
    // Quiet window after each top-level phase so rigid bodies come to rest
    // before the pre-disconnect snapshot pose comparison.
    private const int RestTicks = 60;
    // Sample cadence. Coarse enough to keep CSV size sane across two phases
    // + multiple processes, fine enough that the oscillation shows clearly
    // on the SVG.
    private const int SnapshotIntervalTicks = 3;

    // ── reclaim-window timing ────────────────────────────────────────────────
    // Ticks the server is given to process the disconnect + ENet polls
    // peer_disconnected. Sampled-through during the window so the timeline
    // plot shows server pose + auth=0 continuously rather than a diagonal.
    private const int DisconnectSettleTicks = 60;
    private const int ReconnectSettleTicks = 60;

    // ── tolerances ───────────────────────────────────────────────────────────
    // Pre-disconnect resting pose vs post-reconnect spawn pose. The server's
    // rigid body is at rest under physics for the entire disconnect window,
    // so the position should be bit-stable; allow 5 cm to absorb any
    // sleep/wake jitter.
    private const float ReclaimPositionToleranceM = 0.05f;
    // Yaw drift tolerance over the disconnect window (radians). Players are
    // upright capsules with lock_rotation=true so they should not rotate at
    // all in this scenario, but allow 2° to be safe.
    private const float ReclaimYawToleranceRad = 2f * Mathf.Pi / 180f;
    // Upper bound on max instantaneous tracking error of the OBSERVER's view
    // of the moving player vs. the server's authoritative position. The
    // dominant source of error is the velocity-reversal lag: at MoveX sign
    // flips the server-side body decelerates + reverses within one physics
    // tick (input drives PredictionRigidbody3D), while the observer's local
    // body coasts on its old velocity until the next few snapshots reconcile
    // it past the 20 cm HasMisspredicted threshold. Worst case ≈
    //   |Δv| × (RTT + reconcile-snap budget)
    // = 10 m/s × ~150 ms ≈ 1.5 m at MaxRunSpeed 5 m/s. We also enforce a
    // separate "observer must have actually travelled" lower bound below so
    // a generous tolerance here can't mask "observer body is frozen at
    // spawn" — both checks must pass for observation to be considered
    // working.
    private const float ObserverTrackingToleranceM = 1.5f;
    // Lower bound on how far the OBSERVER's view of the moving player must
    // itself travel during the sub-phase. Catches the "observer body never
    // moves at all" bug: if snapshots aren't reaching B, B's view of A
    // would just sit at spawn pose while A's owner view oscillates. A
    // properly snapshot-driven observer body covers ~the same X excursion
    // the owner does (less by snapshot lag), so requiring 50% of the
    // owner's MinMovementDistanceM threshold is a clear binary signal.
    private const float ObserverMinMovementDistanceM = 0.5f * 1.0f;
    // The MOVING side must travel at least this much during its sub-phase
    // (max X − min X). The schedule drives ±X at MaxRunSpeed 5 m/s for 0.5 s
    // half-periods → ~2.5 m per half-cycle. Asserting ≥1 m catches any bug
    // where input fails to route to the entity (e.g. reclaim handed back the
    // wrong owner, or the harness InputProducer slot was hijacked by the
    // reclaimed player's PlayerInputProducer).
    private const float MinMovementDistanceM = 1.0f;
    // The NON-MOVING side must NOT travel more than this during its
    // sub-phase. Rigid players with zero input and lock_rotation=true should
    // sleep on the floor — a stray push of more than 20 cm means input is
    // leaking between players or the scene has unexpected collisions.
    private const float MaxIdleDriftM = 0.30f;

    [TestCase]
    public void MultiProcess_TwoPlayers_PersistAcrossManualDisconnect_RetainSameEntities()
    {
        if (Orch == null) return;

        var paths = ArtifactsFor("two_players");
        string videoA = Path.Combine(paths.Directory, "two_players.playerA.mp4");
        string videoB = Path.Combine(paths.Directory, "two_players.playerB.mp4");

        using var steps = new StepLogger(paths, "two_players", "MP-PERSIST-01");

        int port = NextPort();
        steps.Log($"using shared ENet port {port}");

        // ── server + 2 clients with video recording ──────────────────────────
        steps.Log("spawning server");
        var server = Orch.Spawn("server", enetPort: port, label: "srv");
        server.WaitReady(networkReady: true, timeoutMs: 30_000);
        ServerLogPath = server.RemoteLogPath;
        steps.Log($"server ready, pid={server.RemotePid}");

        steps.Log("spawning clientA + clientB (windowed, recording video)");
        var clientA = Orch.Spawn("client", enetPort: port, label: "cA", recordVideoPath: videoA);
        var clientB = Orch.Spawn("client", enetPort: port, label: "cB", recordVideoPath: videoB);
        clientA.WaitReady(networkReady: true, timeoutMs: 30_000);
        clientB.WaitReady(networkReady: true, timeoutMs: 30_000);
        steps.Log($"clientA ready, netId={clientA.NetworkId}, pid={clientA.RemotePid}");
        steps.Log($"clientB ready, netId={clientB.NetworkId}, pid={clientB.RemotePid}");

        WaitForClockSync(server, clientA, maxGapTicks: 5, timeoutMs: 5_000);
        WaitForClockSync(server, clientB, maxGapTicks: 5, timeoutMs: 5_000);
        steps.Log("both client clocks converged to within ±5 ticks of server");

        server.WaitForTicks(SnapshotArmTicks);

        // ── spawn the two rigid players ──────────────────────────────────────
        int origNetA = clientA.NetworkId;
        int origNetB = clientB.NetworkId;
        int playerAEid = SpawnEntity(server, EntityTypeRigidPlayer, origNetA,
            PlayerAStartX, PlayerStartY, PlayerStartZ);
        int playerBEid = SpawnEntity(server, EntityTypeRigidPlayer, origNetB,
            PlayerBStartX, PlayerStartY, PlayerStartZ);
        // Rotate each body to face the other. teleport-entity zeros velocity
        // which is fine right after spawn (the body would otherwise be in
        // free-fall but the next physics tick re-applies gravity).
        FaceEntityAt(server, playerAEid, PlayerAStartX, PlayerStartY, PlayerStartZ, SpawnFacingYawA);
        FaceEntityAt(server, playerBEid, PlayerBStartX, PlayerStartY, PlayerStartZ, SpawnFacingYawB);
        steps.Log($"spawned playerA eid={playerAEid} auth={origNetA} @({PlayerAStartX},{PlayerStartY},{PlayerStartZ}) yaw={SpawnFacingYawA:F3} (facing +X)");
        steps.Log($"spawned playerB eid={playerBEid} auth={origNetB} @({PlayerBStartX},{PlayerStartY},{PlayerStartZ}) yaw={SpawnFacingYawB:F3} (facing -X)");

        WaitForClientEntity(clientA, playerAEid, timeoutMs: 5_000);
        WaitForClientEntity(clientA, playerBEid, timeoutMs: 5_000);
        WaitForClientEntity(clientB, playerAEid, timeoutMs: 5_000);
        WaitForClientEntity(clientB, playerBEid, timeoutMs: 5_000);
        steps.Log("both clients have both player entities in their local view");

        // Persistent client identities (sent in every ClientHelloMessage,
        // used as the reclaim-table key on the server). These are read
        // pre- and post-reconnect for two purposes:
        //   (a) Plot / CSV annotation — the 4-char tail matches the
        //       [cid:XXXX] tag in per-process MonkeLogger output, so the
        //       timeline can be cross-referenced against the log files.
        //   (b) Continuity assertion — they MUST be identical across
        //       disconnect/reconnect (the persistent identity NEVER
        //       rotates within a process; this is the headline contract
        //       of the new client-supplied-id design).
        string idAPreFull = ReadClientPersistentId(clientA);
        string idBPreFull = ReadClientPersistentId(clientB);
        string tokenAPre = ReadClientPersistentIdShort(clientA);
        string tokenBPre = ReadClientPersistentIdShort(clientB);
        steps.Log($"client persistent ids pre-disconnect: cA=[cid:{tokenAPre}] cB=[cid:{tokenBPre}]");

        // ── PHASE 1: sub-phase 1A then sub-phase 1B ──────────────────────────
        // Sub-phase 1A: A oscillates, B is still. Sub-phase 1B: A still, B
        // oscillates. Both clients receive a single 180-tick schedule that
        // encodes both sub-phases.
        int anchorA1 = clientA.ReadClientTick() + PlayerFallTicks;
        int anchorB1 = clientB.ReadClientTick() + PlayerFallTicks;
        InstallAlternatingSchedule(clientA, anchorA1, ownerMovesFirst: true);
        InstallAlternatingSchedule(clientB, anchorB1, ownerMovesFirst: false);
        steps.Log($"installed phase-1 alternating schedules anchorA={anchorA1} anchorB={anchorB1} " +
                  $"(A moves first 90 ticks, B moves second 90 ticks)");

        clientA.WaitForClientTick(anchorA1);
        clientB.WaitForClientTick(anchorB1);

        // Capture sampling window covering both sub-phases (each is
        // ActiveTicks of motion + SettleTicks of zero-input deceleration).
        // Anchoring on serverTick instead of clientTick gives every
        // perspective (server, cA, cB) a consistent X axis.
        int p1aStartServer = ReadServerTick(server);
        var phase1Samples = new List<TripleSample>();
        SamplePhase(server, clientA, clientB,
            startServerTick: p1aStartServer,
            durationTicks: 2 * SubPhaseTotalTicks,
            phaseTag: "P1",
            tokenA: tokenAPre, tokenB: tokenBPre,
            netA: origNetA, netB: origNetB,
            into: phase1Samples,
            steps: steps);
        // Sub-phase boundaries within the 180-tick window:
        //   p1aStartServer .. +ActiveTicks         → A moves   (P1A active)
        //   +ActiveTicks  .. +SubPhaseTotalTicks   → settle    (A decelerates)
        //   +SubPhaseTotalTicks .. +SubPhaseTotalTicks+ActiveTicks → B moves (P1B active)
        //   +SubPhaseTotalTicks+ActiveTicks .. end → settle    (B decelerates)
        int p1aActiveEnd  = p1aStartServer + ActiveTicks;
        int p1bStartServer = p1aStartServer + SubPhaseTotalTicks;
        int p1bActiveEnd  = p1bStartServer + ActiveTicks;
        int p1EndServer    = p1aStartServer + 2 * SubPhaseTotalTicks;

        // Rest before the disconnect so bodies sleep and the pre-disconnect
        // resting pose is stable for the reclaim comparison.
        InstallZeroSchedule(clientA, clientA.ReadClientTick() + 1, RestTicks);
        InstallZeroSchedule(clientB, clientB.ReadClientTick() + 1, RestTicks);
        server.WaitForTicks(RestTicks);
        steps.Log($"phase-1 done, rest period {RestTicks} ticks");

        var restingA = ReadServerEntityPose(server, playerAEid);
        var restingB = ReadServerEntityPose(server, playerBEid);
        steps.Log($"pre-disconnect resting: A pos=({restingA.Position.X:F3},{restingA.Position.Y:F3},{restingA.Position.Z:F3}) yaw={restingA.Yaw:F3} " +
                  $"B pos=({restingB.Position.X:F3},{restingB.Position.Y:F3},{restingB.Position.Z:F3}) yaw={restingB.Yaw:F3}");

        // ── DISCONNECT both clients voluntarily ──────────────────────────────
        int disconnectMarkerTick = ReadServerTick(server);
        steps.Log($"requesting voluntary disconnect on both clients at server tick {disconnectMarkerTick}");
        clientA.Send(new { cmd = "disconnect-client" });
        clientB.Send(new { cmd = "disconnect-client" });

        // Sample SERVER ONLY across the disconnect window. The clients no
        // longer have ENet peers, so their ClientEntities is cleared and
        // their sample-state would return empty entity lists anyway. The
        // intermission samples are what keeps the SVG's server / authority
        // polylines continuous through the orphan window instead of
        // interpolating diagonally to the post-reconnect points.
        var intermissionSamples = new List<TripleSample>();
        SampleServerOnly(server,
            startServerTick: disconnectMarkerTick,
            durationTicks: DisconnectSettleTicks,
            phaseTag: "DC",
            into: intermissionSamples,
            steps: steps);

        int peerCountAfterDisconnect = ReadServerPeerCount(server);
        steps.Log($"server peer-count after disconnect settle = {peerCountAfterDisconnect}");
        AssertThat(peerCountAfterDisconnect)
            .OverrideFailureMessage(
                $"server should report 0 connected peers after both clients disconnected; got {peerCountAfterDisconnect}. " +
                $"Manual disconnect must take effect within {DisconnectSettleTicks} server ticks.")
            .IsEqual(0);

        var serverEntsAfterDisconnect = ReadServerEntities(server);
        steps.Log($"server entities post-disconnect: {serverEntsAfterDisconnect.Count}");
        AssertThat(serverEntsAfterDisconnect.Count)
            .OverrideFailureMessage(
                $"server should retain both player entities after voluntary disconnect (ManualDisconnectMode = KeepEntity); " +
                $"got {serverEntsAfterDisconnect.Count}. Entity-persistence regression.")
            .IsEqual(2);
        foreach (var ent in serverEntsAfterDisconnect)
        {
            AssertThat(ent.Authority)
                .OverrideFailureMessage(
                    $"orphaned entity {ent.Id} should have Authority=0 after both owners disconnected; got {ent.Authority}")
                .IsEqual(0);
        }

        // Both clients should be in the "awaiting reconnect" state — the
        // sticky flag set by ClientEntityManager.OnServerDisconnected and
        // cleared on the next OnNetworkReady. This is the demo-UI signal
        // (gates the Reconnect button + camera-pose preservation); the
        // persistent identity itself stays valid regardless and would be
        // sent on the next hello whether or not this flag was set.
        AssertThat(ReadIsAwaitingReconnect(clientA))
            .OverrideFailureMessage("clientA must report IsAwaitingReconnect=true after voluntary disconnect")
            .IsTrue();
        AssertThat(ReadIsAwaitingReconnect(clientB))
            .OverrideFailureMessage("clientB must report IsAwaitingReconnect=true after voluntary disconnect")
            .IsTrue();
        // Server-side: there must be a parked reclaim entry keyed by each
        // client's persistent id, ready to be picked up by the next hello.
        AssertThat(ReadHasPendingReclaimFor(server, idAPreFull))
            .OverrideFailureMessage($"server should have a pending reclaim entry for clientA [cid:{tokenAPre}]")
            .IsTrue();
        AssertThat(ReadHasPendingReclaimFor(server, idBPreFull))
            .OverrideFailureMessage($"server should have a pending reclaim entry for clientB [cid:{tokenBPre}]")
            .IsTrue();

        // ── RECONNECT both clients ───────────────────────────────────────────
        int reconnectMarkerTick = ReadServerTick(server);
        steps.Log($"reconnecting both clients at server tick {reconnectMarkerTick}");
        clientA.Send(new { cmd = "reconnect-client" });
        clientB.Send(new { cmd = "reconnect-client" });

        clientA.WaitReady(networkReady: true, timeoutMs: 30_000);
        clientB.WaitReady(networkReady: true, timeoutMs: 30_000);
        WaitForClockSync(server, clientA, maxGapTicks: 5, timeoutMs: 5_000);
        WaitForClockSync(server, clientB, maxGapTicks: 5, timeoutMs: 5_000);
        server.WaitForTicks(ReconnectSettleTicks);
        steps.Log("reconnect handshake complete, clocks resynced");

        int newNetA = clientA.NetworkId;
        int newNetB = clientB.NetworkId;
        string idAPostFull = ReadClientPersistentId(clientA);
        string idBPostFull = ReadClientPersistentId(clientB);
        string tokenAPost = ReadClientPersistentIdShort(clientA);
        string tokenBPost = ReadClientPersistentIdShort(clientB);
        steps.Log($"post-reconnect netIds: cA {origNetA}→{newNetA}, cB {origNetB}→{newNetB}");
        steps.Log($"post-reconnect client persistent ids: cA [cid:{tokenAPre}]→[cid:{tokenAPost}], cB [cid:{tokenBPre}]→[cid:{tokenBPost}]");

        // Identity continuity — the central contract of the new design.
        // The client's persistent id is generated once (here: passed in via
        // --client-id by MultiProcessOrchestrator) and SHOULD survive every
        // disconnect/reconnect cycle within a process. If these aren't
        // bit-identical the implementation has regressed — the server's
        // reclaim lookup is keyed by this string, and any rotation would
        // break the reclaim chain.
        AssertThat(idAPostFull)
            .OverrideFailureMessage(
                $"clientA persistent id must be IDENTICAL across reconnect; pre={idAPreFull} post={idAPostFull}. " +
                $"The whole point of ClientPersistentIdentity is to not rotate.")
            .IsEqual(idAPreFull);
        AssertThat(idBPostFull)
            .OverrideFailureMessage(
                $"clientB persistent id must be IDENTICAL across reconnect; pre={idBPreFull} post={idBPostFull}")
            .IsEqual(idBPreFull);

        // Now that the hello has been processed, the server should no
        // longer have a parked reclaim entry for either identity — the
        // entries were consumed by the reclaim lookup and the entities
        // are back to being live-owned.
        AssertThat(ReadHasPendingReclaimFor(server, idAPreFull))
            .OverrideFailureMessage($"server should have CONSUMED clientA's reclaim entry [cid:{tokenAPre}] on hello; it's still pending")
            .IsFalse();
        AssertThat(ReadHasPendingReclaimFor(server, idBPreFull))
            .OverrideFailureMessage($"server should have CONSUMED clientB's reclaim entry [cid:{tokenBPre}] on hello; it's still pending")
            .IsFalse();
        // And the awaiting-reconnect flag should have cleared on both
        // clients once NetworkReady fired post-reconnect.
        AssertThat(ReadIsAwaitingReconnect(clientA))
            .OverrideFailureMessage("clientA IsAwaitingReconnect must clear after reconnect handshake")
            .IsFalse();
        AssertThat(ReadIsAwaitingReconnect(clientB))
            .OverrideFailureMessage("clientB IsAwaitingReconnect must clear after reconnect handshake")
            .IsFalse();

        int peerCountAfterReconnect = ReadServerPeerCount(server);
        AssertThat(peerCountAfterReconnect)
            .OverrideFailureMessage($"server should see 2 peers after reconnect; got {peerCountAfterReconnect}")
            .IsEqual(2);

        // ── reclaim verification: same entity, new owner ─────────────────────
        var serverEntsAfterReconnect = ReadServerEntities(server);
        AssertThat(serverEntsAfterReconnect.Count)
            .OverrideFailureMessage($"server should still have 2 player entities post-reconnect; got {serverEntsAfterReconnect.Count}")
            .IsEqual(2);

        var reclaimedA = serverEntsAfterReconnect.Find(e => e.Id == playerAEid);
        var reclaimedB = serverEntsAfterReconnect.Find(e => e.Id == playerBEid);
        AssertThat(reclaimedA)
            .OverrideFailureMessage($"player A's pre-disconnect entity id {playerAEid} was destroyed across the disconnect/reconnect cycle")
            .IsNotNull();
        AssertThat(reclaimedB)
            .OverrideFailureMessage($"player B's pre-disconnect entity id {playerBEid} was destroyed across the disconnect/reconnect cycle")
            .IsNotNull();

        AssertThat(reclaimedA.Authority)
            .OverrideFailureMessage(
                $"player A reclaim failed: entity {playerAEid} authority is {reclaimedA.Authority}, " +
                $"expected the new clientA networkId {newNetA}. Reclaim handshake did NOT reassign ownership.")
            .IsEqual(newNetA);
        AssertThat(reclaimedB.Authority)
            .OverrideFailureMessage(
                $"player B reclaim failed: entity {playerBEid} authority is {reclaimedB.Authority}, " +
                $"expected the new clientB networkId {newNetB}. Reclaim handshake did NOT reassign ownership.")
            .IsEqual(newNetB);

        // ── pose continuity: same position + yaw as pre-disconnect ───────────
        var spawnPoseA = ReadServerEntityPose(server, playerAEid);
        var spawnPoseB = ReadServerEntityPose(server, playerBEid);
        float poseDriftA = (spawnPoseA.Position - restingA.Position).Length();
        float poseDriftB = (spawnPoseB.Position - restingB.Position).Length();
        float yawDriftA = Mathf.Abs(WrapAngle(spawnPoseA.Yaw - restingA.Yaw));
        float yawDriftB = Mathf.Abs(WrapAngle(spawnPoseB.Yaw - restingB.Yaw));
        steps.Log($"reclaim pose continuity: A drift {poseDriftA*100:F1} cm yaw {yawDriftA*180/Mathf.Pi:F2}°, " +
                  $"B drift {poseDriftB*100:F1} cm yaw {yawDriftB*180/Mathf.Pi:F2}°");
        AssertThat(poseDriftA)
            .OverrideFailureMessage(
                $"player A position drifted {poseDriftA*100:F2} cm across disconnect/reconnect " +
                $"(pre={restingA.Position} post={spawnPoseA.Position}), max {ReclaimPositionToleranceM*100} cm. " +
                $"Reclaim should restore the exact pose the body was at when the owner disconnected.")
            .IsLessEqual(ReclaimPositionToleranceM);
        AssertThat(poseDriftB).OverrideFailureMessage(
                $"player B position drifted {poseDriftB*100:F2} cm across disconnect/reconnect")
            .IsLessEqual(ReclaimPositionToleranceM);
        AssertThat(yawDriftA).OverrideFailureMessage(
                $"player A yaw drifted {yawDriftA*180/Mathf.Pi:F2}° across reconnect (max {ReclaimYawToleranceRad*180/Mathf.Pi:F2}°)")
            .IsLessEqual(ReclaimYawToleranceRad);
        AssertThat(yawDriftB).OverrideFailureMessage(
                $"player B yaw drifted {yawDriftB*180/Mathf.Pi:F2}° across reconnect")
            .IsLessEqual(ReclaimYawToleranceRad);

        // ── PHASE 2: alternating-movement post-reclaim ───────────────────────
        int anchorA2 = clientA.ReadClientTick() + 30;
        int anchorB2 = clientB.ReadClientTick() + 30;
        InstallAlternatingSchedule(clientA, anchorA2, ownerMovesFirst: true);
        InstallAlternatingSchedule(clientB, anchorB2, ownerMovesFirst: false);
        steps.Log($"installed phase-2 alternating schedules anchorA={anchorA2} anchorB={anchorB2}");

        clientA.WaitForClientTick(anchorA2);
        clientB.WaitForClientTick(anchorB2);

        int p2aStartServer = ReadServerTick(server);
        var phase2Samples = new List<TripleSample>();
        SamplePhase(server, clientA, clientB,
            startServerTick: p2aStartServer,
            durationTicks: 2 * SubPhaseTotalTicks,
            phaseTag: "P2",
            tokenA: tokenAPost, tokenB: tokenBPost,
            netA: newNetA, netB: newNetB,
            into: phase2Samples,
            steps: steps);
        int p2aActiveEnd  = p2aStartServer + ActiveTicks;
        int p2bStartServer = p2aStartServer + SubPhaseTotalTicks;
        int p2bActiveEnd  = p2bStartServer + ActiveTicks;
        int p2EndServer    = p2aStartServer + 2 * SubPhaseTotalTicks;

        // ── sequential-control assertions per sub-phase ──────────────────────
        // Each sub-phase asserts:
        //   • moving side's owner-view moved ≥ MinMovementDistanceM on X
        //   • moving side's observer-view (other client) tracked server pose
        //   • non-moving side stayed within MaxIdleDriftM (both perspectives)
        // Tick window for each is the ACTIVE portion only (excludes the
        // settle window where the previously-moving body decelerates).
        AssertSubPhase("phase1A", phase1Samples, p1aStartServer, p1aActiveEnd,
            movingEid: playerAEid, movingLabel: "playerA", movingOwnerIsA: true,
            stillEid: playerBEid, stillLabel: "playerB", stillOwnerIsA: false);
        AssertSubPhase("phase1B", phase1Samples, p1bStartServer, p1bActiveEnd,
            movingEid: playerBEid, movingLabel: "playerB", movingOwnerIsA: false,
            stillEid: playerAEid, stillLabel: "playerA", stillOwnerIsA: true);
        AssertSubPhase("phase2A", phase2Samples, p2aStartServer, p2aActiveEnd,
            movingEid: playerAEid, movingLabel: "playerA", movingOwnerIsA: true,
            stillEid: playerBEid, stillLabel: "playerB", stillOwnerIsA: false);
        AssertSubPhase("phase2B", phase2Samples, p2bStartServer, p2bActiveEnd,
            movingEid: playerBEid, movingLabel: "playerB", movingOwnerIsA: false,
            stillEid: playerAEid, stillLabel: "playerA", stillOwnerIsA: true);

        // ── write artefacts ──────────────────────────────────────────────────
        WriteTimelineCsv(paths.Csv, phase1Samples, intermissionSamples, phase2Samples,
            playerAEid, playerBEid,
            origNetA, origNetB, newNetA, newNetB,
            tokenAPre, tokenBPre, tokenAPost, tokenBPost);
        WriteTimelineSvg(paths.Svg,
            phase1Samples, intermissionSamples, phase2Samples,
            playerAEid, playerBEid,
            p1aStartServer, p1aActiveEnd, p1bStartServer, p1bActiveEnd, p1EndServer,
            disconnectMarkerTick, reconnectMarkerTick,
            p2aStartServer, p2aActiveEnd, p2bStartServer, p2bActiveEnd, p2EndServer,
            tokenAPre, tokenBPre, tokenAPost, tokenBPost);
        CopyProcessLog(paths.Directory, server.RemoteLogPath, "two_players.server.log");
        CopyProcessLog(paths.Directory, clientA.RemoteLogPath, "two_players.clientA.log");
        CopyProcessLog(paths.Directory, clientB.RemoteLogPath, "two_players.clientB.log");
        steps.Log("artifacts written");
    }

    // ── helpers: schedules ───────────────────────────────────────────────────

    // 180-tick alternating schedule. <ownerMovesFirst>=true installs:
    //   [0,   ActiveTicks):           square-wave moveX
    //   [ActiveTicks, SubPhaseTotal): zero (settle — let body decelerate)
    //   [SubPhaseTotal, end):         zero (the OTHER side's active window)
    // <ownerMovesFirst>=false flips the order. Both clients receive a
    // 180-tick schedule; in any tick at most one client is driving moveX.
    // The settle window between sub-phases lets the moving body come to
    // rest before the other side starts so residual velocity doesn't
    // pollute the "still side didn't move" assertion (and so the video
    // reads as clean alternation rather than overlap).
    private static void InstallAlternatingSchedule(TestProcess client, int anchorTick, bool ownerMovesFirst)
    {
        var schedule = new List<object>
        {
            new { tick = anchorTick - 1, moveX = 0.0, moveY = 0.0, yaw = 0.0, keys = 0 },
        };
        int totalTicks = 2 * SubPhaseTotalTicks;
        for (int t = 0; t < totalTicks; t += OscillationPeriodTicks)
        {
            // This client moves only during its OWN active window.
            // ownerMovesFirst → active window is [0, ActiveTicks).
            // !ownerMovesFirst → active window is [SubPhaseTotalTicks,
            //                    SubPhaseTotalTicks + ActiveTicks).
            int activeStart = ownerMovesFirst ? 0 : SubPhaseTotalTicks;
            int activeEnd = activeStart + ActiveTicks;
            bool inActive = (t >= activeStart) && (t < activeEnd);
            int half = (t - activeStart) / OscillationPeriodTicks;
            float moveX = inActive ? (half % 2 == 0 ? +1f : -1f) : 0f;
            schedule.Add(new
            {
                tick = anchorTick + t,
                moveX = (double)moveX,
                moveY = 0.0,
                yaw = 0.0,
                keys = 0,
            });
        }
        schedule.Add(new { tick = anchorTick + totalTicks, moveX = 0.0, moveY = 0.0, yaw = 0.0, keys = 0 });
        client.Send(new { cmd = "set-input-schedule", entries = schedule });
    }

    // Held-zero schedule for the rest window between phases. Bookended with
    // two entries so HarnessInputProducer's "last entry ≤ now" lookup always
    // returns zero within the window.
    private static void InstallZeroSchedule(TestProcess client, int anchorTick, int runTicks)
    {
        client.Send(new
        {
            cmd = "set-input-schedule",
            entries = new[]
            {
                new { tick = anchorTick, moveX = 0.0, moveY = 0.0, yaw = 0.0, keys = 0 },
                new { tick = anchorTick + runTicks, moveX = 0.0, moveY = 0.0, yaw = 0.0, keys = 0 },
            },
        });
    }

    // ── helpers: spawn + face ────────────────────────────────────────────────

    // Apply a yaw rotation (Y-axis Euler radians) to a server-side rigid
    // entity. Uses teleport-entity because spawn-entity doesn't carry a yaw
    // field. Velocity is zeroed by teleport-entity, which is intentional
    // here: it's called right after spawn so the body hasn't been integrated
    // yet, and zeroing is a no-op.
    private static void FaceEntityAt(TestProcess server, int eid, float x, float y, float z, float yaw)
    {
        server.Send(new
        {
            cmd = "teleport-entity",
            entity_id = eid,
            position = new[] { (double)x, y, z },
            yaw = (double)yaw,
        });
    }

    // ── helpers: sampling ────────────────────────────────────────────────────

    // Sample (server, clientA, clientB) state from <startServerTick> for
    // <durationTicks> ticks at SnapshotIntervalTicks cadence. All three
    // perspectives use the same target tick so the SVG X axis aligns
    // cleanly across processes.
    private void SamplePhase(TestProcess server, TestProcess clientA, TestProcess clientB,
        int startServerTick, int durationTicks, string phaseTag,
        string tokenA, string tokenB, int netA, int netB,
        List<TripleSample> into, StepLogger steps)
    {
        for (int t = SnapshotIntervalTicks; t <= durationTicks; t += SnapshotIntervalTicks)
        {
            int targetServerTick = startServerTick + t;
            WaitForServerTick(server, targetServerTick);
            into.Add(new TripleSample
            {
                Tick = targetServerTick,
                Phase = phaseTag,
                TokenA = tokenA,
                TokenB = tokenB,
                NetIdA = netA,
                NetIdB = netB,
                Server = CaptureSample(server, targetServerTick),
                ClientA = CaptureSample(clientA, targetServerTick),
                ClientB = CaptureSample(clientB, targetServerTick),
            });
        }
        steps.Log($"captured {into.Count} samples in phase {phaseTag} " +
                  $"(server ticks {startServerTick}..{startServerTick + durationTicks})");
    }

    // Server-only sampling for the intermission window when both clients are
    // disconnected. ClientA/ClientB fields are populated with empty Samples
    // (no entities) so the CSV writer and SVG plotter can treat the entry
    // uniformly with the connected-phase samples.
    private void SampleServerOnly(TestProcess server,
        int startServerTick, int durationTicks, string phaseTag,
        List<TripleSample> into, StepLogger steps)
    {
        var emptySample = new Sample { Tick = 0, Entities = new List<EntityState>() };
        for (int t = SnapshotIntervalTicks; t <= durationTicks; t += SnapshotIntervalTicks)
        {
            int targetServerTick = startServerTick + t;
            WaitForServerTick(server, targetServerTick);
            into.Add(new TripleSample
            {
                Tick = targetServerTick,
                Phase = phaseTag,
                TokenA = null,
                TokenB = null,
                NetIdA = 0,
                NetIdB = 0,
                Server = CaptureSample(server, targetServerTick),
                ClientA = emptySample,
                ClientB = emptySample,
            });
        }
        steps.Log($"captured {into.Count} server-only samples in phase {phaseTag} " +
                  $"(server ticks {startServerTick}..{startServerTick + durationTicks})");
    }

    private static void WaitForServerTick(TestProcess server, int targetTick, int timeoutMs = 30_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        int last = 0;
        while (DateTime.UtcNow < deadline)
        {
            last = ReadServerTick(server);
            if (last >= targetTick) return;
            Thread.Sleep(5);
        }
        throw new TimeoutException($"server tick {targetTick} not reached within {timeoutMs} ms (last={last})");
    }

    private static int ReadServerTick(TestProcess server)
    {
        using var doc = server.Send(new { cmd = "clock-state" });
        return doc.RootElement.GetProperty("data").GetProperty("serverTick").GetInt32();
    }

    // ── helpers: server / client side-channel reads ──────────────────────────

    private static int ReadServerPeerCount(TestProcess server)
    {
        using var doc = server.Send(new { cmd = "server-peer-count" });
        return doc.RootElement.GetProperty("data").GetProperty("count").GetInt32();
    }

    private static List<ServerEntity> ReadServerEntities(TestProcess server)
    {
        using var doc = server.Send(new { cmd = "get-all-entities" });
        var arr = doc.RootElement.GetProperty("data").GetProperty("entities");
        var result = new List<ServerEntity>();
        foreach (var el in arr.EnumerateArray())
        {
            var pos = el.GetProperty("position");
            result.Add(new ServerEntity
            {
                Id = el.GetProperty("id").GetInt32(),
                Type = el.GetProperty("type").GetByte(),
                Authority = el.GetProperty("authority").GetInt32(),
                Position = new Vector3((float)pos[0].GetDouble(), (float)pos[1].GetDouble(), (float)pos[2].GetDouble()),
            });
        }
        return result;
    }

    private static EntityPose ReadServerEntityPose(TestProcess server, int eid)
    {
        var s = CaptureSample(server, sampleTick: 0);
        foreach (var e in s.Entities)
        {
            if (e.Id == eid)
            {
                return new EntityPose
                {
                    Position = e.Position,
                    Velocity = e.Velocity,
                    Yaw = e.Rotation.GetEuler().Y,
                };
            }
        }
        throw new InvalidOperationException($"server-side entity {eid} not found in sample-state");
    }

    // The full ClientPersistentIdentity (server-side reclaim key). Pulled
    // both pre- and post-reconnect to assert identity continuity (same
    // string across the disconnect/reconnect cycle).
    private static string ReadClientPersistentId(TestProcess client)
    {
        using var doc = client.Send(new { cmd = "client-persistent-id" });
        var data = doc.RootElement.GetProperty("data");
        if (!data.TryGetProperty("id", out var t)) return null;
        return t.ValueKind == System.Text.Json.JsonValueKind.String ? t.GetString() : null;
    }

    // 4-char tail of the persistent identity, matching the [cid:XXXX] tag
    // used by ServerConnectionMonitor's logs.
    private static string ReadClientPersistentIdShort(TestProcess client)
    {
        using var doc = client.Send(new { cmd = "client-persistent-id" });
        var data = doc.RootElement.GetProperty("data");
        if (!data.TryGetProperty("idShort", out var t)) return null;
        return t.ValueKind == System.Text.Json.JsonValueKind.String ? t.GetString() : null;
    }

    // True between observed server-disconnect and next NetworkReady. Used
    // by the test to verify the same-process flag flips correctly across
    // the reconnect cycle.
    private static bool ReadIsAwaitingReconnect(TestProcess client)
    {
        using var doc = client.Send(new { cmd = "client-persistent-id" });
        return doc.RootElement.GetProperty("data").GetProperty("isAwaitingReconnect").GetBoolean();
    }

    // Asks the server whether it currently holds a parked reclaim entry
    // for a given ClientPersistentId. True immediately after the matching
    // identity has disconnected (KeepEntity mode); false once that
    // identity reconnects and the entry is consumed by the hello-driven
    // reclaim lookup.
    private static bool ReadHasPendingReclaimFor(TestProcess server, string clientId)
    {
        using var doc = server.Send(new { cmd = "pending-reclaim-for", client_id = clientId });
        return doc.RootElement.GetProperty("data").GetProperty("hasPending").GetBoolean();
    }

    // ── helpers: sub-phase assertions ────────────────────────────────────────

    private static void AssertSubPhase(string subPhaseLabel, List<TripleSample> samples,
        int startServerTick, int endServerTick,
        int movingEid, string movingLabel, bool movingOwnerIsA,
        int stillEid, string stillLabel, bool stillOwnerIsA)
    {
        // Filter samples whose tick falls in [start, end). Both sub-phases
        // (A-moves, B-moves) come from the same phase samples list — we slice
        // by tick instead of capturing two lists so the SVG sees one
        // continuous server line.
        var window = new List<TripleSample>();
        foreach (var s in samples)
            if (s.Tick >= startServerTick && s.Tick < endServerTick) window.Add(s);

        AssertThat(window.Count)
            .OverrideFailureMessage($"{subPhaseLabel}: no samples in tick window [{startServerTick}, {endServerTick})")
            .IsGreater(0);

        Func<TripleSample, Sample> movingOwner = movingOwnerIsA ? (s => s.ClientA) : (s => s.ClientB);
        Func<TripleSample, Sample> movingObserver = movingOwnerIsA ? (s => s.ClientB) : (s => s.ClientA);
        Func<TripleSample, Sample> stillOwner = stillOwnerIsA ? (s => s.ClientA) : (s => s.ClientB);
        Func<TripleSample, Sample> stillObserver = stillOwnerIsA ? (s => s.ClientB) : (s => s.ClientA);
        string movingOwnerLabel = movingOwnerIsA ? "clientA" : "clientB";
        string movingObserverLabel = movingOwnerIsA ? "clientB" : "clientA";
        string stillOwnerLabel = stillOwnerIsA ? "clientA" : "clientB";
        string stillObserverLabel = stillOwnerIsA ? "clientB" : "clientA";

        // 1) The moving side's owner view must travel ≥ MinMovementDistanceM
        //    on X (oscillating around the spawn pose).
        float ownerTravel = TravelOnX(window, movingOwner, movingEid);
        AssertThat(ownerTravel)
            .OverrideFailureMessage(
                $"{subPhaseLabel}: moving {movingLabel} ({movingOwnerLabel} owner view) only travelled {ownerTravel:F3} m on X; " +
                $"expected ≥ {MinMovementDistanceM:F2} m. Input schedule is not driving the entity.")
            .IsGreaterEqual(MinMovementDistanceM);

        // 2a) The moving side's observer view (the OTHER client's snapshot
        //     view of that same entity) must itself travel — proves
        //     snapshots are reaching the observer, not just that they're
        //     near the server pose at start of phase. A frozen observer
        //     body could pass a tracking-error check at the start of
        //     motion and fail to show any movement at all.
        float obsTravel = TravelOnX(window, movingObserver, movingEid);
        AssertThat(obsTravel)
            .OverrideFailureMessage(
                $"{subPhaseLabel}: observer ({movingObserverLabel}) view of {movingLabel} only travelled {obsTravel:F3} m on X; " +
                $"expected ≥ {ObserverMinMovementDistanceM:F2} m. {movingObserverLabel} is not receiving snapshot updates for {movingLabel}.")
            .IsGreaterEqual(ObserverMinMovementDistanceM);

        // 2b) The observer view must also stay within ObserverTrackingToleranceM
        //     of the server's authoritative pose at every sample. Tolerance
        //     is generous (1.5 m) because the worst-case velocity-reversal
        //     lag at MaxRunSpeed is intrinsically ≈ 1 m before reconcile
        //     snaps the observer body back; this check fails on grosser
        //     bugs (snapshot stream broken, observer rendering stale data
        //     by ~seconds, etc.) rather than on transient catch-up.
        float maxObsError = 0f;
        int worstTick = 0;
        foreach (var s in window)
        {
            var serverE = FindEntity(s.Server, movingEid);
            var obsE = FindEntity(movingObserver(s), movingEid);
            if (serverE == null || obsE == null) continue;
            float err = (serverE.Position - obsE.Position).Length();
            if (err > maxObsError) { maxObsError = err; worstTick = s.Tick; }
        }
        AssertThat(maxObsError)
            .OverrideFailureMessage(
                $"{subPhaseLabel}: observer ({movingObserverLabel}) view of {movingLabel} diverged by {maxObsError*100:F1} cm " +
                $"from the server at server tick {worstTick} (max allowed {ObserverTrackingToleranceM*100:F0} cm). " +
                $"Tracking lag exceeds the velocity-reversal-budget for MaxRunSpeed motion — snapshot stream is degraded.")
            .IsLessEqual(ObserverTrackingToleranceM);

        // 3) The non-moving side's owner view must travel ≤ MaxIdleDriftM —
        //    no input means rigid body sleeps on the floor. A non-trivial
        //    excursion here means inputs are leaking across players.
        float stillOwnerTravel = TravelOnX(window, stillOwner, stillEid);
        AssertThat(stillOwnerTravel)
            .OverrideFailureMessage(
                $"{subPhaseLabel}: still {stillLabel} ({stillOwnerLabel} owner view) drifted {stillOwnerTravel:F3} m on X; " +
                $"max allowed {MaxIdleDriftM:F2} m. Input from the moving player is leaking onto the still one.")
            .IsLessEqual(MaxIdleDriftM);

        // 4) The non-moving side's observer view should also be stationary.
        //    Belt-and-braces — catches a server-side bug where the still
        //    entity drifts in its own snapshot stream.
        float stillObsTravel = TravelOnX(window, stillObserver, stillEid);
        AssertThat(stillObsTravel)
            .OverrideFailureMessage(
                $"{subPhaseLabel}: still {stillLabel} ({stillObserverLabel} observer view) drifted {stillObsTravel:F3} m on X; " +
                $"max allowed {MaxIdleDriftM:F2} m.")
            .IsLessEqual(MaxIdleDriftM);
    }

    private static float TravelOnX(List<TripleSample> window, Func<TripleSample, Sample> source, int eid)
    {
        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
        foreach (var s in window)
        {
            var e = FindEntity(source(s), eid);
            if (e == null) continue;
            if (e.Position.X < minX) minX = e.Position.X;
            if (e.Position.X > maxX) maxX = e.Position.X;
        }
        return (minX == float.PositiveInfinity) ? 0f : (maxX - minX);
    }

    private static EntityState FindEntity(Sample s, int eid)
    {
        foreach (var e in s.Entities)
            if (e.Id == eid) return e;
        return null;
    }

    private static float WrapAngle(float a)
    {
        a = (float)Math.IEEERemainder(a, Math.Tau);
        if (a > Mathf.Pi) a -= Mathf.Tau;
        if (a < -Mathf.Pi) a += Mathf.Tau;
        return a;
    }

    // ── helpers: CSV/SVG writers ─────────────────────────────────────────────

    private static void WriteTimelineCsv(string path,
        List<TripleSample> phase1, List<TripleSample> intermission, List<TripleSample> phase2,
        int playerAEid, int playerBEid,
        int origNetA, int origNetB, int newNetA, int newNetB,
        string tokenAPre, string tokenBPre, string tokenAPost, string tokenBPost)
    {
        var sb = new StringBuilder();
        sb.Append("tick,t_s,phase,role,networkId,sessionToken,eid,authority,x,y,z,vx,vy,vz,yaw\n");
        AppendPhase(sb, phase1, srvHasClients: true);
        AppendPhase(sb, intermission, srvHasClients: false);
        AppendPhase(sb, phase2, srvHasClients: true);
        File.WriteAllText(path, sb.ToString());

        void AppendPhase(StringBuilder b, List<TripleSample> samples, bool srvHasClients)
        {
            foreach (var t in samples)
            {
                AppendProcess(b, t, "server", networkId: 1, sessionToken: "----", t.Server);
                if (srvHasClients)
                {
                    AppendProcess(b, t, "clientA",
                        networkId: NetForPhase(t.Phase, origNetA, newNetA),
                        sessionToken: TokForPhase(t.Phase, tokenAPre, tokenAPost),
                        t.ClientA);
                    AppendProcess(b, t, "clientB",
                        networkId: NetForPhase(t.Phase, origNetB, newNetB),
                        sessionToken: TokForPhase(t.Phase, tokenBPre, tokenBPost),
                        t.ClientB);
                }
            }
        }

        void AppendProcess(StringBuilder b, TripleSample t, string role, int networkId, string sessionToken, Sample s)
        {
            string tickStr = t.Tick.ToString(CultureInfo.InvariantCulture);
            string tStr = (t.Tick / 60.0).ToString("F4", CultureInfo.InvariantCulture);
            foreach (var e in s.Entities)
            {
                if (e.Id != playerAEid && e.Id != playerBEid) continue;
                float yaw = e.Rotation.GetEuler().Y;
                b.Append(tickStr).Append(',').Append(tStr).Append(',')
                 .Append(t.Phase).Append(',').Append(role).Append(',')
                 .Append(networkId.ToString(CultureInfo.InvariantCulture)).Append(',')
                 .Append(sessionToken ?? "").Append(',')
                 .Append(e.Id).Append(',').Append(e.Authority).Append(',')
                 .Append(F(e.Position.X)).Append(',').Append(F(e.Position.Y)).Append(',').Append(F(e.Position.Z)).Append(',')
                 .Append(F(e.Velocity.X)).Append(',').Append(F(e.Velocity.Y)).Append(',').Append(F(e.Velocity.Z)).Append(',')
                 .Append(F(yaw))
                 .Append('\n');
            }
        }

        static string F(float v) => v.ToString("F6", CultureInfo.InvariantCulture);
        static int NetForPhase(string phase, int pre, int post) => phase == "P1" ? pre : post;
        static string TokForPhase(string phase, string pre, string post) => phase == "P1" ? pre : post;
    }

    private static void WriteTimelineSvg(string path,
        List<TripleSample> phase1, List<TripleSample> intermission, List<TripleSample> phase2,
        int playerAEid, int playerBEid,
        int p1aStart, int p1aActiveEnd, int p1bStart, int p1bActiveEnd, int p1End,
        int disconnectTick, int reconnectTick,
        int p2aStart, int p2aActiveEnd, int p2bStart, int p2bActiveEnd, int p2End,
        string tokenAPre, string tokenBPre, string tokenAPost, string tokenBPost)
    {
        var plot = new SvgPlot(
            $"two_players persistence — tokens cA:{tokenAPre}→{tokenAPost}  cB:{tokenBPre}→{tokenBPost}")
        {
            Width = 1200,
            PanelHeight = 160,
        };

        var panelA = plot.AddPanel("player A — world X (m)  (only A's owner is driving input in 1A / 2A)", yUnits: "m");
        // Server is sampled across phase1, intermission, AND phase2 — one
        // continuous polyline through everything (including the orphan
        // window where Authority drops to 0 but the body remains where it
        // came to rest).
        AddCombinedServerSeries(panelA, phase1, intermission, phase2, playerAEid,
            $"server eid={playerAEid}", SvgPlot.Palette.Series[0]);
        // Client-A as owner of player A: split into two same-labelled
        // polylines (phase1, phase2) so the SVG doesn't draw a diagonal
        // line across the disconnect interval where no client samples exist.
        // SvgPlot's legend dedupes by label.
        AddClientSeriesSplit(panelA, phase1, phase2, playerAEid, t => t.ClientA,
            $"clientA (owner) eid={playerAEid}", SvgPlot.Palette.Series[1]);
        AddClientSeriesSplit(panelA, phase1, phase2, playerAEid, t => t.ClientB,
            $"clientB (observer) eid={playerAEid}", SvgPlot.Palette.Series[2], dashed: true);

        var panelB = plot.AddPanel("player B — world X (m)  (only B's owner is driving input in 1B / 2B)", yUnits: "m");
        AddCombinedServerSeries(panelB, phase1, intermission, phase2, playerBEid,
            $"server eid={playerBEid}", SvgPlot.Palette.Series[3]);
        AddClientSeriesSplit(panelB, phase1, phase2, playerBEid, t => t.ClientB,
            $"clientB (owner) eid={playerBEid}", SvgPlot.Palette.Series[4]);
        AddClientSeriesSplit(panelB, phase1, phase2, playerBEid, t => t.ClientA,
            $"clientA (observer) eid={playerBEid}", SvgPlot.Palette.Series[5], dashed: true);

        // Authority panel: server's view of each entity's owner over time.
        // Drops to 0 during the orphan window between disconnect and
        // reconnect, then steps back to the (new) clientNetId once the
        // reclaim handshake completes. Combined across all three sample
        // lists so the line is continuous through every transition.
        var panelAuth = plot.AddPanel("server-reported Authority per entity (0 = orphaned)", yUnits: "netId");
        AddCombinedAuthoritySeries(panelAuth, phase1, intermission, phase2, playerAEid,
            $"auth(eid={playerAEid})", SvgPlot.Palette.Series[6]);
        AddCombinedAuthoritySeries(panelAuth, phase1, intermission, phase2, playerBEid,
            $"auth(eid={playerBEid})", SvgPlot.Palette.Series[7]);

        // Vertical markers for every phase boundary. Within each phase the
        // active window (movement) is followed by a settle window (both
        // sides zero input) so the moving body decelerates before the
        // other side starts. The "active-end" markers delimit the active
        // window from the settle gap.
        plot.AddVerticalMarker(p1aActiveEnd, "P1A active end");
        plot.AddVerticalMarker(p1bStart, "P1B: B moves");
        plot.AddVerticalMarker(p1bActiveEnd, "P1B active end");
        plot.AddVerticalMarker(p1End, "rest");
        plot.AddVerticalMarker(disconnectTick, "disconnect");
        plot.AddVerticalMarker(reconnectTick, "reconnect");
        plot.AddVerticalMarker(p2aStart, "P2A: A moves");
        plot.AddVerticalMarker(p2aActiveEnd, "P2A active end");
        plot.AddVerticalMarker(p2bStart, "P2B: B moves");
        plot.AddVerticalMarker(p2bActiveEnd, "P2B active end");

        plot.Save(path);
    }

    // Server samples are continuous across all three lists (phase1,
    // intermission, phase2). One polyline through everything.
    private static void AddCombinedServerSeries(SvgPlot.Panel panel,
        List<TripleSample> phase1, List<TripleSample> intermission, List<TripleSample> phase2,
        int eid, string label, string color)
    {
        var pts = new List<(int, float)>();
        Append(phase1); Append(intermission); Append(phase2);
        panel.AddSeries(label, color, pts);

        void Append(List<TripleSample> src)
        {
            foreach (var t in src)
            {
                var e = FindEntity(t.Server, eid);
                if (e != null) pts.Add((t.Tick, e.Position.X));
            }
        }
    }

    // Client samples skip the intermission. Splitting into two polylines
    // (with the same label so the legend dedupes) makes the SVG render the
    // pre- and post-reconnect traces as separate strokes — no diagonal line
    // bridging the disconnect window.
    private static void AddClientSeriesSplit(SvgPlot.Panel panel,
        List<TripleSample> phase1, List<TripleSample> phase2,
        int eid, Func<TripleSample, Sample> source, string label, string color, bool dashed = false)
    {
        var pts1 = new List<(int, float)>();
        foreach (var t in phase1)
        {
            var e = FindEntity(source(t), eid);
            if (e != null) pts1.Add((t.Tick, e.Position.X));
        }
        if (pts1.Count > 0) panel.AddSeries(label, color, pts1, dashed: dashed);

        var pts2 = new List<(int, float)>();
        foreach (var t in phase2)
        {
            var e = FindEntity(source(t), eid);
            if (e != null) pts2.Add((t.Tick, e.Position.X));
        }
        if (pts2.Count > 0) panel.AddSeries(label, color, pts2, dashed: dashed);
    }

    // Server-side authority across the full timeline — continuous polyline
    // through phase1 → intermission (0 during orphan) → phase2 (new netId).
    private static void AddCombinedAuthoritySeries(SvgPlot.Panel panel,
        List<TripleSample> phase1, List<TripleSample> intermission, List<TripleSample> phase2,
        int eid, string label, string color)
    {
        var pts = new List<(int, float)>();
        Append(phase1); Append(intermission); Append(phase2);
        panel.AddSeries(label, color, pts);

        void Append(List<TripleSample> src)
        {
            foreach (var t in src)
            {
                var e = FindEntity(t.Server, eid);
                if (e != null) pts.Add((t.Tick, e.Authority));
            }
        }
    }

    // ── inner types ──────────────────────────────────────────────────────────

    private class TripleSample
    {
        public int Tick;
        public string Phase;
        public string TokenA;
        public string TokenB;
        public int NetIdA;
        public int NetIdB;
        public Sample Server;
        public Sample ClientA;
        public Sample ClientB;
    }

    private class ServerEntity
    {
        public int Id;
        public byte Type;
        public int Authority;
        public Vector3 Position;
    }

    private class EntityPose
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Yaw;
    }
}
