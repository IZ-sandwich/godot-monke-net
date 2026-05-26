using System;
using System.Collections.Generic;
using System.Text.Json;
using GdUnit4;
using Godot;
using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Artifacts;
using MonkeNet.Tests.Infrastructure.Metrics;

namespace MonkeNet.Tests.Quantitative;

/// <summary>
/// Runs the quantitative test matrix: each scenario × each condition produces
/// one row in the summary CSV. Per cell, the runner spawns a fresh server +
/// client pair routed through a <see cref="UdpRelay"/> configured for that
/// cell's conditions, then delegates to <see cref="IScenario.Setup"/> and
/// <see cref="IScenario.Run"/>.
///
/// <para>
/// Why one fresh process pair per cell: clock-sync convergence is one of the
/// metrics, so it must be observed from a cold start. Re-using a client across
/// conditions would let the previous cell's synced clock and prediction
/// history bleed into the next cell.
/// </para>
/// </summary>
public abstract class QuantitativeTestBase : MultiProcessTestBase
{
    /// <summary>Subdirectory under <c>TestResults/</c>. Default is "Quantitative";
    /// override per-test if you want a separate folder per matrix.</summary>
    protected override string ArtifactSubdir => "Quantitative";

    /// <summary>Run every (scenario × condition) cell and write the summary CSV
    /// + per-scenario radar plots. The summary CSV filename embeds the run
    /// timestamp + git commit; see <see cref="MetricsSummaryCsv"/>.</summary>
    protected void RunMatrix(IScenario[] scenarios)
    {
        if (Orch == null)
        {
            GD.Print("[QuantitativeTestBase] GODOT_BIN not set — skipping matrix");
            return;
        }
        _lastRunScenarios = scenarios;
        // Discard the test-base orchestrator; we own per-cell lifecycle.
        Orch?.Dispose();

        // Compute the per-run folder once so cells (which write MP4 + debug
        // logs) and the post-run writers (CSV + dashboard + strip plots)
        // all target the same directory.
        string runFolderName = MetricsSummaryCsv.RunFolderName(ProjectPath);
        _currentRunArtifactDir = System.IO.Path.Combine(
            ProjectPath, "TestResults", ArtifactSubdir, runFolderName);
        System.IO.Directory.CreateDirectory(_currentRunArtifactDir);
        GD.Print($"[QuantitativeTestBase] run folder → {_currentRunArtifactDir}");

        var summary = new MetricsSummaryCsv();
        var perScenarioRows = new Dictionary<string, List<SyncMetrics.Summary>>();

        foreach (var scenario in scenarios)
        {
            perScenarioRows[scenario.Id] = new List<SyncMetrics.Summary>();
            foreach (var condition in scenario.Conditions)
            {
                GD.Print($"[QuantitativeTestBase] running {scenario.Id} × {condition.Id} ({condition.Label})");
                SyncMetrics.Summary row;
                try
                {
                    row = RunOneCell(scenario, condition);
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[QuantitativeTestBase] {scenario.Id} × {condition.Id} FAILED: {ex.Message}");
                    // Surface the failure as a row with NaN-flagged metrics
                    // (M1=-1) rather than silently dropping it — operators
                    // reading the CSV need to see which cells didn't complete.
                    row = new SyncMetrics.Summary
                    {
                        Scenario = scenario.Id,
                        Condition = condition.Id,
                        M1_ClockConvergenceTicks = -1,
                    };
                }
                summary.Add(row);
                perScenarioRows[scenario.Id].Add(row);
            }
        }

        string artifactDir = _currentRunArtifactDir;
        string summaryPath = summary.Save(artifactDir, ProjectPath);
        GD.Print($"[QuantitativeTestBase] summary written → {summaryPath}");

        // Heatmap dashboard: rows = (scenario × condition), columns = metrics.
        // One-page health-of-the-matrix view; readers drill into individual
        // scenarios via the per-scenario strip plots written below.
        WriteDashboard(System.IO.Path.Combine(artifactDir, "dashboard.svg"), perScenarioRows);

        // Per-scenario strip plots. Each scenario gets ONE strip plot showing
        // only its applicable metrics — irrelevant rows (e.g. M3b ext-force
        // on the idle baseline) are filtered out instead of rendering as a
        // strip-wide row of N/A markers. The scenario's full condition matrix
        // is laid out as markers per strip.
        var scenariosById = new Dictionary<string, IScenario>();
        foreach (var s in _lastRunScenarios) scenariosById[s.Id] = s;
        foreach (var (scenarioId, rows) in perScenarioRows)
        {
            if (rows.Count < 1) continue;
            if (!scenariosById.TryGetValue(scenarioId, out var scenario)) continue;
            WriteScenarioStripPlot(
                System.IO.Path.Combine(artifactDir, scenarioId + ".strip.svg"),
                scenarioId, scenario.ApplicableMetrics, rows);
        }
    }

