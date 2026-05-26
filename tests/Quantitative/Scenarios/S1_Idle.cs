using System.Text.Json;
using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Metrics;

namespace MonkeNet.Tests.Quantitative.Scenarios;

/// <summary>
/// S1 — Idle baseline. Server + 1 client, one static ball entity, no input.
/// Observes the physics-nondeterminism noise floor for everything else in the
/// matrix: with no movement and no interaction, any non-zero M5 or M3
/// indicates cross-process drift that all other scenarios will compound.
/// </summary>
public sealed class S1_Idle : IScenario
{
    public string Id => "S1-Idle";
    public NetworkCondition[] Conditions { get; } = { NetworkCondition.C0_Baseline };
    // Idle baseline exercises bandwidth only. No movement = no mispredicts,
    // no rollbacks, no impulses → M3b/M4/M5/M6/M7/M9 are N/A. M1/M2 are
    // measured by S2 across the full condition matrix (see ClockConvergence).
    public MetricKey ApplicableMetrics => MetricKey.ClockOnly;

    private const int EntityTypeBall = 1;
    private int _ballId;

    public void Setup(TestProcess server, TestProcess client)
    {
        // Spawn one server-authoritative ball above the floor; let it settle.
        using var doc = server.Send(new
        {
            cmd = "spawn-entity",
            entity_type = EntityTypeBall,
            authority = 0,
            position = new[] { 0.0, 1.0, 0.0 },
        });
        _ballId = doc.RootElement.GetProperty("data").GetProperty("entityId").GetInt32();

        // Allow physics to settle so the ball is at rest before sampling starts.
        server.WaitForTicks(120);
    }

    public void Run(TestProcess server, TestProcess client, SyncMetrics metrics)
    {
        // 10 seconds of observation at 60 Hz = 600 ticks. Sample every 4 ticks
        // to keep the RPC overhead bounded.
        const int ObservationTicks = 600;
        const int SampleInterval = 4;

        long startTick = ReadServerTick(server);
        long endTick = startTick + ObservationTicks;
        for (long t = startTick; t < endTick; t += SampleInterval)
        {
            try
            {
                var srvSample = MultiProcessTestBase.CaptureSampleStatic(server, (int)t);
                var cliSample = MultiProcessTestBase.CaptureSampleStatic(client, (int)t);
                RecordError(srvSample, cliSample, metrics, _ballId);
            }
            catch { /* keep sampling on transient JSON errors */ }
            metrics.AddObservationTicks(SampleInterval);
            System.Threading.Thread.Sleep(20);
        }
    }

    internal static void RecordError(Sample srv, Sample cli, SyncMetrics metrics, int entityId)
    {
        var sEnt = FindById(srv, entityId);
        var cEnt = FindById(cli, entityId);
        if (sEnt == null || cEnt == null) return;
        float bodyErr = (sEnt.Position - cEnt.Position).Length();
        float visualErr = (sEnt.Position - cEnt.VisualPosition).Length();
        metrics.RecordPositionError(bodyErr, visualErr);
    }

    internal static EntityState FindById(Sample s, int eid)
    {
        foreach (var e in s.Entities) if (e.Id == eid) return e;
        return null;
    }

    internal static long ReadServerTick(TestProcess server)
    {
        using var doc = server.Send(new { cmd = "tick-count" });
        return doc.RootElement.GetProperty("data").GetProperty("ticks").GetInt64();
    }
}
