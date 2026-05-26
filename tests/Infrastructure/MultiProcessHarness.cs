using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace MonkeNet.Tests.Infrastructure;

/// <summary>
/// Test-side orchestration of multi-process integration tests. Spawns child
/// Godot processes, each loading
/// <c>res://tests/MultiProcess/harness.tscn</c> with role and port arguments,
/// then drives them via line-delimited JSON over TCP.
///
/// Each child process has its OWN Godot World3D, physics space, MonkeNet
/// singletons, and operating system process — eliminating the same-process-
/// shared-state concerns of in-process multi-client tests.
///
/// Typical usage:
/// <code>
///   var orch = new MultiProcessOrchestrator(godotBinPath: GodotBin, projectPath: ProjectPath);
///   var server = orch.Spawn("server", enetPort: 9100, label: "srv");
///   var client = orch.Spawn("client", enetPort: 9100, label: "c1");
///   server.WaitReady();
///   client.WaitReady(networkReady: true);  // waits for ENet handshake
///   server.Send(new { cmd = "spawn-ball", authority = 0, position = new[]{0,5,0} });
///   ...
///   orch.Dispose(); // kills all children
/// </code>
/// </summary>
public class MultiProcessOrchestrator : IDisposable
{
    private readonly string _godotBin;
    private readonly string _projectPath;
    private readonly List<TestProcess> _processes = new();

    // Orch port allocation. The orchestrator talks to each spawned harness over
    // TCP; we ask the OS for a free ephemeral TCP port by binding a TcpListener
    // on port 0, reading the assigned port, and closing the listener. The
    // child Godot process then re-binds with TcpListener on the same port.
    // Same TOCTOU caveat as NextPort above — negligible in practice.
    //
    // Replaces the previous static counter (started at 9500, incremented per
    // Spawn) which collided when two `dotnet test` invocations ran in
    // parallel: both processes started at 9500 and child #2 of run B couldn't
    // bind. OS-assigned ports work across any number of concurrent test runs.
    private static int NextOrchPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public MultiProcessOrchestrator(string godotBinPath, string projectPath)
    {
        _godotBin = godotBinPath;
        _projectPath = projectPath;
    }

