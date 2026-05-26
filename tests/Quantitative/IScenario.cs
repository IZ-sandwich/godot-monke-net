using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Metrics;

namespace MonkeNet.Tests.Quantitative;

/// <summary>
/// One scenario in the quantitative test matrix. The runner takes the scenario,
/// pairs it with a <see cref="NetworkCondition"/>, and produces a row in the
/// summary CSV.
///
/// <para>
/// Lifecycle: <c>Setup</c> spawns whatever entities the scenario needs and
/// installs input schedules. <c>Run</c> drives the scenario forward for its
/// own observation window, calling the <see cref="SyncMetrics"/> recorders.
/// The runner snapshots cumulative mispredict counts before and after so
/// metrics see only this scenario's contribution.
/// </para>
/// </summary>
public interface IScenario
{
    /// <summary>Short identifier (e.g. "S1-Idle"). Forms the scenario column
    /// in the summary CSV and the directory name for per-scenario artifacts.</summary>
    string Id { get; }

    /// <summary>Which network conditions to run this scenario at. Most scenarios
    /// return <see cref="NetworkCondition.All"/>; fixed-condition scenarios (S3
    /// impulse response, S8 degradation) return a single condition.</summary>
    NetworkCondition[] Conditions { get; }

    /// <summary>True if this scenario needs a second client (observer / 2-player)
    /// in addition to the driving client. Defaults to false; scenarios like
    /// S5 multi-client shared physics override to true. When true the runner
    /// also calls <see cref="SetObserver"/> with the spawned observer process
    /// before <see cref="Setup"/>.</summary>
    bool RequiresObserver => false;

    /// <summary>True if the scenario should produce an MP4 video of the
    /// client's viewport. Default false. Enabling it forces the client to
    /// spawn in WINDOWED mode (so the engine actually renders), which costs
    /// the per-cell runtime ~10–20 s of ffmpeg-finalisation; enable only for
    /// scenarios where the video aids interpretation (e.g. S7 multi-body
    /// chaos, where a per-cell MP4 lets a reader see what the cube/ball
    /// pile actually does under different network conditions).</summary>
    bool RecordVideo => false;

    /// <summary>True if the scenario should copy the client's MonkeLogger
    /// debug log into the artifact directory alongside the other outputs.
    /// Default false; useful for scenarios whose video output benefits from
    /// the matching debug trace (S7 multi-body chaos in particular).</summary>
    bool CopyDebugLog => false;

    /// <summary>Which metrics this scenario actually exercises. Metrics
    /// outside this mask render as N/A in artifacts rather than showing a
    /// meaningless zero. Defaults to <see cref="MetricKey.All"/>; scenarios
    /// narrow it to flag clock-only or physics-without-impulse subsets.</summary>
    MetricKey ApplicableMetrics => MetricKey.All;

    /// <summary>Callback invoked by the runner to hand the observer process to
    /// the scenario. Default impl is a no-op; observer-aware scenarios stash
    /// the reference for use in Setup/Run.</summary>
    void SetObserver(TestProcess observer) { }

    /// <summary>Spawn entities, install input schedules, wait for clock sync.
    /// Called once per (scenario, condition) cell after the relay is configured
    /// and processes are spawned + ready.</summary>
    void Setup(TestProcess server, TestProcess client);

    /// <summary>Drive the scenario forward and record metrics. Called once per
    /// cell after Setup completes.</summary>
    void Run(TestProcess server, TestProcess client, SyncMetrics metrics);
}
