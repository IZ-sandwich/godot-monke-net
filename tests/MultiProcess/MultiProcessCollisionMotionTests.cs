using System.Collections.Generic;
using GdUnit4;
using Godot;
using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Artifacts;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.MultiProcess;

/// <summary>
/// MP-COLLISION-MOTION-01/02: head-on collision-motion plot harness.
///
/// Rigid-body player charges straight along -Z into a server-authoritative
/// rigid body sitting on the player's forward axis. Two scenarios:
///   - cube    (EntityType=4): small 1x1x1 box, fully passive-predicted via
///             <see cref="GameDemo.LocalRigidPropPrediction"/>.
///   - vehicle (EntityType=2): larger box-shaped vehicle, same prediction path.
///
/// Both scenarios capture per-tick samples from BOTH processes and produce
/// CSV + SVG + MP4 artefacts. The SVG plots overlay the server's authoritative
/// trace, the client's predicted body trace, and the client's smoothed visual
/// mesh trace — the same picture the deleted single-process CollisionMotion
/// plot test produced, but now driven by the real predict/reconcile pipeline
/// across two Godot processes instead of a hand-rolled in-test loop.
///
/// The previous in-process test enumerated five variants (baseline,
/// listen_cb, listen_rb, host_cb, host_rb). With the CharacterBody player
/// removed from the library only host_rb remains meaningful, and in the
/// multi-instance harness every test IS a host+client process pair — so the
/// five variants collapse into one head-on push scenario per target body.
///
/// Artefacts under <c>TestResults/CollisionMotionPlots/</c>:
///   - head_on_cube.{csv,svg,mp4}
///   - head_on_vehicle.{csv,svg,mp4}
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class MultiProcessCollisionMotionTests : MultiProcessTestBase
{
    protected override string ArtifactSubdir => "CollisionMotionPlots";

    [BeforeTest] public void SetUp() => SetUpInternal();
    [AfterTest]  public void TearDown() => TearDownInternal();

    private const byte EntityTypeVehicle = 2;
    private const byte EntityTypeRigidPlayer = 3;
    private const byte EntityTypeCube = 4;

    // ── scenario geometry ────────────────────────────────────────────────────
    // Target body sits straight ahead of the player on the -Z axis. No lateral
    // offset and no yaw — contact is symmetric so any visible spin or sideways
    // drift on the trace is a divergence between the two Jolt processes, not a
    // designed-in lever arm.
    private const float TargetStartX = 0f;
    private const float TargetStartY = 0.5f;
    private const float TargetStartZ = -2.5f;
    private const float PlayerStartZ = 1.5f;
    private const float PlayerStartY = 0f;

    // ── timing ───────────────────────────────────────────────────────────────
    private const int SnapshotArmTicks = 60;
    private const int TargetSettleTicks = 90;
    private const int PlayerFallTicks = 90;
    private const int PressForwardTicks = 42;
    private const int RunTicks = 150;
    private const int SnapshotIntervalTicks = 2;

    // Test-only physics tuning so the target body coasts long enough for the
    // visual smoother's decay window to be visible on the trace. Demo scenes
    // keep their default friction=0.6 / damp=0.4/0.8.
    private const float TestFriction = 0.15f;
    private const float TestLinearDamp = 0.05f;
    private const float TestAngularDamp = 0.1f;

    // End-of-run pose-agreement budgets. The head-on case is the friendliest
    // collision geometry (no lever arm) but a settle-phase misprediction can
    // leave a few cm of residual drift along the X axis that persists once
    // the body comes to rest before SyncSleepState anchors it to the server
    // pose, so the body budget is set loose enough to absorb that case.
    private const float BodyToleranceM = 0.10f;
    private const float VisualToleranceM = 0.10f;
    // Whole-lifecycle budget — counts mispredicts from spawn through end of
    // coast (target settle + player fall + push + coast). The settle phase
    // can fire a handful of physics-nondeterminism mispredicts as the falling
    // rigid bodies sync between processes; the run phase should add ~0 on top.
    private const int MispredictBudget = 5;

    [TestCase]
    public void MultiProcess_RigidPlayer_HeadOnPushesCube_RendersTraceAndVideo()
        => RunHeadOnScenario("head_on_cube", EntityTypeCube);

    [TestCase]
    public void MultiProcess_RigidPlayer_HeadOnPushesVehicle_RendersTraceAndVideo()
        => RunHeadOnScenario("head_on_vehicle", EntityTypeVehicle);

    private void RunHeadOnScenario(string label, byte targetEntityType)
    {
        if (Orch == null) return;

        var (server, client) = SpawnPair(label, recordVideo: true);

        server.WaitForTicks(SnapshotArmTicks);

        // Capture the misprediction baseline BEFORE any test entities are
        // spawned so every mispredict the test itself triggers — including
        // ones that fire during the target's gravity-settle phase (well
        // before anchorTick) — shows up on the plot. Reading it later would
        // hide pre-anchor events from both the marker rendering and the
        // post-run budget assertion.
        int baselineMispredicts = ReadMispredictCount(client);

        int targetEid = SpawnEntity(server, targetEntityType, authority: 0,
            TargetStartX, TargetStartY, TargetStartZ);

        // Apply low-friction/low-damping override on BOTH replicas so the two
        // Jolt sims run with identical parameters and the coast phase is long
        // enough to read off the plot. Wait for the client's replica to exist
        // before issuing the client-side override.
        server.Send(new
        {
            cmd = "set-entity-physics",
            entity_id = targetEid,
            friction = TestFriction,
            linearDamp = TestLinearDamp,
            angularDamp = TestAngularDamp,
        });
        WaitForClientEntity(client, targetEid, timeoutMs: 5_000);
        client.Send(new
        {
            cmd = "set-entity-physics",
            entity_id = targetEid,
            friction = TestFriction,
            linearDamp = TestLinearDamp,
            angularDamp = TestAngularDamp,
        });

        // Sample start = right after both replicas exist and physics is set,
        // so the entire test lifecycle (target settle → player fall → push →
        // coast) is captured on the plot. Mispredictions fired during target
        // settle (a real Jolt-cross-process event for falling rigid bodies)
        // would otherwise be invisible because the X axis only spans the run.
        int sampleStartTick = client.ReadClientTick();

        server.WaitForTicks(TargetSettleTicks);

        int playerEid = SpawnEntity(server, EntityTypeRigidPlayer, client.NetworkId,
            0f, PlayerStartY, PlayerStartZ);

        int anchorTick = client.ReadClientTick() + PlayerFallTicks;

        var schedule = new List<object>
        {
            new { tick = anchorTick - PlayerFallTicks,   moveX = 0.0, moveY =  0.0, yaw = 0.0, keys = 0 },
            new { tick = anchorTick,                     moveX = 0.0, moveY = -1.0, yaw = 0.0, keys = 0 },
            new { tick = anchorTick + PressForwardTicks, moveX = 0.0, moveY =  0.0, yaw = 0.0, keys = 0 },
            new { tick = anchorTick + RunTicks,          moveX = 0.0, moveY =  0.0, yaw = 0.0, keys = 0 },
        };
        client.Send(new { cmd = "set-input-schedule", entries = schedule });

        // Sample from sampleStartTick (just after target spawn + physics setup)
        // through end-of-run. Captures: target settle → player fall → push →
        // coast. Mispredict markers can land at any tick in this window.
        int sampleEndTick = anchorTick + RunTicks;

        var clientSamples = new List<Sample>();
        var serverSamples = new List<Sample>();
        for (int targetTick = sampleStartTick + SnapshotIntervalTicks;
             targetTick <= sampleEndTick;
             targetTick += SnapshotIntervalTicks)
        {
            client.WaitForClientTick(targetTick);
            clientSamples.Add(CaptureSample(client, targetTick));
            serverSamples.Add(CaptureSample(server, targetTick));
        }

        int finalMispredicts = ReadMispredictCount(client);
        int mispredictsThisRun = finalMispredicts - baselineMispredicts;

        var paths = ArtifactsFor(label);
        WriteCombinedPlot(paths, clientSamples, serverSamples, playerEid, targetEid, baselineMispredicts, label);
        CopyProcessLogs(paths);

        AssertThat(clientSamples.Count)
            .OverrideFailureMessage("expected non-empty sample stream")
            .IsGreater(0);

        bool sawPlayer = false, sawTarget = false;
        foreach (var s in clientSamples)
        {
            foreach (var e in s.Entities)
            {
                if (e.Id == playerEid) sawPlayer = true;
                if (e.Id == targetEid) sawTarget = true;
            }
        }
        AssertThat(sawPlayer && sawTarget)
            .OverrideFailureMessage(
                $"trace must include both player ({playerEid}) and target body ({targetEid}). " +
                $"Artefacts at TestResults/CollisionMotionPlots/{label}.{{csv,svg,mp4}}")
            .IsTrue();

        // End-of-run pose agreement. At RunTicks the body has been coasting
        // and (for low-friction settings) is either at rest or close to it;
        // the SyncSleepState path anchors the client to the server's
        // authoritative pose once both sides agree the body is asleep, and
        // the head-on geometry has no lever arm to amplify Jolt drift. Any
        // residual gap above the tolerance points at a real divergence.
        Vector3 targetServerEnd = Vector3.Zero;
        Vector3 targetClientEnd = Vector3.Zero;
        Vector3 targetClientVisualEnd = Vector3.Zero;
        foreach (var e in serverSamples[serverSamples.Count - 1].Entities)
        {
            if (e.Id == targetEid) { targetServerEnd = e.Position; break; }
        }
        foreach (var e in clientSamples[clientSamples.Count - 1].Entities)
        {
            if (e.Id == targetEid) { targetClientEnd = e.Position; targetClientVisualEnd = e.VisualPosition; break; }
        }

        float bodyError = (targetServerEnd - targetClientEnd).Length();
        AssertThat(bodyError)
            .OverrideFailureMessage(
                $"client target body diverged from server at end of run by {bodyError:F4} m " +
                $"(server={targetServerEnd}, client={targetClientEnd}). " +
                $"Trace at TestResults/CollisionMotionPlots/{label}.{{csv,svg,mp4}}")
            .IsLessEqual(BodyToleranceM);

        float visualError = (targetClientVisualEnd - targetClientEnd).Length();
        AssertThat(visualError)
            .OverrideFailureMessage(
                $"client target visual diverged from body at end of run by {visualError:F4} m " +
                $"(visual={targetClientVisualEnd}, body={targetClientEnd}). " +
                $"Trace at TestResults/CollisionMotionPlots/{label}.{{csv,svg,mp4}}")
            .IsLessEqual(VisualToleranceM);

        AssertThat(mispredictsThisRun)
            .OverrideFailureMessage(
                $"{mispredictsThisRun} mispredictions over the full sample window " +
                $"(target-settle through end-of-coast) exceeds budget {MispredictBudget}. " +
                $"Trace at TestResults/CollisionMotionPlots/{label}.{{csv,svg,mp4}}")
            .IsLessEqual(MispredictBudget);

        Godot.GD.Print($"[MP-COLLISION-MOTION] {label}: {clientSamples.Count} samples, " +
            $"{mispredictsThisRun} mispredictions, body gap {bodyError:F4} m, visual gap {visualError:F4} m. " +
            $"Artefacts: TestResults/CollisionMotionPlots/{label}.{{csv,svg,mp4}}");
    }

    private static void WriteCombinedPlot(ArtifactPaths paths,
        List<Sample> clientSamples, List<Sample> serverSamples,
        int playerEid, int targetEid, int baselineMispredicts, string label)
    {
        using (var w = new System.IO.StreamWriter(paths.Csv))
        {
            w.WriteLine("tick,t_s,role,eid,etype,x,y,z,vx,vy,vz,avx,avy,avz,qx,qy,qz,qw,vis_x,vis_y,vis_z,vis_qx,vis_qy,vis_qz,vis_qw");
            for (int i = 0; i < clientSamples.Count; i++)
            {
                WriteRoleRows(w, clientSamples[i], "client", playerEid, targetEid);
                WriteRoleRows(w, serverSamples[i], "server", playerEid, targetEid);
            }
        }

        var plot = new SvgPlot($"{label} — head-on rigid-player push: server vs client body vs client visual");

        var playerZPanel = plot.AddPanel("player.Z (m) — server vs client (should overlap)", yUnits: "m");
        var targetZPanel = plot.AddPanel("target.Z (m) — server (solid) vs client.body (dashed) vs client.visual (dotted)", yUnits: "m");
        var targetVzPanel = plot.AddPanel("target.Vz (m/s) — server (solid) vs client.body (dashed)", yUnits: "m/s");
        var driftPanel = plot.AddPanel("|server.target.pos − client.target.pos| (m) — drift accumulator", yUnits: "m");

        var sPZ = new List<(int, float)>();
        var cPZ = new List<(int, float)>();
        var sTZ = new List<(int, float)>();
        var cTZ = new List<(int, float)>();
        var visTZ = new List<(int, float)>();
        var sTVz = new List<(int, float)>();
        var cTVz = new List<(int, float)>();
        var drift = new List<(int, float)>();

        for (int i = 0; i < clientSamples.Count; i++)
        {
            int tick = clientSamples[i].Tick;
            Vector3 sTarget = Vector3.Zero, cTarget = Vector3.Zero, cTargetVis = Vector3.Zero;
            Vector3 sTargetVel = Vector3.Zero, cTargetVel = Vector3.Zero;
            Vector3 sPlayer = Vector3.Zero, cPlayer = Vector3.Zero;
            foreach (var e in serverSamples[i].Entities)
            {
                if (e.Id == targetEid) { sTarget = e.Position; sTargetVel = e.Velocity; }
                else if (e.Id == playerEid) sPlayer = e.Position;
            }
            foreach (var e in clientSamples[i].Entities)
            {
                if (e.Id == targetEid) { cTarget = e.Position; cTargetVis = e.VisualPosition; cTargetVel = e.Velocity; }
                else if (e.Id == playerEid) cPlayer = e.Position;
            }
            sPZ.Add((tick, sPlayer.Z)); cPZ.Add((tick, cPlayer.Z));
            sTZ.Add((tick, sTarget.Z)); cTZ.Add((tick, cTarget.Z)); visTZ.Add((tick, cTargetVis.Z));
            sTVz.Add((tick, sTargetVel.Z)); cTVz.Add((tick, cTargetVel.Z));
            drift.Add((tick, (sTarget - cTarget).Length()));
        }

        playerZPanel.AddSeries("server.player.z", SvgPlot.Palette.Series[0], sPZ)
                    .AddSeries("client.player.z", SvgPlot.Palette.Series[1], cPZ, dashed: true);
        targetZPanel.AddSeries("server.target.z", SvgPlot.Palette.Series[0], sTZ)
                    .AddSeries("client.target.z", SvgPlot.Palette.Series[1], cTZ, dashed: true)
                    .AddSeries("client.visual.z", SvgPlot.Palette.Series[2], visTZ, dashed: true);
        targetVzPanel.AddSeries("server.target.vz", SvgPlot.Palette.Series[0], sTVz)
                     .AddSeries("client.target.vz", SvgPlot.Palette.Series[1], cTVz, dashed: true);
        driftPanel.AddSeries("|server−client|", SvgPlot.Palette.Series[3], drift);

        // Each tick where the client's mispredictionsCount increased gets its own
        // labelled marker spanning all panels. Label format: "mispredict #N (Δ=K)"
        // so the reader can count events in order and see how many predictions
        // missed in a single tick (Δ>1 means multiple entities reconciled at once).
        int prev = baselineMispredicts;
        int mispredictEventIdx = 0;
        foreach (var s in clientSamples)
        {
            int delta = s.MispredictionsCount - prev;
            if (delta > 0)
            {
                mispredictEventIdx++;
                plot.AddVerticalMarker(s.Tick, $"mispredict #{mispredictEventIdx} (Δ={delta})");
            }
            prev = s.MispredictionsCount;
        }

        plot.Save(paths.Svg);
        Godot.GD.Print($"[MP-COLLISION-MOTION] wrote {paths.Csv}, {paths.Svg} ({clientSamples.Count} samples)");
    }

    private static void WriteRoleRows(System.IO.StreamWriter w, Sample s, string role, int playerEid, int targetEid)
    {
        string tickStr = s.Tick.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string tStr = (s.Tick / 60.0).ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var e in s.Entities)
        {
            if (e.Id != playerEid && e.Id != targetEid) continue;
            w.Write(tickStr); w.Write(','); w.Write(tStr); w.Write(','); w.Write(role); w.Write(',');
            w.Write(e.Id); w.Write(','); w.Write(e.Type); w.Write(',');
            w.Write(CsvWriter.F(e.Position.X)); w.Write(','); w.Write(CsvWriter.F(e.Position.Y)); w.Write(','); w.Write(CsvWriter.F(e.Position.Z)); w.Write(',');
            w.Write(CsvWriter.F(e.Velocity.X)); w.Write(','); w.Write(CsvWriter.F(e.Velocity.Y)); w.Write(','); w.Write(CsvWriter.F(e.Velocity.Z)); w.Write(',');
            w.Write(CsvWriter.F(e.AngularVelocity.X)); w.Write(','); w.Write(CsvWriter.F(e.AngularVelocity.Y)); w.Write(','); w.Write(CsvWriter.F(e.AngularVelocity.Z)); w.Write(',');
            w.Write(CsvWriter.F(e.Rotation.X)); w.Write(','); w.Write(CsvWriter.F(e.Rotation.Y)); w.Write(','); w.Write(CsvWriter.F(e.Rotation.Z)); w.Write(','); w.Write(CsvWriter.F(e.Rotation.W)); w.Write(',');
            w.Write(CsvWriter.F(e.VisualPosition.X)); w.Write(','); w.Write(CsvWriter.F(e.VisualPosition.Y)); w.Write(','); w.Write(CsvWriter.F(e.VisualPosition.Z)); w.Write(',');
            w.Write(CsvWriter.F(e.VisualRotation.X)); w.Write(','); w.Write(CsvWriter.F(e.VisualRotation.Y)); w.Write(','); w.Write(CsvWriter.F(e.VisualRotation.Z)); w.Write(','); w.Write(CsvWriter.F(e.VisualRotation.W)); w.Write('\n');
        }
    }
}