    public TestProcess Spawn(string role, int enetPort, string label = null,
        string serverAddr = "127.0.0.1", string recordVideoPath = null,
        string clientPersistentId = null, string scenePath = null,
        bool deferVideoStart = false)
    {
        int orchPort = NextOrchPort();
        label ??= role;

        // Normalize the project path to forward slashes — Godot's res:// resolver
        // is happier with POSIX-style separators on Windows even though Win32 APIs
        // accept both. Backslashes from DirectoryInfo.FullName have caused symptom-
        // identical "Cannot open file 'res://...'" errors despite the file existing.
        string projectPath = _projectPath.Replace('\\', '/');

        // Launch MainScene with the --test-harness user arg so MainScene's
        // _Ready detects test-harness mode, hides its UI, and instantiates a
        // MultiClientHarness child node. This works around a Godot resource-
        // loading quirk where a stand-alone harness scene fails to load when
        // launched from inside the gdUnit4 test runner — MainScene is the
        // project's main scene and is therefore reliably loadable.
        var args = new List<string>();

        // Video recording: when recordVideoPath is provided, run the engine in
        // WINDOWED mode (so it actually renders) at a fixed resolution. The
        // harness inside the child Godot process owns the recording — it reads
        // the viewport via GetImage() each frame and pipes raw RGBA bytes to a
        // child ffmpeg process's stdin (see HarnessVideoRecorder in
        // MultiClientHarness.cs). Capture happens entirely inside Godot, so
        // the test window can be off-screen / minimised / occluded and the
        // recording is unaffected — no requirement to keep any window on top.
        //
        // --debug-collisions paints the CollisionShape3D gizmos over every
        // body so collision volumes are visible in the recording. Audio is
        // muted via the Dummy driver so the test run is silent.
        if (recordVideoPath != null)
        {
            args.Add("--resolution"); args.Add("1280x720");
            args.Add("--debug-collisions");
            args.Add("--audio-driver"); args.Add("Dummy");
        }
        else
        {
            args.Add("--headless");
        }

        // Default to MainScene (the project's main scene) for harness loading
        // reliability inside the gdUnit4 runner. Tests that need a different
        // arena layout (e.g. a large unobstructed floor for vehicle driving)
        // pass scenePath; only sibling .tscn files under demo/ are known-safe
        // to load through this codepath.
        string scene = scenePath ?? "res://demo/MainScene.tscn";
        args.AddRange(new[]
        {
            "--path", projectPath,
            scene,
            "--",
            "--test-harness",
            $"--role={role}",
            $"--enet-port={enetPort}",
            $"--orch-port={orchPort}",
            $"--label={label}",
        });
        if (role == "client")
        {
            args.Add($"--server-addr={serverAddr}");
            // Pass an explicit ClientPersistentIdentity per spawned client. If
            // the caller supplied one (e.g. reconnect-as-same-identity test
            // that wants to re-spawn a fresh process with the SAME id), use it;
            // otherwise auto-generate a fresh GUID. Either way the child reads
            // it directly from --client-id= and writes nothing to user://,
            // keeping the harness hermetic and not stepping on the developer's
            // own persistent identity file.
            string id = clientPersistentId ?? Guid.NewGuid().ToString();
            args.Add($"--client-id={id}");
        }
        if (recordVideoPath != null) args.Add($"--record-video={recordVideoPath}");
        // Defer recorder construction until the orchestrator issues
        // "start-recording". Used by the quantitative suite so the captured MP4
        // doesn't include the cold-start clock-sync window — the recorder is
        // started right before scenario.Setup, so the video begins just before
        // entities spawn instead of ~10 s of empty viewport.
        if (deferVideoStart && recordVideoPath != null) args.Add("--defer-video-start");

        var psi = new ProcessStartInfo(_godotBin)
        {
            // Explicitly set the child's CWD to the project root. Without this,
            // the child inherits the dotnet test runner's CWD (typically
            // <project>/tests/) which causes Godot to mis-resolve res:// paths
            // even with --path set.
            WorkingDirectory = projectPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // CreateNoWindow only matters for the console. For headless server
            // processes we hide the console; for windowed clients being
            // captured by ffmpeg gdigrab we MUST allow the real GUI window so
            // the engine actually renders into it. With CreateNoWindow=true
            // Godot's window opens but stays hidden — gdigrab then captures a
            // solid-clear-colour rectangle for the entire test run.
            CreateNoWindow = recordVideoPath == null,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        // Tell the demo player's FirstPersonCameraController not to grab the OS
        // cursor. Without this, a windowed client (recordVideoPath != null) puts
        // Input.MouseMode = Captured and warps/confines the real mouse to its
        // window — even when the window is hidden behind others.
        psi.Environment["MONKENET_TEST"] = "1";

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to spawn Godot for role={role}");

        // Capture stdout/stderr to in-memory buffers in the background so we can
        // include them in error messages if the child fails to come up.
        var stdoutBuf = new System.Text.StringBuilder();
        var stderrBuf = new System.Text.StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (stdoutBuf) stdoutBuf.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderrBuf) stderrBuf.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var tp = new TestProcess(proc, label, orchPort, stdoutBuf, stderrBuf);
        _processes.Add(tp);
        tp.ConnectOrchSocket(timeoutMs: 30_000);
        return tp;
    }


    public void Dispose()
    {
        foreach (var tp in _processes)
        {
            try { tp.Dispose(); } catch { /* best effort */ }
        }
        _processes.Clear();
    }
}

/// <summary>
/// One child Godot process running the harness. Wraps the orch TCP socket and
/// provides typed Send/Wait helpers.
/// </summary>
public class TestProcess : IDisposable
{
    public string Label { get; }
    public int OrchPort { get; }
    public int Pid => _process.Id;
    /// <summary>PID reported by the child Godot process itself (via the "ready" cmd).
    /// May differ from <see cref="Pid"/> if the engine relaunches internally during
    /// startup (e.g. when --write-movie initialises a rendering context). 0 until
    /// <see cref="WaitReady"/> has succeeded at least once.</summary>
    public int RemotePid { get; private set; }
    /// <summary>Absolute path of the child process's MonkeLogger output file, or
    /// null if file logging failed to initialise. Reported by the ready cmd.</summary>
    public string RemoteLogPath { get; private set; }

