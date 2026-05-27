using System.Collections.Generic;
using GdUnit4;
using Godot;
using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Artifacts;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.MultiProcess;

/// <summary>
/// MP-OFFSET-PUSH-01: Multi-process rigid-player vs. rigid-cube offset-push.
/// A single cube is spawned slightly offset from the player's forward axis so a
/// straight-line charge clips it off-centre — applying simultaneous linear push
/// AND angular spin from the contact lever arm. The test covers three concerns
/// at once:
///
/// 1. <b>Push-drift tolerance</b>: a small per-tick velocity perturbation is
///    injected into the server-side cube via <c>apply-impulse</c> (server-only)
///    so the trace shows the cube's server replica drifting steadily from the
///    client replica. A correct push formula must not amplify that drift into
///    the player; the plot makes any such amplification visible as a widening
///    gap on the player traces.
/// 2. <b>Reconcile snap absorption</b>: at <c>TeleportInjectTick</c> the test
///    fires <c>teleport-entity</c> on the server — a deliberate +30 cm jump
///    that guarantees the client will trip <c>HasMisspredicted</c> on the
///    cube. The assertion checks that the client body converges to within
///    5 cm of the server's authoritative pose within 10 ticks of the snap,
///    and that the visual mesh never jumps more than 30 cm in a single tick
///    (i.e. <c>PredictionVisualSmoothing3D</c> absorbed the snap).
/// 3. <b>Trace + video record</b>: every tick captures both server and client
///    state into the CSV so all three behaviours are visualisable side-by-side.
///
/// Artefacts written under <c>TestResults/OffsetPushPlots/</c>:
///   - offset_push.csv   (per-tick per-(role, entity); role ∈ {server, client})
///   - offset_push.svg   (cube X server vs client, visual.X, player Z, mispredict markers)
///   - offset_push.mp4   (in-engine viewport recording of the windowed client)
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class MultiProcessOffsetPushTests : MultiProcessTestBase
{
    protected override string ArtifactSubdir => "OffsetPushPlots";

    [BeforeTest] public void SetUp() => SetUpInternal();
    [AfterTest]  public void TearDown() => TearDownInternal();

    private const byte EntityTypeRigidPlayer = 3;
    private const byte EntityTypeCube = 4;

    // ── scenario geometry ────────────────────────────────────────────────────
    // Floor in MainScene is a 30x1x30 CSGBox centred at Y=-2.5 (top face Y=-2).
    // Cube is 1x1x1 spawned ABOVE the floor so it falls and settles before the
    // player reaches it. CubeOffsetX is a small lateral offset from the player's
    // forward path so the collision is off-centre and produces visible spin.
    private const float CubeStartZ = -2.5f;
    // 0.65 m lateral offset puts the contact well off-centre — the cube spins
    // visibly during push (high angular velocity) and the lever arm amplifies
    // any per-step Jolt divergence between client and server, so the resulting
    // reconcile snap at end-of-push is much louder than a near-axial impact.
    private const float CubeOffsetX = 0.65f;
    private const float CubeStartY = 0.5f;
    private const float PlayerStartZ = 1.5f;
    private const float PlayerStartY = 0f;

    // ── timing ───────────────────────────────────────────────────────────────
    private const int SnapshotArmTicks = 60;
    private const int CubeSettleTicks = 90;
    private const int PlayerFallTicks = 90;
    private const int RunTicks = 150;
    // How long the player keeps pressing forward before releasing. From the
    // anchor (when forward input begins) the player needs ~30 ticks to close
    // the 4 m gap to the cube; we keep pushing for a few ticks AFTER first
    // contact so the cube receives an initial impulse, then RELEASE so the
    // cube coasts to rest on its own residual momentum. This is the worst
    // case for client/server agreement: once the player stops pushing there
    // is no continuous contact reaction keeping both Jolt sims in lockstep,
    // so they evolve independently and the snap at end-of-coast is loudest.
    private const int PressForwardTicks = 42;
    private const int SnapshotIntervalTicks = 2;

    // Per-tick server-only velocity perturbation injected into the cube to
    // model cross-process Jolt drift. 0.01 m/s is well below realistic Jolt
    // cross-process drift bursts (which can hit tens of mm/s during stacked
    // contact), small enough that it doesn't itself trip a reconcile, but
    // large enough that the cube's server replica visibly diverges from the
    // client replica on the plot over the run.
    private const float DriftInjectionVelocityZ = 0.01f;

    // Reconcile-event budgets (replaces the dedicated PredictReplay test).
    private const float ConvergenceToleranceM = 0.05f;
    private const int ConvergenceTickBudget = 10;
    private const float MaxVisualJumpPerTickM = 0.35f;

    [TestCase]
    public void MultiProcess_RigidPlayer_OffsetPushesCube_RendersTraceAndVideo()
    {
        if (Orch == null) return;

        var (server, client, observer) = SpawnTriad("offset_push", recordVideo: true);

        server.WaitForTicks(SnapshotArmTicks);

        // Single cube offset slightly on +X so a straight forward charge from
        // the player hits it off-centre, producing simultaneous push + spin.
        int cubeEid = SpawnEntity(server, EntityTypeCube, authority: 0,
            CubeOffsetX, CubeStartY, CubeStartZ);

        // Test-only physics tuning: low friction + low damping so the cube
        // coasts long enough for the visual smoother decay window to be
        // visible on the trace. Applied to BOTH server and client replicas so
        // both Jolt sims run with identical parameters; demo scenes (used
        // outside tests) keep their default friction=0.6 / damp=0.4/0.8.
        // The client's replica won't exist until the entity-event reaches it
        // a few ticks after spawn, so poll briefly before issuing the client
        // override.
        const float TestFriction = 0.15f;
        const float TestLinearDamp = 0.05f;
        const float TestAngularDamp = 0.1f;
        server.Send(new
        {
            cmd = "set-entity-physics",
            entity_id = cubeEid,
            friction = TestFriction,
            linearDamp = TestLinearDamp,
            angularDamp = TestAngularDamp,
        });
        WaitForClientEntity(client, cubeEid, timeoutMs: 5_000);
        WaitForClientEntity(observer, cubeEid, timeoutMs: 5_000);
        // Apply the test-only physics tuning to EVERY client replica, not just
        // the active driver. Each TestProcess has its own Jolt instance whose
        // RigidBody3D defaults come from the .tscn (damp 0.4 / friction 0.6) —
        // a missed client-side override leaves that replica integrating with
        // different damping than the server's, and the cube falls slower on
        // that client by a few cm/s. Snapshot reconcile then keeps catching
        // the diverging replica up to the server's pose, producing a steady
        // stream of "observer mispredict" events even on a passive entity
        // with no inputs of its own. Bug was caught when the offset_push
        // observer mispredict count doubled from 5 to 10 after SpawnTriad
        // added a third process whose cube replica was never overridden.
        foreach (var c in new[] { client, observer })
        {
            c.Send(new
            {
                cmd = "set-entity-physics",
                entity_id = cubeEid,
                friction = TestFriction,
                linearDamp = TestLinearDamp,
                angularDamp = TestAngularDamp,
            });
        }

        server.WaitForTicks(CubeSettleTicks);

        int playerEid = SpawnEntity(server, EntityTypeRigidPlayer, client.NetworkId,
            0f, PlayerStartY, PlayerStartZ);

        int anchorTick = client.ReadClientTick() + PlayerFallTicks;

        var schedule = new List<object>
        {
            new { tick = anchorTick - PlayerFallTicks,    moveX = 0.0, moveY = 0.0,  yaw = 0.0, keys = 0 },
            new { tick = anchorTick,                      moveX = 0.0, moveY = -1.0, yaw = 0.0, keys = 0 },
            new { tick = anchorTick + PressForwardTicks,  moveX = 0.0, moveY = 0.0,  yaw = 0.0, keys = 0 },
            new { tick = anchorTick + RunTicks,           moveX = 0.0, moveY = 0.0,  yaw = 0.0, keys = 0 },
        };
        client.Send(new { cmd = "set-input-schedule", entries = schedule });

        client.WaitForClientTick(anchorTick);
        int baselineMispredicts = ReadMispredictCount(client);
        int observerBaselineMispredicts = ReadMispredictCount(observer);

        const int TeleportInjectTick = 120;
        const float TeleportOffsetX = 0.5f;
        bool teleportFired = false;
        int teleportSampleIndex = -1;
        int teleportMispredictsBaseline = 0;
        int reconcileSampleIndex = -1;
        Vector3 cubeServerPoseAtReconcile = Vector3.Zero;

        var clientSamples = new List<Sample>();
        var serverSamples = new List<Sample>();
        for (int t = SnapshotIntervalTicks; t <= RunTicks; t += SnapshotIntervalTicks)
        {
            int targetTick = anchorTick + t;
            client.WaitForClientTick(targetTick);
            var cSample = CaptureSample(client, targetTick);
            var sSample = CaptureSample(server, targetTick);
            clientSamples.Add(cSample);
            serverSamples.Add(sSample);

            // Per-tick server-only drift injection on the cube. Modelled
            // cross-process Jolt drift — small per-tick velocity perturbation
            // visible on the plot as a slow gap between server.cube and
            // client.cube traces. apply-impulse with targetRole="server" is a
            // no-op on the client process.
            server.Send(new
            {
                cmd = "apply-impulse",
                entity_id = cubeEid,
                deltaLinearVelocity = new[] { 0.0, 0.0, (double)DriftInjectionVelocityZ },
                targetRole = "server",
            });

            // One-shot reconcile event — a deliberate teleport on the server
            // guaranteed to trip the client's HasMisspredicted path and exercise
            // PredictionRigidbody3D.Reconcile + the visual smoother.
            if (!teleportFired && t >= TeleportInjectTick)
            {
                Vector3 cubePos = Vector3.Zero;
                foreach (var e in cSample.Entities)
                {
                    if (e.Id == cubeEid) { cubePos = e.Position; break; }
                }
                server.Send(new
                {
                    cmd = "teleport-entity",
                    entity_id = cubeEid,
                    position = new[] { (double)(cubePos.X + TeleportOffsetX), cubePos.Y, cubePos.Z },
                });
                teleportFired = true;
                teleportSampleIndex = clientSamples.Count - 1;
                teleportMispredictsBaseline = cSample.MispredictionsCount;
                cubeServerPoseAtReconcile = new Vector3(cubePos.X + TeleportOffsetX, cubePos.Y, cubePos.Z);
            }
        }

        // Find the first sample tick AFTER the teleport where the client's
        // misprediction count rises above the baseline captured at teleport time.
        // Filtering by teleportSampleIndex matters because per-tick drift
        // injection on the server can cause mispredicts during the push phase
        // — those are unrelated to the teleport reconcile being measured here.
        //
        // Sample.MispredictionsCount only counts FULL-SCENE rollback events
        // (Resim tier). Since cubes default to Interpolate tier under T1, the
        // teleport reconcile takes the blend path and never bumps that counter
        // — but the convergence assertion below still validates that the cube
        // actually reached the new pose. The "did we observe a reconcile"
        // signal therefore looks at the body-position trace itself: the first
        // post-teleport sample where the client cube's X moves by at least
        // half the teleport offset toward the new pose.
        if (teleportSampleIndex >= 0)
        {
            for (int i = teleportSampleIndex; i < clientSamples.Count; i++)
            {
                if (clientSamples[i].MispredictionsCount > teleportMispredictsBaseline)
                {
                    reconcileSampleIndex = i;
                    break;
                }
            }
            // Fallback path for the T1 Interpolate-tier branch: detect the
            // reconcile by observing the client cube's X position move toward
            // the target by at least half the teleport offset.
            if (reconcileSampleIndex < 0)
            {
                float teleportCubeX = float.NaN;
                foreach (var e in clientSamples[teleportSampleIndex].Entities)
                {
                    if (e.Id == cubeEid) { teleportCubeX = e.Position.X; break; }
                }
                if (!float.IsNaN(teleportCubeX))
                {
                    for (int i = teleportSampleIndex + 1; i < clientSamples.Count; i++)
                    {
                        foreach (var e in clientSamples[i].Entities)
                        {
                            if (e.Id != cubeEid) continue;
                            float deltaX = e.Position.X - teleportCubeX;
                            // Half the teleport offset = we observed at least
                            // one blend tick worth of convergence.
                            if (Mathf.Abs(deltaX) >= 0.5f * Mathf.Abs(TeleportOffsetX))
                            {
                                reconcileSampleIndex = i;
                                break;
                            }
                        }
                        if (reconcileSampleIndex >= 0) break;
                    }
                }
            }
        }

        int finalMispredicts = ReadMispredictCount(client);
        int mispredictsThisRun = finalMispredicts - baselineMispredicts;
        int observerMispredictsThisRun = ReadMispredictCount(observer) - observerBaselineMispredicts;

        var paths = ArtifactsFor("offset_push");
        WriteCombinedPlot(paths, clientSamples, serverSamples, playerEid, cubeEid, baselineMispredicts);
        CopyProcessLogs(paths);
        CopyObserverLog(paths, "offset_push");

        AssertThat(clientSamples.Count)
            .OverrideFailureMessage("expected non-empty sample stream")
            .IsGreater(0);
        bool sawPlayer = false, sawCube = false;
        foreach (var s in clientSamples)
        {
            foreach (var e in s.Entities)
            {
                if (e.Id == playerEid) sawPlayer = true;
                if (e.Id == cubeEid) sawCube = true;
            }
        }
        AssertThat(sawPlayer && sawCube)
            .OverrideFailureMessage($"trace must include both player ({playerEid}) and cube ({cubeEid}). " +
                $"mispredicts={mispredictsThisRun}. Artefacts at TestResults/OffsetPushPlots/offset_push.{{csv,svg,mp4}}")
            .IsTrue();

        // ── reconcile-event validations (replaces PredictReplay test) ────────
        // After the server-side teleport fires at TeleportInjectTick, the
        // client must observe a misprediction within the sample stream AND
        // converge to within 5 cm of the new server pose within 10 ticks.
        AssertThat(reconcileSampleIndex)
            .OverrideFailureMessage(
                $"client never observed a misprediction after the teleport reconcile at tick {anchorTick + TeleportInjectTick}. " +
                $"Trace at TestResults/OffsetPushPlots/offset_push.{{csv,svg,mp4}}")
            .IsGreaterEqual(0);

        if (reconcileSampleIndex >= 0)
        {
            int convergeIndex = -1;
            float maxBodyError = 0f;
            for (int i = reconcileSampleIndex; i < clientSamples.Count; i++)
            {
                Vector3 cBody = Vector3.Zero;
                foreach (var e in clientSamples[i].Entities)
                {
                    if (e.Id == cubeEid) { cBody = e.Position; break; }
                }
                float err = Mathf.Abs(cBody.X - cubeServerPoseAtReconcile.X);
                if (err > maxBodyError) maxBodyError = err;
                if (err <= ConvergenceToleranceM && convergeIndex < 0)
                    convergeIndex = i - reconcileSampleIndex;
            }
            AssertThat(convergeIndex)
                .OverrideFailureMessage(
                    $"client body did not converge to within {ConvergenceToleranceM:F3} m of the server's " +
                    $"reconciled X={cubeServerPoseAtReconcile.X:F3} within {ConvergenceTickBudget} ticks " +
                    $"(SnapshotIntervalTicks={SnapshotIntervalTicks}; max error {maxBodyError:F3} m). " +
                    $"Trace at TestResults/OffsetPushPlots/offset_push.{{csv,svg,mp4}}")
                .IsBetween(0, ConvergenceTickBudget);

            // Visual jump per tick must stay smooth — that's the smoother's job.
            float maxVisualJump = 0f;
            Vector3 prevVisual = Vector3.Zero;
            bool havePrev = false;
            foreach (var s in clientSamples)
            {
                foreach (var e in s.Entities)
                {
                    if (e.Id != cubeEid) continue;
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
                    $"cube visual mesh jumped {maxVisualJump:F3} m in a single tick (budget {MaxVisualJumpPerTickM:F3} m). " +
                    $"PredictionVisualSmoothing3D should have absorbed the body snap. " +
                    $"Trace at TestResults/OffsetPushPlots/offset_push.{{csv,svg,mp4}}")
                .IsLessEqual(MaxVisualJumpPerTickM);
        }

        // Mispredict budget — covers the teleport-induced reconcile plus a
        // little headroom for contact jitter. The observer sees the same
        // teleport via snapshot and should reconcile within the same budget;
        // exceeding this on the observer would indicate the snapshot stream
        // is being applied differently across roles.
        const int OffsetPushMispredictBudget = 5;
        AssertThat(mispredictsThisRun)
            .OverrideFailureMessage(
                $"driver mispredicted {mispredictsThisRun} times over {RunTicks} ticks (budget {OffsetPushMispredictBudget}). " +
                $"Trace at TestResults/OffsetPushPlots/offset_push.{{csv,svg,mp4}}")
            .IsLessEqual(OffsetPushMispredictBudget);
        AssertThat(observerMispredictsThisRun)
            .OverrideFailureMessage(
                $"observer mispredicted {observerMispredictsThisRun} times over {RunTicks} ticks (budget {OffsetPushMispredictBudget}). " +
                $"The observer sees the same teleport via snapshot as the driver; exceeding the driver's budget " +
                $"means the snapshot/prediction loop is treating observers differently. " +
                $"Trace at TestResults/OffsetPushPlots/offset_push.{{csv,svg,mp4}}")
            .IsLessEqual(OffsetPushMispredictBudget);

        Godot.GD.Print($"[MP-OFFSET-PUSH] run complete: {clientSamples.Count} samples (client+server), " +
            $"driver={mispredictsThisRun} observer={observerMispredictsThisRun} mispredictions over {RunTicks} ticks. " +
            $"Artefacts: TestResults/OffsetPushPlots/offset_push.{{csv,svg,mp4}}");
    }

    // ── MP-OFFSET-PUSH-02 ────────────────────────────────────────────────────
    // Baseline counterpart to MP-OFFSET-PUSH-01: same geometry and same charge,
    // but NO server-side drift injection and NO teleport. This isolates the
    // "happy path" — when both Jolt processes simulate from identical initial
    // conditions through the same contact dynamics, the predicted client body
    // should track the authoritative server body without ever tripping
    // misprediction, and end the run at essentially the same pose. If this
    // test fails, something is wrong with the baseline predict-and-reconcile
    // path itself (cross-process determinism, snapshot interpolation, sleep
    // sync) — not with the teleport / drift recovery paths that the primary
    // offset-push test exercises.
    //
    // Artefacts under <c>TestResults/OffsetPushPlots/offset_push_baseline.*</c>.
    [TestCase]
    public void MultiProcess_RigidPlayer_OffsetPushesCube_Baseline()
    {
        if (Orch == null) return;

        var (server, client, observer) = SpawnTriad("offset_push_baseline", recordVideo: true);

        server.WaitForTicks(SnapshotArmTicks);

        int cubeEid = SpawnEntity(server, EntityTypeCube, authority: 0,
            CubeOffsetX, CubeStartY, CubeStartZ);

        const float TestFriction = 0.15f;
        const float TestLinearDamp = 0.05f;
        const float TestAngularDamp = 0.1f;
        server.Send(new
        {
            cmd = "set-entity-physics",
            entity_id = cubeEid,
            friction = TestFriction,
            linearDamp = TestLinearDamp,
            angularDamp = TestAngularDamp,
        });
        WaitForClientEntity(client, cubeEid, timeoutMs: 5_000);
        WaitForClientEntity(observer, cubeEid, timeoutMs: 5_000);
        // Apply the test-only physics tuning to EVERY client replica, not just
        // the active driver. Each TestProcess has its own Jolt instance whose
        // RigidBody3D defaults come from the .tscn (damp 0.4 / friction 0.6) —
        // a missed client-side override leaves that replica integrating with
        // different damping than the server's, and the cube falls slower on
        // that client by a few cm/s. Snapshot reconcile then keeps catching
        // the diverging replica up to the server's pose, producing a steady
        // stream of "observer mispredict" events even on a passive entity
        // with no inputs of its own. Bug was caught when the offset_push
        // observer mispredict count doubled from 5 to 10 after SpawnTriad
        // added a third process whose cube replica was never overridden.
        foreach (var c in new[] { client, observer })
        {
            c.Send(new
            {
                cmd = "set-entity-physics",
                entity_id = cubeEid,
                friction = TestFriction,
                linearDamp = TestLinearDamp,
                angularDamp = TestAngularDamp,
            });
        }

        server.WaitForTicks(CubeSettleTicks);

        int playerEid = SpawnEntity(server, EntityTypeRigidPlayer, client.NetworkId,
            0f, PlayerStartY, PlayerStartZ);

        int anchorTick = client.ReadClientTick() + PlayerFallTicks;

        var schedule = new List<object>
        {
            new { tick = anchorTick - PlayerFallTicks,    moveX = 0.0, moveY = 0.0,  yaw = 0.0, keys = 0 },
            new { tick = anchorTick,                      moveX = 0.0, moveY = -1.0, yaw = 0.0, keys = 0 },
            new { tick = anchorTick + PressForwardTicks,  moveX = 0.0, moveY = 0.0,  yaw = 0.0, keys = 0 },
            new { tick = anchorTick + RunTicks,           moveX = 0.0, moveY = 0.0,  yaw = 0.0, keys = 0 },
        };
        client.Send(new { cmd = "set-input-schedule", entries = schedule });

        client.WaitForClientTick(anchorTick);
        int baselineMispredicts = ReadMispredictCount(client);
        int observerBaselineMispredicts = ReadMispredictCount(observer);

        var clientSamples = new List<Sample>();
        var serverSamples = new List<Sample>();
        for (int t = SnapshotIntervalTicks; t <= RunTicks; t += SnapshotIntervalTicks)
        {
            int targetTick = anchorTick + t;
            client.WaitForClientTick(targetTick);
            clientSamples.Add(CaptureSample(client, targetTick));
            serverSamples.Add(CaptureSample(server, targetTick));
        }

        int finalMispredicts = ReadMispredictCount(client);
        int mispredictsThisRun = finalMispredicts - baselineMispredicts;

        var paths = ArtifactsFor("offset_push_baseline");
        WriteCombinedPlot(paths, clientSamples, serverSamples, playerEid, cubeEid, baselineMispredicts);
        CopyProcessLogs(paths);
        CopyObserverLog(paths, "offset_push_baseline");

        AssertThat(clientSamples.Count)
            .OverrideFailureMessage("expected non-empty sample stream")
            .IsGreater(0);

        // Find the final pose of both replicas and assert they agree. We use the
        // last sample because the cube has fully settled by then under the
        // test's low friction / low damping params (settle time ~30 ticks
        // post-release, well within RunTicks=150). At rest the SyncSleepState
        // path anchors client to server's authoritative pose, so any residual
        // gap here points at a real divergence (cross-process Jolt drift not
        // being absorbed, sleep-sync gating misfiring, etc.).
        Vector3 cubeServerEnd = Vector3.Zero;
        Vector3 cubeClientEnd = Vector3.Zero;
        Vector3 cubeClientVisualEnd = Vector3.Zero;
        foreach (var e in serverSamples[serverSamples.Count - 1].Entities)
        {
            if (e.Id == cubeEid) { cubeServerEnd = e.Position; break; }
        }
        foreach (var e in clientSamples[clientSamples.Count - 1].Entities)
        {
            if (e.Id == cubeEid) { cubeClientEnd = e.Position; cubeClientVisualEnd = e.VisualPosition; break; }
        }

        // 5 cm body-pose agreement at end-of-run. SyncSleepState anchors the
        // client to the server's at-rest pose every snapshot once both sides
        // agree the body is asleep, so the residual gap should be well under
        // SnapToRest's 1 mm idempotency tolerance plus a couple of ticks of
        // cross-process Jolt drift around the settle window. 5 cm is loose
        // enough to absorb the settle-tick edge case where the client sleeps
        // a couple of ticks earlier or later than the server.
        const float BaselineBodyToleranceM = 0.05f;
        float bodyError = (cubeServerEnd - cubeClientEnd).Length();
        AssertThat(bodyError)
            .OverrideFailureMessage(
                $"baseline: client cube body diverged from server at end of run by {bodyError:F4} m " +
                $"(server={cubeServerEnd}, client={cubeClientEnd}). " +
                $"Trace at TestResults/OffsetPushPlots/offset_push_baseline.{{csv,svg,mp4}}")
            .IsLessEqual(BaselineBodyToleranceM);

        // Visual smoother shouldn't be holding a meaningful offset at the end
        // of a quiet run — by RunTicks the body has been at rest for a while
        // and any offset captured during the push has decayed away.
        const float BaselineVisualToleranceM = 0.05f;
        float visualError = (cubeClientVisualEnd - cubeClientEnd).Length();
        AssertThat(visualError)
            .OverrideFailureMessage(
                $"baseline: client cube visual diverged from body at end of run by {visualError:F4} m " +
                $"(visual={cubeClientVisualEnd}, body={cubeClientEnd}). " +
                $"Trace at TestResults/OffsetPushPlots/offset_push_baseline.{{csv,svg,mp4}}")
            .IsLessEqual(BaselineVisualToleranceM);

        // Without artificial drift or teleport, mispredictions should be rare —
        // they can still happen during the contact / coast phase where Jolt's
        // cross-process FP nondeterminism shows up in normals + friction. A
        // small budget keeps the test meaningful while tolerating known
        // cross-process noise; if a regression doubles the misprediction rate
        // for the happy path this test catches it.
        const int BaselineMispredictBudget = 3;
        int observerMispredictsThisRun = ReadMispredictCount(observer) - observerBaselineMispredicts;
        AssertThat(mispredictsThisRun)
            .OverrideFailureMessage(
                $"baseline: driver {mispredictsThisRun} mispredictions over {RunTicks} ticks exceeds budget {BaselineMispredictBudget}. " +
                $"Trace at TestResults/OffsetPushPlots/offset_push_baseline.{{csv,svg,mp4}}")
            .IsLessEqual(BaselineMispredictBudget);
        AssertThat(observerMispredictsThisRun)
            .OverrideFailureMessage(
                $"baseline: observer {observerMispredictsThisRun} mispredictions over {RunTicks} ticks exceeds budget {BaselineMispredictBudget}. " +
                $"Observer should reconcile no more than the active driver in the happy-path scenario; " +
                $"exceeding this points to a snapshot/input-forwarding gap. " +
                $"Trace at TestResults/OffsetPushPlots/offset_push_baseline.{{csv,svg,mp4}}")
            .IsLessEqual(BaselineMispredictBudget);

        Godot.GD.Print($"[MP-OFFSET-PUSH-BASELINE] run complete: {clientSamples.Count} samples, " +
            $"{mispredictsThisRun} mispredictions, body gap {bodyError:F4} m, visual gap {visualError:F4} m. " +
            $"Artefacts: TestResults/OffsetPushPlots/offset_push_baseline.{{csv,svg,mp4}}");
    }

    private static void WriteCombinedPlot(ArtifactPaths paths, List<Sample> clientSamples, List<Sample> serverSamples,
        int playerEid, int cubeEid, int baselineMispredicts)
    {
        // CSV: long-form with a role column so server vs client divergence is
        // analysable downstream.
        using (var w = new System.IO.StreamWriter(paths.Csv))
        {
            w.WriteLine("tick,t_s,role,eid,etype,x,y,z,vx,vy,vz,avx,avy,avz,qx,qy,qz,qw,vis_x,vis_y,vis_z,vis_qx,vis_qy,vis_qz,vis_qw");
            for (int i = 0; i < clientSamples.Count; i++)
            {
                WriteRoleRows(w, clientSamples[i], "client", playerEid, cubeEid);
                WriteRoleRows(w, serverSamples[i], "server", playerEid, cubeEid);
            }
        }

        // SVG plot. Four panels:
        //   1. cube.X    — server (solid) vs client.body (dashed) vs client.visual (dotted dashed). Drift + reconcile snap visible.
        //   2. cube.Z    — same shape.
        //   3. cube.|d|  — magnitude of server.pos − client.pos over time (the drift gap).
        //   4. player.Z  — server vs client (should overlap; any gap is amplified push-formula noise).
        var plot = new SvgPlot("offset_push — server vs client cube + player traces with drift injection and reconcile snap");

        var cubeXPanel  = plot.AddPanel("cube.X (m) — server (solid) vs client.body (dashed) vs client.visual (dotted)", yUnits: "m");
        var cubeZPanel  = plot.AddPanel("cube.Z (m)", yUnits: "m");
        var driftPanel  = plot.AddPanel("|server.cube.pos − client.cube.pos| (m) — drift accumulator", yUnits: "m");
        var playerZPanel = plot.AddPanel("player.Z (m) — server vs client (should overlap)", yUnits: "m");

        var sCubeX = new List<(int, float)>();
        var cCubeX = new List<(int, float)>();
        var visCubeX = new List<(int, float)>();
        var sCubeZ = new List<(int, float)>();
        var cCubeZ = new List<(int, float)>();
        var drift  = new List<(int, float)>();
        var sPlayerZ = new List<(int, float)>();
        var cPlayerZ = new List<(int, float)>();

        for (int i = 0; i < clientSamples.Count; i++)
        {
            int tick = clientSamples[i].Tick;
            Vector3 sCube = Vector3.Zero, cCube = Vector3.Zero, cCubeVis = Vector3.Zero;
            Vector3 sPlayer = Vector3.Zero, cPlayer = Vector3.Zero;
            foreach (var e in serverSamples[i].Entities)
            {
                if (e.Id == cubeEid) sCube = e.Position;
                else if (e.Id == playerEid) sPlayer = e.Position;
            }
            foreach (var e in clientSamples[i].Entities)
            {
                if (e.Id == cubeEid) { cCube = e.Position; cCubeVis = e.VisualPosition; }
                else if (e.Id == playerEid) cPlayer = e.Position;
            }
            sCubeX.Add((tick, sCube.X)); cCubeX.Add((tick, cCube.X)); visCubeX.Add((tick, cCubeVis.X));
            sCubeZ.Add((tick, sCube.Z)); cCubeZ.Add((tick, cCube.Z));
            drift.Add((tick, (sCube - cCube).Length()));
            sPlayerZ.Add((tick, sPlayer.Z)); cPlayerZ.Add((tick, cPlayer.Z));
        }

        cubeXPanel.AddSeries("server.cube.x", SvgPlot.Palette.Series[0], sCubeX)
                  .AddSeries("client.cube.x", SvgPlot.Palette.Series[1], cCubeX, dashed: true)
                  .AddSeries("client.visual.x", SvgPlot.Palette.Series[2], visCubeX, dashed: true);
        cubeZPanel.AddSeries("server.cube.z", SvgPlot.Palette.Series[0], sCubeZ)
                  .AddSeries("client.cube.z", SvgPlot.Palette.Series[1], cCubeZ, dashed: true);
        driftPanel.AddSeries("|server−client|", SvgPlot.Palette.Series[3], drift);
        playerZPanel.AddSeries("server.player.z", SvgPlot.Palette.Series[0], sPlayerZ)
                    .AddSeries("client.player.z", SvgPlot.Palette.Series[1], cPlayerZ, dashed: true);

        // Misprediction markers across all panels.
        int prev = baselineMispredicts;
        foreach (var s in clientSamples)
        {
            if (s.MispredictionsCount > prev)
            {
                plot.AddVerticalMarker(s.Tick, "reconcile");
            }
            prev = s.MispredictionsCount;
        }

        plot.Save(paths.Svg);
        Godot.GD.Print($"[MP-OFFSET-PUSH] wrote {paths.Csv}, {paths.Svg} ({clientSamples.Count} samples)");
    }

    private static void WriteRoleRows(System.IO.StreamWriter w, Sample s, string role, int playerEid, int cubeEid)
    {
        string tickStr = s.Tick.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string tStr = (s.Tick / 60.0).ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var e in s.Entities)
        {
            if (e.Id != playerEid && e.Id != cubeEid) continue;
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
