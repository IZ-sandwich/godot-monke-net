using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using Godot;
using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Artifacts;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.MultiProcess;

/// <summary>
/// MP-RAMP-MOTION-01..06: rigid-player ramp traversal across two client processes.
///
/// Scenario: server + two real client processes. Player A (owned by client A) is
/// the active actor — it walks the ramp end-to-end, then walks-and-jumps along
/// the same ramp. Player B (owned by client B) is a passive observer that
/// stands off to the side; its purpose is to verify the remote view of player A
/// stays smooth and that no extra reconciles are induced on the observer when
/// the actor crosses a sloped static collider.
///
/// Six test cases, three ramp-up + three ramp-down, at 15°/30°/45°:
///   - RampUp_15deg     player walks up the slope (then walks-and-jumps up it)
///   - RampUp_30deg
///   - RampUp_45deg
///   - RampDown_15deg   player walks down the slope (then walks-and-jumps down)
///   - RampDown_30deg
///   - RampDown_45deg
///
/// Geometry: each test spawns a fresh static-collider ramp via the spawn-ramp
/// orch command (the harness creates an independent StaticBody3D on every
/// process). The ramp is positioned so its LOW end sits at the floor's top
/// surface (Y=-2). For walk-up the player spawns on the floor just past the
/// low end and walks in -Z; for walk-down the player spawns above the HIGH end
/// (which is at +Z because angleDeg is negated) and walks in -Z down the slope.
///
/// Artefacts under <c>TestResults/RampMotionPlots/</c> per case:
///   - {label}.csv               long-form samples (server + clientA + clientB)
///   - {label}.svg               multi-panel plot: body Y/Z, visual Y/Z,
///                               velocity magnitude, velocity Y, mispredict
///                               markers
///   - {label}.mp4               clientA's recorded viewport (the actor)
///   - {label}.observer.mp4      clientB's recorded viewport (the observer)
///   - {label}.steps.log         StepLogger narration
///   - {label}.{server,client}.log
///
/// Assertion: the misprediction count accumulated on EITHER client during the
/// scenario stays under a budget. The intent is "mispredictions are not
/// constantly happening throughout the motion" — a tight zero-budget would be
/// flaky on 45° slopes (the contact normal changes as the player crosses the
/// floor/ramp/floor seams), so the budget is set to 10 per client.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class MultiProcessRampMotionTests : MultiProcessTestBase
{
    protected override string ArtifactSubdir => "RampMotionPlots";

    [BeforeTest] public void SetUp() => SetUpInternal();
    [AfterTest]  public void TearDown() => TearDownInternal();

    private const byte EntityTypeRigidPlayer = 3;

    // InputFlags.Space (jump). Must match demo/players/SharedPlayerMovement.cs.
    private const int SpaceKeyBit = 0b_0000_0001;

    // ── Ramp geometry ────────────────────────────────────────────────────────
    // Length along the slope (local Z). Width and thickness picked so the player
    // (~0.6 m wide capsule) has visible margin on either side and the slab is
    // thick enough to never tunnel under the floor when tilted.
    private const float RampLength = 8f;
    private const float RampWidth = 4f;
    private const float RampThickness = 0.5f;
    // Floor top surface in MainScene is Y=-2 (CSGBox at -2.5 with size.y=1).
    private const float FloorTopY = -2f;

    // ── Timing (client ticks @ 60 Hz) ────────────────────────────────────────
    private const int SnapshotArmTicks = 60;
    // Free-fall settle after player spawn. Walk-down spawns the player elevated
    // above the ramp's high end so the fall can be longer in that case; using a
    // single generous value keeps the schedule shape identical across cases.
    private const int PlayerFallTicks = 90;
    // Walk-only phase: long enough at 5 m/s to traverse the 8 m slope plus a
    // metre of run-up either side, ~2 s.
    private const int WalkOnlyTicks = 120;
    // Walk-and-jump phase: jump pulses every 30 ticks (0.5 s) repeated for 2 s
    // give ~4 jumps. The schedule pulses Space on a 30-tick rising-edge
    // cadence; the actual airborne intervals are bounded by gravity, not the
    // input cadence.
    private const int JumpPhaseTicks = 120;
    // The Space-bit rising-edge window. Held for 6 ticks, released for 24, so
    // each jump cycle is 30 ticks. Six ticks held > 1 physics tick so the
    // rising edge is reliably observed even across input-resampling jitter.
    private const int JumpHoldTicks = 6;
    private const int JumpCyclePeriodTicks = 30;
    private const int SnapshotIntervalTicks = 2;

    // Per-client misprediction budget for the whole scenario (walk + jump).
    private const int MispredictBudget = 10;

    // ── Test cases ───────────────────────────────────────────────────────────

    [TestCase] public void RampUp_15deg()   => RunRampScenario("ramp_up_15",   walkUp: true,  angleDeg: 15f);
    [TestCase] public void RampUp_30deg()   => RunRampScenario("ramp_up_30",   walkUp: true,  angleDeg: 30f);
    [TestCase] public void RampUp_45deg()   => RunRampScenario("ramp_up_45",   walkUp: true,  angleDeg: 45f);
    [TestCase] public void RampDown_15deg() => RunRampScenario("ramp_down_15", walkUp: false, angleDeg: 15f);
    [TestCase] public void RampDown_30deg() => RunRampScenario("ramp_down_30", walkUp: false, angleDeg: 30f);
    [TestCase] public void RampDown_45deg() => RunRampScenario("ramp_down_45", walkUp: false, angleDeg: 45f);

    // ── Body ─────────────────────────────────────────────────────────────────

    // angleDeg is always positive; walkUp flips its sign for the ramp's
    // installed rotation so the player ALWAYS walks in the -Z direction —
    // keeping the input schedule identical between up and down cases and
    // making the SVG plots directly comparable. The high end of the ramp is
    // therefore at -Z for walk-up cases and at +Z for walk-down cases; the
    // player spawns near the LOW end of the ramp in both directions, then
    // walks toward the high end in walk-up and away from it in walk-down.
    private void RunRampScenario(string label, bool walkUp, float angleDeg)
    {
        if (Orch == null) return;

        var paths = ArtifactsFor(label);
        using var steps = new StepLogger(paths, label, $"MP-RAMP-MOTION-{label}");

        // ── 1. Spawn server + two clients (both record video) ────────────────
        int port = NextPort();
        steps.Log($"spawning server on port {port}");
        var server = Orch.Spawn("server", enetPort: port, label: "srv");
        server.WaitReady(networkReady: true, timeoutMs: 30_000);
        ServerLogPath = server.RemoteLogPath;

        string observerVideoPath = System.IO.Path.Combine(paths.Directory, label + ".observer.mp4");
        steps.Log("spawning client A (actor, records video)");
        var clientA = Orch.Spawn("client", enetPort: port, label: "cA", recordVideoPath: paths.Mp4);
        steps.Log("spawning client B (observer, records video)");
        var clientB = Orch.Spawn("client", enetPort: port, label: "cB", recordVideoPath: observerVideoPath);
        clientA.WaitReady(networkReady: true, timeoutMs: 30_000);
        clientB.WaitReady(networkReady: true, timeoutMs: 30_000);
        ClientLogPath = clientA.RemoteLogPath;
        steps.Log($"clients ready: A.netId={clientA.NetworkId} B.netId={clientB.NetworkId}");

        WaitForClockSync(server, clientA, maxGapTicks: 5, timeoutMs: 5_000);
        WaitForClockSync(server, clientB, maxGapTicks: 5, timeoutMs: 5_000);
        steps.Log("clocks synced");

        server.WaitForTicks(SnapshotArmTicks);

        // ── 2. Compute ramp pose + player spawn positions ────────────────────
        // For walk-up, install the ramp tilted so its +Z end is LOW (at floor
        // level) and its -Z end is HIGH — i.e. positive rotation around X.
        // For walk-down, negate the rotation so the +Z end is HIGH and the -Z
        // end is LOW. In BOTH cases the LOW end is anchored to floor.top so
        // the actor can step onto the ramp without a vertical seam.
        float halfLen = RampLength * 0.5f;
        float angleRad = Mathf.DegToRad(angleDeg);
        float sin = Mathf.Sin(angleRad);
        float cos = Mathf.Cos(angleRad);

        float installedAngleDeg = walkUp ? +angleDeg : -angleDeg;
        // Center elevation chosen so the LOW end's top surface sits flush with
        // the floor's top surface (Y=FloorTopY). After rotation, the LOW end
        // is at world Y = rampBaseY - halfLen·sin, plus thickness/2 for the
        // top face. Solving for rampBaseY:
        float rampBaseY = FloorTopY - RampThickness * 0.5f + halfLen * sin;
        var rampCenter = new Vector3(0f, rampBaseY, 0f);

        // High end (where the actor heads for walk-up, where it starts for walk-down):
        //   walk-up:   local -Z end → world (0, rampBaseY + halfLen·sin, -halfLen·cos)
        //   walk-down: local +Z end → world (0, rampBaseY + halfLen·sin, +halfLen·cos)
        // Low end Z magnitudes are symmetric. Always |highEnd.Z| = |lowEnd.Z| = halfLen·cos.
        float lowEndZ_world = walkUp ? +halfLen * cos : -halfLen * cos;
        float highEndY_world = rampBaseY + halfLen * sin;

        // Player A spawn: at the LOW end with a small run-up so the first few
        // ticks of motion are on FLAT floor; the trace makes the floor→ramp
        // transition visible. Walk-down spawns the player slightly above the
        // HIGH end (which is at +Z on a walk-down ramp) so it lands on the
        // ramp top after PlayerFallTicks of free-fall.
        Vector3 playerASpawn;
        if (walkUp)
        {
            // Spawn beyond the +Z low end, on the floor. Falls onto floor first.
            playerASpawn = new Vector3(0f, 0f, lowEndZ_world + 1.5f);
        }
        else
        {
            // Spawn above the +Z high end so the player lands on the ramp top
            // about 1 m inside its footprint. From there walking -Z slides
            // down the slope. Y = high-end top surface + small clearance.
            float highEndZ_world = +halfLen * cos;
            playerASpawn = new Vector3(0f, highEndY_world + RampThickness * 0.5f + 0.5f,
                                       highEndZ_world - 1.0f);
        }

        // Player B (observer): off to the side, well clear of the ramp footprint
        // and any walls. Floor is 30×30 centered at origin; east wall at X=+14.5.
        Vector3 playerBSpawn = new Vector3(8f, 0f, 3f);

        steps.Log($"ramp: angleDeg(installed)={installedAngleDeg:F1} center={rampCenter} halfLenCos={halfLen * cos:F2}");
        steps.Log($"player A spawn={playerASpawn}, player B spawn={playerBSpawn}");

        // ── 3. Spawn the ramp on all 3 processes ─────────────────────────────
        // spawn-ramp is fire-and-forget per-process; each side creates its own
        // StaticBody3D + CollisionShape3D. Static colliders don't need
        // networking — both replicas resolve collisions identically because no
        // per-process Jolt integration is applied to the ramp itself.
        var rampReq = new
        {
            cmd = "spawn-ramp",
            position = new[] { (double)rampCenter.X, rampCenter.Y, rampCenter.Z },
            angleDeg = (double)installedAngleDeg,
            length = (double)RampLength,
            width = (double)RampWidth,
            thickness = (double)RampThickness,
        };
        server.Send(rampReq);
        clientA.Send(rampReq);
        clientB.Send(rampReq);
        steps.Log("ramp spawned on server + both clients");

        // ── 4. Spawn the two players ─────────────────────────────────────────
        int playerAEid = SpawnEntity(server, EntityTypeRigidPlayer, clientA.NetworkId,
            playerASpawn.X, playerASpawn.Y, playerASpawn.Z);
        int playerBEid = SpawnEntity(server, EntityTypeRigidPlayer, clientB.NetworkId,
            playerBSpawn.X, playerBSpawn.Y, playerBSpawn.Z);
        steps.Log($"spawned players: A eid={playerAEid}, B eid={playerBEid}");
        WaitForClientEntity(clientA, playerAEid, timeoutMs: 5_000);
        WaitForClientEntity(clientA, playerBEid, timeoutMs: 5_000);
        WaitForClientEntity(clientB, playerAEid, timeoutMs: 5_000);
        WaitForClientEntity(clientB, playerBEid, timeoutMs: 5_000);

        // ── 5. Build A's input schedule + B's idle schedule ──────────────────
        int aAnchor = clientA.ReadClientTick() + PlayerFallTicks;
        int bAnchor = clientB.ReadClientTick() + PlayerFallTicks;

        // Phase 1: walk only (no jumping). moveY=-1 for the full duration so
        // the player walks straight along -Z onto / down the ramp.
        // Phase 2: walk while pulsing Space on a fixed cadence — every
        // JumpCyclePeriodTicks the Space bit is held for JumpHoldTicks then
        // released. The full Space-bit + moveY=-1 input is reasserted at every
        // edge so the producer always has a fresh entry to apply.
        var schedule = new List<object>
        {
            // Idle during free-fall settle.
            new { tick = aAnchor - PlayerFallTicks, moveX = 0.0, moveY = 0.0, yaw = 0.0, keys = 0 },
            // Start walking forward.
            new { tick = aAnchor,                   moveX = 0.0, moveY = -1.0, yaw = 0.0, keys = 0 },
        };
        // Phase 2 begins at aAnchor + WalkOnlyTicks; from there pulse jumps.
        int jumpPhaseStart = aAnchor + WalkOnlyTicks;
        int jumpPhaseEnd   = jumpPhaseStart + JumpPhaseTicks;
        for (int t = jumpPhaseStart; t < jumpPhaseEnd; t += JumpCyclePeriodTicks)
        {
            // Hold Space for JumpHoldTicks (rising edge at t, falling edge at
            // t + JumpHoldTicks). Walking continues throughout.
            schedule.Add(new { tick = t,                  moveX = 0.0, moveY = -1.0, yaw = 0.0, keys = SpaceKeyBit });
            schedule.Add(new { tick = t + JumpHoldTicks,  moveX = 0.0, moveY = -1.0, yaw = 0.0, keys = 0 });
        }
        // Brake at the end of the scenario so the player stops on the trace.
        schedule.Add(new { tick = jumpPhaseEnd, moveX = 0.0, moveY = 0.0, yaw = 0.0, keys = 0 });
        clientA.Send(new { cmd = "set-input-schedule", entries = schedule });
        steps.Log($"A schedule: walk[{aAnchor}..{jumpPhaseStart}) jump-walk[{jumpPhaseStart}..{jumpPhaseEnd}) entries={schedule.Count}");

        clientB.Send(new
        {
            cmd = "set-input-schedule",
            entries = new[] { new { tick = bAnchor, moveX = 0.0, moveY = 0.0, yaw = 0.0, keys = 0 } },
        });

        int totalTicks = WalkOnlyTicks + JumpPhaseTicks;
        steps.Log($"running {totalTicks} ticks ({totalTicks / 60.0:F1} s wall), sampling every {SnapshotIntervalTicks} ticks");

        clientA.WaitForClientTick(aAnchor);
        int driverBaseline = ReadMispredictCount(clientA);
        int observerBaseline = ReadMispredictCount(clientB);

        // ── 6. Sample every SnapshotIntervalTicks from all three processes ───
        var clientASamples = new List<Sample>();
        var clientBSamples = new List<Sample>();
        var serverSamples  = new List<Sample>();
        for (int t = SnapshotIntervalTicks; t <= totalTicks; t += SnapshotIntervalTicks)
        {
            int targetTick = aAnchor + t;
            clientA.WaitForClientTick(targetTick);
            clientASamples.Add(CaptureSample(clientA, targetTick));
            clientBSamples.Add(CaptureSample(clientB, targetTick));
            serverSamples.Add(CaptureSample(server, targetTick));
        }

        int driverMispredicts   = ReadMispredictCount(clientA) - driverBaseline;
        int observerMispredicts = ReadMispredictCount(clientB) - observerBaseline;
        steps.Log($"run complete: driver mispredicts={driverMispredicts}, observer mispredicts={observerMispredicts}");

        WriteRampPlot(paths, label, walkUp, angleDeg, clientASamples, clientBSamples, serverSamples,
            playerAEid, playerBEid, driverBaseline, observerBaseline, jumpPhaseStart);
        CopyProcessLogs(paths);

        // ── 7. Assertions ────────────────────────────────────────────────────
        AssertThat(clientASamples.Count)
            .OverrideFailureMessage("expected non-empty sample stream")
            .IsGreater(0);

        // Sanity: the actor's body should actually have moved along -Z. A
        // misconfigured spawn (e.g. ramp blocks the player at spawn) would
        // leave the position roughly constant; this catches it before the
        // mispredict assertion misdiagnoses a static-stuck player as "no
        // mispredictions, all good".
        float startZ = clientASamples.First().Entities.First(e => e.Id == playerAEid).Position.Z;
        float endZ   = clientASamples.Last().Entities.First(e => e.Id == playerAEid).Position.Z;
        float zTravel = startZ - endZ; // positive = moved in -Z direction (forward)
        AssertThat(zTravel)
            .OverrideFailureMessage(
                $"player A only moved {zTravel:F3} m along -Z over {totalTicks} ticks " +
                $"(start.Z={startZ:F3}, end.Z={endZ:F3}). Expected substantial forward travel — the " +
                $"player is probably stuck against the ramp or wedged at spawn. " +
                $"Trace at TestResults/RampMotionPlots/{label}.{{csv,svg,mp4}}")
            .IsGreater(2.0f);

        AssertThat(driverMispredicts)
            .OverrideFailureMessage(
                $"client A (actor) accrued {driverMispredicts} mispredictions over the {totalTicks}-tick " +
                $"ramp traversal (budget {MispredictBudget}). Constant mispredictions on a static-collider " +
                $"slope indicate the predictor's contact resolution diverges from the server's. " +
                $"Trace at TestResults/RampMotionPlots/{label}.{{csv,svg,mp4}}")
            .IsLessEqual(MispredictBudget);

        AssertThat(observerMispredicts)
            .OverrideFailureMessage(
                $"client B (observer) accrued {observerMispredicts} mispredictions over the run " +
                $"(budget {MispredictBudget}). Observer is idle — any reconciles here are induced by " +
                $"the remote player A's snapshots not matching the observer's predicted/interpolated " +
                $"version. Trace at TestResults/RampMotionPlots/{label}.{{csv,svg,mp4}}")
            .IsLessEqual(MispredictBudget);

        Godot.GD.Print($"[MP-RAMP-MOTION] {label}: {clientASamples.Count} samples, " +
            $"driver mispredicts={driverMispredicts}, observer mispredicts={observerMispredicts}. " +
            $"Artefacts: TestResults/RampMotionPlots/{label}.{{csv,svg,mp4,observer.mp4,steps.log}}");
    }

    // Plot layout: every panel shares the same X axis (network tick). The
    // jump-phase boundary is rendered as a vertical reference line so the
    // reader can see where walk-only ends and walk-and-jump begins; each
    // misprediction across both clients is a separate orange marker so a
    // cluster is visible at a glance.
    private static void WriteRampPlot(ArtifactPaths paths, string label, bool walkUp, float angleDeg,
        List<Sample> clientA, List<Sample> clientB, List<Sample> server,
        int playerAEid, int playerBEid, int driverBaseline, int observerBaseline, int jumpPhaseStart)
    {
        // ── CSV ──────────────────────────────────────────────────────────────
        using (var w = new System.IO.StreamWriter(paths.Csv))
        {
            w.WriteLine("tick,t_s,role,eid,etype,x,y,z,vx,vy,vz,vis_x,vis_y,vis_z,mispredicts");
            int n = System.Math.Min(System.Math.Min(clientA.Count, clientB.Count), server.Count);
            for (int i = 0; i < n; i++)
            {
                WriteRows(w, clientA[i], "clientA", playerAEid, playerBEid, clientA[i].MispredictionsCount);
                WriteRows(w, clientB[i], "clientB", playerAEid, playerBEid, clientB[i].MispredictionsCount);
                WriteRows(w, server[i],  "server",  playerAEid, playerBEid, 0);
            }
        }

        // ── SVG ──────────────────────────────────────────────────────────────
        string direction = walkUp ? "UP" : "DOWN";
        var plot = new SvgPlot($"{label} — rigid player walks {direction} a {angleDeg:F0}° ramp, then walks-and-jumps");
        var pYPanel  = plot.AddPanel("player A body.Y (m) — server (solid) vs clientA.body (dashed) vs clientA.visual (dotted)", yUnits: "m");
        var pZPanel  = plot.AddPanel("player A body.Z (m) — server vs clientA.body vs clientA.visual", yUnits: "m");
        var velMagPanel = plot.AddPanel("player A |velocity| (m/s) — server vs clientA.body", yUnits: "m/s");
        var velYPanel = plot.AddPanel("player A velocity.Y (m/s) — jumps appear as upward spikes", yUnits: "m/s");
        var observerPanel = plot.AddPanel("clientB's view of player A: |body.pos − server.pos| and |visual.pos − server.pos| (m)", yUnits: "m");

        var sY = new List<(int, float)>(); var caY = new List<(int, float)>(); var caVisY = new List<(int, float)>();
        var sZ = new List<(int, float)>(); var caZ = new List<(int, float)>(); var caVisZ = new List<(int, float)>();
        var sVm = new List<(int, float)>(); var caVm = new List<(int, float)>();
        var sVy = new List<(int, float)>(); var caVy = new List<(int, float)>();
        var obsBodyGap = new List<(int, float)>(); var obsVisualGap = new List<(int, float)>();

        int n2 = System.Math.Min(System.Math.Min(clientA.Count, clientB.Count), server.Count);
        for (int i = 0; i < n2; i++)
        {
            int tick = clientA[i].Tick;
            var sP  = server[i].Entities.FirstOrDefault(e => e.Id == playerAEid);
            var caP = clientA[i].Entities.FirstOrDefault(e => e.Id == playerAEid);
            var cbP = clientB[i].Entities.FirstOrDefault(e => e.Id == playerAEid);
            if (sP == null || caP == null) continue;
            sY.Add((tick, sP.Position.Y));   caY.Add((tick, caP.Position.Y));   caVisY.Add((tick, caP.VisualPosition.Y));
            sZ.Add((tick, sP.Position.Z));   caZ.Add((tick, caP.Position.Z));   caVisZ.Add((tick, caP.VisualPosition.Z));
            sVm.Add((tick, sP.Velocity.Length()));
            caVm.Add((tick, caP.Velocity.Length()));
            sVy.Add((tick, sP.Velocity.Y));
            caVy.Add((tick, caP.Velocity.Y));
            if (cbP != null)
            {
                obsBodyGap.Add((tick, (cbP.Position - sP.Position).Length()));
                obsVisualGap.Add((tick, (cbP.VisualPosition - sP.Position).Length()));
            }
        }

        pYPanel.AddSeries("server.body.y",  SvgPlot.Palette.Series[0], sY)
               .AddSeries("clientA.body.y", SvgPlot.Palette.Series[1], caY, dashed: true)
               .AddSeries("clientA.visual.y", SvgPlot.Palette.Series[2], caVisY, dashed: true);
        pZPanel.AddSeries("server.body.z",  SvgPlot.Palette.Series[0], sZ)
               .AddSeries("clientA.body.z", SvgPlot.Palette.Series[1], caZ, dashed: true)
               .AddSeries("clientA.visual.z", SvgPlot.Palette.Series[2], caVisZ, dashed: true);
        velMagPanel.AddSeries("server", SvgPlot.Palette.Series[0], sVm)
                   .AddSeries("clientA", SvgPlot.Palette.Series[1], caVm, dashed: true);
        velYPanel.AddSeries("server.vy",  SvgPlot.Palette.Series[0], sVy)
                 .AddSeries("clientA.vy", SvgPlot.Palette.Series[1], caVy, dashed: true);
        observerPanel.AddSeries("|clientB.body − server.body|", SvgPlot.Palette.Series[3], obsBodyGap)
                     .AddSeries("|clientB.visual − server.body|", SvgPlot.Palette.Series[4], obsVisualGap, dashed: true);

        // Vertical marker at the walk → walk-and-jump transition.
        plot.AddVerticalMarker(jumpPhaseStart, "jumps begin");

        // Mispredict markers: one per increment on either client. Driver and
        // observer increments overlap on the plot but use distinct labels so
        // the SVG reader can attribute each marker to its source.
        int prevA = driverBaseline;
        foreach (var s in clientA)
        {
            if (s.MispredictionsCount > prevA) plot.AddVerticalMarker(s.Tick, "A mispredict");
            prevA = s.MispredictionsCount;
        }
        int prevB = observerBaseline;
        foreach (var s in clientB)
        {
            if (s.MispredictionsCount > prevB) plot.AddVerticalMarker(s.Tick, "B mispredict");
            prevB = s.MispredictionsCount;
        }

        plot.Save(paths.Svg);
        Godot.GD.Print($"[MP-RAMP-MOTION] wrote {paths.Csv}, {paths.Svg} ({n2} samples)");
    }

    private static void WriteRows(System.IO.StreamWriter w, Sample s, string role,
        int playerAEid, int playerBEid, int mispredicts)
    {
        string tickStr = s.Tick.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string tStr = (s.Tick / 60.0).ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var e in s.Entities)
        {
            if (e.Id != playerAEid && e.Id != playerBEid) continue;
            w.Write(tickStr); w.Write(','); w.Write(tStr); w.Write(','); w.Write(role); w.Write(',');
            w.Write(e.Id); w.Write(','); w.Write(e.Type); w.Write(',');
            w.Write(CsvWriter.F(e.Position.X)); w.Write(','); w.Write(CsvWriter.F(e.Position.Y)); w.Write(','); w.Write(CsvWriter.F(e.Position.Z)); w.Write(',');
            w.Write(CsvWriter.F(e.Velocity.X)); w.Write(','); w.Write(CsvWriter.F(e.Velocity.Y)); w.Write(','); w.Write(CsvWriter.F(e.Velocity.Z)); w.Write(',');
            w.Write(CsvWriter.F(e.VisualPosition.X)); w.Write(','); w.Write(CsvWriter.F(e.VisualPosition.Y)); w.Write(','); w.Write(CsvWriter.F(e.VisualPosition.Z)); w.Write(',');
            w.Write(mispredicts); w.Write('\n');
        }
    }
}
