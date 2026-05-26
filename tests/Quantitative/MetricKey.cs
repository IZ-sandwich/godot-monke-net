using System;

namespace MonkeNet.Tests.Quantitative;

/// <summary>
/// Stable identifiers for the metrics that appear in summary CSVs and the
/// dashboard. Each scenario opts in to the metrics it actually exercises via
/// <c>IScenario.ApplicableMetrics</c>; metrics not in a scenario's mask render
/// as N/A in artifacts rather than showing a meaningless zero (e.g. M3b
/// external-force rate is undefined for the idle-baseline scenario which
/// never applies any external forces).
/// </summary>
[Flags]
public enum MetricKey
{
    None    = 0,
    M1      = 1 << 0,   // clock convergence (ticks)
    M2      = 1 << 1,   // clock RMS (ticks)
    M3b     = 1 << 2,   // external-force mispredict rate (%)
    M4      = 1 << 3,   // rollback depth P99 (ticks)
    M5_rms  = 1 << 4,   // position error RMS (m)
    M5_p95  = 1 << 5,   // position error P95 (m)
    M6      = 1 << 6,   // visual smoothing ratio
    M7      = 1 << 7,   // post-rollback convergence P95 (ticks)
    M9      = 1 << 8,   // missed-input rate (%)
    M10     = 1 << 9,   // bandwidth (kB/s)

    /// <summary>Clock convergence metrics. Only S2_LinearMotion opts into these
    /// because M1/M2 are purely a function of the NetworkCondition (latency +
    /// jitter + loss) and the library — the scene contents don't affect them.
    /// S2 runs the full condition matrix, so measuring M1/M2 there covers every
    /// condition once and saves the ~5 s sampling window in every other cell.</summary>
    ClockConvergence = M1 | M2,

    /// <summary>Bandwidth-only baseline. Used by idle / static-ball scenarios
    /// that produce no movement and therefore no other physics metrics.</summary>
    ClockOnly = M10,

    /// <summary>Single-client physics scenarios that exercise prediction +
    /// rollback but never an explicit out-of-band impulse.</summary>
    PhysicsBasic = ClockOnly | M3b | M4 | M5_rms | M5_p95 | M6 | M9,

    /// <summary>S2's mask: PhysicsBasic plus the clock-convergence metrics.
    /// S2 is the only scenario that runs the cold-start clock-sync sampling
    /// window; see <see cref="ClockConvergence"/>.</summary>
    PhysicsBasicWithClock = PhysicsBasic | ClockConvergence,

    /// <summary>Full set: every metric is applicable. Used by impulse-response,
    /// multi-body chaos, and multi-client shared physics.</summary>
    All = ClockOnly | M3b | M4 | M5_rms | M5_p95 | M6 | M7 | M9,
}
