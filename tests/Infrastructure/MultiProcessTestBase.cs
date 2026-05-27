using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using GdUnit4;
using Godot;
using MonkeNet.Tests.Infrastructure.Artifacts;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Infrastructure;

/// <summary>
/// Shared base class for all multi-process tests. Centralises the boilerplate
/// every <c>MultiProcess*Tests</c> file used to copy-paste: <c>GODOT_BIN</c>
/// resolution, project-path discovery, orchestrator lifecycle, artifact
/// directory creation, ENet port allocation, clock-sync waits, mispredict-count
/// reads, sample capture, log copying.
///
/// Derived classes:
/// <list type="bullet">
///   <item>Override <see cref="ArtifactSubdir"/> to name the per-scenario
///         directory under <c>tests/TestResults/</c>.</item>
///   <item>Declare a <c>[BeforeTest]</c> / <c>[AfterTest]</c> pair that calls
///         <see cref="SetUpInternal"/> / <see cref="TearDownInternal"/>. We use
///         explicit forwarding rather than relying on GdUnit4 picking up
///         inherited attributes — this works regardless of attribute
///         inheritance semantics.</item>
///   <item>Skip the test body early when <see cref="Orch"/> is null — that
///         means <c>GODOT_BIN</c> wasn't set, so we can't spawn children.</item>
/// </list>
/// </summary>
public abstract class MultiProcessTestBase
{
    /// <summary>Subdirectory name under <c>tests/TestResults/</c> where this
    /// scenario's artifacts live. E.g. <c>"PredictReplay"</c>.</summary>
    protected abstract string ArtifactSubdir { get; }

    protected string GodotBin { get; private set; }
    protected string ProjectPath { get; private set; }
    protected MultiProcessOrchestrator Orch { get; private set; }

    /// <summary>Per-process MonkeLogger log paths reported via the harness ready
    /// cmd. Populated by <see cref="SpawnPair"/>; used by <see cref="WriteArtifacts"/>
    /// to copy each subprocess's log into the artifact dir. Derived classes may
    /// also set these directly when they manage their own spawn lifecycle
    /// (e.g. multi-server / multi-client lifecycle scenarios).</summary>
    protected string ServerLogPath { get; set; }
    protected string ClientLogPath { get; set; }

