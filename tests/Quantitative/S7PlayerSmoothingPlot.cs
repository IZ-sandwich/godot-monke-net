using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using GdUnit4;
using MonkeNet.Tests.Infrastructure.Artifacts;

namespace MonkeNet.Tests.Quantitative;

// Post-run plot generator for S7 C4. Reads the client log produced by
// S7C4FocusedSuite, extracts per-CLIENT-TICK rigidbody + visual pose AND
// per-render-frame wallclock timing, and writes a 4-panel SVG keyed on the
// network tick (the same number the HUD shows in the recorded MP4, so a
// viewer can cross-reference any tick they see in the video with the plot).
// One of the panels shows tick-to-tick wallclock duration so engine freezes
// (long physics-process events from heavy reconcile+resim work) jump out as
// spikes.
[TestSuite]
[RequireGodotRuntime]
public class S7PlayerSmoothingPlot : QuantitativeTestBase
{
    [BeforeTest] public void SetUp()    => SetUpInternal();
    [AfterTest]  public void TearDown() => TearDownInternal();

    [TestCase]
    public void PlotFromMostRecentRun()
    {
        string artifactsRoot = Path.Combine(ProjectPath, "TestResults", "Quantitative");
        if (!Directory.Exists(artifactsRoot))
        {
            Godot.GD.Print("[S7PlayerSmoothingPlot] no TestResults/Quantitative — skipping");
            return;
        }

        string logPath = null;
        var runs = Directory.GetDirectories(artifactsRoot);
        Array.Sort(runs);
        Array.Reverse(runs);
        foreach (var run in runs)
        {
            var candidate = Path.Combine(run, "S7-MultiBodyChaos.C4.client.log");
            if (File.Exists(candidate)) { logPath = candidate; break; }
        }
        if (logPath == null)
        {
            Godot.GD.Print("[S7PlayerSmoothingPlot] no S7-MultiBodyChaos.C4.client.log found");
            return;
        }
        Godot.GD.Print($"[S7PlayerSmoothingPlot] reading {logPath}");

        // Two streams: SMOOTH-FRAME (render-frame, has visual pos + wallclock
        // timestamp) and PRED-REG (per-CLIENT-TICK, has body pos keyed by
        // network tick). Cross-correlate them via wallclock timestamps so the
        // visual position can be looked up at each network tick.
        var bodySamples = ParseBodyByTick(logPath);
        var visualSamples = ParseVisualByTimestamp(logPath);
        var absorbs = ParseAbsorbs(logPath);
        var rollbacks = ParseRollbacks(logPath);

        Godot.GD.Print($"[S7PlayerSmoothingPlot] body ticks={bodySamples.Count} visual samples={visualSamples.Count} absorbs={absorbs.Count} rollbacks={rollbacks.Count}");

        if (bodySamples.Count == 0) return;

        // Cross-correlate visual samples to body ticks: for each body tick T,
        // find the nearest visual sample by wallclock. Since both come from
        // the same log, the nearest visual sample within ±50 ms of a tick's
        // log time is the visual state the user saw at that tick.
        var visualByTick = AlignVisualToTicks(bodySamples, visualSamples);

        // Build series keyed on CLIENT-TICK (the HUD number).
        var rawZ = new List<(int, float)>();
        var visZ = new List<(int, float)>();
        var velZ = new List<(int, float)>();
        var rawY = new List<(int, float)>();
        var tickGapMs = new List<(int, float)>();
        DateTime? prevTickTime = null;
        int prevTick = -1;
        foreach (var s in bodySamples)
        {
            rawZ.Add((s.Tick, s.BodyZ));
            rawY.Add((s.Tick, s.BodyY));
            velZ.Add((s.Tick, s.VelZ));
            if (visualByTick.TryGetValue(s.Tick, out var v)) visZ.Add((s.Tick, v));
            if (prevTickTime.HasValue && s.Tick == prevTick + 1)
            {
                float gapMs = (float)(s.Time - prevTickTime.Value).TotalMilliseconds;
                if (gapMs < 0) gapMs += 60000; // logger second-wrap
                tickGapMs.Add((s.Tick, gapMs));
            }
            prevTickTime = s.Time;
            prevTick = s.Tick;
        }

        var plot = new SvgPlot("S7-MultiBodyChaos C4 — player rigidbody vs visual (keyed on network tick = HUD tick)");
        plot.Width = 1600;
        plot.PanelHeight = 160;

        plot.AddPanel("Z position (m)  — forward = -Z (player walks toward 0 and past)")
            .AddSeries("rigidbody.z",  SvgPlot.Palette.Series[0], rawZ)
            .AddSeries("visual.z",     SvgPlot.Palette.Series[1], visZ, dashed: true);

        plot.AddPanel("Y position (m)  — ground at -2.0; spike = lifted off / overlap with cube")
            .AddSeries("rigidbody.y",  SvgPlot.Palette.Series[0], rawY);

        plot.AddPanel("body.vel.z (m/s)  — natural walk ≈ -5.0")
            .AddSeries("body vel.z",   SvgPlot.Palette.Series[2], velZ);

        plot.AddPanel("wallclock duration per CLIENT-TICK (ms)  — 16.67 = real-time, spikes = engine freezes")
            .AddSeries("tick gap ms",  SvgPlot.Palette.Series[3], tickGapMs);

        // Mark each rollback (PRED-ROLLBACK fires once per reconcile-and-resim)
        // with a vertical line keyed on the rollback's tick — that's the
        // server tick the rollback returned to, and lines up with the
        // CLIENT-TICK x-axis on the position panels.
        foreach (var rb in rollbacks)
        {
            plot.AddVerticalMarker(rb.Tick, label: $"rb d={rb.ResimTicks}");
        }

        string outDir = Path.GetDirectoryName(logPath);
        string outPath = Path.Combine(outDir, "S7-MultiBodyChaos.C4.player_smoothing.svg");
        plot.Save(outPath);
        Godot.GD.Print($"[S7PlayerSmoothingPlot] wrote {outPath}");
    }

