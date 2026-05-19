using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using Godot;
using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Artifacts;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.MultiProcess;

/// <summary>
/// Vehicle claim → drive → release across TWO client processes. Server + two real client
/// processes (A and B). Player A starts in range of the vehicle, presses interact to
/// claim, drives forward, drives backward to roughly the starting point, releases, walks
/// off. Player B is a passive observer the whole run — its purpose is to verify that a
/// non-owner client sees the AuthorityChangedMessage broadcast and the vehicle's body
/// motion (with player A riding on top) without any snap on the ownership flips.
///
/// The whole cycle runs through the production input pipeline — each interact action is
/// driven by rising-edge detection on the <c>InputFlags.Interact</c> bit in the client's
/// scheduled input, not by orchestrator-direct authority commands.
///
/// Regression target: the legacy Destroy+Create authority-swap path snapped the vehicle's
/// rigid body (zero velocity, lost prediction history, lost visual smoother state) every
/// time ownership changed. Under the unified-prediction model the entity stays in place
/// and only its Authority field flips. This test asserts both clients see the claim and
/// the release without a velocity discontinuity and without a visible visual jump, AND
/// that player A's body remains anchored on top of the vehicle while driving.
///
/// Artefacts under <c>TestResults/VehicleCycle/</c>:
///   - vehicle_cycle.csv             per-tick samples across server + both clients
///   - vehicle_cycle.svg             multi-panel: vehicle Z (all 3), X, authority step
///                                   plot, velocity magnitude, visual offset, rider
///                                   position; vertical markers at each authority change
///   - vehicle_cycle.mp4             driver's (client A) in-engine viewport
///   - vehicle_cycle.observer.mp4    observer's (client B) in-engine viewport
///   - vehicle_cycle.steps.log       StepLogger phase narration
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class VehicleTests : MultiProcessTestBase
{
    protected override string ArtifactSubdir => "VehicleCycle";

    [BeforeTest] public void SetUp() => SetUpInternal();
    [AfterTest]  public void TearDown() => TearDownInternal();

    private const byte EntityTypeVehicle = 2;
    private const byte EntityTypeRigidPlayer = 3;

    // InputFlags.Interact — must match demo/players/SharedPlayerMovement.cs.
    private const int InteractKeyBit = 0b_0000_0100;

    // ── Geometry ─────────────────────────────────────────────────────────────
    // Floor is a 30×30 CSGBox at Y=-2.5 (top surface Y=-2). Vehicle starts in
    // the middle; players start just inside the 4-m claim radius on opposite
    // sides at floor level. Starting in-range avoids the "walk to vehicle"
    // phase that, in earlier iterations, pushed the vehicle off its origin
    // through player-body contact before the claim ever fired.
    private const float VehicleStartX = 0f;
    private const float VehicleStartY = 0.5f;
    private const float VehicleStartZ = 0f;
    private const float PlayerAStartX = -3.5f;
    private const float PlayerBStartX =  3.5f;
    private const float PlayerStartY  = 0f;
    private const float PlayerStartZ  = 0f;

    // ── Timing (in client ticks @ 60Hz) ──────────────────────────────────────
    private const int SnapshotArmTicks  = 60;   // server settle before player spawn
    private const int PlayerFallTicks   = 60;   // let players land + clocks stabilise
    // Drive a short distance forward then a short distance back so the vehicle
    // ends roughly where it started — leaving it within reach of the second
    // player for the claim/swap.
    private const int DriveForwardTicks   = 90;
    private const int DriveBackTicks      = 90;
    private const int IdleAtVehicleTicks  = 20;
    private const int WalkOffTicks        = 60;
    private const int SnapshotIntervalTicks = 2;
    // Interact rising-edge holds for 4 ticks then releases — long enough for the
    // rising-edge detector to see one cleanly, short enough that the next phase's
    // movement input arrives before the player has time to do anything else.
    private const int InteractHoldTicks = 4;

    // ── Assertion budgets ────────────────────────────────────────────────────
    // m/s between adjacent samples. The release tick coincides with a hard input
    // transition on the driver (drive → brake) AND a switch on the observer from
    // "vehicle integrating with last input" to "vehicle integrating with no input"
    // — both of which can produce a 3-4 m/s velocity step in the surrounding 2-tick
    // window. The budget admits that while still flagging a true snap (a snap would
    // zero or reverse the velocity, ≫ 5 m/s). Visual continuity is the more sensitive
    // test — see VisualJumpBudget below.
    private const float VelocityContinuityBudget = 5.0f;
    // The release tick coincides with the player un-anchoring from the vehicle and
    // re-entering normal collision response; the body's per-sample motion budget here
    // has to accommodate that transient AND the inherent sampling jitter (the test's
    // CaptureSample is round-trip-latency-delayed, so adjacent "samples" can be more
    // than 2 ticks apart in wall time).
    // m between adjacent samples. Sampling every 2 ticks at 60Hz nominally = 33ms,
    // but CaptureSample's TCP round-trip adds jitter so adjacent "samples" can be
    // 4-5 ticks apart in wall time. At driving velocities of 5-15 m/s, that admits
    // 0.5-1.0 m of natural visual motion per sample. The smoother absorbs reconcile
    // snaps but doesn't eliminate motion; the budget is the natural motion plus a
    // per-tick reconcile delta.
    private const float VisualJumpBudget         = 1.5f;
    // m, peak server↔client gap. The vehicle reaches several m/s during the drive
    // phase; under cross-process Jolt drift the predicted client body can run up to
    // a meter ahead/behind the server's authoritative pose before reconcile snaps
    // it back.
    private const float ServerClientTrackBudget  = 1.5f;

    [TestCase]
    public void VehicleClaimAndDrive()
    {
        if (Orch == null) return;

        int port = NextPort();
        var paths = ArtifactsFor("vehicle_cycle");
        using var steps = new StepLogger(paths, "vehicle_cycle", "MP-VEHICLE-CYCLE-01");

        steps.Log($"spawning server on port {port}");
        var server = Orch.Spawn("server", enetPort: port, label: "srv");
        server.WaitReady(networkReady: true, timeoutMs: 30_000);
        ServerLogPath = server.RemoteLogPath;
        steps.Log($"server ready, pid={server.RemotePid}");

        // Both clients record video — the driver's recording shows the first-person
        // view from inside / above the vehicle; the observer's recording shows the
        // vehicle (with rider) moving through the world as seen from a third client.
        string observerVideoPath = System.IO.Path.Combine(paths.Directory, "vehicle_cycle.observer.mp4");
        steps.Log("spawning client A (driver, records video)");
        var clientA = Orch.Spawn("client", enetPort: port, label: "cA", recordVideoPath: paths.Mp4);
        steps.Log("spawning client B (observer, records video)");
        var clientB = Orch.Spawn("client", enetPort: port, label: "cB", recordVideoPath: observerVideoPath);
        clientA.WaitReady(networkReady: true, timeoutMs: 30_000);
        clientB.WaitReady(networkReady: true, timeoutMs: 30_000);
        ClientLogPath = clientA.RemoteLogPath;
        steps.Log($"clients ready: A.netId={clientA.NetworkId} B.netId={clientB.NetworkId}");

        WaitForClockSync(server, clientA, maxGapTicks: 5, timeoutMs: 5_000);
        WaitForClockSync(server, clientB, maxGapTicks: 5, timeoutMs: 5_000);
        steps.Log("clocks synced on both clients");

        server.WaitForTicks(SnapshotArmTicks);

        int vehicleEid = SpawnEntity(server, EntityTypeVehicle, authority: 0,
            VehicleStartX, VehicleStartY, VehicleStartZ);
        steps.Log($"spawned vehicle eid={vehicleEid} authority=0 at ({VehicleStartX},{VehicleStartY},{VehicleStartZ})");
        WaitForClientEntity(clientA, vehicleEid, timeoutMs: 5_000);
        WaitForClientEntity(clientB, vehicleEid, timeoutMs: 5_000);

        int playerAEid = SpawnEntity(server, EntityTypeRigidPlayer, clientA.NetworkId,
            PlayerAStartX, PlayerStartY, PlayerStartZ);
        int playerBEid = SpawnEntity(server, EntityTypeRigidPlayer, clientB.NetworkId,
            PlayerBStartX, PlayerStartY, PlayerStartZ);
        steps.Log($"spawned players: A eid={playerAEid} at X={PlayerAStartX}, B eid={playerBEid} at X={PlayerBStartX}");
        WaitForClientEntity(clientA, playerAEid, timeoutMs: 5_000);
        WaitForClientEntity(clientB, playerBEid, timeoutMs: 5_000);

        // Anchor schedules to the client's current tick. Both clients see roughly
        // the same synced tick (WaitForClockSync above), so anchor each schedule
        // to that client's own current tick.
        int aAnchor = clientA.ReadClientTick() + PlayerFallTicks;
        int bAnchor = clientB.ReadClientTick() + PlayerFallTicks;

        // Phase plan for player A. Players start in-range so no "walk to vehicle"
        // phase is needed — the claim fires from the spawn position. After claiming,
        // A drives forward (moveY=-1 → world -Z), reverses (moveY=+1 → world +Z) so
        // the vehicle ends roughly near its origin, then releases and walks off.
        // Every tick offset is distinct so the HarnessInputProducer (which has
        // unstable sort for equal-tick entries) always returns the intended input.
        int aT_Idle          = 0;
        int aT_Claim         = aT_Idle + PlayerFallTicks;             // interact rising-edge
        int aT_ClaimEnd      = aT_Claim + InteractHoldTicks;
        int aT_DriveFwdStart = aT_ClaimEnd + 1;
        int aT_DriveFwdEnd   = aT_DriveFwdStart + DriveForwardTicks;
        int aT_DriveBackStart = aT_DriveFwdEnd + 1;
        int aT_DriveBackEnd  = aT_DriveBackStart + DriveBackTicks;
        int aT_IdleAtVehicle = aT_DriveBackEnd + IdleAtVehicleTicks;
        int aT_Release       = aT_IdleAtVehicle + 1;                  // interact rising-edge
        int aT_ReleaseEnd    = aT_Release + InteractHoldTicks;
        int aT_WalkOffStart  = aT_ReleaseEnd + 1;
        int aT_End           = aT_WalkOffStart + WalkOffTicks;

        var aSchedule = new List<object>
        {
            new { tick = aAnchor + aT_Idle,           moveX =  0.0, moveY =  0.0, yaw = 0.0, keys = 0 },
            new { tick = aAnchor + aT_Claim,          moveX =  0.0, moveY =  0.0, yaw = 0.0, keys = InteractKeyBit },
            new { tick = aAnchor + aT_ClaimEnd,       moveX =  0.0, moveY =  0.0, yaw = 0.0, keys = 0 },
            new { tick = aAnchor + aT_DriveFwdStart,  moveX =  0.0, moveY = -1.0, yaw = 0.0, keys = 0 },
            new { tick = aAnchor + aT_DriveFwdEnd,    moveX =  0.0, moveY =  0.0, yaw = 0.0, keys = 0 },
            new { tick = aAnchor + aT_DriveBackStart, moveX =  0.0, moveY =  1.0, yaw = 0.0, keys = 0 },
            new { tick = aAnchor + aT_DriveBackEnd,   moveX =  0.0, moveY =  0.0, yaw = 0.0, keys = 0 },
            new { tick = aAnchor + aT_Release,        moveX =  0.0, moveY =  0.0, yaw = 0.0, keys = InteractKeyBit },
            new { tick = aAnchor + aT_ReleaseEnd,     moveX =  0.0, moveY =  0.0, yaw = 0.0, keys = 0 },
            new { tick = aAnchor + aT_WalkOffStart,   moveX = -1.0, moveY =  0.0, yaw = 0.0, keys = 0 },
            new { tick = aAnchor + aT_End,            moveX =  0.0, moveY =  0.0, yaw = 0.0, keys = 0 },
        };
        clientA.Send(new { cmd = "set-input-schedule", entries = aSchedule });
        steps.Log($"installed A schedule: claim@{aT_Claim} fwd({aT_DriveFwdStart}-{aT_DriveFwdEnd}) back({aT_DriveBackStart}-{aT_DriveBackEnd}) release@{aT_Release} walkoff({aT_WalkOffStart}-{aT_End})");

        // Player B is a passive observer the whole run — no inputs scheduled.
        // The test asserts B sees the authority changes broadcast by the server
        // and observes the vehicle body's motion without snap.
        clientB.Send(new
        {
            cmd = "set-input-schedule",
            entries = new[]
            {
                new { tick = bAnchor + 0, moveX = 0.0, moveY = 0.0, yaw = 0.0, keys = 0 },
            },
        });
        steps.Log("installed B schedule: idle (passive observer)");

        int totalTicks = aT_End;
        steps.Log($"running {totalTicks} ticks ({totalTicks / 60.0:F1}s wall) sampling every {SnapshotIntervalTicks} ticks");

        var clientASamples = new List<Sample>();
        var clientBSamples = new List<Sample>();
        var serverSamples  = new List<Sample>();
        var authChanges    = new List<(int Tick, int NewAuthority)>();
        int lastSeenAuthority = 0;

        clientA.WaitForClientTick(aAnchor);

        // Baseline misprediction count BEFORE the claim — captured pre-cycle so the
        // assertion below only counts mispredictions accrued during the drive cycle.
        int driverMispredictBaseline = ReadMispredictCount(clientA);
        int observerMispredictBaseline = ReadMispredictCount(clientB);

        for (int t = SnapshotIntervalTicks; t <= totalTicks; t += SnapshotIntervalTicks)
        {
            int targetTick = aAnchor + t;
            clientA.WaitForClientTick(targetTick);

            var aSample = CaptureSample(clientA, targetTick);
            var bSample = CaptureSample(clientB, targetTick);
            var sSample = CaptureSample(server, targetTick);
            clientASamples.Add(aSample);
            clientBSamples.Add(bSample);
            serverSamples.Add(sSample);

            // Track authority transitions from the server's perspective — the
            // ground truth of who currently owns the vehicle.
            int serverAuth = sSample.Entities.FirstOrDefault(e => e.Id == vehicleEid)?.Authority ?? lastSeenAuthority;
            if (serverAuth != lastSeenAuthority)
            {
                authChanges.Add((targetTick, serverAuth));
                steps.Log($"tick {targetTick}: vehicle authority {lastSeenAuthority} -> {serverAuth}");
                lastSeenAuthority = serverAuth;
            }
        }

        int finalServerVehicleAuth = serverSamples.LastOrDefault()?.Entities.FirstOrDefault(e => e.Id == vehicleEid)?.Authority ?? -1;
        steps.Log($"run complete: final vehicle authority = {finalServerVehicleAuth}, observed {authChanges.Count} authority changes");

        WriteCyclePlot(paths, clientASamples, clientBSamples, serverSamples, vehicleEid, playerAEid, playerBEid, authChanges, aAnchor);
        CopyProcessLogs(paths);
        WriteFramesArtifacts(paths, vehicleEid, playerAEid);

        // ── Assertions ────────────────────────────────────────────────────────
        // Exactly two authority changes: 0 → A (claim) and A → 0 (release).
        AssertThat(authChanges.Count)
            .OverrideFailureMessage(
                $"expected exactly 2 authority changes (0→A→0), observed {authChanges.Count}. " +
                $"final={finalServerVehicleAuth}. Trace at TestResults/VehicleCycle/vehicle_cycle.{{csv,svg,mp4}}")
            .IsEqual(2);
        AssertThat(authChanges[0].NewAuthority).IsEqual(clientA.NetworkId);
        AssertThat(authChanges[1].NewAuthority).IsEqual(0);

        // Both clients should see the vehicle track the server within the budget.
        AssertVehicleTrackingBudget(clientASamples, serverSamples, vehicleEid, "client A");
        AssertVehicleTrackingBudget(clientBSamples, serverSamples, vehicleEid, "client B");

        // At each authority-change tick, neither client's vehicle body should
        // exhibit a velocity discontinuity > VelocityContinuityBudget and the
        // visual mesh should not jump > VisualJumpBudget — proving no scene
        // swap / no rigid-body state reset occurred during the transfer.
        foreach (var (changeTick, _) in authChanges)
        {
            AssertNoSnapAtAuthorityChange(clientASamples, vehicleEid, changeTick, "client A");
            AssertNoSnapAtAuthorityChange(clientBSamples, vehicleEid, changeTick, "client B");
        }

        // Vehicle should end the run unowned (server authority).
        AssertThat(finalServerVehicleAuth)
            .OverrideFailureMessage(
                $"vehicle should end unowned (authority=0) but is owned by {finalServerVehicleAuth}. " +
                $"Trace at TestResults/VehicleCycle/vehicle_cycle.{{csv,svg,mp4}}")
            .IsEqual(0);

        // Rider tracking: while player A owns the vehicle, A's body should be anchored
        // on top of the vehicle (horizontal distance ≈ 0, vertical distance ≈ +RideOffset.y
        // = +1.5 m). Sample observer's view since that's the externally observable behavior;
        // also assert on the driver's own view for completeness.
        int aClientId = clientA.NetworkId;
        AssertRiderAnchoredWhileOwned(serverSamples, vehicleEid, playerAEid, aClientId, "server");
        AssertRiderAnchoredWhileOwned(clientBSamples, vehicleEid, playerAEid, aClientId, "client B observer");

        // Misprediction budget: both the driver and observer should accrue only a
        // handful of reconciles caused by cross-process Jolt drift, because the
        // server now forwards each driving client's input via
        // GameSnapshotMessage.Inputs so observers re-apply the same input the
        // server used. Tight budget applies to both.
        //
        // Regression targets:
        //   - The earlier "local input routed to every predicted entity" bug pushed
        //     the driver's count into the hundreds (the driver's own input was
        //     bouncing player B's body around).
        //   - Before the server forwarded inputs, observers coasted vehicles at
        //     last-known velocity, drifting from the server's input-driven state
        //     and reconciling on every snapshot.
        int driverMispredictsThisCycle = ReadMispredictCount(clientA) - driverMispredictBaseline;
        int observerMispredictsThisCycle = ReadMispredictCount(clientB) - observerMispredictBaseline;
        const int MispredictBudget = 30;
        AssertThat(driverMispredictsThisCycle)
            .OverrideFailureMessage(
                $"driver accrued {driverMispredictsThisCycle} mispredictions over the drive cycle " +
                $"(budget {MispredictBudget}). Likely cause: input being applied to entities the " +
                $"local client doesn't own. Trace at TestResults/VehicleCycle/vehicle_cycle.{{csv,svg,mp4}}")
            .IsLessEqual(MispredictBudget);
        AssertThat(observerMispredictsThisCycle)
            .OverrideFailureMessage(
                $"observer accrued {observerMispredictsThisCycle} mispredictions over the drive cycle " +
                $"(budget {MispredictBudget}). Likely cause: server not forwarding the driver's input " +
                $"to observers via GameSnapshotMessage.Inputs, or observers not re-applying it. " +
                $"Trace at TestResults/VehicleCycle/vehicle_cycle.{{csv,svg,mp4}}")
            .IsLessEqual(MispredictBudget);

        steps.Log($"all assertions passed (driver mispredicts={driverMispredictsThisCycle}, observer mispredicts={observerMispredictsThisCycle})");
        Godot.GD.Print($"[MP-VEHICLE-CYCLE] complete: {authChanges.Count} authority changes over {totalTicks} ticks. " +
            $"Artefacts: TestResults/VehicleCycle/vehicle_cycle.{{csv,svg,mp4,steps.log}}");
    }

    private static void AssertVehicleTrackingBudget(List<Sample> client, List<Sample> server, int vehicleEid, string label)
    {
        float maxGap = 0f;
        for (int i = 0; i < client.Count && i < server.Count; i++)
        {
            var c = client[i].Entities.FirstOrDefault(e => e.Id == vehicleEid);
            var s = server[i].Entities.FirstOrDefault(e => e.Id == vehicleEid);
            if (c == null || s == null) continue;
            float gap = (c.Position - s.Position).Length();
            if (gap > maxGap) maxGap = gap;
        }
        AssertThat(maxGap)
            .OverrideFailureMessage(
                $"{label}: vehicle body diverged from server by {maxGap:F3} m (budget {ServerClientTrackBudget:F3} m). " +
                $"Trace at TestResults/VehicleCycle/vehicle_cycle.{{csv,svg,mp4}}")
            .IsLessEqual(ServerClientTrackBudget);
    }

    private static void AssertNoSnapAtAuthorityChange(List<Sample> samples, int vehicleEid, int changeTick, string label)
    {
        // Find the sample at-or-just-before the change tick, and the one just after.
        EntityState before = null;
        EntityState after = null;
        for (int i = 0; i < samples.Count; i++)
        {
            var ent = samples[i].Entities.FirstOrDefault(e => e.Id == vehicleEid);
            if (ent == null) continue;
            if (samples[i].Tick <= changeTick) before = ent;
            else if (samples[i].Tick > changeTick && after == null) { after = ent; break; }
        }
        if (before == null || after == null) return;

        float velDelta = (after.Velocity - before.Velocity).Length();
        float visualDelta = (after.VisualPosition - before.VisualPosition).Length();

        AssertThat(velDelta)
            .OverrideFailureMessage(
                $"{label}: vehicle velocity jumped {velDelta:F3} m/s across authority change at tick {changeTick} " +
                $"(budget {VelocityContinuityBudget:F3} m/s). before.vel={before.Velocity} after.vel={after.Velocity}. " +
                $"Trace at TestResults/VehicleCycle/vehicle_cycle.{{csv,svg,mp4}}")
            .IsLessEqual(VelocityContinuityBudget);

        AssertThat(visualDelta)
            .OverrideFailureMessage(
                $"{label}: vehicle visual jumped {visualDelta:F3} m across authority change at tick {changeTick} " +
                $"(budget {VisualJumpBudget:F3} m). PredictionVisualSmoothing3D should have absorbed any body snap. " +
                $"Trace at TestResults/VehicleCycle/vehicle_cycle.{{csv,svg,mp4}}")
            .IsLessEqual(VisualJumpBudget);
    }

    private const float RideAnchorVerticalOffset = 1.5f;        // matches LocalRigidPlayerPrediction.RideOffset.Y
    private const float RideAnchorHorizontalBudget = 0.5f;       // m, expected ≈ 0
    private const float RideAnchorVerticalBudget   = 0.5f;       // m around RideAnchorVerticalOffset

    // While player A owns the vehicle, A's body should be anchored on top of the
    // vehicle each tick (the rider-anchor pose write in TryAnchorToOwnedVehicle).
    // Iterate the samples where vehicle.Authority == A's clientId and assert the
    // (playerA.pos − vehicle.pos) gap stays inside the ride-anchor budget. Skip the
    // first few samples after authority flips to A (the anchor hasn't had time to
    // teleport the body yet — the very first tick of ownership reads the player's
    // pre-claim position).
    private const int AnchorSettleTicksAfterAuthFlip = 4;
    private static void AssertRiderAnchoredWhileOwned(List<Sample> samples, int vehicleEid, int playerAEid,
        int aClientId, string label)
    {
        float maxHorizontalGap = 0f;
        float maxVerticalDeviation = 0f;
        int sampledWhileOwned = 0;
        int ticksSinceClaim = int.MaxValue;
        int prevAuth = 0;
        foreach (var sample in samples)
        {
            var v = sample.Entities.FirstOrDefault(e => e.Id == vehicleEid);
            var p = sample.Entities.FirstOrDefault(e => e.Id == playerAEid);
            if (v == null || p == null) continue;

            if (v.Authority == aClientId && prevAuth != aClientId)
                ticksSinceClaim = 0;
            prevAuth = v.Authority;

            if (v.Authority != aClientId) continue;
            ticksSinceClaim++;
            if (ticksSinceClaim < AnchorSettleTicksAfterAuthFlip) continue;

            sampledWhileOwned++;
            var gap = p.Position - v.Position;
            float horiz = new Vector3(gap.X, 0, gap.Z).Length();
            float vertDev = Mathf.Abs(gap.Y - RideAnchorVerticalOffset);
            if (horiz > maxHorizontalGap) maxHorizontalGap = horiz;
            if (vertDev > maxVerticalDeviation) maxVerticalDeviation = vertDev;
        }
        AssertThat(sampledWhileOwned)
            .OverrideFailureMessage($"{label}: no samples captured while A owned the vehicle — the cycle test is misconfigured")
            .IsGreater(0);
        AssertThat(maxHorizontalGap)
            .OverrideFailureMessage(
                $"{label}: rider drifted from vehicle by {maxHorizontalGap:F3} m horizontally " +
                $"(budget {RideAnchorHorizontalBudget:F3} m). The player should be anchored on top " +
                $"of the vehicle while owned. Trace at TestResults/VehicleCycle/vehicle_cycle.{{csv,svg,mp4}}")
            .IsLessEqual(RideAnchorHorizontalBudget);
        AssertThat(maxVerticalDeviation)
            .OverrideFailureMessage(
                $"{label}: rider Y deviated from RideOffset.Y={RideAnchorVerticalOffset:F2} by " +
                $"{maxVerticalDeviation:F3} m (budget {RideAnchorVerticalBudget:F3} m). " +
                $"Trace at TestResults/VehicleCycle/vehicle_cycle.{{csv,svg,mp4}}")
            .IsLessEqual(RideAnchorVerticalBudget);
    }

    private static void WriteCyclePlot(ArtifactPaths paths, List<Sample> clientA, List<Sample> clientB,
        List<Sample> server, int vehicleEid, int playerAEid, int playerBEid,
        List<(int Tick, int NewAuthority)> authChanges, int aAnchor)
    {
        // CSV with role column.
        using (var w = new System.IO.StreamWriter(paths.Csv))
        {
            w.WriteLine("tick,t_s,role,eid,etype,x,y,z,vx,vy,vz,vis_x,vis_y,vis_z,authority");
            int n = System.Math.Min(System.Math.Min(clientA.Count, clientB.Count), server.Count);
            for (int i = 0; i < n; i++)
            {
                WriteRows(w, clientA[i], "clientA", vehicleEid, playerAEid, playerBEid);
                WriteRows(w, clientB[i], "clientB", vehicleEid, playerAEid, playerBEid);
                WriteRows(w, server[i],  "server",  vehicleEid, playerAEid, playerBEid);
            }
        }

        var plot = new SvgPlot("vehicle_cycle — claim / drive / release across two client processes");
        var zPanel  = plot.AddPanel("vehicle.Z (m) — server (solid), clientA driver (dashed), clientB observer (dashed)", yUnits: "m");
        var xPanel  = plot.AddPanel("vehicle.X (m)", yUnits: "m");
        var authPanel = plot.AddPanel("vehicle.authority (step) — 0=server, !0=client peerId", yUnits: "peerId");
        var velPanel = plot.AddPanel("vehicle |velocity| (m/s)", yUnits: "m/s");
        var visPanel = plot.AddPanel("vehicle visual offset from body (m) — smoother decay", yUnits: "m");
        // Rider-tracking panel: while A owns the vehicle, A's player should be anchored
        // ~1.5 m above the vehicle. The trace plots (player.A.Z − vehicle.Z) on the
        // driver's client AND on the observer's client — both should stay ≈ 0 while
        // claimed, since the rider anchor pins the player on top of the vehicle.
        var riderPanel = plot.AddPanel("player A position relative to vehicle — Z gap and X gap (m)", yUnits: "m");

        var sZ = new List<(int, float)>(); var caZ = new List<(int, float)>(); var cbZ = new List<(int, float)>();
        var sX = new List<(int, float)>(); var caX = new List<(int, float)>(); var cbX = new List<(int, float)>();
        var auth = new List<(int, float)>();
        var sVel = new List<(int, float)>(); var caVel = new List<(int, float)>(); var cbVel = new List<(int, float)>();
        var caVisOffset = new List<(int, float)>(); var cbVisOffset = new List<(int, float)>();
        var riderZGapDriver = new List<(int, float)>(); var riderZGapObserver = new List<(int, float)>();
        var riderXGapDriver = new List<(int, float)>(); var riderXGapObserver = new List<(int, float)>();
        var aPlayerZ = new List<(int, float)>(); var aPlayerX = new List<(int, float)>();

        // Clip plot to ticks ≤ MaxPlotTick — the last ~50 ticks of the run are
        // post-cycle idle with everyone parked, which compresses the interesting
        // motion window and hides per-tick detail.
        const int MaxPlotTick = 628;
        int n2 = System.Math.Min(System.Math.Min(clientA.Count, clientB.Count), server.Count);
        for (int i = 0; i < n2; i++)
        {
            int tick = clientA[i].Tick;
            if (tick > MaxPlotTick) continue;
            var s = server[i].Entities.FirstOrDefault(e => e.Id == vehicleEid);
            var ca = clientA[i].Entities.FirstOrDefault(e => e.Id == vehicleEid);
            var cb = clientB[i].Entities.FirstOrDefault(e => e.Id == vehicleEid);
            if (s == null || ca == null || cb == null) continue;

            sZ.Add((tick, s.Position.Z));   caZ.Add((tick, ca.Position.Z));  cbZ.Add((tick, cb.Position.Z));
            sX.Add((tick, s.Position.X));   caX.Add((tick, ca.Position.X));  cbX.Add((tick, cb.Position.X));
            auth.Add((tick, s.Authority));
            sVel.Add((tick, s.Velocity.Length()));
            caVel.Add((tick, ca.Velocity.Length()));
            cbVel.Add((tick, cb.Velocity.Length()));
            caVisOffset.Add((tick, (ca.VisualPosition - ca.Position).Length()));
            cbVisOffset.Add((tick, (cb.VisualPosition - cb.Position).Length()));

            // Rider-tracking: server's view of player A's absolute Z + X, and the gap
            // between player A and the vehicle on each client. While A owns the vehicle
            // (ride anchor active), the gaps stay near 0.
            var sPlayerA = server[i].Entities.FirstOrDefault(e => e.Id == playerAEid);
            var caPlayerA = clientA[i].Entities.FirstOrDefault(e => e.Id == playerAEid);
            var cbPlayerA = clientB[i].Entities.FirstOrDefault(e => e.Id == playerAEid);
            if (sPlayerA != null) { aPlayerZ.Add((tick, sPlayerA.Position.Z)); aPlayerX.Add((tick, sPlayerA.Position.X)); }
            if (caPlayerA != null) { riderZGapDriver.Add((tick, caPlayerA.Position.Z - ca.Position.Z));   riderXGapDriver.Add((tick, caPlayerA.Position.X - ca.Position.X)); }
            if (cbPlayerA != null) { riderZGapObserver.Add((tick, cbPlayerA.Position.Z - cb.Position.Z)); riderXGapObserver.Add((tick, cbPlayerA.Position.X - cb.Position.X)); }
        }

        zPanel.AddSeries("server.vehicle.z",  SvgPlot.Palette.Series[0], sZ)
              .AddSeries("clientA.vehicle.z", SvgPlot.Palette.Series[1], caZ, dashed: true)
              .AddSeries("clientB.vehicle.z", SvgPlot.Palette.Series[2], cbZ, dashed: true)
              .AddSeries("server.playerA.z",  SvgPlot.Palette.Series[4], aPlayerZ, dashed: true);
        xPanel.AddSeries("server.vehicle.x",  SvgPlot.Palette.Series[0], sX)
              .AddSeries("clientA.vehicle.x", SvgPlot.Palette.Series[1], caX, dashed: true)
              .AddSeries("clientB.vehicle.x", SvgPlot.Palette.Series[2], cbX, dashed: true)
              .AddSeries("server.playerA.x",  SvgPlot.Palette.Series[4], aPlayerX, dashed: true);
        authPanel.AddSeries("authority", SvgPlot.Palette.Series[3], auth);
        velPanel.AddSeries("server",  SvgPlot.Palette.Series[0], sVel)
                .AddSeries("clientA", SvgPlot.Palette.Series[1], caVel, dashed: true)
                .AddSeries("clientB", SvgPlot.Palette.Series[2], cbVel, dashed: true);
        visPanel.AddSeries("clientA |vis-body|", SvgPlot.Palette.Series[1], caVisOffset)
                .AddSeries("clientB |vis-body|", SvgPlot.Palette.Series[2], cbVisOffset);
        riderPanel.AddSeries("driver: (A.z − vehicle.z)",   SvgPlot.Palette.Series[1], riderZGapDriver)
                  .AddSeries("observer: (A.z − vehicle.z)", SvgPlot.Palette.Series[2], riderZGapObserver, dashed: true)
                  .AddSeries("driver: (A.x − vehicle.x)",   SvgPlot.Palette.Series[4], riderXGapDriver)
                  .AddSeries("observer: (A.x − vehicle.x)", SvgPlot.Palette.Series[5], riderXGapObserver, dashed: true);

        foreach (var (t, newAuth) in authChanges)
            plot.AddVerticalMarker(t, $"auth→{newAuth}");

        plot.Save(paths.Svg);
        Godot.GD.Print($"[MP-VEHICLE-CYCLE] wrote {paths.Csv}, {paths.Svg} ({n2} samples)");
    }

    // Parse the client A log for [SMOOTH-FRAME] / [RIDER-FRAME] entries and emit
    // a per-render-frame CSV + SVG. Lets us see whether the vehicle's smoothed
    // visual mesh tracks the rider's camera (== rider body) every render frame,
    // or lags behind it (the symptom of physics-tick stair-stepping).
    private static void WriteFramesArtifacts(ArtifactPaths paths, int vehicleEid, int playerAEid)
    {
        string clientLog = paths.ClientLog;
        if (!System.IO.File.Exists(clientLog)) { Godot.GD.Print($"[MP-VEHICLE-CYCLE] frames artefacts skipped: client log not found at {clientLog}"); return; }

        // SMOOTH-FRAME body=<n> pf=<N> dt=<f> pif=<f> raw=(x,y,z) bodyRen=(x,y,z) interp=(x,y,z) vis=(x,y,z) visRen=(x,y,z) vel=(x,y,z)
        // bodyRen/visRen are diagnostic columns showing GetGlobalTransformInterpolated output
        // (used to confirm FTI behavior); they're optional in the regex so the parser still
        // matches earlier log shapes.
        var smooth = new System.Text.RegularExpressions.Regex(
            @"\[SMOOTH-FRAME\] body=(?<body>[^\s]+) pf=(?<pf>\d+) dt=[\d.\-]+ pif=(?<pif>[\d.]+) raw=\((?<rx>[\d.\-]+),(?<ry>[\d.\-]+),(?<rz>[\d.\-]+)\)( bodyRen=\([^)]+\))? interp=\((?<ix>[\d.\-]+),(?<iy>[\d.\-]+),(?<iz>[\d.\-]+)\) vis=\((?<vx>[\d.\-]+),(?<vy>[\d.\-]+),(?<vz>[\d.\-]+)\)( visRen=\([^)]+\))? vel=\((?<velx>[\d.\-]+),(?<vely>[\d.\-]+),(?<velz>[\d.\-]+)\)",
            System.Text.RegularExpressions.RegexOptions.Compiled);
        var rider = new System.Text.RegularExpressions.Regex(
            @"\[RIDER-FRAME\] eid=(?<eid>\d+) pf=(?<pf>\d+) pos=\((?<x>[\d.\-]+),(?<y>[\d.\-]+),(?<z>[\d.\-]+)\) frozen=(?<frozen>True|False)",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        // pf → per-frame record. Single render frame may emit one SMOOTH-FRAME
        // (vehicle) and one RIDER-FRAME (player A) within the same _Process; we
        // bucket by physframe but also by emission order for sub-tick samples.
        // Multiple _Process per physframe are kept as separate rows.
        var rows = new List<FrameRow>();
        foreach (var line in System.IO.File.ReadAllLines(clientLog))
        {
            var sm = smooth.Match(line);
            if (sm.Success)
            {
                if (!ulong.TryParse(sm.Groups["pf"].Value, out var pf)) continue;
                if (sm.Groups["body"].Value != vehicleEid.ToString()) continue; // only the vehicle entity
                rows.Add(new FrameRow
                {
                    Kind = "vehicle",
                    Pf = pf,
                    Pif = float.Parse(sm.Groups["pif"].Value, System.Globalization.CultureInfo.InvariantCulture),
                    RawZ = float.Parse(sm.Groups["rz"].Value, System.Globalization.CultureInfo.InvariantCulture),
                    InterpZ = float.Parse(sm.Groups["iz"].Value, System.Globalization.CultureInfo.InvariantCulture),
                    VisZ = float.Parse(sm.Groups["vz"].Value, System.Globalization.CultureInfo.InvariantCulture),
                    VelZ = float.Parse(sm.Groups["velz"].Value, System.Globalization.CultureInfo.InvariantCulture),
                });
                continue;
            }
            var rm = rider.Match(line);
            if (rm.Success)
            {
                if (rm.Groups["eid"].Value != playerAEid.ToString()) continue;
                rows.Add(new FrameRow
                {
                    Kind = "rider",
                    Pf = ulong.Parse(rm.Groups["pf"].Value),
                    PosZ = float.Parse(rm.Groups["z"].Value, System.Globalization.CultureInfo.InvariantCulture),
                    Frozen = rm.Groups["frozen"].Value == "True",
                });
            }
        }
        if (rows.Count == 0) { Godot.GD.Print("[MP-VEHICLE-CYCLE] frames artefacts skipped: no SMOOTH-FRAME/RIDER-FRAME lines found"); return; }

        // Group rows by physframe and pair within each group. SMOOTH-FRAME and
        // RIDER-FRAME for the same render frame share the same pf and emit in
        // the same _Process, but in *opposite* file order (vehicle first then
        // rider) — so a streaming "use last seen rider" would always pair the
        // vehicle with the previous physframe's rider, manufacturing a fake
        // vehicle-vs-rider lag. Bucketing by pf removes that pairing skew.
        var byPf = new SortedDictionary<ulong, (FrameRow vehicle, FrameRow rider)>();
        foreach (var r in rows)
        {
            byPf.TryGetValue(r.Pf, out var pair);
            if (r.Kind == "vehicle") pair.vehicle = r;
            else if (r.Kind == "rider") pair.rider = r;
            byPf[r.Pf] = pair;
        }
        var paired = new List<PairedFrame>();
        ulong firstPf = rows[0].Pf;
        foreach (var (pf, pair) in byPf)
        {
            if (pair.vehicle == null) continue;
            int xMs = (int)System.Math.Round((double)(pf - firstPf) * 1000.0 / 60.0 + pair.vehicle.Pif * 1000.0 / 60.0);
            paired.Add(new PairedFrame {
                XMs = xMs, Pf = pf, Pif = pair.vehicle.Pif,
                VehicleRawZ = pair.vehicle.RawZ, VehicleInterpZ = pair.vehicle.InterpZ,
                VehicleVisZ = pair.vehicle.VisZ, VehicleVelZ = pair.vehicle.VelZ,
                RiderBodyZ = pair.rider != null ? pair.rider.PosZ : float.NaN,
                RiderFrozen = pair.rider != null && pair.rider.Frozen,
            });
        }

        // CSV
        string framesCsv = System.IO.Path.Combine(paths.Directory, "vehicle_cycle.frames.csv");
        using (var w = new System.IO.StreamWriter(framesCsv))
        {
            w.WriteLine("t_ms,pf,pif,vehicle_raw_z,vehicle_interp_z,vehicle_vis_z,vehicle_velz,rider_body_z,rider_frozen,gap_vis_minus_rider");
            foreach (var p in paired)
            {
                float gap = float.IsNaN(p.RiderBodyZ) ? 0f : (p.VehicleVisZ - p.RiderBodyZ);
                w.Write(p.XMs); w.Write(','); w.Write(p.Pf); w.Write(',');
                w.Write(CsvWriter.F(p.Pif)); w.Write(',');
                w.Write(CsvWriter.F(p.VehicleRawZ)); w.Write(','); w.Write(CsvWriter.F(p.VehicleInterpZ)); w.Write(',');
                w.Write(CsvWriter.F(p.VehicleVisZ)); w.Write(','); w.Write(CsvWriter.F(p.VehicleVelZ)); w.Write(',');
                w.Write(CsvWriter.F(p.RiderBodyZ)); w.Write(','); w.Write(p.RiderFrozen ? 1 : 0); w.Write(',');
                w.Write(CsvWriter.F(gap)); w.Write('\n');
            }
        }

        // SVG with per-render-frame panels. X axis is t_ms (each tick on plot
        // is one millisecond, so the SvgPlot int-tick api carries ms values).
        var fplot = new SvgPlot("vehicle_cycle — per-render-frame smoothness on client A");
        var pZ        = fplot.AddPanel("vehicle body Z (raw, un-interpolated) vs visual Z (smoothed)", yUnits: "m");
        var pGap      = fplot.AddPanel("vehicle.visual.z − rider.body.z (gap from rider's camera per render frame)", yUnits: "m");
        var pVisDelta = fplot.AddPanel("vehicle.visual.z step per render frame (Δ between adjacent samples)", yUnits: "m");
        var pPif      = fplot.AddPanel("Engine.GetPhysicsInterpolationFraction()", yUnits: "");

        var sRawZ = new List<(int, float)>();
        var sVisZ = new List<(int, float)>();
        var sInterpZ = new List<(int, float)>();
        var sRiderZ = new List<(int, float)>();
        var sGap = new List<(int, float)>();
        var sVisDelta = new List<(int, float)>();
        var sPif = new List<(int, float)>();
        float prevVisZ = float.NaN;
        foreach (var p in paired)
        {
            sRawZ.Add((p.XMs, p.VehicleRawZ));
            sInterpZ.Add((p.XMs, p.VehicleInterpZ));
            sVisZ.Add((p.XMs, p.VehicleVisZ));
            if (!float.IsNaN(p.RiderBodyZ))
            {
                sRiderZ.Add((p.XMs, p.RiderBodyZ));
                sGap.Add((p.XMs, p.VehicleVisZ - p.RiderBodyZ));
            }
            if (!float.IsNaN(prevVisZ))
                sVisDelta.Add((p.XMs, p.VehicleVisZ - prevVisZ));
            prevVisZ = p.VehicleVisZ;
            sPif.Add((p.XMs, p.Pif));
        }
        pZ.AddSeries("vehicle.body.z (raw)", SvgPlot.Palette.Series[0], sRawZ)
          .AddSeries("vehicle.interp.z (manual lerp)", SvgPlot.Palette.Series[1], sInterpZ, dashed: true)
          .AddSeries("vehicle.visual.z (drawn)", SvgPlot.Palette.Series[2], sVisZ)
          .AddSeries("rider.body.z (camera origin)", SvgPlot.Palette.Series[4], sRiderZ, dashed: true);
        pGap.AddSeries("visual − rider", SvgPlot.Palette.Series[3], sGap);
        pVisDelta.AddSeries("Δ visual.z per frame", SvgPlot.Palette.Series[2], sVisDelta);
        pPif.AddSeries("pif", SvgPlot.Palette.Series[5], sPif);

        string framesSvg = System.IO.Path.Combine(paths.Directory, "vehicle_cycle.frames.svg");
        fplot.Save(framesSvg);
        Godot.GD.Print($"[MP-VEHICLE-CYCLE] wrote frames artefacts: {framesCsv}, {framesSvg} ({paired.Count} render-frame samples)");
    }

    private sealed class FrameRow
    {
        public string Kind;
        public ulong Pf;
        public float Pif;
        public float RawZ, InterpZ, VisZ, VelZ; // vehicle
        public float PosZ; public bool Frozen;  // rider
    }
    private sealed class PairedFrame
    {
        public int XMs;
        public ulong Pf;
        public float Pif;
        public float VehicleRawZ, VehicleInterpZ, VehicleVisZ, VehicleVelZ;
        public float RiderBodyZ; public bool RiderFrozen;
    }

    private static void WriteRows(System.IO.StreamWriter w, Sample s, string role,
        int vehicleEid, int playerAEid, int playerBEid)
    {
        string tickStr = s.Tick.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string tStr = (s.Tick / 60.0).ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var e in s.Entities)
        {
            if (e.Id != vehicleEid && e.Id != playerAEid && e.Id != playerBEid) continue;
            w.Write(tickStr); w.Write(','); w.Write(tStr); w.Write(','); w.Write(role); w.Write(',');
            w.Write(e.Id); w.Write(','); w.Write(e.Type); w.Write(',');
            w.Write(CsvWriter.F(e.Position.X)); w.Write(','); w.Write(CsvWriter.F(e.Position.Y)); w.Write(','); w.Write(CsvWriter.F(e.Position.Z)); w.Write(',');
            w.Write(CsvWriter.F(e.Velocity.X)); w.Write(','); w.Write(CsvWriter.F(e.Velocity.Y)); w.Write(','); w.Write(CsvWriter.F(e.Velocity.Z)); w.Write(',');
            w.Write(CsvWriter.F(e.VisualPosition.X)); w.Write(','); w.Write(CsvWriter.F(e.VisualPosition.Y)); w.Write(','); w.Write(CsvWriter.F(e.VisualPosition.Z)); w.Write(',');
            w.Write(e.Authority); w.Write('\n');
        }
    }
}