    // ENet port allocation. ENet uses UDP; we ask the OS for a free ephemeral
    // UDP port by binding a UdpClient on port 0, reading the assigned port off
    // LocalEndPoint, and closing the socket. The port is then handed to the
    // server harness which re-binds with ENet. There's a microscopic TOCTOU
    // window between close and re-bind, but the ephemeral-port range is large
    // enough (49152-65535 on Windows by default) that a collision with another
    // process is effectively impossible.
    //
    // This replaces the previous static counter (started at 9100, incremented
    // per call) which broke parallel `dotnet test` invocations: two test
    // processes both started at 9100 and the second's CreateServer failed with
    // WSAEADDRINUSE. OS-assigned ports work across any number of concurrent
    // test runs.
    protected static int NextPort()
    {
        using var udp = new System.Net.Sockets.UdpClient(
            new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        return ((System.Net.IPEndPoint)udp.Client.LocalEndPoint).Port;
    }

    /// <summary>Active UDP relays, owned by the test base so they live alongside the
    /// orchestrator and are torn down in <see cref="TearDownInternal"/>.</summary>
    private readonly List<UdpRelay> _relays = new();

    /// <summary>Start a UDP relay in front of <paramref name="serverEnetPort"/>.
    /// Clients should connect to the returned relay's <c>ListenPort</c> instead
    /// of the server's port directly. Use <see cref="UdpRelay.SetConditions"/>
    /// to configure latency, jitter, and packet loss; the relay starts in
    /// transparent (zero-condition) mode so the call is safe even for baseline
    /// runs.</summary>
    protected UdpRelay StartRelay(int serverEnetPort)
    {
        var relay = new UdpRelay(serverEnetPort);
        _relays.Add(relay);
        return relay;
    }

    /// <summary>Walks up from CWD looking for the nearest <c>project.godot</c>.
    /// When tests run via <c>dotnet test</c>, CWD is typically the <c>tests/</c>
    /// dir, which contains its own <c>project.godot</c>; that's the path
    /// returned. Artifacts therefore land under <c>tests/TestResults/...</c>.</summary>
    public static string ResolveProjectPath()
    {
        var dir = new DirectoryInfo(System.Environment.CurrentDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "project.godot"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate project.godot from current working directory");
    }

    /// <summary>Derived <c>[BeforeTest]</c> calls this. Resolves <c>GODOT_BIN</c>;
    /// if missing, leaves <see cref="Orch"/> null so the test body can early-out.</summary>
    protected void SetUpInternal()
    {
        GodotBin = System.Environment.GetEnvironmentVariable("GODOT_BIN");
        if (string.IsNullOrEmpty(GodotBin) || !File.Exists(GodotBin)) return;

        ProjectPath = ResolveProjectPath();
        Directory.CreateDirectory(Path.Combine(ProjectPath, "TestResults", ArtifactSubdir));
        Orch = new MultiProcessOrchestrator(GodotBin, ProjectPath);
    }

    /// <summary>Derived <c>[AfterTest]</c> calls this. Kills all spawned children
    /// and tears down any active UDP relays.</summary>
    protected void TearDownInternal()
    {
        Orch?.Dispose();
        Orch = null;
        foreach (var r in _relays) { try { r.Dispose(); } catch { /* best effort */ } }
        _relays.Clear();
    }

    /// <summary>Get the artifact path bundle for a given label. Always under
    /// <c>{projectPath}/TestResults/{ArtifactSubdir}/{label}.{ext}</c>.</summary>
    protected ArtifactPaths ArtifactsFor(string label) => ArtifactPaths.For(ProjectPath, ArtifactSubdir, label);

    /// <summary>Per-scenario observer log path captured by <see cref="SpawnTriad"/>.
    /// Tests that use SpawnTriad call <see cref="CopyObserverLog"/> in the artefacts
    /// phase to land this alongside server/client logs.</summary>
    protected string ObserverLogPath { get; set; }

    /// <summary>Spawn one server + one active client + one passive observer client.
    /// All three processes load the default <c>MainScene.tscn</c> unless
    /// <paramref name="scenePath"/> is set. The active client gets
    /// <c>recordVideoPath = paths.Mp4</c> and the observer gets
    /// <c>{paths.Directory}/{label}.observer.mp4</c> when <paramref name="recordVideo"/>
    /// is true. The observer is parked on an idle input schedule so it doesn't
    /// emit inputs that could perturb its predicted state.
    ///
    /// Use this from any physics test where you have an active driver but would
    /// otherwise spawn no second client — it adds a passive witness so a third-
    /// party perspective is recorded.</summary>
    protected (TestProcess server, TestProcess client, TestProcess observer) SpawnTriad(
        string label, bool recordVideo = false, string scenePath = null)
    {
        int port = NextPort();
        var server = Orch.Spawn("server", enetPort: port, label: "srv", scenePath: scenePath);
        server.WaitReady(networkReady: true, timeoutMs: 30_000);

        string clientVideo = null, observerVideo = null;
        if (recordVideo)
        {
            var paths = ArtifactsFor(label);
            clientVideo   = paths.Mp4;
            observerVideo = System.IO.Path.Combine(paths.Directory, label + ".observer.mp4");
        }

        var client   = Orch.Spawn("client", enetPort: port, label: "c1",
            recordVideoPath: clientVideo, scenePath: scenePath);
        var observer = Orch.Spawn("client", enetPort: port, label: "observer",
            recordVideoPath: observerVideo, scenePath: scenePath);
        client.WaitReady(networkReady: true, timeoutMs: 30_000);
        observer.WaitReady(networkReady: true, timeoutMs: 30_000);

        ServerLogPath = server.RemoteLogPath;
        ClientLogPath = client.RemoteLogPath;
        ObserverLogPath = observer.RemoteLogPath;

        WaitForClockSync(server, client,   maxGapTicks: 5, timeoutMs: 5_000);
        WaitForClockSync(server, observer, maxGapTicks: 5, timeoutMs: 5_000);

        int clientNetId = client.NetworkId;
        AssertThat(clientNetId).OverrideFailureMessage("client must have a non-zero ENet peer id").IsNotEqual(0);

        // Per-prop tier icons are on by default in tests so recorded videos
        // make tier transitions self-evident. Gameplay defaults off — see
        // LocalRigidPropPrediction.ShowTierIcons. Flip on both the driver and
        // the observer; the observer's video should show the same glyphs.
        EnableTierIcons(client);
        EnableTierIcons(observer);

        // Park the observer idle from its current tick so it doesn't try to
        // generate inputs of its own. The harness's default input is already
        // zeroed, but installing an explicit schedule of one idle entry keeps
        // the harness's input-producer pipeline consistent with the driver.
        observer.Send(new
        {
            cmd = "set-input-schedule",
            entries = new[] { new { tick = observer.ReadClientTick(), moveX = 0.0, moveY = 0.0, yaw = 0.0, keys = 0 } },
        });

        return (server, client, observer);
    }

    /// <summary>Copy the observer's MonkeLogger output into the artefact dir
    /// under <c>{label}.observer.log</c>. Pairs with <see cref="SpawnTriad"/>.</summary>
    protected void CopyObserverLog(ArtifactPaths paths, string label)
    {
        if (string.IsNullOrEmpty(ObserverLogPath)) return;
        CopyProcessLog(paths.Directory, ObserverLogPath, label + ".observer.log");
    }

    /// <summary>Spawn one server + one client, wait for both to be network-ready,
    /// wait for the clocks to converge, and return the pair. The client is
    /// optionally configured to record video via the shared
    /// <see cref="ArtifactPaths.Mp4"/> path.</summary>
    protected (TestProcess server, TestProcess client) SpawnPair(string label, bool recordVideo = false)
    {
        int port = NextPort();
        var server = Orch.Spawn("server", enetPort: port, label: "srv");
        server.WaitReady(networkReady: true, timeoutMs: 30_000);

        string videoPath = null;
        if (recordVideo)
        {
            var paths = ArtifactsFor(label);
            videoPath = paths.Mp4;
        }

        var client = Orch.Spawn("client", enetPort: port, label: "c1", recordVideoPath: videoPath);
        client.WaitReady(networkReady: true, timeoutMs: 30_000);

        ServerLogPath = server.RemoteLogPath;
        ClientLogPath = client.RemoteLogPath;

        // Wait for the client clock to be synced to the server before any
        // entity spawns. networkReady=true alone doesn't imply the clock has
        // converged in all topologies — see MultiProcessClockSyncTests for
        // the baseline. Without this, the first entity spawn races the first
        // averaged clock-sync window and the client renders entities at stale
        // tick offsets, inflating measured misprediction counts for reasons
        // unrelated to physics.
        WaitForClockSync(server, client, maxGapTicks: 5, timeoutMs: 5_000);

        int clientNetId = client.NetworkId;
        AssertThat(clientNetId).OverrideFailureMessage("client must have a non-zero ENet peer id").IsNotEqual(0);

        // See SpawnTriad for the rationale on enabling tier icons by default
        // in test runs.
        EnableTierIcons(client);

        return (server, client);
    }

    /// <summary>Flip the per-prop tier indicator on for a client/observer
    /// process. Tier icons are off in gameplay but on for tests so the
    /// recorded video makes the R/I transitions easy to spot. Failures here
    /// are swallowed — an older harness without the cmd shouldn't break
    /// existing tests, it just means no glyphs in the recording.</summary>
    protected static void EnableTierIcons(TestProcess proc)
    {
        try { proc.Send(new { cmd = "set-tier-icons", enabled = true }); }
        catch { /* harness too old for this cmd; ignore */ }
    }

    /// <summary>Polls both processes' clock-state until the absolute gap
    /// (clientSyncedTick − serverTick − latency) is within budget. Establishes
    /// a synced clock before the test starts spawning entities so the trace
    /// measures physics misprediction, not "client clock catching up to server".</summary>
    protected static void WaitForClockSync(TestProcess server, TestProcess client,
        int maxGapTicks, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        int lastGap = int.MinValue;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var sDoc = server.Send(new { cmd = "clock-state" });
                using var cDoc = client.Send(new { cmd = "clock-state" });
                int serverTick = sDoc.RootElement.GetProperty("data").GetProperty("serverTick").GetInt32();
                int syncedTick = cDoc.RootElement.GetProperty("data").GetProperty("syncedTick").GetInt32();
                int latency = cDoc.RootElement.GetProperty("data").GetProperty("averageLatencyTicks").GetInt32();
                int gap = syncedTick - serverTick - latency;
                lastGap = gap;
                if (Math.Abs(gap) <= maxGapTicks) return;
            }
            catch { /* harness not ready yet; retry */ }
            Thread.Sleep(50);
        }
        Godot.GD.PrintErr($"[MultiProcessTestBase] clock did not converge to ±{maxGapTicks} within {timeoutMs}ms; last gap={lastGap}");
    }