    // Stash so per-scenario writers can look up applicability masks.
    private IScenario[] _lastRunScenarios = Array.Empty<IScenario>();
    // Per-run artifact directory shared by RunMatrix (radar/dashboard SVG
    // outputs) and RunOneCell (per-cell MP4 + debug-log writers). Set once
    // per RunMatrix invocation so every artifact lands in the same per-run
    // folder.
    private string _currentRunArtifactDir = null;

    private SyncMetrics.Summary RunOneCell(IScenario scenario, NetworkCondition condition)
    {
        int serverPort = NextPort();
        using var relay = new UdpRelay(serverPort);
        relay.SetConditions(condition.LatencyMs, condition.JitterMs, condition.LossRate);

        using var orch = new MultiProcessOrchestrator(GodotBin, ProjectPath);
        var server = orch.Spawn("server", enetPort: serverPort, label: "srv");
        server.WaitReady(networkReady: true, timeoutMs: 30_000);

        // Client connects to the relay's listen port, not the server's real
        // port — that's how the injected conditions take effect.
        string videoPath = null;
        if (scenario.RecordVideo && _currentRunArtifactDir != null)
        {
            videoPath = System.IO.Path.Combine(_currentRunArtifactDir,
                $"{scenario.Id}.{condition.Id}.mp4");
        }
        // Defer recorder construction so the captured MP4 doesn't include the
        // ~5 s of clock-sync sampling that runs before scenario.Setup. The
        // recorder is started below right before Setup, so the video begins
        // just as the scenario spawns its entities — which is what a reader
        // actually wants to see.
        var client = orch.Spawn("client", enetPort: relay.ListenPort, label: "c1",
            recordVideoPath: videoPath, deferVideoStart: videoPath != null);
        client.WaitReady(networkReady: true, timeoutMs: 60_000);

        // Spawn a second client when the scenario opts in (e.g. S5 multi-
        // client shared physics). Both clients go through the same relay so
        // both see the same injected network conditions. Once spawned, the
        // scenario gets a chance to stash the reference via SetObserver.
        TestProcess observer = null;
        if (scenario.RequiresObserver)
        {
            observer = orch.Spawn("client", enetPort: relay.ListenPort, label: "c2");
            observer.WaitReady(networkReady: true, timeoutMs: 60_000);
            scenario.SetObserver(observer);
        }

        var metrics = new SyncMetrics();

        // Sample clock-sync convergence aggressively for ~5 seconds — but only
        // when the scenario actually exposes M1/M2 (just S2). M1/M2 are a pure
        // function of the NetworkCondition and the library; the scene contents
        // don't affect them. S2 runs the full condition matrix, so measuring
        // convergence there covers every condition once for the whole suite —
        // every other scenario can skip the 5 s sampling window and just wait
        // briefly for the clock to converge before its action begins.
        if ((scenario.ApplicableMetrics & MetricKey.ClockConvergence) != 0)
        {
            // The library's steady-state clock-sync algorithm uses 11 samples
            // at 1s intervals before applying an averaged correction, so
            // anything shorter than ~3s would only ever see the fast-start
            // phase — but the metric is supposed to characterise the
            // cold-start-to-steady-state behaviour.
            SampleClockConvergence(server, client, metrics, samples: 150, intervalMs: 35);
        }
        else
        {
            // Still wait for the clock to converge before the scenario starts
            // spawning entities, so the trace measures physics misprediction
            // rather than "client clock catching up to server". Per-condition
            // timeout sized to ~p99 × 1.5 of expected cold-start convergence
            // (NetworkCondition.ClockSyncTimeoutMs) — typically returns much
            // sooner; the budget only matters on tail-latency runs.
            WaitForClockSync(server, client, maxGapTicks: 5,
                timeoutMs: condition.ClockSyncTimeoutMs);
        }

        // Reset bandwidth counters before the observation window so the
        // first sample doesn't include warm-up handshake traffic. PopStatistic
        // is destructive, so this read implicitly zeros the counters.
        try { client.Send(new { cmd = "bandwidth-stats" }); } catch { }
        var bandwidthStopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Kick off the deferred recorder (no-op if this cell isn't recording or
        // the recorder is already running). Placed here so the MP4 starts just
        // before entities spawn, trimming the cold-start clock-sync window.
        if (!string.IsNullOrEmpty(videoPath))
        {
            try { using var _ = client.Send(new { cmd = "start-recording" }); }
            catch (Exception ex) { GD.PrintErr($"[QuantitativeTestBase] start-recording failed: {ex.Message}"); }
        }

        // Optional profiler-attach pause. When MONKENET_TEST_PROFILE=1, hold
        // before scenario.Setup so the user can hook dotnet-trace onto the
        // client + server PIDs before the rollback storm starts. The duration
        // defaults to 20 s; override with MONKENET_TEST_PROFILE_PAUSE_MS. The
        // pause resolves early if a trigger file appears at
        // <projectPath>/profile-go.
        MaybeProfilerPause(client, server, scenario, condition);

        scenario.Setup(server, client);
        scenario.Run(server, client, metrics);

        // Snapshot bandwidth + missed-input at the end of the observation
        // window. For S5 (multi-client) we use the observer's counters since
        // the metric of interest is non-authoritative client behaviour.
        TestProcess subject = client;  // override below for S5
        if (!scenario.RequiresObserver)
        {
            using var doc = client.Send(new { cmd = "mispredict-classification-counts" });
            var d = doc.RootElement.GetProperty("data");
            metrics.SetMispredictTotals(
                externalForce: d.GetProperty("externalForce").GetInt32(),
                physicsNondet: d.GetProperty("physicsNondeterminism").GetInt32(),
                degradedNetwork: d.GetProperty("degradedNetwork").GetInt32());
        }
        try
        {
            using var bDoc = subject.Send(new { cmd = "bandwidth-stats" });
            var b = bDoc.RootElement.GetProperty("data");
            int sent = b.GetProperty("sentBytes").GetInt32();
            int recv = b.GetProperty("recvBytes").GetInt32();
            metrics.AddBandwidthSample(sent, recv, bandwidthStopwatch.Elapsed.TotalSeconds);
        }
        catch (Exception ex) { GD.PrintErr($"[QuantitativeTestBase] bandwidth-stats failed: {ex.Message}"); }
        try
        {
            using var miDoc = subject.Send(new { cmd = "missed-input-count" });
            metrics.SetMissedInputTotal(miDoc.RootElement.GetProperty("data").GetProperty("count").GetInt32());
        }
        catch (Exception ex) { GD.PrintErr($"[QuantitativeTestBase] missed-input-count failed: {ex.Message}"); }

        // Copy the client's MonkeLogger debug log into the artifact dir.
        // CopyProcessLog uses FileShare.ReadWrite so we can grab it while the
        // child process still owns the handle; the per-cell filename embeds
        // scenario + condition so multiple cells don't trample each other.
        if (scenario.CopyDebugLog && !string.IsNullOrEmpty(client.RemoteLogPath)
            && _currentRunArtifactDir != null)
        {
            string targetName = $"{scenario.Id}.{condition.Id}.client.log";
            CopyProcessLog(_currentRunArtifactDir, client.RemoteLogPath, targetName);
        }
        // Also copy the server's debug log so cross-process timelines can be
        // reconstructed (e.g. comparing client's auth-snapshot reception
        // timestamps to the server's send timestamps for the same tick).
        if (scenario.CopyDebugLog && !string.IsNullOrEmpty(server.RemoteLogPath)
            && _currentRunArtifactDir != null)
        {
            string targetName = $"{scenario.Id}.{condition.Id}.server.log";
            CopyProcessLog(_currentRunArtifactDir, server.RemoteLogPath, targetName);
        }

        // Mask out metrics the scenario does NOT exercise — render as NaN in
        // the summary so they show as N/A in the dashboard / strip plots
        // rather than as misleading zeros.
        var summary = metrics.ToSummary(scenario.Id, condition.Id);
        MaskInapplicableMetrics(summary, scenario.ApplicableMetrics);
        return summary;
    }

    /// <summary>Hold before scenario.Setup so a PID-attached profiler can
    /// hook the client. Two modes:
    ///
    /// <list type="bullet">
    /// <item><b>Scripted</b> (MONKENET_TEST_PROFILE_DIR set) — write a
    /// handshake file <c>next.txt</c> in the comm dir with PID + scenario +
    /// condition, then poll for a <c>go</c> file the runner script drops
    /// once <c>dotnet-trace</c> is attached. No fallback timeout; the runner
    /// owns the schedule.</item>
    /// <item><b>Manual</b> (MONKENET_TEST_PROFILE=1 but no comm dir) — print
    /// a copy-paste command and sleep for MONKENET_TEST_PROFILE_PAUSE_MS
    /// (default 20 s), or until a <c>profile-go</c> file appears in the cwd.</item>
    /// </list></summary>
    private static void MaybeProfilerPause(TestProcess client, TestProcess server, IScenario scenario, NetworkCondition condition)
    {
        if (!IsEnvTruthy("MONKENET_TEST_PROFILE")) return;

        int clientPid = client.RemotePid;
        int serverPid = server.RemotePid;
        string commDir = System.Environment.GetEnvironmentVariable("MONKENET_TEST_PROFILE_DIR");

        if (!string.IsNullOrEmpty(commDir))
        {
            ScriptedProfilerPause(commDir, clientPid, serverPid, scenario, condition);
            return;
        }

        ManualProfilerPause(clientPid, serverPid, scenario, condition);
    }

    private static void ScriptedProfilerPause(string commDir, int clientPid, int serverPid, IScenario scenario, NetworkCondition condition)
    {
        try { System.IO.Directory.CreateDirectory(commDir); } catch { /* best effort */ }
        string nextFile = System.IO.Path.Combine(commDir, "next.txt");
        string goFile   = System.IO.Path.Combine(commDir, "go");

        // Defensive: clear any stale go from a previous cell so we wait for
        // the runner to drop a fresh one *for this cell*.
        try { if (System.IO.File.Exists(goFile)) System.IO.File.Delete(goFile); } catch { }

        // Hand both PIDs + cell identity to the runner script. Four lines so
        // PowerShell's `Get-Content` reads each part on its own line:
        //   line 0 = client pid
        //   line 1 = server pid
        //   line 2 = scenario id
        //   line 3 = condition id
        try
        {
            System.IO.File.WriteAllText(nextFile,
                $"{clientPid}\n{serverPid}\n{scenario.Id}\n{condition.Id}\n");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[PROFILE] failed to write next.txt: {ex.Message} — falling back to manual mode");
            ManualProfilerPause(clientPid, serverPid, scenario, condition);
            return;
        }

        GD.PrintErr($"[PROFILE] cell handshake → client={clientPid} server={serverPid} {scenario.Id} × {condition.Id} (waiting for runner to attach dotnet-trace)");

        // Bounded wait so a runner crash doesn't hang the suite forever.
        // 5 min is more than enough to launch dotnet-trace even on a slow box.
        var deadline = DateTime.UtcNow.AddMilliseconds(300_000);
        while (DateTime.UtcNow < deadline)
        {
            if (System.IO.File.Exists(goFile))
            {
                try { System.IO.File.Delete(goFile); } catch { }
                GD.PrintErr($"[PROFILE] runner signalled go — resuming {scenario.Id} × {condition.Id}");
                return;
            }
            System.Threading.Thread.Sleep(100);
        }
        GD.PrintErr($"[PROFILE] timed out waiting for runner go signal — resuming without attached trace");
    }

    private static void ManualProfilerPause(int clientPid, int serverPid, IScenario scenario, NetworkCondition condition)
    {
        int pauseMs = 20_000;
        var raw = System.Environment.GetEnvironmentVariable("MONKENET_TEST_PROFILE_PAUSE_MS");
        if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out var parsed) && parsed > 0)
            pauseMs = parsed;

