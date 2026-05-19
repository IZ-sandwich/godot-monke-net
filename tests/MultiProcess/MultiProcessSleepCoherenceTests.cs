using System.Collections.Generic;
using System.Text;
using GdUnit4;
using Godot;
using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Artifacts;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.MultiProcess;

/// <summary>
/// MP-SLEEP-COHERENCE-01: cross-process variant of RBS-02 in
/// <see cref="MonkeNet.Tests.Integration.RigidbodyStabilityTests"/>. Spawns a
/// single server-authoritative cube above the floor and watches when both
/// sides transition to <c>Sleeping=true</c>. The two independent Jolt
/// instances should reach sleep within a few ticks of each other; a large
/// asymmetry would mean the client and server have meaningfully different
/// rest detection (a real production-bug surface — sleep state divergence
/// causes snapshot SyncSleepState to flip the body in and out of the awake
/// pool, costing a snap each time).
///
/// Visible test output (this is the primary diagnostic surface — the SVG is
/// boring because the cube barely moves):
///   - One-line summary of the project's sleep config at run start.
///   - Per-30-tick progress line showing both sides' |v|, |w|, and sleeping.
///   - Exact sleep-transition tick on each side, and the |v|/|w| at the tick
///     just before that transition ("values that pushed it under threshold").
///   - Final summary with delta and assertion budget.
///
/// Artefacts under <c>TestResults/SleepCoherence/</c>:
///   - sleep_coherence.csv  (per-tick per-side velocity + sleep flags)
///   - sleep_coherence.svg  (vy on both sides + sleep markers)
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class MultiProcessSleepCoherenceTests : MultiProcessTestBase
{
    protected override string ArtifactSubdir => "SleepCoherence";

    [BeforeTest] public void SetUp() => SetUpInternal();
    [AfterTest]  public void TearDown() => TearDownInternal();

    private const byte EntityTypeCube = 4;

    private const int SnapshotArmTicks = 60;
    private const int MaxRunTicks = 600;        // 10 s
    private const int SnapshotIntervalTicks = 1;
    private const int ProgressLogEveryNTicks = 30;
    private const int MaxSleepTickDelta = 5;

    // Tower geometry — matches MultiProcessMispredictTests' tower so the
    // sleep-coherence test exercises the same stacked-contact scenario.
    private const int TowerCubeCount = 5;
    private const float CubeSpacingY = 1.5f;
    private const float TowerBaseY = 0.5f;

    [TestCase]
    public void MultiProcess_CubeRest_BothSidesReachSleepInLockstep()
    {
        if (Orch == null) return;

        // Video off: the cube barely moves; the SVG and the printed log do the diagnostic work.
        var (server, client) = SpawnPair("sleep_coherence", recordVideo: false);

        server.WaitForTicks(SnapshotArmTicks);

        // Read the project's sleep thresholds from Godot's project settings.
        // These are the criteria the cube is racing against on each side.
        var settings = ProjectSettings.Singleton;
        double linearSleepThreshold = (double)(settings.GetSettingWithOverride("physics/3d/sleep_threshold_linear").AsDouble());
        double angularSleepThreshold = (double)(settings.GetSettingWithOverride("physics/3d/sleep_threshold_angular").AsDouble());
        double sleepTimeBeforeSleep = (double)(settings.GetSettingWithOverride("physics/3d/time_before_sleep").AsDouble());

        // Spawn the cube ~1 m above the floor so it free-falls a short distance,
        // bounces once or twice, and comes to rest. Default cube friction/damping
        // — we explicitly want to test the standard sleep behaviour, NOT an
        // ice-puck override.
        int cubeEid = SpawnEntity(server, EntityTypeCube, authority: 0, 0f, 1.0f, 0f);
        WaitForClientEntity(client, cubeEid, timeoutMs: 5_000);

        Godot.GD.Print("[MP-SLEEP-COHERENCE] sleep config: " +
            $"linearThreshold={linearSleepThreshold:F4} m/s, " +
            $"angularThreshold={angularSleepThreshold:F4} rad/s, " +
            $"timeBeforeSleep={sleepTimeBeforeSleep:F3} s — both sides race these criteria");

        int startTick = client.ReadClientTick();
        int serverSleepTick = -1;
        int clientSleepTick = -1;
        var serverSamples = new List<Sample>();
        var clientSamples = new List<Sample>();

        // Per-side: last sample BEFORE sleeping flipped true (used to print the
        // "values that pushed it under threshold" diagnostic at the end).
        EntityState lastBeforeServerSleep = null;
        EntityState lastBeforeClientSleep = null;

        for (int t = SnapshotIntervalTicks; t <= MaxRunTicks; t += SnapshotIntervalTicks)
        {
            int targetTick = startTick + t;
            client.WaitForClientTick(targetTick);

            var sSample = CaptureSample(server, targetTick);
            var cSample = CaptureSample(client, targetTick);
            serverSamples.Add(sSample);
            clientSamples.Add(cSample);

            var sCube = FindEntity(sSample, cubeEid);
            var cCube = FindEntity(cSample, cubeEid);

            // Capture the "last awake" pose for diagnostics.
            if (sCube != null && !sCube.Sleeping && serverSleepTick < 0) lastBeforeServerSleep = sCube;
            if (cCube != null && !cCube.Sleeping && clientSleepTick < 0) lastBeforeClientSleep = cCube;

            // Detect first sleep transition per side.
            if (sCube != null && sCube.Sleeping && serverSleepTick < 0) serverSleepTick = targetTick;
            if (cCube != null && cCube.Sleeping && clientSleepTick < 0) clientSleepTick = targetTick;

            // Periodic progress line.
            if (t % ProgressLogEveryNTicks == 0)
            {
                Godot.GD.Print($"[MP-SLEEP-COHERENCE] t={targetTick}  " +
                    $"server: |v|={Mag(sCube?.Velocity):F4} |w|={Mag(sCube?.AngularVelocity):F4} sleeping={sCube?.Sleeping}  |  " +
                    $"client: |v|={Mag(cCube?.Velocity):F4} |w|={Mag(cCube?.AngularVelocity):F4} sleeping={cCube?.Sleeping}");
            }

            // Early exit once BOTH sides have slept — no point continuing to
            // burn ticks on a static scene.
            if (serverSleepTick >= 0 && clientSleepTick >= 0)
            {
                // Run a few more ticks to capture the "both asleep" tail on
                // the SVG so it doesn't end at the exact transition.
                int tailStop = System.Math.Max(serverSleepTick, clientSleepTick) + 30 - startTick;
                if (t >= tailStop) break;
            }
        }

        // Per-side "values that pushed it under threshold" lines — print the
        // velocity magnitudes at the tick just before sleep flipped true.
        Godot.GD.Print(lastBeforeServerSleep != null
            ? $"[MP-SLEEP-COHERENCE] server pre-sleep state: " +
              $"|v|={Mag(lastBeforeServerSleep.Velocity):F6} (threshold {linearSleepThreshold:F4})  " +
              $"|w|={Mag(lastBeforeServerSleep.AngularVelocity):F6} (threshold {angularSleepThreshold:F4})"
            : "[MP-SLEEP-COHERENCE] server never recorded a pre-sleep sample (already sleeping on first capture?)");
        Godot.GD.Print(lastBeforeClientSleep != null
            ? $"[MP-SLEEP-COHERENCE] client pre-sleep state: " +
              $"|v|={Mag(lastBeforeClientSleep.Velocity):F6} (threshold {linearSleepThreshold:F4})  " +
              $"|w|={Mag(lastBeforeClientSleep.AngularVelocity):F6} (threshold {angularSleepThreshold:F4})"
            : "[MP-SLEEP-COHERENCE] client never recorded a pre-sleep sample (already sleeping on first capture?)");

        int delta = (serverSleepTick >= 0 && clientSleepTick >= 0)
            ? Mathf.Abs(serverSleepTick - clientSleepTick) : -1;
        Godot.GD.Print($"[MP-SLEEP-COHERENCE] summary: serverSleepTick={serverSleepTick} " +
            $"clientSleepTick={clientSleepTick} delta={delta} (budget {MaxSleepTickDelta})");

        var paths = ArtifactsFor("sleep_coherence");
        WritePlot(paths, serverSamples, clientSamples, cubeEid, serverSleepTick, clientSleepTick);
        CopyProcessLogs(paths);

        AssertThat(serverSleepTick).OverrideFailureMessage(
            $"server-side cube never slept within {MaxRunTicks} ticks. Trace at TestResults/SleepCoherence/sleep_coherence.{{csv,svg}}")
            .IsGreaterEqual(0);
        AssertThat(clientSleepTick).OverrideFailureMessage(
            $"client-side cube never slept within {MaxRunTicks} ticks. Trace at TestResults/SleepCoherence/sleep_coherence.{{csv,svg}}")
            .IsGreaterEqual(0);
        AssertThat(delta).OverrideFailureMessage(
            $"server and client reached sleep state {delta} ticks apart (budget {MaxSleepTickDelta}). " +
            $"Trace at TestResults/SleepCoherence/sleep_coherence.{{csv,svg}}")
            .IsLessEqual(MaxSleepTickDelta);
    }

    // MP-SLEEP-COHERENCE-02 ────────────────────────────────────────────────────
    // 5-cube tower variant. Each cube should transition to sleeping=true on both
    // sides within the same budget; this exercises the harder case of stacked
    // contact (where cross-process Jolt has documented mm-scale drift on the
    // upper cubes — see MultiProcessMispredictTests' comment block).
    //
    // Per-cube sleep tick + |v|/|w| is printed so the test report shows the
    // dynamics on each cube, not just the assert/pass summary.
    [TestCase]
    public void MultiProcess_CubeTower_AllCubesReachSleepInLockstep()
    {
        if (Orch == null) return;

        var (server, client) = SpawnPair("tower_sleep", recordVideo: false);
        server.WaitForTicks(SnapshotArmTicks);

        var cubeEids = new List<int>();
        for (int i = 0; i < TowerCubeCount; i++)
        {
            float y = TowerBaseY + i * CubeSpacingY;
            int eid = SpawnEntity(server, EntityTypeCube, authority: 0, 0f, y, 0f);
            cubeEids.Add(eid);
            WaitForClientEntity(client, eid, timeoutMs: 5_000);
        }

        Godot.GD.Print($"[MP-SLEEP-COHERENCE/tower] spawned {TowerCubeCount}-cube tower (eids {string.Join(",", cubeEids)})");

        int startTick = client.ReadClientTick();
        var serverSamples = new List<Sample>();
        var clientSamples = new List<Sample>();
        var serverSleepTickByEid = new Dictionary<int, int>();
        var clientSleepTickByEid = new Dictionary<int, int>();
        foreach (int eid in cubeEids) { serverSleepTickByEid[eid] = -1; clientSleepTickByEid[eid] = -1; }

        bool allAsleepBoth = false;
        for (int t = SnapshotIntervalTicks; t <= MaxRunTicks && !allAsleepBoth; t += SnapshotIntervalTicks)
        {
            int targetTick = startTick + t;
            client.WaitForClientTick(targetTick);
            var sSample = CaptureSample(server, targetTick);
            var cSample = CaptureSample(client, targetTick);
            serverSamples.Add(sSample);
            clientSamples.Add(cSample);

            foreach (int eid in cubeEids)
            {
                var sCube = FindEntity(sSample, eid);
                var cCube = FindEntity(cSample, eid);
                if (sCube != null && sCube.Sleeping && serverSleepTickByEid[eid] < 0) serverSleepTickByEid[eid] = targetTick;
                if (cCube != null && cCube.Sleeping && clientSleepTickByEid[eid] < 0) clientSleepTickByEid[eid] = targetTick;
            }

            // Early exit once every cube is asleep on both sides (with a tail).
            bool everySleeping = true;
            foreach (int eid in cubeEids)
            {
                if (serverSleepTickByEid[eid] < 0 || clientSleepTickByEid[eid] < 0) { everySleeping = false; break; }
            }
            if (everySleeping)
            {
                int latest = 0;
                foreach (int eid in cubeEids)
                {
                    if (serverSleepTickByEid[eid] > latest) latest = serverSleepTickByEid[eid];
                    if (clientSleepTickByEid[eid] > latest) latest = clientSleepTickByEid[eid];
                }
                int tailStop = latest + 30 - startTick;
                if (t >= tailStop) allAsleepBoth = true;
            }
        }

        // Print per-cube sleep ticks and deltas — this is the diagnostic
        // surface for "which cube in the stack settled last and how do the
        // two sides compare."
        var sb = new StringBuilder();
        sb.AppendLine("[MP-SLEEP-COHERENCE/tower] per-cube sleep transitions:");
        int worstDelta = 0;
        int worstEid = -1;
        foreach (int eid in cubeEids)
        {
            int sT = serverSleepTickByEid[eid];
            int cT = clientSleepTickByEid[eid];
            int delta = (sT >= 0 && cT >= 0) ? Mathf.Abs(sT - cT) : -1;
            sb.AppendLine($"  eid={eid}  serverSleepTick={sT,4}  clientSleepTick={cT,4}  delta={delta}");
            if (delta > worstDelta) { worstDelta = delta; worstEid = eid; }
        }
        Godot.GD.Print(sb.ToString());

        var paths = ArtifactsFor("tower_sleep");
        WriteTowerPlot(paths, serverSamples, clientSamples, cubeEids, serverSleepTickByEid, clientSleepTickByEid);
        CopyProcessLogs(paths);

        foreach (int eid in cubeEids)
        {
            AssertThat(serverSleepTickByEid[eid]).OverrideFailureMessage(
                $"server-side cube eid={eid} never slept within {MaxRunTicks} ticks. " +
                $"Trace at TestResults/SleepCoherence/tower_sleep.{{csv,svg}}").IsGreaterEqual(0);
            AssertThat(clientSleepTickByEid[eid]).OverrideFailureMessage(
                $"client-side cube eid={eid} never slept within {MaxRunTicks} ticks. " +
                $"Trace at TestResults/SleepCoherence/tower_sleep.{{csv,svg}}").IsGreaterEqual(0);
        }
        AssertThat(worstDelta).OverrideFailureMessage(
            $"worst per-cube sleep-tick mismatch was {worstDelta} ticks on eid={worstEid} (budget {MaxSleepTickDelta}). " +
            $"Trace at TestResults/SleepCoherence/tower_sleep.{{csv,svg}}")
            .IsLessEqual(MaxSleepTickDelta);
    }

    private static void WriteTowerPlot(ArtifactPaths paths, List<Sample> serverSamples, List<Sample> clientSamples,
        List<int> cubeEids, Dictionary<int, int> serverSleepTickByEid, Dictionary<int, int> clientSleepTickByEid)
    {
        using (var w = new System.IO.StreamWriter(paths.Csv))
        {
            w.WriteLine("tick,t_s,role,eid,|v|,|w|,sleeping");
            for (int i = 0; i < serverSamples.Count; i++)
            {
                WriteRows(w, serverSamples[i], "server", cubeEids);
                WriteRows(w, clientSamples[i], "client", cubeEids);
            }
        }

        var plot = new SvgPlot("tower_sleep — per-cube velocity decay and sleep transition");
        var vPanel = plot.AddPanel("|v| per cube (m/s) — server solid, client dashed", yUnits: "m/s");
        var wPanel = plot.AddPanel("|w| per cube (rad/s)", yUnits: "rad/s");

        for (int i = 0; i < cubeEids.Count; i++)
        {
            int eid = cubeEids[i];
            string color = SvgPlot.Palette.Series[i % SvgPlot.Palette.Series.Length];
            var sV = new List<(int, float)>();
            var cV = new List<(int, float)>();
            var sW = new List<(int, float)>();
            var cW = new List<(int, float)>();
            for (int j = 0; j < serverSamples.Count; j++)
            {
                var sE = FindEntity(serverSamples[j], eid);
                var cE = FindEntity(clientSamples[j], eid);
                if (sE != null) { sV.Add((serverSamples[j].Tick, sE.Velocity.Length())); sW.Add((serverSamples[j].Tick, sE.AngularVelocity.Length())); }
                if (cE != null) { cV.Add((clientSamples[j].Tick, cE.Velocity.Length())); cW.Add((clientSamples[j].Tick, cE.AngularVelocity.Length())); }
            }
            vPanel.AddSeries($"server eid={eid}", color, sV)
                  .AddSeries($"client eid={eid}", color, cV, dashed: true);
            wPanel.AddSeries($"server eid={eid}", color, sW)
                  .AddSeries($"client eid={eid}", color, cW, dashed: true);

            int sT = serverSleepTickByEid[eid];
            int cT = clientSleepTickByEid[eid];
            if (sT >= 0) plot.AddVerticalMarker(sT, $"eid={eid} server slept", color);
            if (cT >= 0) plot.AddVerticalMarker(cT, $"eid={eid} client slept", color);
        }

        plot.Save(paths.Svg);
        Godot.GD.Print($"[MP-SLEEP-COHERENCE/tower] wrote {paths.Csv}, {paths.Svg}");
    }

    private static void WriteRows(System.IO.StreamWriter w, Sample s, string role, List<int> cubeEids)
    {
        string tickStr = s.Tick.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string tStr = (s.Tick / 60.0).ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        foreach (int eid in cubeEids)
        {
            var e = FindEntity(s, eid);
            if (e == null) continue;
            w.Write(tickStr); w.Write(','); w.Write(tStr); w.Write(','); w.Write(role); w.Write(','); w.Write(eid); w.Write(',');
            w.Write(CsvWriter.F(e.Velocity.Length())); w.Write(',');
            w.Write(CsvWriter.F(e.AngularVelocity.Length())); w.Write(',');
            w.Write(e.Sleeping ? "1" : "0"); w.Write('\n');
        }
    }

    private static EntityState FindEntity(Sample s, int eid)
    {
        foreach (var e in s.Entities) if (e.Id == eid) return e;
        return null;
    }

    private static float Mag(Vector3? v) => v?.Length() ?? 0f;

    private static void WritePlot(ArtifactPaths paths, List<Sample> serverSamples, List<Sample> clientSamples,
        int cubeEid, int serverSleepTick, int clientSleepTick)
    {
        using (var w = new System.IO.StreamWriter(paths.Csv))
        {
            w.WriteLine("tick,t_s,role,vx,vy,vz,wx,wy,wz,sleeping");
            for (int i = 0; i < serverSamples.Count; i++)
            {
                WriteRow(w, serverSamples[i], cubeEid, "server");
                WriteRow(w, clientSamples[i], cubeEid, "client");
            }
        }

        var plot = new SvgPlot("sleep_coherence — server vs client velocity decay and sleep transition");
        var vMagPanel = plot.AddPanel("linear velocity magnitude |v| (m/s)", yUnits: "m/s");
        var wMagPanel = plot.AddPanel("angular velocity magnitude |w| (rad/s)", yUnits: "rad/s");

        var sV = new List<(int, float)>();
        var cV = new List<(int, float)>();
        var sW = new List<(int, float)>();
        var cW = new List<(int, float)>();
        for (int i = 0; i < serverSamples.Count; i++)
        {
            var sE = FindEntity(serverSamples[i], cubeEid);
            var cE = FindEntity(clientSamples[i], cubeEid);
            if (sE != null) { sV.Add((serverSamples[i].Tick, sE.Velocity.Length())); sW.Add((serverSamples[i].Tick, sE.AngularVelocity.Length())); }
            if (cE != null) { cV.Add((clientSamples[i].Tick, cE.Velocity.Length())); cW.Add((clientSamples[i].Tick, cE.AngularVelocity.Length())); }
        }
        vMagPanel.AddSeries("server.|v|", SvgPlot.Palette.Series[0], sV)
                 .AddSeries("client.|v|", SvgPlot.Palette.Series[1], cV, dashed: true);
        wMagPanel.AddSeries("server.|w|", SvgPlot.Palette.Series[0], sW)
                 .AddSeries("client.|w|", SvgPlot.Palette.Series[1], cW, dashed: true);

        if (serverSleepTick >= 0) plot.AddVerticalMarker(serverSleepTick, "server slept", SvgPlot.Palette.Series[0]);
        if (clientSleepTick >= 0) plot.AddVerticalMarker(clientSleepTick, "client slept", SvgPlot.Palette.Series[1]);

        plot.Save(paths.Svg);
        Godot.GD.Print($"[MP-SLEEP-COHERENCE] wrote {paths.Csv}, {paths.Svg}");
    }

    private static void WriteRow(System.IO.StreamWriter w, Sample s, int cubeEid, string role)
    {
        var e = FindEntity(s, cubeEid);
        if (e == null) return;
        string tickStr = s.Tick.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string tStr = (s.Tick / 60.0).ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        w.Write(tickStr); w.Write(','); w.Write(tStr); w.Write(','); w.Write(role); w.Write(',');
        w.Write(CsvWriter.F(e.Velocity.X)); w.Write(','); w.Write(CsvWriter.F(e.Velocity.Y)); w.Write(','); w.Write(CsvWriter.F(e.Velocity.Z)); w.Write(',');
        w.Write(CsvWriter.F(e.AngularVelocity.X)); w.Write(','); w.Write(CsvWriter.F(e.AngularVelocity.Y)); w.Write(','); w.Write(CsvWriter.F(e.AngularVelocity.Z)); w.Write(',');
        w.Write(e.Sleeping ? "1" : "0"); w.Write('\n');
    }
}