    /// <summary>Returns the client's accumulated misprediction count.</summary>
    protected static int ReadMispredictCount(TestProcess client)
    {
        using var doc = client.Send(new { cmd = "mispredict-count" });
        return doc.RootElement.GetProperty("data").GetProperty("count").GetInt32();
    }

    /// <summary>Public wrapper around <see cref="CaptureSample"/> so non-subclass
    /// callers (the quantitative test scenarios) can read the same snapshot.</summary>
    public static Sample CaptureSampleStatic(TestProcess proc, int sampleTick) =>
        CaptureSample(proc, sampleTick);

    /// <summary>Capture a <see cref="Sample"/> from a process's harness state.
    /// Reads <c>sample-state</c> and pulls every entity's position, velocity,
    /// rotation, angular velocity, visual pose, and sleep flags (optional fields
    /// fall back gracefully if the harness doesn't supply them).</summary>
    protected static Sample CaptureSample(TestProcess proc, int sampleTick)
    {
        using var doc = proc.Send(new { cmd = "sample-state" });
        var root = doc.RootElement.GetProperty("data");
        var s = new Sample
        {
            Tick = sampleTick,
            MispredictionsCount = root.TryGetProperty("mispredictionsCount", out var mc) ? mc.GetInt32() : 0,
            Entities = new List<EntityState>(),
        };
        foreach (var el in root.GetProperty("entities").EnumerateArray())
        {
            var pos = el.GetProperty("position");
            var vel = el.GetProperty("velocity");
            var st = new EntityState
            {
                Id = el.GetProperty("id").GetInt32(),
                Type = el.GetProperty("type").GetInt32(),
                Authority = el.GetProperty("authority").GetInt32(),
                Position = new Vector3((float)pos[0].GetDouble(), (float)pos[1].GetDouble(), (float)pos[2].GetDouble()),
                Velocity = new Vector3((float)vel[0].GetDouble(), (float)vel[1].GetDouble(), (float)vel[2].GetDouble()),
            };
            if (el.TryGetProperty("angularVelocity", out var av))
            {
                st.AngularVelocity = new Vector3((float)av[0].GetDouble(), (float)av[1].GetDouble(), (float)av[2].GetDouble());
            }
            if (el.TryGetProperty("rotation", out var rq))
            {
                st.Rotation = new Quaternion(
                    (float)rq[0].GetDouble(), (float)rq[1].GetDouble(),
                    (float)rq[2].GetDouble(), (float)rq[3].GetDouble());
            }
            if (el.TryGetProperty("visualPosition", out var vp))
            {
                st.VisualPosition = new Vector3((float)vp[0].GetDouble(), (float)vp[1].GetDouble(), (float)vp[2].GetDouble());
            }
            else st.VisualPosition = st.Position;
            if (el.TryGetProperty("visualRotation", out var vr))
            {
                st.VisualRotation = new Quaternion(
                    (float)vr[0].GetDouble(), (float)vr[1].GetDouble(),
                    (float)vr[2].GetDouble(), (float)vr[3].GetDouble());
            }
            else st.VisualRotation = st.Rotation;
            if (el.TryGetProperty("sleeping", out var sl)) st.Sleeping = sl.GetBoolean();
            if (el.TryGetProperty("canSleep", out var cs)) st.CanSleep = cs.GetBoolean();
            s.Entities.Add(st);
        }
        return s;
    }

