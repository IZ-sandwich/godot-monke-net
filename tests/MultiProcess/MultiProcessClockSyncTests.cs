using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using GdUnit4;
using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Artifacts;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.MultiProcess;

/// <summary>
/// MP-CLOCK-01: clock-sync convergence in the multi-process harness.
///
/// Spawns a real server + real client and samples each side's
/// <c>clock-state</c> at ~20 Hz from the moment the client process is up.
/// Tracks the clock gap (<c>clientSyncedTick − serverTick − latency</c>) over
/// time and asserts that it converges into a Photon-Fusion-2-class window
/// (within a few ticks, within a second of connecting) — i.e. the client's
/// prediction tick lands close to the server's authoritative tick after the
/// expected network latency.
///
/// Two artefact SVGs are written for visual inspection:
///   - <c>clock_sync.by_tick.svg</c>       X axis = client synced tick. Lines up
///                                         with CLIENT-TICK in the MonkeLogger output.
///   - <c>clock_sync.by_wallclock.svg</c>  X axis = wall-clock ms since first sample.
///                                         Answers "how fast in real time does sync converge?".
///
/// Plus <c>clock_sync.csv</c> with the raw trace, and the per-process
/// MonkeLogger files copied alongside.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class MultiProcessClockSyncTests : MultiProcessTestBase
{
    protected override string ArtifactSubdir => "ClockSync";

    [BeforeTest] public void SetUp() => SetUpInternal();
    [AfterTest]  public void TearDown() => TearDownInternal();

    // Sample cadence + total run length. 50 ms × 200 = 10 s of trace, plenty
    // to see the ramp-up and a few steady-state cycles after the first sync
    // window completes.
    private const int SampleIntervalMs = 50;
    private const int RunMs = 10_000;

    // Photon-Fusion-2-class convergence target. See file's original
    // doc-comment block for the rationale.
    private const int ConvergeWithinMs = 1_000;
    private const int ConvergedAbsGapTicks = 5;
    private const int ConsecutiveConvergedSamples = 4;   // 4 × 50 ms = 200 ms stable

    [TestCase]
    public void MultiProcess_ClockSync_ConvergesWithinFusionClassWindow()
    {
        if (Orch == null) return;

        int port = NextPort();
        var server = Orch.Spawn("server", enetPort: port, label: "srv");
        server.WaitReady(networkReady: true, timeoutMs: 30_000);

        // Spawn the client and DO NOT wait for it to be networkReady — we
        // want to capture the entire ramp-up including the pre-sync window.
        // Sampling starts as soon as the orch socket accepts, then we identify
        // the "connect moment" post-hoc as the first sample where
        // networkReady=true. We bypass SpawnPair() for this reason — it would
        // block until networkReady true on the client, skipping the early ramp.
        var client = Orch.Spawn("client", enetPort: port, label: "c1");

        var samples = new List<ClockSyncPlot.Sample>();
        long t0Ms = -1;
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < RunMs)
        {
            var sample = SampleClocks(server, client);
            if (sample == null) { Thread.Sleep(SampleIntervalMs); continue; }
            if (t0Ms < 0) t0Ms = sample.ClientWallMs;
            samples.Add(sample);
            Thread.Sleep(SampleIntervalMs);
        }

        // Populate client.RemoteLogPath via one ready cmd so log copy works
        // (intentionally not done at the start so pre-network-ready samples
        // are captured in the trace).
        try { client.WaitReady(networkReady: false, timeoutMs: 2_000); } catch { }

        var paths = ArtifactsFor("clock_sync");
        WriteArtifacts(paths, samples, t0Ms, server, client);

        long connectMsRel = -1;
        foreach (var s in samples)
        {
            if (s.NetworkReady) { connectMsRel = s.ClientWallMs - t0Ms; break; }
        }

        long convergedAtMsRel = FindConvergenceMsRel(samples, t0Ms);

        AssertThat(convergedAtMsRel)
            .OverrideFailureMessage(
                $"clock-sync gap never converged below ±{ConvergedAbsGapTicks} ticks for " +
                $"{ConsecutiveConvergedSamples} consecutive samples ({ConsecutiveConvergedSamples * SampleIntervalMs} ms). " +
                $"Trace + plot at TestResults/ClockSync/clock_sync.{{csv,by_tick.svg,by_wallclock.svg}}. " +
                $"Last sample's gap was {(samples.Count > 0 ? samples[^1].ClientSyncedTick - samples[^1].ServerTick - samples[^1].LatencyTicks : 0)} ticks.")
            .IsGreaterEqual(0);

        Godot.GD.Print($"[MP-CLOCK] networkReady at +{connectMsRel} ms, gap converged at +{convergedAtMsRel} ms");

        AssertThat(convergedAtMsRel)
            .OverrideFailureMessage(
                $"clock-sync took {convergedAtMsRel} ms from process start to converge (Fusion-class budget {ConvergeWithinMs} ms). " +
                $"networkReady fired at +{connectMsRel} ms. Plot at TestResults/ClockSync/clock_sync.by_wallclock.svg")
            .IsLessEqual(ConvergeWithinMs);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static ClockSyncPlot.Sample SampleClocks(TestProcess server, TestProcess client)
    {
        try
        {
            using var sDoc = server.Send(new { cmd = "clock-state" });
            using var cDoc = client.Send(new { cmd = "clock-state" });
            var s = sDoc.RootElement.GetProperty("data");
            var c = cDoc.RootElement.GetProperty("data");
            return new ClockSyncPlot.Sample
            {
                ServerWallMs = s.GetProperty("wallMs").GetInt64(),
                ClientWallMs = c.GetProperty("wallMs").GetInt64(),
                ServerTick = s.GetProperty("serverTick").GetInt32(),
                ClientRawTick = c.GetProperty("rawTick").GetInt32(),
                ClientSyncedTick = c.GetProperty("syncedTick").GetInt32(),
                LatencyTicks = c.GetProperty("averageLatencyTicks").GetInt32(),
                JitterTicks = c.GetProperty("jitterTicks").GetInt32(),
                OffsetTicks = c.GetProperty("averageOffsetTicks").GetInt32(),
                SyncWindowsApplied = c.GetProperty("syncWindowsApplied").GetInt32(),
                NetworkReady = c.GetProperty("networkReady").GetBoolean(),
            };
        }
        catch
        {
            return null;
        }
    }

    private static long FindConvergenceMsRel(List<ClockSyncPlot.Sample> samples, long t0Ms)
    {
        int streak = 0;
        int streakStartIdx = -1;
        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            int gap = s.ClientSyncedTick - s.ServerTick - s.LatencyTicks;
            if (Math.Abs(gap) <= ConvergedAbsGapTicks)
            {
                if (streak == 0) streakStartIdx = i;
                streak++;
                if (streak >= ConsecutiveConvergedSamples)
                    return samples[streakStartIdx].ClientWallMs - t0Ms;
            }
            else
            {
                streak = 0;
                streakStartIdx = -1;
            }
        }
        return -1;
    }

    private void WriteArtifacts(ArtifactPaths paths, List<ClockSyncPlot.Sample> samples, long t0Ms,
        TestProcess server, TestProcess client)
    {
        var svgByTick = System.IO.Path.Combine(paths.Directory,
            System.IO.Path.GetFileNameWithoutExtension(paths.Svg) + ".by_tick.svg");
        var svgByWall = System.IO.Path.Combine(paths.Directory,
            System.IO.Path.GetFileNameWithoutExtension(paths.Svg) + ".by_wallclock.svg");
        ClockSyncPlot.WriteCsv(paths.Csv, samples, t0Ms);
        ClockSyncPlot.WriteSvgByNetworkTick(svgByTick, samples, "Clock sync — by client synced tick");
        ClockSyncPlot.WriteSvgByWallClock(svgByWall, samples, "Clock sync — by wall-clock ms", t0Ms);
        Godot.GD.Print($"[MP-CLOCK] wrote {paths.Csv}, {svgByTick}, {svgByWall} ({samples.Count} samples)");

        // ClockSync has its own log paths since SpawnPair was not used; copy
        // them manually via the base-class helper.
        CopyProcessLog(paths.Directory, server.RemoteLogPath, System.IO.Path.GetFileName(paths.ServerLog));
        CopyProcessLog(paths.Directory, client.RemoteLogPath, System.IO.Path.GetFileName(paths.ClientLog));
    }
}