    private readonly Process _process;
    private readonly System.Text.StringBuilder _stdoutBuf;
    private readonly System.Text.StringBuilder _stderrBuf;
    private TcpClient _orchClient;
    private NetworkStream _orchStream;
    private StreamReader _reader;
    private StreamWriter _writer;
    private bool _disposed;

    internal TestProcess(Process process, string label, int orchPort,
        System.Text.StringBuilder stdoutBuf, System.Text.StringBuilder stderrBuf)
    {
        _process = process;
        Label = label;
        OrchPort = orchPort;
        _stdoutBuf = stdoutBuf;
        _stderrBuf = stderrBuf;
    }

    /// <summary>Polls the orch port until accept succeeds (child takes ~1-3 s
    /// to bring up the listener) or <paramref name="timeoutMs"/> elapses.</summary>
    internal void ConnectOrchSocket(int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        Exception lastErr = null;
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
            {
                string stderrSnap, stdoutSnap;
                lock (_stderrBuf) stderrSnap = _stderrBuf.ToString();
                lock (_stdoutBuf) stdoutSnap = _stdoutBuf.ToString();
                throw new InvalidOperationException(
                    $"[{Label}] child exited (code {_process.ExitCode}) before orch socket opened.\n--- child stdout ---\n{stdoutSnap}\n--- child stderr ---\n{stderrSnap}");
            }
            try
            {
                var c = new TcpClient();
                c.Connect("127.0.0.1", OrchPort);
                _orchClient = c;
                _orchStream = c.GetStream();
                _reader = new StreamReader(_orchStream, new UTF8Encoding(false));
                _writer = new StreamWriter(_orchStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                return;
            }
            catch (Exception e) { lastErr = e; Thread.Sleep(150); }
        }
        // Include child stdout/stderr in the timeout message so test logs show
        // what Godot reported when bringing up the harness scene.
        string stdout, stderr;
        lock (_stdoutBuf) stdout = _stdoutBuf.ToString();
        lock (_stderrBuf) stderr = _stderrBuf.ToString();
        const int Tail = 4000;
        if (stdout.Length > Tail) stdout = stdout.Substring(stdout.Length - Tail);
        if (stderr.Length > Tail) stderr = stderr.Substring(stderr.Length - Tail);
        throw new TimeoutException(
            $"[{Label}] orch socket on port {OrchPort} never accepted within {timeoutMs} ms. last err: {lastErr?.Message}\n--- child stdout (tail) ---\n{stdout}\n--- child stderr (tail) ---\n{stderr}");
    }

    /// <summary>Sends a command and reads the single-line JSON response.</summary>
    public JsonDocument Send(object request, int timeoutMs = 10_000)
    {
        if (_writer == null) throw new InvalidOperationException($"[{Label}] not connected");
        string line = JsonSerializer.Serialize(request);
        _writer.WriteLine(line);

        _orchStream.ReadTimeout = timeoutMs;
        string resp = _reader.ReadLine();
        if (resp == null) throw new InvalidOperationException($"[{Label}] orch socket closed before response");
        var doc = JsonDocument.Parse(resp);
        if (doc.RootElement.TryGetProperty("ok", out var ok) && !ok.GetBoolean())
        {
            string err = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : "<no error>";
            throw new InvalidOperationException($"[{Label}] command failed: {err}");
        }
        return doc;
    }

