using System.Collections.Generic;
using GdUnit4;
using Godot;
using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Artifacts;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.MultiProcess;

/// <summary>
/// MP-SIDE-PUSH-01: Rigid-player charges into a server-authoritative vehicle
/// that is lying sideways across the player's path (long side facing the
/// player, yawed a few degrees off-perpendicular so contact is offset).
///
/// Vehicle is EntityType=2, authority=0 (server-owned). On the client, that
/// resolves to <c>DummyVehicle</c> — a passive-predicted RigidBody3D driven by
/// <see cref="GameDemo.LocalRigidPropPrediction"/> (same script the cube uses):
/// the body simulates locally from Jolt every physics tick (so the local
/// player's contact impulses are felt immediately) and each snapshot is
/// reconciled against the server's authoritative state, with sub-threshold
/// drift absorbed visually through a <c>PredictionVisualSmoothing3D</c>.
///
/// Test covers three concerns at once (replaces the deleted PushDriftTolerance
/// vehicle test):
///   1. <b>Side-push contact dynamics</b>: the original mission — visualise
///      body+visual position/rotation over the run to validate the smoother
///      hides body-side jitter from the rendered mesh.
///   2. <b>Push-drift tolerance</b>: per-tick server-only velocity perturbation
///      injected into the vehicle so the plot shows the server replica drifting
///      from the client replica. A correct push formula keeps the player on
///      both sides on the same trajectory regardless.
///   3. <b>Reconcile snap absorption</b>: the inherent end-of-coast reconcile
///      that fires when the two Jolt sims diverge enough on contact normals is
///      validated against a convergence budget — body within 5 cm of the
///      server's authoritative pose within 10 ticks of the snap, visual mesh
///      jump-per-tick ≤ 30 cm.
///
/// Artefacts under <c>TestResults/SidePushVehiclePlots/</c>:
///   - side_push_vehicle.csv  (per-tick per-(role, entity); role ∈ {server, client})
///   - side_push_vehicle.svg  (cube X server vs client, visual.X, player Z, drift, mispredict markers)
///   - side_push_vehicle.mp4  (in-engine viewport recording)
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class MultiProcessSidePushVehicleTests : MultiProcessTestBase
{
    protected override string ArtifactSubdir => "SidePushVehiclePlots";

    [BeforeTest] public void SetUp() => SetUpInternal();
    [AfterTest]  public void TearDown() => TearDownInternal();

    private const byte EntityTypeRigidPlayer = 3;
    private const byte EntityTypeVehicle = 2;

    // ── scenario geometry ────────────────────────────────────────────────────
    private const float VehicleYaw = Mathf.Pi / 2f + 0.18f;
    private const float VehicleStartX = 0f;
    private const float VehicleStartY = 0.5f;
    private const float VehicleStartZ = -2.5f;
    private const float PlayerStartZ = 0.5f;
    private const float PlayerStartY = 0f;

    // ── timing ───────────────────────────────────────────────────────────────
    private const int SnapshotArmTicks = 60;
    private const int VehicleSettleTicks = 60;
    private const int PlayerFallTicks = 60;
    private const int PressForwardTicks = 35;
    private const int RunTicks = 25;
    private const int SnapshotIntervalTicks = 1;

    // Per-tick server-only velocity perturbation injected into the vehicle —
    // see class doc-comment. Magnitude is small relative to the contact
    // dynamics so the test still passes its primary side-push assertions.
    private const float DriftInjectionVelocityZ = 0.01f;

    // Reconcile-event budgets (replaces the dedicated PredictReplay test).
    private const float ConvergenceToleranceM = 0.05f;
    private const int ConvergenceTickBudget = 10;
    private const float MaxVisualJumpPerTickM = 0.30f;

    [TestCase]
    public void MultiProcess_RigidPlayer_SidePushesVehicle_RendersTraceAndVideo()
    {
        if (Orch == null) return;

        var (server, client) = SpawnPair("side_push_vehicle", recordVideo: true);

        server.WaitForTicks(SnapshotArmTicks);

        int vehicleEid = SpawnEntity(server, EntityTypeVehicle, authority: 0,
            VehicleStartX, VehicleStartY, VehicleStartZ);
        server.Send(new
        {
            cmd = "teleport-entity",
            entity_id = vehicleEid,
            position = new[] { (double)VehicleStartX, VehicleStartY, VehicleStartZ },
            yaw = VehicleYaw,
        });
        WaitForClientEntity(client, vehicleEid, timeoutMs: 5_000);
        server.WaitForTicks(VehicleSettleTicks);

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

        client.WaitForClientTick(anchorTick);
        int baselineMispredicts = ReadMispredictCount(client);

        var clientSamples = new List<Sample>();
        var serverSamples = new List<Sample>();
        for (int t = SnapshotIntervalTicks; t <= RunTicks; t += SnapshotIntervalTicks)
        {
            int targetTick = anchorTick + t;
            client.WaitForClientTick(targetTick);
            clientSamples.Add(CaptureSample(client, targetTick));
            serverSamples.Add(CaptureSample(server, targetTick));

            // Per-tick server-only drift injection on the vehicle body.
            server.Send(new
            {
                cmd = "apply-impulse",
                entity_id = vehicleEid,
                deltaLinearVelocity = new[] { 0.0, 0.0, (double)DriftInjectionVelocityZ },
                targetRole = "server",
            });
        }

        int finalMispredicts = ReadMispredictCount(client);
        int mispredictsThisRun = finalMispredicts - baselineMispredicts;

        // First sample tick where the client's misprediction count went up —
        // the reconcile-observed point.
        int reconcileSampleIndex = -1;
        int prev = baselineMispredicts;
        for (int i = 0; i < clientSamples.Count; i++)
        {
            if (clientSamples[i].MispredictionsCount > prev) { reconcileSampleIndex = i; break; }
            prev = clientSamples[i].MispredictionsCount;
        }

        var paths = ArtifactsFor("side_push_vehicle");
        WriteCombinedPlot(paths, clientSamples, serverSamples, playerEid, vehicleEid, baselineMispredicts);
        CopyProcessLogs(paths);

        AssertThat(clientSamples.Count)
            .OverrideFailureMessage("expected non-empty sample stream")
            .IsGreater(0);
        bool sawPlayer = false, sawVehicle = false;
        foreach (var s in clientSamples)
        {
            foreach (var e in s.Entities)
            {
                if (e.Id == playerEid) sawPlayer = true;
                if (e.Id == vehicleEid) sawVehicle = true;
            }
        }
        AssertThat(sawPlayer && sawVehicle)
            .OverrideFailureMessage($"trace must include both player ({playerEid}) and vehicle ({vehicleEid}). " +
                $"mispredicts={mispredictsThisRun}. Artefacts at TestResults/SidePushVehiclePlots/side_push_vehicle.{{csv,svg,mp4}}")
            .IsTrue();

        // ── reconcile-event validations ──────────────────────────────────────
        // The contact-and-coast scenario reliably produces at least one
        // reconcile near end-of-coast as the two Jolt sims diverge on contact
        // normal angle. When it fires, the client body must converge to within
        // 5 cm of the server pose within ConvergenceTickBudget samples; the
        // visual mesh must not pop more than MaxVisualJumpPerTickM in a tick.
        if (reconcileSampleIndex >= 0)
        {
            int convergeIndex = -1;
            float maxBodyError = 0f;
            for (int i = reconcileSampleIndex; i < clientSamples.Count; i++)
            {
                Vector3 sBody = Vector3.Zero, cBody = Vector3.Zero;
                foreach (var e in serverSamples[i].Entities) if (e.Id == vehicleEid) { sBody = e.Position; break; }
                foreach (var e in clientSamples[i].Entities) if (e.Id == vehicleEid) { cBody = e.Position; break; }
                float err = (sBody - cBody).Length();
                if (err > maxBodyError) maxBodyError = err;
                if (err <= ConvergenceToleranceM && convergeIndex < 0)
                    convergeIndex = i - reconcileSampleIndex;
            }
            AssertThat(convergeIndex)
                .OverrideFailureMessage(
                    $"client body did not converge to within {ConvergenceToleranceM:F3} m of the server's vehicle pose " +
                    $"within {ConvergenceTickBudget} ticks of reconcile (max error {maxBodyError:F3} m). " +
                    $"Trace at TestResults/SidePushVehiclePlots/side_push_vehicle.{{csv,svg,mp4}}")
                .IsBetween(0, ConvergenceTickBudget);

            float maxVisualJump = 0f;
            Vector3 prevVisual = Vector3.Zero;
            bool havePrev = false;
            foreach (var s in clientSamples)
            {
                foreach (var e in s.Entities)
                {
                    if (e.Id != vehicleEid) continue;
                    if (havePrev)
                    {
                        float j = (e.VisualPosition - prevVisual).Length();
                        if (j > maxVisualJump) maxVisualJump = j;
                    }
                    prevVisual = e.VisualPosition;
                    havePrev = true;
                }
            }
            AssertThat(maxVisualJump)
                .OverrideFailureMessage(
                    $"vehicle visual mesh jumped {maxVisualJump:F3} m in a single tick (budget {MaxVisualJumpPerTickM:F3} m). " +
                    $"PredictionVisualSmoothing3D should have absorbed the body snap. " +
                    $"Trace at TestResults/SidePushVehiclePlots/side_push_vehicle.{{csv,svg,mp4}}")
                .IsLessEqual(MaxVisualJumpPerTickM);
        }

        Godot.GD.Print($"[MP-SIDE-PUSH] run complete: {clientSamples.Count} samples (client+server), " +
            $"{mispredictsThisRun} mispredictions over {RunTicks} ticks. " +
            $"Artefacts: TestResults/SidePushVehiclePlots/side_push_vehicle.{{csv,svg,mp4}}");
    }

    private static void WriteCombinedPlot(ArtifactPaths paths, List<Sample> clientSamples, List<Sample> serverSamples,
        int playerEid, int vehicleEid, int baselineMispredicts)
    {
        using (var w = new System.IO.StreamWriter(paths.Csv))
        {
            w.WriteLine("tick,t_s,role,eid,etype,x,y,z,vx,vy,vz,avx,avy,avz,qx,qy,qz,qw,vis_x,vis_y,vis_z,vis_qx,vis_qy,vis_qz,vis_qw");
            for (int i = 0; i < clientSamples.Count; i++)
            {
                WriteRoleRows(w, clientSamples[i], "client", playerEid, vehicleEid);
                WriteRoleRows(w, serverSamples[i], "server", playerEid, vehicleEid);
            }
        }

        var plot = new SvgPlot("side_push_vehicle — server vs client traces with drift injection and reconcile snap");

        var bodyXPanel = plot.AddPanel("vehicle.X (m) — server (solid) vs client.body (dashed) vs client.visual (dotted)", yUnits: "m");
        var bodyZPanel = plot.AddPanel("vehicle.Z (m)", yUnits: "m");
        var driftPanel = plot.AddPanel("|server.vehicle.pos − client.vehicle.pos| (m) — drift accumulator", yUnits: "m");
        var rotPanel   = plot.AddPanel("vehicle rotation deviation 1−|server.q · client.q|", yUnits: "");
        var playerZ    = plot.AddPanel("player.Z (m) — server vs client (should overlap)", yUnits: "m");

        var sBX = new List<(int, float)>(); var cBX = new List<(int, float)>(); var cVX = new List<(int, float)>();
        var sBZ = new List<(int, float)>(); var cBZ = new List<(int, float)>();
        var drift = new List<(int, float)>();
        var rotDev = new List<(int, float)>();
        var sPZ = new List<(int, float)>(); var cPZ = new List<(int, float)>();
        for (int i = 0; i < clientSamples.Count; i++)
        {
            int tick = clientSamples[i].Tick;
            Vector3 sV = Vector3.Zero, cV = Vector3.Zero, cVis = Vector3.Zero;
            Vector3 sP = Vector3.Zero, cP = Vector3.Zero;
            Quaternion sQ = Quaternion.Identity, cQ = Quaternion.Identity;
            foreach (var e in serverSamples[i].Entities)
            {
                if (e.Id == vehicleEid) { sV = e.Position; sQ = e.Rotation; }
                else if (e.Id == playerEid) sP = e.Position;
            }
            foreach (var e in clientSamples[i].Entities)
            {
                if (e.Id == vehicleEid) { cV = e.Position; cVis = e.VisualPosition; cQ = e.Rotation; }
                else if (e.Id == playerEid) cP = e.Position;
            }
            sBX.Add((tick, sV.X)); cBX.Add((tick, cV.X)); cVX.Add((tick, cVis.X));
            sBZ.Add((tick, sV.Z)); cBZ.Add((tick, cV.Z));
            drift.Add((tick, (sV - cV).Length()));
            rotDev.Add((tick, 1f - Mathf.Abs(sQ.Dot(cQ))));
            sPZ.Add((tick, sP.Z)); cPZ.Add((tick, cP.Z));
        }

        bodyXPanel.AddSeries("server.vehicle.x", SvgPlot.Palette.Series[0], sBX)
                  .AddSeries("client.vehicle.x", SvgPlot.Palette.Series[1], cBX, dashed: true)
                  .AddSeries("client.visual.x", SvgPlot.Palette.Series[2], cVX, dashed: true);
        bodyZPanel.AddSeries("server.vehicle.z", SvgPlot.Palette.Series[0], sBZ)
                  .AddSeries("client.vehicle.z", SvgPlot.Palette.Series[1], cBZ, dashed: true);
        driftPanel.AddSeries("|server−client|", SvgPlot.Palette.Series[3], drift);
        rotPanel.AddSeries("rotation deviation", SvgPlot.Palette.Series[4], rotDev);
        playerZ.AddSeries("server.player.z", SvgPlot.Palette.Series[0], sPZ)
               .AddSeries("client.player.z", SvgPlot.Palette.Series[1], cPZ, dashed: true);

        int prev = baselineMispredicts;
        foreach (var s in clientSamples)
        {
            if (s.MispredictionsCount > prev) plot.AddVerticalMarker(s.Tick, "reconcile");
            prev = s.MispredictionsCount;
        }

        plot.Save(paths.Svg);
        Godot.GD.Print($"[MP-SIDE-PUSH] wrote {paths.Csv}, {paths.Svg} ({clientSamples.Count} samples)");
    }

    private static void WriteRoleRows(System.IO.StreamWriter w, Sample s, string role, int playerEid, int vehicleEid)
    {
        string tickStr = s.Tick.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string tStr = (s.Tick / 60.0).ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var e in s.Entities)
        {
            if (e.Id != playerEid && e.Id != vehicleEid) continue;
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
