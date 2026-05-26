using System.Collections.Generic;
using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Metrics;

namespace MonkeNet.Tests.Quantitative.Scenarios;

/// <summary>
/// S2 — Deterministic linear motion. Rigid-body player walks forward at constant
/// velocity on an empty floor; no collisions, no ramps, no external forces. The
/// reference scenario for "prediction quality on fully predictable motion" —
/// M3b should be near zero across every condition, and M5 should stay under
/// Gaffer's 0.1 m no-correction floor.
/// </summary>
public sealed class S2_LinearMotion : IScenario
{
    public string Id => "S2-LinearMotion";
    public NetworkCondition[] Conditions => NetworkCondition.All;
    // Deterministic linear motion — no impulses → M7 N/A; everything else
    // applies (M5 measures position error scale with latency). S2 is also the
    // sole scenario that opts into ClockConvergence (M1/M2): it runs the full
    // condition matrix, M1/M2 don't depend on scene contents, so measuring them
    // here covers every condition and avoids the ~5 s sampling window in every
    // other cell.
    public MetricKey ApplicableMetrics => MetricKey.PhysicsBasicWithClock;

    private const int EntityTypeRigidPlayer = 3;
    private int _playerId;

    public void Setup(TestProcess server, TestProcess client)
    {
        int clientNetId = client.NetworkId;
        using var spawnDoc = server.Send(new
        {
            cmd = "spawn-entity",
            entity_type = EntityTypeRigidPlayer,
            authority = clientNetId,
            position = new[] { 0.0, 1.0, 5.0 },
        });
        _playerId = spawnDoc.RootElement.GetProperty("data").GetProperty("entityId").GetInt32();

        // Let the player drop onto the floor and reach rest before we start
        // driving — otherwise the initial settle phase trips physics-nondeterminism
        // mispredicts that aren't representative of steady-state linear motion.
        server.WaitForTicks(60);
    }

    public void Run(TestProcess server, TestProcess client, SyncMetrics metrics)
    {
        const int RunTicks = 480;             // 8 seconds @ 60 Hz
        const int SampleInterval = 4;

        int anchor = client.ReadClientTick();
        var schedule = new List<object>
        {
            new { tick = anchor + 5,             moveX = 0.0, moveY = -1.0, yaw = 0.0, keys = 0 },
            new { tick = anchor + RunTicks - 5,  moveX = 0.0, moveY =  0.0, yaw = 0.0, keys = 0 },
        };
        client.Send(new { cmd = "set-input-schedule", entries = schedule });

        long startTick = S1_Idle.ReadServerTick(server);
        long endTick = startTick + RunTicks;
        int lastMispredict = 0;
        for (long t = startTick; t < endTick; t += SampleInterval)
        {
            try
            {
                var srv = MultiProcessTestBase.CaptureSampleStatic(server, (int)t);
                var cli = MultiProcessTestBase.CaptureSampleStatic(client, (int)t);
                S1_Idle.RecordError(srv, cli, metrics, _playerId);

                // Sample rollback depth on every mispredict-count increase.
                if (cli.MispredictionsCount > lastMispredict)
                {
                    using var dDoc = client.Send(new { cmd = "rollback-depth-sample" });
                    int depth = dDoc.RootElement.GetProperty("data").GetProperty("depth").GetInt32();
                    metrics.RecordRollbackDepth(depth);
                    lastMispredict = cli.MispredictionsCount;
                }
            }
            catch { /* keep sampling on transient errors */ }
            metrics.AddObservationTicks(SampleInterval);
            System.Threading.Thread.Sleep(20);
        }
    }
}