    /// <summary>Polls the harness's "ready" command until <paramref name="networkReady"/>
    /// matches (or until <paramref name="timeoutMs"/> elapses). For clients, networkReady=true
    /// means the ENet handshake completed and the client clock is synced.</summary>
    public void WaitReady(bool networkReady = false, int timeoutMs = 30_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            using var doc = Send(new { cmd = "ready" }, timeoutMs: 5_000);
            var data = doc.RootElement.GetProperty("data");
            bool isReady = data.GetProperty("ready").GetBoolean();
            bool isNetReady = data.GetProperty("networkReady").GetBoolean();
            if (data.TryGetProperty("pid", out var pidProp)) RemotePid = pidProp.GetInt32();
            if (data.TryGetProperty("logPath", out var lpProp) && lpProp.ValueKind == JsonValueKind.String)
                RemoteLogPath = lpProp.GetString();
            if (isReady && (!networkReady || isNetReady)) return;
            Thread.Sleep(150);
        }
        throw new TimeoutException($"[{Label}] not ready within {timeoutMs} ms (networkReady={networkReady})");
    }

    /// <summary>Coarse fallback — sleeps wall-clock time approximately equal to N
    /// physics ticks at 60 Hz. Use <see cref="WaitForTicks"/> instead when the
    /// child has its own physics loop and you need to advance N ticks ON THAT
    /// process, not just N ticks of wall time.</summary>
    public static void SleepTicks(int ticks) => Thread.Sleep(ticks * 1000 / 60 + 50);

    /// <summary>Polls the harness's tick-count command until at least
    /// <paramref name="ticks"/> have elapsed since the call started. Cleaner
    /// than wall-clock sleep — works correctly even if the child Godot process
    /// is slow to start, busy with other work, or running on a loaded machine.</summary>
    public void WaitForTicks(int ticks, int timeoutMs = 30_000)
    {
        long startTicks = ReadTickCount();
        long target = startTicks + ticks;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (ReadTickCount() >= target) return;
            Thread.Sleep(50);
        }
        throw new TimeoutException($"[{Label}] did not advance {ticks} physics ticks within {timeoutMs} ms (started at {startTicks}, last seen {ReadTickCount()})");
    }

    private long ReadTickCount()
    {
        using var doc = Send(new { cmd = "tick-count" });
        return doc.RootElement.GetProperty("data").GetProperty("ticks").GetInt64();
    }

    /// <summary>Reads the client's current synced network tick. Cheap enough
    /// for polling at high frequency. Throws if called on a server process.</summary>
    public int ReadClientTick()
    {
        using var doc = Send(new { cmd = "get-client-tick" });
        return doc.RootElement.GetProperty("data").GetProperty("syncedTick").GetInt32();
    }

    /// <summary>Tight-polls the client's synced network tick until it reaches
    /// at least <paramref name="targetTick"/>. Tighter polling than
    /// <see cref="WaitForTicks"/> (~5 ms vs 50 ms) so the test returns within
    /// roughly one physics tick of the target, which is the resolution needed
    /// to anchor input schedules deterministically.</summary>
    public void WaitForClientTick(int targetTick, int timeoutMs = 30_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        int last = 0;
        while (DateTime.UtcNow < deadline)
        {
            last = ReadClientTick();
            if (last >= targetTick) return;
            Thread.Sleep(5);
        }
        throw new TimeoutException($"[{Label}] client tick {targetTick} not reached within {timeoutMs} ms (last={last})");
    }

    /// <summary>The peer's Godot multiplayer network ID. For the server this is 1;
    /// for clients it's the dynamically assigned ID (typically 2, 3, ... in
    /// connection order, but never assume order — use this method).</summary>
    public int NetworkId
    {
        get
        {
            using var doc = Send(new { cmd = "get-network-id" });
            return doc.RootElement.GetProperty("data").GetProperty("networkId").GetInt32();
        }
    }

    private string ReadStderrSafe()
    {
        try { return _process.StandardError.ReadToEnd(); }
        catch { return "<unavailable>"; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Shutdown gracefully via the orch socket. The harness's shutdown
        // handler finalises the in-engine video recorder (drains the frame
        // queue + waits for ffmpeg to write the mp4 moov atom) BEFORE
        // calling GetTree().Quit(), so by the time the Godot child exits the
        // video file is complete. We give the orch command 30 s to allow for
        // up to 15 s of ffmpeg-finalise wait inside the harness.
        try
        {
            if (_writer != null) Send(new { cmd = "shutdown" }, timeoutMs: 30_000);
        }
        catch { /* best effort */ }

        try { _writer?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _orchStream?.Dispose(); } catch { }
        try { _orchClient?.Dispose(); } catch { }

        try
        {
            // 30 s ceiling: the harness's shutdown handler runs
            // HarnessVideoRecorder.StopAndFlush which can spend up to 15 s
            // waiting for ffmpeg to finalise the mp4 container. Killing
            // earlier would leave a truncated file behind.
            if (!_process.WaitForExit(30_000)) _process.Kill(entireProcessTree: true);
        }
        catch { try { _process.Kill(entireProcessTree: true); } catch { } }
    }
}
