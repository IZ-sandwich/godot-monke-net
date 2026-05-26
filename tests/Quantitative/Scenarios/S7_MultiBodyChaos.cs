using System.Collections.Generic;
using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Metrics;

namespace MonkeNet.Tests.Quantitative.Scenarios;

/// <summary>
/// S7 — Multi-body chaos. Player walks into a pile of 20 cubes + 20 balls.
/// Tests prediction quality and rollback cost at high entity count with
/// complex contact manifolds — each rollback must resimulate all 40 bodies,
/// so this is a stress test for M8 (scalability) and M4 (rollback depth).
///
/// <para>
/// Per the plan: M3a (physics-nondeterminism rate) will be high by design;
/// the metric that actually matters is <b>M3b (external-force rate) &lt; 10%</b>
/// — the player-contact events are the only true external forces, the rest is
/// sub-millimeter Jolt FP noise from inter-body contacts and not user-visible.
/// </para>
/// </summary>
public sealed class S7_MultiBodyChaos : IScenario
{
    public string Id => "S7-MultiBodyChaos";
    public NetworkCondition[] Conditions { get; } =
    {
        NetworkCondition.C0_Baseline,
        NetworkCondition.C2_GoodBroadband,
        NetworkCondition.C3_AvgBroadband,
        NetworkCondition.C4_Poor,
    };

    // Record a per-cell MP4 of the client's viewport. The pile-collision
    // scenario is the most visually informative one in the matrix — a video
    // lets a reader see exactly what 40 bodies under prediction look like
    // when latency degrades. Costs an extra ~15 s per cell for ffmpeg
    // finalisation; worth it for the diagnostic value.
    public bool RecordVideo => true;

    // Companion to the video: the MonkeLogger debug log is rich with
    // per-tick PRED-* and PHYS-RB-* lines that explain what each frame in
    // the video is doing. Copying it next to the MP4 means a reader can
    // open both side-by-side without hunting through user://.
    public bool CopyDebugLog => true;

    public MetricKey ApplicableMetrics => MetricKey.PhysicsBasic;

    private const int EntityTypeBall = 1;
    private const int EntityTypeRigidPlayer = 3;
    private const int EntityTypeCube = 4;

    private const int CubeCount = 20;
    private const int BallCount = 20;

    private int _playerId;
    private readonly List<int> _propIds = new();

    public void Setup(TestProcess server, TestProcess client)
    {
        int clientNetId = client.NetworkId;

        // Player starts 8m in front of the pile so a forward walk closes the
        // distance and triggers contact at a consistent tick across conditions.
        using var pDoc = server.Send(new
        {
            cmd = "spawn-entity",
            entity_type = EntityTypeRigidPlayer,
            authority = clientNetId,
            position = new[] { 0.0, 1.0, 8.0 },
        });
        _playerId = pDoc.RootElement.GetProperty("data").GetProperty("entityId").GetInt32();

        // Spawn cubes in a 5×4 grid at +X, balls in a 5×4 grid offset along -X.
        // Both groups sit on top of the player's path so the walk pushes through
        // them. Y is staggered so they pile rather than line up.
        for (int i = 0; i < CubeCount; i++)
        {
            float x = -2.5f + (i % 5) * 1.1f;
            float y = 0.6f + (i / 5) * 1.1f;
            using var d = server.Send(new
            {
                cmd = "spawn-entity",
                entity_type = EntityTypeCube,
                authority = 0,
                position = new[] { (double)x, y, 0.0 },
            });
            _propIds.Add(d.RootElement.GetProperty("data").GetProperty("entityId").GetInt32());
        }
        for (int i = 0; i < BallCount; i++)
        {
            float x = 2.5f - (i % 5) * 1.1f;
            float y = 0.6f + (i / 5) * 1.1f;
            using var d = server.Send(new
            {
                cmd = "spawn-entity",
                entity_type = EntityTypeBall,
                authority = 0,
                position = new[] { (double)x, y, 0.0 },
            });
            _propIds.Add(d.RootElement.GetProperty("data").GetProperty("entityId").GetInt32());
        }

        // Settle phase — let the pile come to rest before the player engages.
        // 240 ticks (4 s) gives stacked bodies time to fully sleep.
        server.WaitForTicks(240);

        // Park the camera at a fixed world position pointed at the centre of
        // the pile. A follow-camera was hiding the very artifact this test
        // exists to diagnose — when the camera tracks the player, any
        // teleport / smoother offset translates the *background* relative to
        // the player rather than the player relative to the world, so a
        // viewer cannot read forward/backward motion from the player's
        // on-screen pixels alone. With a static camera the player's pixel
        // position IS the world Z (modulo perspective) and rollback snaps
        // become unmistakable.
        //
        // The harness only installs an observer camera in windowed
        // (video-recording) mode, so these commands error on headless
        // clients — swallow failures so non-video runs don't break.
        try
        {
            client.Send(new
            {
                cmd = "set-camera",
                position = new[] { 12.0, 4.0, 5.0 },
                lookAt = new[] { 0.0, -1.0, 4.0 },
            });
        }
        catch { /* headless: no camera to set */ }
    }

    public void Run(TestProcess server, TestProcess client, SyncMetrics metrics)
    {
        const int RunTicks = 900;             // 15 seconds @ 60 Hz
        const int SampleInterval = 6;

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

                // Record position error for the player + every prop. Averaging
                // across 40 bodies smooths per-body Jolt noise out of M5.
                S1_Idle.RecordError(srv, cli, metrics, _playerId);
                foreach (var eid in _propIds)
                    S1_Idle.RecordError(srv, cli, metrics, eid);

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
            System.Threading.Thread.Sleep(25);
        }
    }
}
