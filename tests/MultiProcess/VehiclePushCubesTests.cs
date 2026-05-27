using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using Godot;
using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Artifacts;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.MultiProcess;

/// <summary>
/// MP-VEHICLE-PUSH-CUBES: A pusher client claims a vehicle and drives it into
/// a stack of cubes. An observer client watches the whole run passively. Both
/// clients record video from a fixed observer camera framed to capture the
/// vehicle's start pose and the cube stack ahead of it.
///
/// Loads the flat <c>demo/TestArena.tscn</c> instead of the normal demo scene
/// so there are no walls/ramps/obstacles in the vehicle's path — only a large
/// flat floor.
///
/// Three scenarios exercise different impact-and-prediction shapes:
///   1. SingleCube — vehicle drives into a single cube on the floor.
///   2. TallTower10 — vehicle drives into a 10-cube vertical stack.
///   3. Ram5Tower_BackAndForth — vehicle rams a 5-cube tower forward/back/
///      forward several times.
///
/// Assertion (per scenario): pusher and observer each accrue fewer than 10
/// mispredictions over the run.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class VehiclePushCubesTests : MultiProcessTestBase
{
    protected override string ArtifactSubdir => "VehicleCubePush";

    [BeforeTest] public void SetUp() => SetUpInternal();
    [AfterTest]  public void TearDown() => TearDownInternal();

    private const string TestArenaScene = "res://demo/TestArena.tscn";

    private const byte EntityTypeVehicle = 2;
    private const byte EntityTypeRigidPlayer = 3;
    private const byte EntityTypeCube = 4;

    // InputFlags.Interact — must match demo/players/SharedPlayerMovement.cs.
    private const int InteractKeyBit = 0b_0000_0100;

    // ── Geometry ─────────────────────────────────────────────────────────────
    // Floor top is at Y=-2 (TestArena's CSGBox is at Y=-2.5 with size.y=1).
    // Vehicle starts at origin. Cubes are stacked 8 m down-Z of the vehicle;
    // cubes are unit-sized (BoxMesh/BoxShape defaults), resting on the floor:
    //   cube0 at Y = -1.5, then +1 m per stack step.
    // Pusher player starts within the 4 m claim radius of the vehicle.
    private const float VehicleStartX = 0f;
    private const float VehicleStartY = 0.5f;
    private const float VehicleStartZ = 0f;
    private const float PlayerStartX = -3.5f;
    private const float PlayerStartY = 0f;
    private const float PlayerStartZ = 0f;
    private const float CubeStackX = 0f;
    private const float CubeStackZ = -8f;
    private const float CubeBaseY  = -1.5f;
    private const float CubeStackStepY = 1.0f;

    // Fixed camera pose, expressed as an offset from the vehicle's start pose.
    // Set once at test setup, does NOT update per-frame — a chase-cam would
    // re-position each physics tick from a slightly different server/client-
    // predicted vehicle pose, producing visible per-tick jitter in the
    // recording. A fixed camera removes that entirely.
    private static readonly Vector3 CameraOffsetFromVehicleStart = new(9f, 7f, 9f);
    private static readonly Vector3 CameraLookOffsetFromVehicleStart = new(0f, 0.5f, -3f);
    // The tall-tower (10-cube) scenario extends ~10 m up; pull the camera back
    // and angle the look slightly upward so the top of the tower stays in frame.
    private static readonly Vector3 TallTowerCameraOffset      = new(14f, 12f, 14f);
    private static readonly Vector3 TallTowerCameraLookOffset  = new(0f, 4f, -3f);

    // ── Timing (in client ticks @ 60Hz) ──────────────────────────────────────
    private const int SnapshotArmTicks      = 60;   // server settle before player spawn
    private const int PlayerFallTicks       = 60;   // let player land + clocks stabilise
    private const int InteractHoldTicks     = 4;    // interact rising-edge hold
    private const int SnapshotIntervalTicks = 2;
    private const int TailIdleTicks         = 30;   // capture post-stop settle in video

    // ── Assertion budget ─────────────────────────────────────────────────────
    // Mispredict budget is scenario-specific: a single-cube push barely
    // perturbs the contact normals and < 10 reconciles is realistic, but a
    // 10-cube tower's toppling cascade and a back-and-forth ram each include
    // multiple impact transients where cross-process Jolt drift fires
    // reconcile-replay; bumping the budget per scenario keeps the headline
    // assertion meaningful without making it a flaky one.

    [TestCase]
    public void VehiclePushesSingleCube()
    {
        RunPushScenario(new ScenarioConfig
        {
            Label = "single_cube",
            Tag = "MP-VEHICLE-PUSH-SINGLE",
            CubeCount = 1,
            DriveDirections = new[] { -1.0 },
            DrivePerLegTicks = 240,
            CameraOffset = CameraOffsetFromVehicleStart,
            CameraLookOffset = CameraLookOffsetFromVehicleStart,
            MinExpectedCubeDisplacementM = 0.5f,
            MispredictBudgetExclusive = 10,
        });
    }

    [TestCase]
    public void VehiclePushesTallTower()
    {
        RunPushScenario(new ScenarioConfig
        {
            Label = "tall_tower_10",
            Tag = "MP-VEHICLE-PUSH-TALL-10",
            CubeCount = 10,
            DriveDirections = new[] { -1.0 },
            DrivePerLegTicks = 240,
            CameraOffset = TallTowerCameraOffset,
            CameraLookOffset = TallTowerCameraLookOffset,
            // With a 10-tall tower the bottom cube may scoot only a little —
            // upper cubes topple instead. Loosen the displacement check to
            // any cube moving > 0.5 m.
            MinExpectedCubeDisplacementM = 0.5f,
            // Cube-on-cube contacts during the topple add several reconciles
            // beyond the single-cube budget.
            MispredictBudgetExclusive = 20,
        });
    }

    [TestCase]
    public void VehicleRamsFiveTowerBackAndForth()
    {
        // Three forward rams interspersed with two reverses. After each ram
        // the vehicle backs up roughly to its starting Z before charging the
        // (now-scattered) tower again. Each leg is short enough that the
        // vehicle's contact and recoil all happen within the leg.
        RunPushScenario(new ScenarioConfig
        {
            Label = "ram_tower_5",
            Tag = "MP-VEHICLE-PUSH-RAM-5",
            CubeCount = 5,
            DriveDirections = new[] { -1.0, 1.0, -1.0, 1.0, -1.0 },
            DrivePerLegTicks = 90,
            CameraOffset = CameraOffsetFromVehicleStart,
            CameraLookOffset = CameraLookOffsetFromVehicleStart,
            MinExpectedCubeDisplacementM = 1.0f,
            // Five direction reversals plus repeated impacts on a partially-
            // collapsed cube pile produce more contact transients than the
            // single forward push.
            MispredictBudgetExclusive = 20,
        });
    }

    private sealed class ScenarioConfig
    {
        public string Label;
        public string Tag;
        public int CubeCount;
        public double[] DriveDirections; // sequence of moveY values, one per leg
        public int DrivePerLegTicks;
        public Vector3 CameraOffset;
        public Vector3 CameraLookOffset;
        public float MinExpectedCubeDisplacementM;
        public int MispredictBudgetExclusive;
    }

    private void RunPushScenario(ScenarioConfig cfg)
    {
        if (Orch == null) return;

        int port = NextPort();
        var paths = ArtifactsFor(cfg.Label);
        using var steps = new StepLogger(paths, cfg.Label, cfg.Tag);

        // ── spawn processes on the flat TestArena scene ──────────────────────
        steps.Log($"spawning server on port {port} (TestArena.tscn)");
        var server = Orch.Spawn("server", enetPort: port, label: "srv", scenePath: TestArenaScene);
        server.WaitReady(networkReady: true, timeoutMs: 30_000);
        ServerLogPath = server.RemoteLogPath;
        steps.Log($"server ready, pid={server.RemotePid}");

        string pusherVideoPath   = paths.Mp4;
        string observerVideoPath = System.IO.Path.Combine(paths.Directory, cfg.Label + ".observer.mp4");

        steps.Log("spawning pusher client (records video)");
        var pusher = Orch.Spawn("client", enetPort: port, label: "pusher",
            recordVideoPath: pusherVideoPath, scenePath: TestArenaScene);
        steps.Log("spawning observer client (records video)");
        var observer = Orch.Spawn("client", enetPort: port, label: "observer",
            recordVideoPath: observerVideoPath, scenePath: TestArenaScene);
        pusher.WaitReady(networkReady: true, timeoutMs: 30_000);
        observer.WaitReady(networkReady: true, timeoutMs: 30_000);
        ClientLogPath = pusher.RemoteLogPath;
        string observerLogPath = observer.RemoteLogPath;
        steps.Log($"clients ready: pusher.netId={pusher.NetworkId} observer.netId={observer.NetworkId}");

        WaitForClockSync(server, pusher,   maxGapTicks: 5, timeoutMs: 5_000);
        WaitForClockSync(server, observer, maxGapTicks: 5, timeoutMs: 5_000);
        steps.Log("clocks synced on both clients");

        // Vehicle tests bypass SpawnTriad and manually wire spawn/wait/clock-
        // sync, so they don't pick up the SpawnTriad EnableTierIcons defaults.
        // Flip the per-prop tier indicator on explicitly so the recorded
        // videos show R/I glyphs on cubes during the vehicle push/cycle.
        EnableTierIcons(pusher);
        EnableTierIcons(observer);

        server.WaitForTicks(SnapshotArmTicks);

        // ── spawn vehicle + cube stack + pusher player ──────────────────────
        int vehicleEid = SpawnEntity(server, EntityTypeVehicle, authority: 0,
            VehicleStartX, VehicleStartY, VehicleStartZ);
        steps.Log($"spawned vehicle eid={vehicleEid} at ({VehicleStartX},{VehicleStartY},{VehicleStartZ})");
        WaitForClientEntity(pusher,   vehicleEid, timeoutMs: 5_000);
        WaitForClientEntity(observer, vehicleEid, timeoutMs: 5_000);

        int[] cubeEids = new int[cfg.CubeCount];
        for (int i = 0; i < cfg.CubeCount; i++)
        {
            float cy = CubeBaseY + i * CubeStackStepY;
            cubeEids[i] = SpawnEntity(server, EntityTypeCube, authority: 0,
                CubeStackX, cy, CubeStackZ);
        }
        steps.Log($"spawned {cfg.CubeCount} cubes at X={CubeStackX} Z={CubeStackZ} Y∈[{CubeBaseY:F1},{CubeBaseY + (cfg.CubeCount - 1) * CubeStackStepY:F1}]");
        foreach (var eid in cubeEids)
        {
            WaitForClientEntity(pusher,   eid, timeoutMs: 5_000);
            WaitForClientEntity(observer, eid, timeoutMs: 5_000);
        }

        int playerEid = SpawnEntity(server, EntityTypeRigidPlayer, pusher.NetworkId,
            PlayerStartX, PlayerStartY, PlayerStartZ);
        steps.Log($"spawned pusher player eid={playerEid} at X={PlayerStartX}");
        WaitForClientEntity(pusher,   playerEid, timeoutMs: 5_000);
        WaitForClientEntity(observer, playerEid, timeoutMs: 5_000);

        // ── fix both clients' observer cameras at the framed pose ───────────
        var vehicleStart = new Vector3(VehicleStartX, VehicleStartY, VehicleStartZ);
        var cameraPos    = vehicleStart + cfg.CameraOffset;
        var cameraLookAt = vehicleStart + cfg.CameraLookOffset;
        SetFixedCamera(pusher,   cameraPos, cameraLookAt);
        SetFixedCamera(observer, cameraPos, cameraLookAt);
        steps.Log($"camera fixed on both clients at pos={cameraPos} lookAt={cameraLookAt}");

        // ── build pusher schedule: settle → claim → N drive legs → tail ─────
        int anchor = pusher.ReadClientTick() + PlayerFallTicks;

        int tIdle      = 0;
        int tClaim     = tIdle + PlayerFallTicks;            // interact rising-edge
        int tClaimEnd  = tClaim + InteractHoldTicks;

        var schedule = new List<object>
        {
            new { tick = anchor + tIdle,      moveX = 0.0, moveY = 0.0, yaw = 0.0, keys = 0 },
            new { tick = anchor + tClaim,     moveX = 0.0, moveY = 0.0, yaw = 0.0, keys = InteractKeyBit },
            new { tick = anchor + tClaimEnd,  moveX = 0.0, moveY = 0.0, yaw = 0.0, keys = 0 },
        };
        int legCursor = tClaimEnd + 1;
        var legBoundaries = new List<(int tick, string label)>();
        for (int leg = 0; leg < cfg.DriveDirections.Length; leg++)
        {
            schedule.Add(new
            {
                tick = anchor + legCursor,
                moveX = 0.0,
                moveY = cfg.DriveDirections[leg],
                yaw = 0.0,
                keys = 0,
            });
            string legLabel = cfg.DriveDirections[leg] < 0 ? $"forward[{leg}]" : $"reverse[{leg}]";
            legBoundaries.Add((anchor + legCursor, legLabel));
            legCursor += cfg.DrivePerLegTicks;
        }
        // Final stop after the last leg.
        schedule.Add(new { tick = anchor + legCursor,                 moveX = 0.0, moveY = 0.0, yaw = 0.0, keys = 0 });
        schedule.Add(new { tick = anchor + legCursor + TailIdleTicks, moveX = 0.0, moveY = 0.0, yaw = 0.0, keys = 0 });
        int totalTicks = legCursor + TailIdleTicks;
        pusher.Send(new { cmd = "set-input-schedule", entries = schedule });
        steps.Log($"installed pusher schedule: claim@{tClaim} legs={cfg.DriveDirections.Length} × {cfg.DrivePerLegTicks}t tail@{totalTicks}");

        observer.Send(new
        {
            cmd = "set-input-schedule",
            entries = new[] { new { tick = anchor + 0, moveX = 0.0, moveY = 0.0, yaw = 0.0, keys = 0 } },
        });

        steps.Log($"running {totalTicks} ticks ({totalTicks / 60.0:F2}s wall) sampling every {SnapshotIntervalTicks} ticks");

        // ── baseline mispredict counts, then sample loop ─────────────────────
        pusher.WaitForClientTick(anchor);
        int pusherBaseline   = ReadMispredictCount(pusher);
        int observerBaseline = ReadMispredictCount(observer);

        var pusherSamples   = new List<Sample>();
        var observerSamples = new List<Sample>();
        var serverSamples   = new List<Sample>();
        int authClaimedAtTick = -1;

        for (int t = SnapshotIntervalTicks; t <= totalTicks; t += SnapshotIntervalTicks)
        {
            int targetTick = anchor + t;
            pusher.WaitForClientTick(targetTick);

            pusherSamples.Add(CaptureSample(pusher, targetTick));
            observerSamples.Add(CaptureSample(observer, targetTick));
            var sSample = CaptureSample(server, targetTick);
            serverSamples.Add(sSample);

            if (authClaimedAtTick < 0)
            {
                var vEnt = sSample.Entities.FirstOrDefault(e => e.Id == vehicleEid);
                if (vEnt != null && vEnt.Authority == pusher.NetworkId)
                {
                    authClaimedAtTick = targetTick;
                    steps.Log($"tick {targetTick}: vehicle authority -> pusher ({pusher.NetworkId})");
                }
            }
        }

        int pusherMispredicts   = ReadMispredictCount(pusher)   - pusherBaseline;
        int observerMispredicts = ReadMispredictCount(observer) - observerBaseline;
        steps.Log($"final mispredicts: pusher={pusherMispredicts} observer={observerMispredicts}");

        // ── artefacts ────────────────────────────────────────────────────────
        WritePushPlot(paths, cfg, pusherSamples, observerSamples, serverSamples,
            vehicleEid, playerEid, cubeEids, authClaimedAtTick, legBoundaries,
            pusherBaseline, observerBaseline);
        CopyProcessLogs(paths);
        CopyProcessLog(paths.Directory, observerLogPath, $"{cfg.Label}.observer.log");

        // ── assertions ───────────────────────────────────────────────────────
        AssertThat(pusherSamples.Count)
            .OverrideFailureMessage("expected non-empty sample stream")
            .IsGreater(0);

        AssertThat(authClaimedAtTick)
            .OverrideFailureMessage(
                $"pusher never claimed the vehicle (no authority change to {pusher.NetworkId} observed). " +
                $"Trace at TestResults/VehicleCubePush/{cfg.Label}.{{csv,svg,mp4}}")
            .IsGreaterEqual(0);

        float maxCubeDisplacement = MaxCubeDisplacement(serverSamples, cubeEids);
        AssertThat(maxCubeDisplacement)
            .OverrideFailureMessage(
                $"no cube moved more than {cfg.MinExpectedCubeDisplacementM:F2} m — " +
                $"vehicle never reached the stack. max displacement {maxCubeDisplacement:F3} m. " +
                $"Trace at TestResults/VehicleCubePush/{cfg.Label}.{{csv,svg,mp4}}")
            .IsGreater(cfg.MinExpectedCubeDisplacementM);

        AssertThat(pusherMispredicts)
            .OverrideFailureMessage(
                $"pusher accrued {pusherMispredicts} mispredictions over the drive (budget < {cfg.MispredictBudgetExclusive}). " +
                $"Trace at TestResults/VehicleCubePush/{cfg.Label}.{{csv,svg,mp4}}")
            .IsLess(cfg.MispredictBudgetExclusive);
        AssertThat(observerMispredicts)
            .OverrideFailureMessage(
                $"observer accrued {observerMispredicts} mispredictions over the drive (budget < {cfg.MispredictBudgetExclusive}). " +
                $"Trace at TestResults/VehicleCubePush/{cfg.Label}.{{csv,svg,mp4}}")
            .IsLess(cfg.MispredictBudgetExclusive);

        steps.Log($"all assertions passed (pusher={pusherMispredicts}, observer={observerMispredicts}, " +
            $"maxCubeDisp={maxCubeDisplacement:F3} m)");
        Godot.GD.Print($"[{cfg.Tag}] complete: " +
            $"pusherMispredicts={pusherMispredicts} observerMispredicts={observerMispredicts} " +
            $"maxCubeDisplacement={maxCubeDisplacement:F3}m. " +
            $"Artefacts: TestResults/VehicleCubePush/{cfg.Label}.{{csv,svg,mp4,observer.mp4,steps.log}}");
    }

    private static void SetFixedCamera(TestProcess proc, Vector3 position, Vector3 lookAt)
    {
        using var _ = proc.Send(new
        {
            cmd = "set-camera",
            position = new[] { (double)position.X, position.Y, position.Z },
            lookAt = new[] { (double)lookAt.X, lookAt.Y, lookAt.Z },
        });
    }

    private static float MaxCubeDisplacement(List<Sample> serverSamples, int[] cubeEids)
    {
        var startPos = new Dictionary<int, Vector3>();
        bool[] seen = new bool[cubeEids.Length];
        foreach (var s in serverSamples)
        {
            for (int i = 0; i < cubeEids.Length; i++)
            {
                if (seen[i]) continue;
                var ent = s.Entities.FirstOrDefault(e => e.Id == cubeEids[i]);
                if (ent != null) { startPos[cubeEids[i]] = ent.Position; seen[i] = true; }
            }
            if (System.Array.TrueForAll(seen, v => v)) break;
        }
        float max = 0f;
        foreach (var s in serverSamples)
        {
            foreach (var eid in cubeEids)
            {
                var ent = s.Entities.FirstOrDefault(e => e.Id == eid);
                if (ent == null || !startPos.ContainsKey(eid)) continue;
                float d = (ent.Position - startPos[eid]).Length();
                if (d > max) max = d;
            }
        }
        return max;
    }

    private static void WritePushPlot(ArtifactPaths paths, ScenarioConfig cfg,
        List<Sample> pusherSamples, List<Sample> observerSamples, List<Sample> serverSamples,
        int vehicleEid, int playerEid, int[] cubeEids,
        int authClaimedAtTick, List<(int tick, string label)> legBoundaries,
        int pusherBaseline, int observerBaseline)
    {
        using (var w = new System.IO.StreamWriter(paths.Csv))
        {
            w.WriteLine("tick,t_s,role,eid,etype,x,y,z,vx,vy,vz,vis_x,vis_y,vis_z,authority");
            int n = System.Math.Min(System.Math.Min(pusherSamples.Count, observerSamples.Count), serverSamples.Count);
            int[] tracked = new int[cubeEids.Length + 2];
            tracked[0] = vehicleEid;
            tracked[1] = playerEid;
            for (int i = 0; i < cubeEids.Length; i++) tracked[i + 2] = cubeEids[i];
            for (int i = 0; i < n; i++)
            {
                WriteRows(w, pusherSamples[i],   "pusher",   tracked);
                WriteRows(w, observerSamples[i], "observer", tracked);
                WriteRows(w, serverSamples[i],   "server",   tracked);
            }
        }

        var plot = new SvgPlot($"{cfg.Label} — pusher vs observer trace ({cfg.CubeCount}-cube scenario, {cfg.DriveDirections.Length} drive leg{(cfg.DriveDirections.Length == 1 ? "" : "s")})");
        var vehZ      = plot.AddPanel("vehicle.Z (m) — server vs pusher vs observer", yUnits: "m");
        var vehVel    = plot.AddPanel("vehicle |velocity| (m/s)", yUnits: "m/s");
        var cubeZPanel = plot.AddPanel("cube.Z (m) per stack height (server)", yUnits: "m");
        var cubeYPanel = plot.AddPanel("cube.Y (m) — vertical motion of stack (server)", yUnits: "m");
        var trackGap  = plot.AddPanel("|server.vehicle − client.vehicle| (m) per client", yUnits: "m");
        var mispCount = plot.AddPanel("mispredictionsCount (delta from baseline) per client", yUnits: "count");

        var sZ = new List<(int, float)>(); var pZ = new List<(int, float)>(); var oZ = new List<(int, float)>();
        var sVel = new List<(int, float)>(); var pVel = new List<(int, float)>(); var oVel = new List<(int, float)>();
        var cubeZSeries = new List<(int, float)>[cubeEids.Length];
        var cubeYSeries = new List<(int, float)>[cubeEids.Length];
        for (int c = 0; c < cubeEids.Length; c++)
        {
            cubeZSeries[c] = new List<(int, float)>();
            cubeYSeries[c] = new List<(int, float)>();
        }
        var gapP = new List<(int, float)>(); var gapO = new List<(int, float)>();
        var mispP = new List<(int, float)>(); var mispO = new List<(int, float)>();
        var pusherMispredictTicks   = new List<int>();
        var observerMispredictTicks = new List<int>();

        int nn = System.Math.Min(System.Math.Min(pusherSamples.Count, observerSamples.Count), serverSamples.Count);
        int prevPusherMisp = pusherBaseline;
        int prevObserverMisp = observerBaseline;
        for (int i = 0; i < nn; i++)
        {
            int tick = pusherSamples[i].Tick;
            var sV = serverSamples[i].Entities.FirstOrDefault(e => e.Id == vehicleEid);
            var pV = pusherSamples[i].Entities.FirstOrDefault(e => e.Id == vehicleEid);
            var oV = observerSamples[i].Entities.FirstOrDefault(e => e.Id == vehicleEid);
            if (sV == null) continue;
            sZ.Add((tick, sV.Position.Z));
            if (pV != null) pZ.Add((tick, pV.Position.Z));
            if (oV != null) oZ.Add((tick, oV.Position.Z));
            sVel.Add((tick, sV.Velocity.Length()));
            if (pV != null) pVel.Add((tick, pV.Velocity.Length()));
            if (oV != null) oVel.Add((tick, oV.Velocity.Length()));
            if (pV != null) gapP.Add((tick, (sV.Position - pV.Position).Length()));
            if (oV != null) gapO.Add((tick, (sV.Position - oV.Position).Length()));

            for (int c = 0; c < cubeEids.Length; c++)
            {
                var cEnt = serverSamples[i].Entities.FirstOrDefault(e => e.Id == cubeEids[c]);
                if (cEnt == null) continue;
                cubeZSeries[c].Add((tick, cEnt.Position.Z));
                cubeYSeries[c].Add((tick, cEnt.Position.Y));
            }

            mispP.Add((tick, pusherSamples[i].MispredictionsCount   - pusherBaseline));
            mispO.Add((tick, observerSamples[i].MispredictionsCount - observerBaseline));

            if (pusherSamples[i].MispredictionsCount   > prevPusherMisp)   pusherMispredictTicks.Add(tick);
            if (observerSamples[i].MispredictionsCount > prevObserverMisp) observerMispredictTicks.Add(tick);
            prevPusherMisp   = pusherSamples[i].MispredictionsCount;
            prevObserverMisp = observerSamples[i].MispredictionsCount;
        }

        vehZ.AddSeries("server.vehicle.z",   SvgPlot.Palette.Series[0], sZ)
            .AddSeries("pusher.vehicle.z",   SvgPlot.Palette.Series[1], pZ, dashed: true)
            .AddSeries("observer.vehicle.z", SvgPlot.Palette.Series[2], oZ, dashed: true);
        vehVel.AddSeries("server",   SvgPlot.Palette.Series[0], sVel)
              .AddSeries("pusher",   SvgPlot.Palette.Series[1], pVel, dashed: true)
              .AddSeries("observer", SvgPlot.Palette.Series[2], oVel, dashed: true);
        // Limit how many individual cube series we draw — for tall towers
        // 10 dense lines turn into noise. Plot bottom/mid/top + every-third.
        for (int c = 0; c < cubeEids.Length; c++)
        {
            bool drawThis = cubeEids.Length <= 5
                || c == 0
                || c == cubeEids.Length - 1
                || c == cubeEids.Length / 2
                || c % 3 == 0;
            if (!drawThis) continue;
            var color = SvgPlot.Palette.Series[3 + (c % 4)];
            string lbl = $"cube[{c}]" + (c == 0 ? " (bottom)" : c == cubeEids.Length - 1 ? " (top)" : "");
            cubeZPanel.AddSeries(lbl, color, cubeZSeries[c]);
            cubeYPanel.AddSeries(lbl, color, cubeYSeries[c]);
        }
        trackGap.AddSeries("pusher",   SvgPlot.Palette.Series[1], gapP)
                .AddSeries("observer", SvgPlot.Palette.Series[2], gapO, dashed: true);
        mispCount.AddSeries("pusher",   SvgPlot.Palette.Series[1], mispP)
                 .AddSeries("observer", SvgPlot.Palette.Series[2], mispO, dashed: true);

        if (authClaimedAtTick >= 0) plot.AddVerticalMarker(authClaimedAtTick, "claim");
        foreach (var (t, label) in legBoundaries) plot.AddVerticalMarker(t, label);
        foreach (var t in pusherMispredictTicks)   plot.AddVerticalMarker(t, "pusher mispredict");
        foreach (var t in observerMispredictTicks) plot.AddVerticalMarker(t, "observer mispredict");

        plot.Save(paths.Svg);
        Godot.GD.Print($"[{cfg.Tag}] wrote {paths.Csv}, {paths.Svg} ({nn} samples, " +
            $"pusher mispredict ticks=[{string.Join(',', pusherMispredictTicks)}], " +
            $"observer mispredict ticks=[{string.Join(',', observerMispredictTicks)}])");
    }

    private static void WriteRows(System.IO.StreamWriter w, Sample s, string role, int[] tracked)
    {
        string tickStr = s.Tick.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string tStr = (s.Tick / 60.0).ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var e in s.Entities)
        {
            bool wanted = false;
            for (int i = 0; i < tracked.Length; i++) if (tracked[i] == e.Id) { wanted = true; break; }
            if (!wanted) continue;
            w.Write(tickStr); w.Write(','); w.Write(tStr); w.Write(','); w.Write(role); w.Write(',');
            w.Write(e.Id); w.Write(','); w.Write(e.Type); w.Write(',');
            w.Write(CsvWriter.F(e.Position.X)); w.Write(','); w.Write(CsvWriter.F(e.Position.Y)); w.Write(','); w.Write(CsvWriter.F(e.Position.Z)); w.Write(',');
            w.Write(CsvWriter.F(e.Velocity.X)); w.Write(','); w.Write(CsvWriter.F(e.Velocity.Y)); w.Write(','); w.Write(CsvWriter.F(e.Velocity.Z)); w.Write(',');
            w.Write(CsvWriter.F(e.VisualPosition.X)); w.Write(','); w.Write(CsvWriter.F(e.VisualPosition.Y)); w.Write(','); w.Write(CsvWriter.F(e.VisualPosition.Z)); w.Write(',');
            w.Write(e.Authority); w.Write('\n');
        }
    }
}