    private struct BodySample { public int Tick; public DateTime Time; public float BodyY, BodyZ, VelZ; }
    private struct VisualSample { public DateTime Time; public float VisZ; }
    private struct RollbackEvent { public int Tick; public int ResimTicks; }
    private struct AbsorbEvent { public int NearestTick; public float JumpZ; }

    // [DATETIME] [...] [PRED-REG] tick=N eid=1 input=... pos=(X,Y,Z) vel=(VX,VY,VZ) angvel=(...)
    private static readonly Regex BodyRx = new Regex(
        @"\[(?<ts>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3})\].*?\[PRED-REG\] tick=(?<tick>\d+) eid=1 .*?pos=\((?<px>[-\d\.]+),(?<py>[-\d\.]+),(?<pz>[-\d\.]+)\) vel=\((?<vx>[-\d\.]+),(?<vy>[-\d\.]+),(?<vz>[-\d\.]+)\)",
        RegexOptions.Compiled);

    // [DATETIME] [...] [SMOOTH-FRAME] body=1 ... raw=(X,Y,Z) ... vis=(VX,VY,VZ) ...
    private static readonly Regex VisualRx = new Regex(
        @"\[(?<ts>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3})\].*?\[SMOOTH-FRAME\] body=1 .*?vis=\((?<vx>[-\d\.]+),(?<vy>[-\d\.]+),(?<vz>[-\d\.]+)\)",
        RegexOptions.Compiled);

    // [DATETIME] [...] [PRED-ROLLBACK] tick=N entities=E resimTicks=R listenServer=...
    private static readonly Regex RollbackRx = new Regex(
        @"\[PRED-ROLLBACK\] tick=(?<tick>\d+) entities=\d+ resimTicks=(?<resim>\d+)",
        RegexOptions.Compiled);

    private static readonly Regex AbsorbRx = new Regex(
        @"\[SMOOTH-ABSORB\] body=1 prePos=\([^)]+\) postPos=\([^,]+,[^,]+,[^)]+\).*?jump=\([^,]+,[^,]+,(?<jz>[-\d\.]+)\)",
        RegexOptions.Compiled);

