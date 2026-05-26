using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MonkeNet.Tests.Infrastructure.Metrics;

/// <summary>
/// Writes the quantitative-suite summary CSV. The filename inside the per-run
/// folder is the fixed <c>summary.csv</c>; the parent folder name (computed
/// by <see cref="RunFolderName"/>) carries the timestamp + commit so each
/// run's artifacts live together and can be diffed by simply diffing folders.
/// Header rows (lines starting with <c>#</c>) embed the full commit hash,
/// branch name, dirty-file list, host OS, and run start time so the CSV is
/// still self-identifying once detached from its folder.
/// </summary>
public sealed class MetricsSummaryCsv
{
    private readonly List<SyncMetrics.Summary> _rows = new();

    public void Add(SyncMetrics.Summary row) => _rows.Add(row);

    /// <summary>Compute the canonical per-run folder name for a quantitative-
    /// suite invocation: <c>{date}--{time}.{shortCommit}[+dirty]</c>, e.g.
    /// <c>2026-05-20--04-15-17.191b9ab+dirty</c>. Used by the runner to root
    /// every artifact (CSV, SVGs, MP4s, logs) under one directory.
    ///
    /// <para>Source of the commit + dirty info: <c>MONKENET_RUN_COMMIT</c> /
    /// <c>MONKENET_RUN_DIRTY</c> env vars first, then fall back to running
    /// <c>git</c> in <paramref name="repoRoot"/>. The env-var path exists so
    /// <c>Invoke-TestInWorktree.ps1</c> can capture the originating-tree's
    /// commit in the outer shell and propagate it into the worktree copy
    /// (which excludes <c>.git</c>); without it, the worktree's git lookup
    /// returns "unknown".</para></summary>
    public static string RunFolderName(string repoRoot)
    {
        (string commitFull, bool dirty) = ResolveCommitAndDirty(repoRoot);
        string commitShort = commitFull.Length >= 7 ? commitFull.Substring(0, 7) : commitFull;
        string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd--HH-mm-ss", CultureInfo.InvariantCulture);
        return $"{timestamp}.{commitShort}{(dirty ? "+dirty" : "")}";
    }

    private static (string commitFull, bool dirty) ResolveCommitAndDirty(string repoRoot)
    {
        string envCommit = Environment.GetEnvironmentVariable("MONKENET_RUN_COMMIT");
        string envDirty  = Environment.GetEnvironmentVariable("MONKENET_RUN_DIRTY");
        if (!string.IsNullOrWhiteSpace(envCommit))
        {
            bool d = string.Equals(envDirty, "true", StringComparison.OrdinalIgnoreCase);
            return (envCommit.Trim(), d);
        }
        string commit = RunGit(repoRoot, "rev-parse HEAD") ?? "unknown";
        bool dirty = !string.IsNullOrWhiteSpace(RunGit(repoRoot, "status --porcelain") ?? "");
        return (commit, dirty);
    }

    /// <summary>Writes the summary CSV into <paramref name="directory"/> as the
    /// fixed filename <c>summary.csv</c>. Returns the absolute path written.</summary>
    public string Save(string directory, string repoRoot)
    {
        Directory.CreateDirectory(directory);

        // Prefer env vars (set by Invoke-TestInWorktree.ps1 from the outer
        // source tree) over running git in the current directory — see the
        // RunFolderName docstring for why.
        string envCommit   = Environment.GetEnvironmentVariable("MONKENET_RUN_COMMIT");
        string envBranch   = Environment.GetEnvironmentVariable("MONKENET_RUN_BRANCH");
        string envDirtyL   = Environment.GetEnvironmentVariable("MONKENET_RUN_DIRTY_LIST");
        string envDirty    = Environment.GetEnvironmentVariable("MONKENET_RUN_DIRTY");
        string commitFull  = !string.IsNullOrWhiteSpace(envCommit) ? envCommit.Trim() : (RunGit(repoRoot, "rev-parse HEAD") ?? "unknown");
        string branch      = !string.IsNullOrWhiteSpace(envBranch) ? envBranch.Trim() : (RunGit(repoRoot, "rev-parse --abbrev-ref HEAD") ?? "unknown");
        string dirtyList   = envDirtyL ?? (RunGit(repoRoot, "status --porcelain") ?? "");
        bool dirty         = !string.IsNullOrWhiteSpace(envCommit)
            ? string.Equals(envDirty, "true", StringComparison.OrdinalIgnoreCase)
            : !string.IsNullOrWhiteSpace(dirtyList);

        string path = Path.Combine(directory, "summary.csv");

        using var w = new StreamWriter(path);
        w.NewLine = "\n";
        w.WriteLine($"# commit: {commitFull}");
        w.WriteLine($"# branch: {branch}");
        w.WriteLine($"# dirty: {(dirty ? "yes" : "no")}");
        if (dirty)
        {
            foreach (var line in dirtyList.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0))
                w.WriteLine($"#   {line}");
        }
        w.WriteLine($"# host: {Environment.OSVersion} / {Environment.MachineName} / {Environment.ProcessorCount} CPUs");
        w.WriteLine($"# run_utc: {DateTime.UtcNow:O}");
        w.WriteLine($"# rows: {_rows.Count}");
        w.WriteLine("#");
        w.WriteLine("scenario,condition,M1_clock_conv_ticks,M2_clock_rms_ticks," +
                    "M3_mispredict_pct,M3a_phys_nondet_pct,M3b_ext_force_pct,M3c_degraded_pct," +
                    "M4_rb_p50,M4_rb_p95,M4_rb_p99," +
                    "M5_pos_rms_m,M5_pos_p95_m,M6_visual_ratio,M7_post_rb_p95_ticks," +
                    "M9_missed_input_pct,M10_bandwidth_kbps," +
                    "obs_ticks,samples");
        var ci = CultureInfo.InvariantCulture;
        foreach (var r in _rows)
        {
            w.WriteLine(string.Join(",",
                Csv(r.Scenario),
                Csv(r.Condition),
                r.M1_ClockConvergenceTicks.ToString(ci),
                r.M2_ClockSteadyStateRmsTicks.ToString("0.###", ci),
                r.M3_MispredictRatePct.ToString("0.###", ci),
                r.M3a_PhysicsNondetRatePct.ToString("0.###", ci),
                r.M3b_ExternalForceRatePct.ToString("0.###", ci),
                r.M3c_DegradedNetworkRatePct.ToString("0.###", ci),
                r.M4_RollbackDepthP50.ToString(ci),
                r.M4_RollbackDepthP95.ToString(ci),
                r.M4_RollbackDepthP99.ToString(ci),
                r.M5_PositionErrorRms.ToString("0.####", ci),
                r.M5_PositionErrorP95.ToString("0.####", ci),
                r.M6_VisualSmoothRatio.ToString("0.###", ci),
                r.M7_PostRollbackConvergenceP95.ToString(ci),
                r.M9_MissedInputRatePct.ToString("0.###", ci),
                r.M10_BandwidthKBps.ToString("0.##", ci),
                r.ObservationTicks.ToString(ci),
                r.SampleCount.ToString(ci)
            ));
        }

        return path;
    }

    private static string Csv(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.IndexOfAny(new[] { ',', '"', '\n' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>Run <c>git &lt;args&gt;</c> in <paramref name="repoRoot"/> and
    /// return stdout trimmed. Returns null on any failure (git not installed,
    /// not a repo, ...). All callers treat null as "unknown".</summary>
    private static string RunGit(string repoRoot, string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            string stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5_000);
            return p.ExitCode == 0 ? stdout.Trim() : null;
        }
        catch { return null; }
    }
}