        string traceClient = $"profile-{scenario.Id}.{condition.Id}.client.nettrace";
        string traceServer = $"profile-{scenario.Id}.{condition.Id}.server.nettrace";
        string triggerPath = System.IO.Path.Combine(System.Environment.CurrentDirectory, "profile-go");

        GD.PrintErr("");
        GD.PrintErr("================================================================");
        GD.PrintErr($"[PROFILE] paused before {scenario.Id} × {condition.Id} setup");
        GD.PrintErr($"[PROFILE] client PID = {clientPid}, server PID = {serverPid}");
        GD.PrintErr($"[PROFILE] in another terminal, run (one per process):");
        GD.PrintErr($"[PROFILE]   dotnet-trace collect --process-id {clientPid} --duration 00:00:18 -o {traceClient}");
        GD.PrintErr($"[PROFILE]   dotnet-trace collect --process-id {serverPid} --duration 00:00:18 -o {traceServer}");
        GD.PrintErr($"[PROFILE] then EITHER touch '{triggerPath}' to continue immediately,");
        GD.PrintErr($"[PROFILE] OR wait up to {pauseMs} ms for the pause to lapse.");
        GD.PrintErr("================================================================");

        var deadline = DateTime.UtcNow.AddMilliseconds(pauseMs);
        while (DateTime.UtcNow < deadline)
        {
            if (System.IO.File.Exists(triggerPath))
            {
                try { System.IO.File.Delete(triggerPath); } catch { }
                GD.PrintErr("[PROFILE] trigger file detected — continuing now.");
                break;
            }
            System.Threading.Thread.Sleep(250);
        }
        GD.PrintErr($"[PROFILE] resuming — open '{traceClient}' / '{traceServer}' in https://www.speedscope.app when done.");
    }

    private static bool IsEnvTruthy(string name)
    {
        var v = System.Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(v)) return false;
        v = v.Trim().ToLowerInvariant();
        return v == "1" || v == "true" || v == "yes" || v == "on";
    }

    /// <summary>Replace metrics outside <paramref name="applicable"/> with
    /// <c>float.NaN</c> / sentinel values so downstream artifact writers
    /// render them as N/A. Called per cell once the summary is built.</summary>
    private static void MaskInapplicableMetrics(SyncMetrics.Summary s, MetricKey applicable)
    {
        if ((applicable & MetricKey.M1) == 0)     s.M1_ClockConvergenceTicks = float.NaN;
        if ((applicable & MetricKey.M2) == 0)     s.M2_ClockSteadyStateRmsTicks = float.NaN;
        if ((applicable & MetricKey.M3b) == 0)    s.M3b_ExternalForceRatePct = float.NaN;
        if ((applicable & MetricKey.M4) == 0)     { s.M4_RollbackDepthP50 = float.NaN; s.M4_RollbackDepthP95 = float.NaN; s.M4_RollbackDepthP99 = float.NaN; }
        if ((applicable & MetricKey.M5_rms) == 0) s.M5_PositionErrorRms = float.NaN;
        if ((applicable & MetricKey.M5_p95) == 0) s.M5_PositionErrorP95 = float.NaN;
        if ((applicable & MetricKey.M6) == 0)     s.M6_VisualSmoothRatio = float.NaN;
        if ((applicable & MetricKey.M7) == 0)     s.M7_PostRollbackConvergenceP95 = float.NaN;
        if ((applicable & MetricKey.M9) == 0)     s.M9_MissedInputRatePct = float.NaN;
        if ((applicable & MetricKey.M10) == 0)    s.M10_BandwidthKBps = float.NaN;
    }

    /// <summary>Poll clock-state from both processes and record the gap. Used
    /// at the start of every cell to measure M1/M2 from a cold start. Also
    /// emits one-line GD.Print at start + end of sampling so a transcript shows
    /// the actual gap values for tuning convergence thresholds.</summary>
    private static void SampleClockConvergence(TestProcess server, TestProcess client,
        SyncMetrics metrics, int samples, int intervalMs)
    {
        int firstGap = int.MinValue, lastGap = int.MinValue, minAbs = int.MaxValue, maxAbs = 0;
        for (int i = 0; i < samples; i++)
        {
            try
            {
                using var sDoc = server.Send(new { cmd = "clock-state" });
                using var cDoc = client.Send(new { cmd = "clock-state" });
                int serverTick = sDoc.RootElement.GetProperty("data").GetProperty("serverTick").GetInt32();
                int syncedTick = cDoc.RootElement.GetProperty("data").GetProperty("syncedTick").GetInt32();
                int latency = cDoc.RootElement.GetProperty("data").GetProperty("averageLatencyTicks").GetInt32();
                int gap = syncedTick - serverTick - latency;
                if (firstGap == int.MinValue) firstGap = gap;
                lastGap = gap;
                int abs = Math.Abs(gap);
                if (abs < minAbs) minAbs = abs;
                if (abs > maxAbs) maxAbs = abs;
                metrics.RecordClockSample(gap);
            }
            catch { /* harness not ready yet; continue sampling */ }
            System.Threading.Thread.Sleep(intervalMs);
        }
        GD.Print($"[clock-sample] N={samples} firstGap={firstGap} lastGap={lastGap} |gap| min={minAbs} max={maxAbs}");
    }

    // Stable colour assignment per condition so multiple metric strips
    // colour-match — i.e. C0 is the same blue across every strip.
    private static readonly Dictionary<string, string> ConditionColors = new()
    {
        ["C0"]        = "#1f77b4",   // blue
        ["C1"]        = "#2ca02c",   // green
        ["C2"]        = "#ff7f0e",   // orange
        ["C3"]        = "#d62728",   // red
        ["C4"]        = "#9467bd",   // purple
        ["C5"]        = "#8c564b",   // brown
        ["C2-GoodBroadband"] = "#ff7f0e",
        ["CJITTER"]   = "#17becf",   // cyan — isolated-jitter condition (NetworkCondition.C_Jitter)
    };

    private static string ColorFor(string conditionId) =>
        ConditionColors.TryGetValue(conditionId, out var c) ? c : "#7f7f7f";

    /// <summary>Canonical per-metric spec used by both the dashboard heatmap
    /// and the per-scenario strip plots. Defined once so the two artifacts
    /// stay aligned on thresholds, units, and descriptions.</summary>
    private static readonly StripPlot.MetricSpec[] CanonicalMetrics =
    {
        new StripPlot.MetricSpec { Name = "M1 clock conv",   Unit = "ticks", Threshold = 60f,   AxisMax = 120f,
            Description = "Clock convergence time — ticks until 10 consecutive |gap| < 2-tick samples. Target ≤ 60 (1 s @ 60 Hz). FAIL = no convergence detected." },
        new StripPlot.MetricSpec { Name = "M2 clock RMS",    Unit = "ticks", Threshold = 1.5f,  AxisMax = 5f,
            Description = "Steady-state clock RMS. Reflects underlying network jitter; physically floors at jitterMs / 16.67." },
        new StripPlot.MetricSpec { Name = "M3b ext-force",   Unit = "%",     Threshold = 5f,    AxisMax = 20f,
            Description = "User-visible mispredict rate — server impulses or remote-player collisions the client failed to predict. < 5 % matches Gaffer's 90 % ballistic-accuracy reference." },
        new StripPlot.MetricSpec { Name = "M4 rollback P99", Unit = "ticks", Threshold = 7f,    AxisMax = 60f,
            Description = "99th-percentile rollback depth. GGPO disconnects peers exceeding 7 frames; MonkeNet's 120-tick buffer handles deeper rollbacks but they're visible." },
        new StripPlot.MetricSpec { Name = "M5 pos RMS",      Unit = "m",     Threshold = 0.1f,  AxisMax = 1.0f,
            Description = "Pre-reconcile position error RMS. Matches Gaffer's 0.1 m no-correction floor; above this corrections become visible to the player." },
        new StripPlot.MetricSpec { Name = "M5 pos P95",      Unit = "m",     Threshold = 1.0f,  AxisMax = 5.0f,
            Description = "Tail position error. Must stay below Gaffer's 2 m hard snap-to threshold; P95 captures occasional outliers under bad networks." },
        new StripPlot.MetricSpec { Name = "M6 visual ratio", Unit = "ratio", Threshold = 0.6f,  AxisMax = 1.5f,
            Description = "Visual smoothing effectiveness — mean(visual err) / mean(body err). < 0.6 = smoother cuts perceived error by > 40 %; ~1.0 in steady state (no body error to smooth)." },
        new StripPlot.MetricSpec { Name = "M7 post-RB conv", Unit = "ticks", Threshold = 7f,    AxisMax = 30f,
            Description = "Ticks to recover < 0.1 m post-rollback. Only meaningful in S3 impulse-response where exactly one external force is applied at a known tick." },
        new StripPlot.MetricSpec { Name = "M9 missed input", Unit = "evt",   Threshold = 10f,   AxisMax = 60f,
            Description = "Cumulative count of (tick × entity) events where the predictor had to fall back to default input because no cached server input was available for a remote entity that previously HAD input." },
        new StripPlot.MetricSpec { Name = "M10 bandwidth",   Unit = "kB/s",  Threshold = 5f,    AxisMax = 30f,
            Description = "Client-side sent + received bytes / second. Industry-tuned games target 2–5 kB/s; unoptimised replication is 30–50 kB/s." },
    };

    /// <summary>One-line metric-name + axis-key mapping used to project a
    /// scenario's MetricKey mask down to the columns of CanonicalMetrics.</summary>
    private static readonly MetricKey[] CanonicalMetricKeys =
    {
        MetricKey.M1, MetricKey.M2, MetricKey.M3b, MetricKey.M4,
        MetricKey.M5_rms, MetricKey.M5_p95, MetricKey.M6, MetricKey.M7,
        MetricKey.M9, MetricKey.M10,
    };

    private static void WriteDashboard(string path,
        Dictionary<string, List<SyncMetrics.Summary>> rowsByScenario)
    {
        // Heatmap rows = (scenario × condition). Columns = the canonical 10
        // metrics. Each scenario's applicable-mask hides nothing here — the
        // dashboard SHOWS all 10 columns for every row so cross-scenario
        // comparisons stay column-aligned; N/A cells render greyed-out.
        var axes = new QuantitativeDashboard.AxisSpec[CanonicalMetrics.Length];
        for (int i = 0; i < CanonicalMetrics.Length; i++)
        {
            var m = CanonicalMetrics[i];
            axes[i] = new QuantitativeDashboard.AxisSpec
            {
                Name = m.Name,
                Unit = m.Unit,
                Threshold = m.Threshold,
                Description = m.Description,
            };
        }

        var dashboard = new QuantitativeDashboard(
            title: "MonkeNet quantitative-suite dashboard",
            commit: "",
            timestamp: DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm UTC"),
            axes: axes);

        foreach (var (scenarioId, rows) in rowsByScenario)
        {
            foreach (var r in rows)
            {
                dashboard.AddRow(scenarioId, r.Condition, BuildValueRow(r));
            }
        }
        dashboard.Save(path);
        GD.Print($"[QuantitativeTestBase] dashboard written → {path}");
    }

    private static float[] BuildValueRow(SyncMetrics.Summary r) => new[]
    {
        r.M1_ClockConvergenceTicks, r.M2_ClockSteadyStateRmsTicks,
        r.M3b_ExternalForceRatePct, r.M4_RollbackDepthP99,
        r.M5_PositionErrorRms, r.M5_PositionErrorP95,
        r.M6_VisualSmoothRatio, r.M7_PostRollbackConvergenceP95,
        r.M9_MissedInputRatePct, r.M10_BandwidthKBps,
    };

    /// <summary>Per-scenario strip plot. Metrics outside the scenario's
    /// applicability mask are filtered OUT entirely — the SVG only shows the
    /// strips the scenario actually exercises. With ~3 conditions per scenario
    /// and 3–6 applicable metrics, the plot is compact and metric-focused.</summary>
    private static void WriteScenarioStripPlot(string path, string scenarioId,
        MetricKey applicable, List<SyncMetrics.Summary> rows)
    {
        // Project the canonical 10 down to just the applicable subset.
        var filteredMetrics = new List<StripPlot.MetricSpec>();
        var includedIdx = new List<int>();
        for (int i = 0; i < CanonicalMetrics.Length; i++)
        {
            if ((applicable & CanonicalMetricKeys[i]) != 0)
            {
                filteredMetrics.Add(CanonicalMetrics[i]);
                includedIdx.Add(i);
            }
        }
        if (filteredMetrics.Count == 0) return;

        var plot = new StripPlot(
            title: $"{scenarioId} — metric strip plot",
            subtitle: $"{rows.Count} condition(s); irrelevant metrics hidden",
            metrics: filteredMetrics.ToArray());

        foreach (var r in rows)
        {
            var fullRow = BuildValueRow(r);
            var values = new float[includedIdx.Count];
            for (int j = 0; j < includedIdx.Count; j++) values[j] = fullRow[includedIdx[j]];
            plot.AddCell(new StripPlot.Cell
            {
                Scenario = scenarioId,
                Condition = r.Condition,
                ConditionColor = ColorFor(r.Condition),
                Values = values,
            });
        }
        plot.Save(path);
    }
}