    // Parse PRED-REG body=1 entries. Each gives a (CLIENT-TICK, time, pos, vel).
    // The same tick may appear twice (live + resim); we keep only the FIRST
    // occurrence so the plot shows live-prediction trajectory.
    private static List<BodySample> ParseBodyByTick(string path)
    {
        var list = new List<BodySample>();
        var seen = new HashSet<int>();
        DateTime? prevDt = null;
        foreach (var line in File.ReadLines(path))
        {
            var m = BodyRx.Match(line);
            if (!m.Success) continue;
            int tick = int.Parse(m.Groups["tick"].Value, CultureInfo.InvariantCulture);
            if (!seen.Add(tick)) continue;
            DateTime dt = ParseMonotone(m.Groups["ts"].Value, prevDt);
            prevDt = dt;
            list.Add(new BodySample
            {
                Tick  = tick,
                Time  = dt,
                BodyY = float.Parse(m.Groups["py"].Value, CultureInfo.InvariantCulture),
                BodyZ = float.Parse(m.Groups["pz"].Value, CultureInfo.InvariantCulture),
                VelZ  = float.Parse(m.Groups["vz"].Value, CultureInfo.InvariantCulture),
            });
        }
        return list;
    }

    private static List<VisualSample> ParseVisualByTimestamp(string path)
    {
        var list = new List<VisualSample>();
        DateTime? prevDt = null;
        foreach (var line in File.ReadLines(path))
        {
            var m = VisualRx.Match(line);
            if (!m.Success) continue;
            DateTime dt = ParseMonotone(m.Groups["ts"].Value, prevDt);
            prevDt = dt;
            list.Add(new VisualSample
            {
                Time = dt,
                VisZ = float.Parse(m.Groups["vz"].Value, CultureInfo.InvariantCulture),
            });
        }
        return list;
    }

    private static List<RollbackEvent> ParseRollbacks(string path)
    {
        var list = new List<RollbackEvent>();
        foreach (var line in File.ReadLines(path))
        {
            var m = RollbackRx.Match(line);
            if (!m.Success) continue;
            list.Add(new RollbackEvent
            {
                Tick = int.Parse(m.Groups["tick"].Value, CultureInfo.InvariantCulture),
                ResimTicks = int.Parse(m.Groups["resim"].Value, CultureInfo.InvariantCulture),
            });
        }
        return list;
    }

    private static List<AbsorbEvent> ParseAbsorbs(string path)
    {
        var list = new List<AbsorbEvent>();
        foreach (var line in File.ReadLines(path))
        {
            var m = AbsorbRx.Match(line);
            if (!m.Success) continue;
            list.Add(new AbsorbEvent
            {
                NearestTick = 0,
                JumpZ = float.Parse(m.Groups["jz"].Value, CultureInfo.InvariantCulture),
            });
        }
        return list;
    }

    private static Dictionary<int, float> AlignVisualToTicks(List<BodySample> body, List<VisualSample> visual)
    {
        var result = new Dictionary<int, float>();
        if (visual.Count == 0) return result;
        int vi = 0;
        foreach (var b in body)
        {
            // Advance visual pointer until it's >= body tick time.
            while (vi + 1 < visual.Count && visual[vi + 1].Time <= b.Time) vi++;
            // Pick whichever of visual[vi] or visual[vi+1] is closer.
            int pick = vi;
            if (vi + 1 < visual.Count)
            {
                var dPrev = (b.Time - visual[vi].Time).TotalMilliseconds;
                if (dPrev < 0) dPrev = -dPrev;
                var dNext = (visual[vi + 1].Time - b.Time).TotalMilliseconds;
                if (dNext < 0) dNext = -dNext;
                if (dNext < dPrev) pick = vi + 1;
            }
            var d = (b.Time - visual[pick].Time).TotalMilliseconds;
            if (d < 0) d = -d;
            if (d <= 100) // only accept within 100 ms
                result[b.Tick] = visual[pick].VisZ;
        }
        return result;
    }

    // MonkeLogger's seconds field occasionally rolls over without incrementing
    // the minute, producing apparent backward timestamps. Walk forward 60 s
    // at a time until the timestamp is monotonic vs the previous parsed value.
    private static DateTime ParseMonotone(string ts, DateTime? prev)
    {
        var dt = DateTime.ParseExact(ts, "yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);
        if (prev.HasValue)
        {
            while (dt < prev.Value) dt = dt.AddSeconds(60);
        }
        return dt;
    }
}
