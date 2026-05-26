namespace MonkeNet.Tests.Quantitative;

/// <summary>
/// One row from the quantitative-suite network condition matrix. Compact,
/// human-readable label + the three parameters the UDP relay needs to inject
/// the condition.
/// </summary>
public sealed class NetworkCondition
{
    public string Id { get; }
    public string Label { get; }
    public int LatencyMs { get; }
    public float LossRate { get; }
    public int JitterMs { get; }
    /// <summary>Upper bound for `WaitForClockSync` in this condition. Sized to
    /// roughly p99 × 1.5 of expected cold-start clock convergence — gives the
    /// tail ~50 % headroom without runaway hangs. Drives the per-cell settle
    /// phase before scenario.Setup; previously a flat 5 s for every cell.
    /// p99 model: fast-start cadence (100 ms × 6 samples) + RTT + a couple of
    /// EWMA slew steps, plus 1–3 lost-packet retries at the cell's loss rate
    /// (Binomial(6, loss) tail). See QuantitativeTestBase.WaitForClockSync.</summary>
    public int ClockSyncTimeoutMs { get; }

    public NetworkCondition(string id, string label, int latencyMs, float lossRate, int jitterMs,
        int clockSyncTimeoutMs)
    {
        Id = id;
        Label = label;
        LatencyMs = latencyMs;
        LossRate = lossRate;
        JitterMs = jitterMs;
        ClockSyncTimeoutMs = clockSyncTimeoutMs;
    }

    public override string ToString() => $"{Id} ({Label}: {LatencyMs}ms / {LossRate:P0} loss / ±{JitterMs}ms jitter)";

    // The canonical matrix from the plan. Tests use IDs C0..C5 to filter which
    // conditions to run for a given scenario; the matrix is the single source
    // of truth for "what does C3 mean?".
    public static readonly NetworkCondition C0_Baseline       = new("C0",      "Baseline",       latencyMs:   0, lossRate: 0.00f, jitterMs:  0, clockSyncTimeoutMs:  750);
    public static readonly NetworkCondition C1_Lan            = new("C1",      "LAN",            latencyMs:  50, lossRate: 0.00f, jitterMs:  0, clockSyncTimeoutMs:  900);
    public static readonly NetworkCondition C2_GoodBroadband  = new("C2",      "GoodBroadband",  latencyMs: 100, lossRate: 0.01f, jitterMs: 10, clockSyncTimeoutMs: 1500);
    public static readonly NetworkCondition C3_AvgBroadband   = new("C3",      "AvgBroadband",   latencyMs: 200, lossRate: 0.02f, jitterMs: 20, clockSyncTimeoutMs: 2700);
    public static readonly NetworkCondition C4_Poor           = new("C4",      "Poor",           latencyMs: 300, lossRate: 0.05f, jitterMs: 30, clockSyncTimeoutMs: 4800);
    public static readonly NetworkCondition C5_Stress         = new("C5",      "Stress",         latencyMs: 300, lossRate: 0.10f, jitterMs: 50, clockSyncTimeoutMs: 8000);
    // Isolated-jitter profile. Picked up from the (now-removed) S6_JitterStress
    // scenario so the clock-sync metrics still get a high-jitter / zero-loss
    // cell — S2 is the only scenario that measures M1/M2, so the jitter
    // regression check lives there now.
    public static readonly NetworkCondition C_Jitter          = new("CJITTER", "JitterIsolated", latencyMs:  50, lossRate: 0.00f, jitterMs: 50, clockSyncTimeoutMs: 2300);

    public static readonly NetworkCondition[] All =
    {
        C0_Baseline, C1_Lan, C2_GoodBroadband, C3_AvgBroadband, C4_Poor, C5_Stress, C_Jitter,
    };
}