    /// <summary>Polls the client until the given entity id appears in its
    /// <c>ClientEntities</c> collection (i.e. the server's spawn has been
    /// snapshotted and registered locally). Returns silently on timeout —
    /// the caller's subsequent operations will surface the real error.</summary>
    protected static void WaitForClientEntity(TestProcess client, int eid, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            using var doc = client.Send(new { cmd = "get-all-entities" });
            foreach (var e in doc.RootElement.GetProperty("data").GetProperty("entities").EnumerateArray())
            {
                if (e.GetProperty("id").GetInt32() == eid) return;
            }
            Thread.Sleep(20);
        }
        Godot.GD.PrintErr($"[MultiProcessTestBase] client entity {eid} did not appear within {timeoutMs}ms");
    }

    /// <summary>Spawn a server-side entity. Returns the entity id.</summary>
    protected static int SpawnEntity(TestProcess server, byte entityType, int authority,
        float x, float y, float z)
    {
        using var r = server.Send(new
        {
            cmd = "spawn-entity",
            entity_type = (int)entityType,
            authority,
            position = new[] { (double)x, y, z },
        });
        return r.RootElement.GetProperty("data").GetProperty("entityId").GetInt32();
    }

    /// <summary>Copy a subprocess's MonkeLogger output file into the artifact
    /// directory under a role-tagged name. Uses FileShare.ReadWrite so the
    /// child process can still be writing to the source file.</summary>
    protected static void CopyProcessLog(string artifactDir, string srcPath, string targetName)
    {
        if (string.IsNullOrEmpty(srcPath))
        {
            Godot.GD.PrintErr($"[MultiProcessTestBase] no log path reported for {targetName}");
            return;
        }
        if (!File.Exists(srcPath))
        {
            Godot.GD.PrintErr($"[MultiProcessTestBase] log file does not exist: {srcPath}");
            return;
        }
        try
        {
            using var src = new FileStream(srcPath, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
            using var dst = new FileStream(Path.Combine(artifactDir, targetName), FileMode.Create, System.IO.FileAccess.Write, FileShare.Read);
            src.CopyTo(dst);
            Godot.GD.Print($"[MultiProcessTestBase] copied log {Path.GetFileName(srcPath)} → {targetName}");
        }
        catch (Exception ex)
        {
            Godot.GD.PrintErr($"[MultiProcessTestBase] failed to copy {srcPath}: {ex.Message}");
        }
    }

    /// <summary>Copy both server and client logs into the artifact dir alongside
    /// the CSV/SVG/MP4 written by the test.</summary>
    protected void CopyProcessLogs(ArtifactPaths paths)
    {
        CopyProcessLog(paths.Directory, ServerLogPath, Path.GetFileName(paths.ServerLog));
        CopyProcessLog(paths.Directory, ClientLogPath, Path.GetFileName(paths.ClientLog));
    }

    /// <summary>
    /// A persistent per-scenario step log. Multi-process lifecycle tests use
    /// this to record each phase ("spawning server2", "client1 connected,
    /// netId=2", ...) to a file alongside the per-process logs, so reading
    /// the log answers "what did the test orchestrate and in what order?"
    /// without re-running.
    ///
    /// Each <see cref="Log"/> call timestamps the line, prints via
    /// <c>Godot.GD.Print</c> (test-runner stdout), and appends to
    /// <c>&lt;artifactDir&gt;/&lt;label&gt;.steps.log</c>. The file is
    /// flushed per line so a crash mid-test leaves a partial trace rather
    /// than a buffered no-op.
    /// </summary>
    protected sealed class StepLogger : IDisposable
    {
        private readonly System.IO.StreamWriter _writer;
        private readonly System.Diagnostics.Stopwatch _clock;
        private readonly string _tag;

        public StepLogger(ArtifactPaths paths, string label, string tag)
        {
            string path = System.IO.Path.Combine(paths.Directory, label + ".steps.log");
            _writer = new System.IO.StreamWriter(path) { AutoFlush = true };
            _clock = System.Diagnostics.Stopwatch.StartNew();
            _tag = tag;
            Log($"step log opened at {DateTime.Now:O}");
        }

        public void Log(string message)
        {
            long ms = _clock.ElapsedMilliseconds;
            string line = $"[{_tag}] +{ms,6} ms — {message}";
            Godot.GD.Print(line);
            try { _writer.WriteLine(line); } catch { /* best effort */ }
        }

        public void Dispose()
        {
            try { _writer.Dispose(); } catch { }
        }
    }
}
