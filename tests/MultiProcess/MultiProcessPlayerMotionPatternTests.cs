using System;
using System.Collections.Generic;
using GdUnit4;
using Godot;
using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Artifacts;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.MultiProcess;

/// <summary>
/// MP-PLAYER-MOTION-01..04: Two clients connect; one (the "mover") drives a
/// player around in a deterministic pattern. The other (the "observer") sits
/// still. We measure the OBSERVER'S misprediction count for the mover's
/// player — that is the manifestation of the "constant mispredictions when
/// a player changes direction" report from interactive logs.
///
/// Observers don't own the moving player, so their local prediction of it is
/// driven by the input the server forwards via <c>GameSnapshotMessage.Inputs</c>.
/// If the server's forwarded input goes stale (e.g. the owner stops pressing
/// keys and the server falls back to "default" locally, but the snapshot
/// still reports the LAST RECEIVED non-zero input), the observer accelerates
/// its predicted body every resim tick using stale input while the server
/// keeps the body decaying — every subsequent snapshot then trips
/// HasMisspredicted with small-but-persistent vel deltas.
///
/// Four patterns exercise different combinations of input edge cases:
///   01-square-no-turn:  moveX/moveY rotates around 4 cardinal directions
///                       with constant yaw=0. Tests stale-input after each
///                       direction change.
///   02-square-turning:  moveY=-1 (forward) constant; yaw rotates 90° each
///                       leg so the world-frame motion traces a square. Tests
///                       yaw-only changes with sustained move input.
///   03-circle-no-mouse: moveX/moveY trace a circle (cosθ, sinθ); yaw=0.
///                       Tests gradual direction change without mouse.
///   04-circle-mouse:    moveY=-1 (forward) constant; yaw sweeps continuously.
///                       Tests sustained-forward + rotating frame.
///
/// All four use the same MAX_OBSERVER_MISPREDICTS budget (≤ 3 over the run).
/// Free-space motion without collision should produce no mispredicts beyond
/// occasional Jolt cross-process drift — the interactive log this test
/// targets showed 50 mispredicts in 20 s with the same kind of motion.
///
/// Artefacts under <c>TestResults/PlayerMotionPattern/</c>:
///   - {label}.client.log   observer (client B) monke-net log
///   - {label}.mover.log    mover  (client A) monke-net log
///   - {label}.server.log   server log
///   - {label}.mp4          mover (client A) windowed render
///   - {label}.observer.mp4 observer (client B) windowed render
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class MultiProcessPlayerMotionPatternTests : MultiProcessTestBase
{
    protected override string ArtifactSubdir => "PlayerMotionPattern";

    [BeforeTest] public void SetUp() => SetUpInternal();
    [AfterTest]  public void TearDown() => TearDownInternal();

    private const byte EntityTypeRigidPlayer = 3;

    // ── timing ───────────────────────────────────────────────────────────────
    private const int SnapshotArmTicks = 60;
    private const int PlayerFallTicks  = 90;
    private const int SegmentTicks     = 30;     // 0.5 s @ 60Hz per leg
    private const int Repeats          = 3;      // 12 direction changes for square
    private const int CircleTicks      = 240;    // 4 s of circular motion @ 60Hz
    private const int TailIdleTicks    = 60;

    // ── geometry ────────────────────────────────────────────────────────────
    private const float MoverStartX    = 0f;
    private const float ObserverStartX = 5f;
    private const float PlayerStartY   = 0f;
    private const float PlayerStartZ   = 0f;

    // ── budget ──────────────────────────────────────────────────────────────
    // Free-space motion with no collision should produce ≤ 3 mispredicts on
    // the observer side (1-2 startup events + occasional Jolt drift). The
    // bug this test targets produced 50+.
    private const int MispredictBudget = 3;

    // ─── 01 — square, no turning (axis-aligned cardinal moves) ──────────────
    [TestCase]
    public void MP_PlayerMotion_01_SquareNoTurn_ObserverNoMispredict()
    {
        if (Orch == null) return;

        var schedule = new System.Func<int, List<object>>(anchor =>
        {
            var s = new List<object> { new { tick = anchor - PlayerFallTicks, moveX = 0.0, moveY = 0.0, yaw = 0.0, keys = 0 } };
            int t = 0;
            for (int r = 0; r < Repeats; r++)
            {
                s.Add(new { tick = anchor + t,                  moveX =  0.0, moveY = -1.0, yaw = 0.0, keys = 0 });
                s.Add(new { tick = anchor + t + SegmentTicks,   moveX =  1.0, moveY =  0.0, yaw = 0.0, keys = 0 });
                s.Add(new { tick = anchor + t + SegmentTicks*2, moveX =  0.0, moveY =  1.0, yaw = 0.0, keys = 0 });
                s.Add(new { tick = anchor + t + SegmentTicks*3, moveX = -1.0, moveY =  0.0, yaw = 0.0, keys = 0 });
                t += SegmentTicks * 4;
            }
            s.Add(new { tick = anchor + t, moveX = 0.0, moveY = 0.0, yaw = 0.0, keys = 0 });
            return s;
        });
        RunMotionPattern("square_no_turn", "MP-PLAYER-MOTION-01", schedule, Repeats * SegmentTicks * 4 + TailIdleTicks);
    }

    // ─── 02 — square WITH turning (forward+yaw, world-frame square) ─────────
    [TestCase]
    public void MP_PlayerMotion_02_SquareWithTurn_ObserverNoMispredict()
    {
        if (Orch == null) return;
        var schedule = new System.Func<int, List<object>>(anchor =>
        {
            var s = new List<object> { new { tick = anchor - PlayerFallTicks, moveX = 0.0, moveY = 0.0, yaw = 0.0, keys = 0 } };
            int t = 0;
            for (int r = 0; r < Repeats; r++)
            {
                // 4 legs, each yaw rotated by an additional 90° (π/2 radians).
                // moveY = -1 always means "forward in screen space"; the body
                // physics rotates that into world frame via cameraYaw.
                s.Add(new { tick = anchor + t,                  moveX = 0.0, moveY = -1.0, yaw =  0.0,           keys = 0 });
                s.Add(new { tick = anchor + t + SegmentTicks,   moveX = 0.0, moveY = -1.0, yaw =  Mathf.Pi / 2,   keys = 0 });
                s.Add(new { tick = anchor + t + SegmentTicks*2, moveX = 0.0, moveY = -1.0, yaw =  Mathf.Pi,       keys = 0 });
                s.Add(new { tick = anchor + t + SegmentTicks*3, moveX = 0.0, moveY = -1.0, yaw = -Mathf.Pi / 2,   keys = 0 });
                t += SegmentTicks * 4;
            }
            s.Add(new { tick = anchor + t, moveX = 0.0, moveY = 0.0, yaw = 0.0, keys = 0 });
            return s;
        });
        RunMotionPattern("square_with_turn", "MP-PLAYER-MOTION-02", schedule, Repeats * SegmentTicks * 4 + TailIdleTicks);
    }

    // ─── 03 — circle, no mouse (moveX/moveY trace circle, yaw=0) ────────────
    [TestCase]
    public void MP_PlayerMotion_03_CircleNoMouse_ObserverNoMispredict()
    {
        if (Orch == null) return;
        var schedule = new System.Func<int, List<object>>(anchor =>
        {
            var s = new List<object> { new { tick = anchor - PlayerFallTicks, moveX = 0.0, moveY = 0.0, yaw = 0.0, keys = 0 } };
            // Sample the circle at 1-tick granularity so the harness's
            // tick-keyed schedule has fine resolution. Two full revolutions.
            const float RevolutionsPerSec = 0.5f;
            int totalTicks = CircleTicks;
            for (int i = 0; i <= totalTicks; i++)
            {
                float phase = (float)(2 * Math.PI * RevolutionsPerSec * i / 60f);
                s.Add(new { tick = anchor + i, moveX = (double)Mathf.Cos(phase), moveY = (double)Mathf.Sin(phase), yaw = 0.0, keys = 0 });
            }
            s.Add(new { tick = anchor + totalTicks + 1, moveX = 0.0, moveY = 0.0, yaw = 0.0, keys = 0 });
            return s;
        });
        RunMotionPattern("circle_no_mouse", "MP-PLAYER-MOTION-03", schedule, CircleTicks + TailIdleTicks);
    }

    // ─── 04 — circle WITH mouse (forward + continuous yaw sweep) ────────────
    [TestCase]
    public void MP_PlayerMotion_04_CircleWithMouse_ObserverNoMispredict()
    {
        if (Orch == null) return;
        var schedule = new System.Func<int, List<object>>(anchor =>
        {
            var s = new List<object> { new { tick = anchor - PlayerFallTicks, moveX = 0.0, moveY = 0.0, yaw = 0.0, keys = 0 } };
            const float RevolutionsPerSec = 0.5f;
            int totalTicks = CircleTicks;
            for (int i = 0; i <= totalTicks; i++)
            {
                float yaw = (float)(2 * Math.PI * RevolutionsPerSec * i / 60f);
                s.Add(new { tick = anchor + i, moveX = 0.0, moveY = -1.0, yaw = (double)yaw, keys = 0 });
            }
            s.Add(new { tick = anchor + totalTicks + 1, moveX = 0.0, moveY = 0.0, yaw = 0.0, keys = 0 });
            return s;
        });
        RunMotionPattern("circle_with_mouse", "MP-PLAYER-MOTION-04", schedule, CircleTicks + TailIdleTicks);
    }

    // ─── shared scaffolding ─────────────────────────────────────────────────

    /// <summary>
    /// Spawn server + two clients (mover = client A, observer = client B),
    /// install the supplied tick-keyed input schedule on A, leave B idle,
    /// and measure the OBSERVER's misprediction count over the run.
    /// </summary>
    private void RunMotionPattern(string label, string stepId,
        System.Func<int, List<object>> scheduleBuilder, int runTicks)
    {
        int port = NextPort();
        var paths = ArtifactsFor(label);
        using var steps = new StepLogger(paths, label, stepId);

        steps.Log($"spawning server on port {port}");
        var server = Orch.Spawn("server", enetPort: port, label: "srv");
        server.WaitReady(networkReady: true, timeoutMs: 30_000);
        ServerLogPath = server.RemoteLogPath;

        string observerVideoPath = System.IO.Path.Combine(paths.Directory, label + ".observer.mp4");
        steps.Log("spawning client A (mover, records video)");
        var clientA = Orch.Spawn("client", enetPort: port, label: "cA", recordVideoPath: paths.Mp4);
        steps.Log("spawning client B (observer, records video)");
        var clientB = Orch.Spawn("client", enetPort: port, label: "cB", recordVideoPath: observerVideoPath);
        clientA.WaitReady(networkReady: true, timeoutMs: 30_000);
        clientB.WaitReady(networkReady: true, timeoutMs: 30_000);
        // Observer is the test subject — its log is the one we want copied.
        ClientLogPath = clientB.RemoteLogPath;
        steps.Log($"clients ready: A.netId={clientA.NetworkId} B.netId={clientB.NetworkId}");

        WaitForClockSync(server, clientA, maxGapTicks: 5, timeoutMs: 5_000);
        WaitForClockSync(server, clientB, maxGapTicks: 5, timeoutMs: 5_000);

        server.WaitForTicks(SnapshotArmTicks);

        int moverEid    = SpawnEntity(server, EntityTypeRigidPlayer, clientA.NetworkId, MoverStartX,    PlayerStartY, PlayerStartZ);
        int observerEid = SpawnEntity(server, EntityTypeRigidPlayer, clientB.NetworkId, ObserverStartX, PlayerStartY, PlayerStartZ);
        steps.Log($"spawned mover eid={moverEid} at X={MoverStartX}, observer eid={observerEid} at X={ObserverStartX}");
        WaitForClientEntity(clientA, moverEid,    timeoutMs: 5_000);
        WaitForClientEntity(clientA, observerEid, timeoutMs: 5_000);
        WaitForClientEntity(clientB, moverEid,    timeoutMs: 5_000);
        WaitForClientEntity(clientB, observerEid, timeoutMs: 5_000);

        int anchorA = clientA.ReadClientTick() + PlayerFallTicks;
        int anchorB = clientB.ReadClientTick();

        var aSchedule = scheduleBuilder(anchorA);
        clientA.Send(new { cmd = "set-input-schedule", entries = aSchedule });
        steps.Log($"installed mover schedule: {aSchedule.Count} entries, anchor={anchorA}, runTicks={runTicks}");

        clientB.Send(new
        {
            cmd = "set-input-schedule",
            entries = new[] { new { tick = anchorB, moveX = 0.0, moveY = 0.0, yaw = 0.0, keys = 0 } },
        });

        // Wait for player A to land + the observer's clock to be ready. The
        // observer's baseline mispredict count is captured AFTER the fall so
        // we measure only the pattern phase, not the falling-onto-floor phase.
        clientB.WaitForClientTick(anchorA);
        int baseline = ReadMispredictCount(clientB);
        steps.Log($"baseline observer mispredicts at anchor={anchorA}: {baseline}");

        int endTick = anchorA + runTicks;
        clientB.WaitForClientTick(endTick);
        int finalCount = ReadMispredictCount(clientB);
        int patternMispredicts = finalCount - baseline;
        steps.Log($"pattern complete @ tick={endTick}: total={finalCount}, baseline={baseline}, observerMispredicts={patternMispredicts}");

        CopyProcessLogs(paths);
        // Also preserve the mover's (clientA) log alongside the observer's so we
        // can see the mover-side misprediction counter for the same scenario —
        // the observer log (paths.ClientLog) only captures eid=1 from the
        // remote perspective with the loosened threshold.
        CopyProcessLog(paths.Directory, clientA.RemoteLogPath, $"{label}.mover.log");

        AssertThat(patternMispredicts)
            .OverrideFailureMessage(
                $"observer accrued {patternMispredicts} mispredictions for the mover during '{label}' " +
                $"(budget {MispredictBudget}). The interactive log this reproduces showed 50 mispredicts in 20 s. " +
                $"Likely cause: server's snapshot.Inputs[] reports the last RECEIVED input from the mover " +
                $"(stale after the owner stops sending), so the observer's prediction accelerates the body " +
                $"every resim tick using stale input. Trace at TestResults/PlayerMotionPattern/{label}.client.log " +
                $"— look for RIDER-MISPREDICT-TRIP for which threshold fires.")
            .IsLessEqual(MispredictBudget);
    }
}
