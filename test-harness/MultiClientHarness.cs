using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using GameDemo;
using Godot;
using MonkeNet.Client;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Server;
using MonkeNet.Shared;

namespace MonkeNet.Tests.MultiProcess;

/// <summary>
/// Orchestrated test harness for multi-process integration testing. A spawned
/// Godot child process loads <c>res://tests/MultiProcess/harness.tscn</c> with
/// command-line args specifying its role (server or client), ENet port, and an
/// orchestration TCP port. The orchestrator (running in the GdUnit4 test process)
/// connects to the orch port and drives the harness via line-delimited JSON.
///
/// Each child process has its OWN Godot World3D, physics space, MonkeNet
/// singletons, and OS process — eliminating the same-process-shared-state
/// concerns of in-process multi-client testing.
///
/// CLI args (after a single `--` separator on Godot's command line):
///   --role=server|client
///   --enet-port=N        Server listens on this port; client connects to it
///   --orch-port=N        Orchestrator connects to this port to drive the harness
///   --server-addr=IP     (client only) defaults to 127.0.0.1
///   --label=str          Diagnostic label printed in logs
///
/// Protocol: line-delimited JSON over TCP. Each request is a single JSON object
/// with a "cmd" field; the response is a single JSON object on one line.
/// Commands: ready, spawn-ball, send-input, set-authority, get-all-entities,
///           get-entity, run-ticks, shutdown.
/// </summary>
public partial class MultiClientHarness : Node
{
    private TcpListener _orchListener;
    private TcpClient _orchClient;
    private NetworkStream _orchStream;
    private readonly List<byte> _accumulator = new();
    private readonly byte[] _readBuf = new byte[8192];

    private string _role = "?";
    private string _label = "";
    private Camera3D _observerCamera;
    private Label _hudLabel;
    private string _desiredWindowTitle;
    private HarnessVideoRecorder _videoRecorder;
    // Per-second performance snapshot state. Once a wall-clock second elapses,
    // log a single line summarising Godot's Performance monitors so the cell's
    // client.log can be diffed across conditions to localise the slowdown
    // (managed vs physics vs render vs viewport readback). Cheap: one read per
    // monitor per second, no per-frame overhead.
    private long _perfNextLogMs = 0;
    private int _perfFramesThisWindow;
    private double _perfWorstFrameMs;
    private int _perfMispredictsAtWindowStart;
    private long _perfRecorderFramesAtWindowStart;
    private long _perfRecorderDroppedAtWindowStart;

    // Stashed video-recorder parameters when --defer-video-start is passed.
    // Construction happens on the "start-recording" orch command instead of in
    // _Ready, so the captured MP4 can start at the moment of the test's choosing
    // (e.g. right before entities spawn) rather than at process boot.
    private string _pendingVideoPath;
    private string _pendingFfmpegBin;
    private int _pendingVideoWidth;
    private int _pendingVideoHeight;

    // When non-zero, the observer camera tracks the entity with this id each
    // _Process at a fixed offset; the camera looks at the entity. Set via the
    // "camera-follow-entity" orch command; cleared by passing entity_id=0.
    private int _cameraFollowEntityId;
    private Vector3 _cameraFollowOffset = new(10f, 6f, 10f);
    private Vector3 _cameraFollowLookOffset = Vector3.Zero;

    // Runtime-spawned static collider ramps. Per-process; each side independently
    // creates its own StaticBody3D via the spawn-ramp orch command and stores it
    // here for lifetime tracking. See SpawnRamp.
    private readonly List<Node3D> _spawnedRamps = new();

    // Retained client connection params so reconnect-client can re-establish using the
    // SAME server address / ENet port the harness was launched with. Without this the
    // disconnect-client → reconnect-client flow couldn't be driven entirely via the
    // orch protocol (the orchestrator would have to remember the spawn args).
    private string _clientServerAddr;
    private int _clientEnetPort;

    public override void _Ready()
    {
        var args = ParseArgs(OS.GetCmdlineUserArgs());
        _role = args.GetValueOrDefault("role", "?");
        _label = args.GetValueOrDefault("label", _role);

        // Deterministic RNG seed for the harness process. Game code currently
        // uses no RNG, but Godot internals (particle systems, jitter on physics
        // contacts in some setups) can sample the global state. Pinning the
        // seed removes one source of run-to-run drift; tests that need
        // different seeds can pass --rng-seed=N on the command line.
        if (int.TryParse(args.GetValueOrDefault("rng-seed", "1"), out int seed))
            GD.Seed((ulong)seed);

        if (!int.TryParse(args.GetValueOrDefault("orch-port"), out int orchPort))
        {
            GD.PrintErr($"[harness {_label}] missing --orch-port");
            GetTree().Quit(1);
            return;
        }
        if (!int.TryParse(args.GetValueOrDefault("enet-port"), out int enetPort))
        {
            GD.PrintErr($"[harness {_label}] missing --enet-port");
            GetTree().Quit(1);
            return;
        }

        // Window title is re-asserted each _Process tick (see UpdateWindowTitle);
        // setting it once here isn't sufficient because Godot's main loop sets
        // its own debug-mode "monke-net (DEBUG)" title after autoloads finish.
        _desiredWindowTitle = $"MonkeNetTest: {_label}";

        // Open orch listener BEFORE wiring up MonkeNet so the orchestrator can
        // detect a "process started" signal as soon as the TCP port accepts.
        _orchListener = new TcpListener(IPAddress.Loopback, orchPort);
        _orchListener.Start();
        GD.Print($"[harness {_label}] orch listening on {orchPort}");

        if (_role == "server")
        {
            MonkeNetManager.Instance.CreateServer(enetPort);
            GD.Print($"[harness {_label}] enet server on port {enetPort}");
        }
        else if (_role == "client")
        {
            string addr = args.GetValueOrDefault("server-addr", "127.0.0.1");
            _clientServerAddr = addr;
            _clientEnetPort = enetPort;
            MonkeNetManager.Instance.CreateClient(addr, enetPort);
            GD.Print($"[harness {_label}] enet client connecting to {addr}:{enetPort}");
            InstallObserverCamera();
            InstallHud();
            // If --record-video=path was passed, start the in-engine viewport
            // recorder. Capture is from inside Godot (Viewport.GetImage) and
            // pipes raw RGBA frames to ffmpeg's stdin, so the OS doesn't need
            // to see the window at all — it can be minimised, off-screen, or
            // behind any other window without affecting the recording.
            string videoPath = args.GetValueOrDefault("record-video", null);
            if (!string.IsNullOrEmpty(videoPath))
            {
                string ffmpegBin = ResolveFfmpegBin();
                if (!string.IsNullOrEmpty(ffmpegBin))
                {
                    // Match the engine's window resolution to the encoder size
                    // so GetImage().Width/Height matches what we declared to
                    // ffmpeg via -video_size.
                    var winSize = DisplayServer.WindowGetSize();
                    bool defer = args.ContainsKey("defer-video-start");
                    if (defer)
                    {
                        // Stash params; the recorder is constructed when the
                        // orchestrator sends "start-recording".
                        _pendingVideoPath = videoPath;
                        _pendingFfmpegBin = ffmpegBin;
                        _pendingVideoWidth = winSize.X;
                        _pendingVideoHeight = winSize.Y;
                        GD.PrintErr($"[VIDEO] deferred — waiting for start-recording (path={videoPath})");
                    }
                    else
                    {
                        try
                        {
                            _videoRecorder = new HarnessVideoRecorder(
                                ffmpegBin, videoPath, winSize.X, winSize.Y, frameRate: 60);
                        }
                        catch (Exception ex)
                        {
                            GD.PrintErr($"[VIDEO] recorder init failed: {ex.GetType().Name}: {ex.Message}");
                            _videoRecorder = null;
                        }
                    }
                }
                else
                {
                    GD.PrintErr("[VIDEO] ffmpeg not found in PATH or FFMPEG_BIN; video will not be recorded.");
                }
            }
        }
        else
        {
            GD.PrintErr($"[harness {_label}] unknown role '{_role}'; valid: server|client");
            GetTree().Quit(1);
        }
    }

    // Third-person side-view camera installed on the client so video recordings
    // capture the whole scenario (tower, player, floor) instead of the player's
    // first-person view (which is empty until the player spawns and only shows
    // what's directly in front of it after that). Positioned looking down the
    // +X axis at the scene origin, framed wide enough to include the tower at
    // Z=-3 and the player's spawn position at Z=+1.5.
    private void InstallObserverCamera()
    {
        _observerCamera = new Camera3D
        {
            Position = new Vector3(10, 1.5f, -1),
            Fov = 65f,
            Current = true,
        };
        AddChild(_observerCamera);
        _observerCamera.LookAt(new Vector3(0, 0, -1), Vector3.Up);
    }

    // On-screen HUD showing the current physics tick + running misprediction
    // count, drawn in the top-left of the recorded video. Lives on its own
    // CanvasLayer so it's screen-space and not affected by the camera position.
    private void InstallHud()
    {
        var canvas = new CanvasLayer();
        AddChild(canvas);
        _hudLabel = new Label
        {
            Position = new Vector2(16, 12),
            Text = "tick=- mispredictions=0",
            LabelSettings = new LabelSettings
            {
                FontSize = 26,
                FontColor = Colors.White,
                OutlineColor = new Color(0, 0, 0, 1),
                OutlineSize = 4,
            },
        };
        canvas.AddChild(_hudLabel);
    }

    private void UpdateHud()
    {
        if (_hudLabel == null) return;
        int mispredicts = 0;
        int netTick = -1;
        var cm = ClientManager.Instance;
        if (cm != null)
        {
            // Scene tree uses the node name "PredictionManager" (not
            // "ClientPredictionManager") — look up by component type instead so
            // a rename of the scene node doesn't silently zero out our counters.
            var pm = FindChildOfType<ClientPredictionManager>(cm);
            if (pm != null) mispredicts = pm.MispredictionsCount;
            // Show MonkeNet's network-synced tick (matches CLIENT-TICK in
            // MonkeLogger lines), not Engine.GetPhysicsFrames(). The two diverge
            // in --write-movie mode where the engine runs at fixed video rate
            // and ClientNetworkClock applies clock-sync offsets — using the
            // network tick means HUD numbers in the video line up directly with
            // log lines, so a moment in the recording can be cross-referenced
            // against MonkeLogger output by tick number.
            var clock = cm.GetNodeOrNull<ClientNetworkClock>("ClientNetworkClock");
            if (clock != null) netTick = clock.GetCurrentTick();
        }
        string tickStr = netTick >= 0 ? netTick.ToString() : "-";
        _hudLabel.Text = $"tick={tickStr}    mispredictions={mispredicts}";
    }

    // Once per wall-clock second, emit a single [PERF] line summarising Godot's
    // Performance monitors + harness counters. Diff-able across cells to localise
    // what's consuming CPU under bad-network conditions (script/physics/render/
    // viewport readback for recording).
    private void MaybeLogPerfSummary()
    {
        if (_role != "client") return;
        long nowMs = (long)Time.GetTicksMsec();
        if (_perfNextLogMs == 0)
        {
            // First call: prime the window without emitting a line so the very
            // first sample isn't biased by process-startup costs.
            _perfNextLogMs = nowMs + 1000;
            _perfFramesThisWindow = 0;
            _perfWorstFrameMs = 0;
            _perfMispredictsAtWindowStart = ReadMispredictCountSafe();
            _perfRecorderFramesAtWindowStart = _videoRecorder?.Produced ?? 0;
            _perfRecorderDroppedAtWindowStart = _videoRecorder?.Dropped ?? 0;
            return;
        }
        if (nowMs < _perfNextLogMs) return;

        // Godot Performance API — `GetMonitor` returns a double; times are in
        // seconds-per-frame for the most recent frame.
        double timeProcessMs = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0;
        double timePhysicsMs = Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0;
        double fps = Performance.GetMonitor(Performance.Monitor.TimeFps);
        double drawCalls = Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
        double objsInFrame = Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame);
        double physActive = Performance.GetMonitor(Performance.Monitor.Physics3DActiveObjects);
        double physPairs = Performance.GetMonitor(Performance.Monitor.Physics3DCollisionPairs);
        double physIslands = Performance.GetMonitor(Performance.Monitor.Physics3DIslandCount);
        double objCount = Performance.GetMonitor(Performance.Monitor.ObjectCount);
        double nodeCount = Performance.GetMonitor(Performance.Monitor.ObjectNodeCount);
        double videoMemMb = Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / (1024.0 * 1024.0);

        int mispredictNow = ReadMispredictCountSafe();
        int mispredictDelta = mispredictNow - _perfMispredictsAtWindowStart;

        long producedNow = _videoRecorder?.Produced ?? 0;
        long droppedNow = _videoRecorder?.Dropped ?? 0;
        long produced = producedNow - _perfRecorderFramesAtWindowStart;
        long dropped = droppedNow - _perfRecorderDroppedAtWindowStart;

        int tick = -1;
        var cm = ClientManager.Instance;
        if (cm != null)
        {
            var clock = cm.GetNodeOrNull<ClientNetworkClock>("ClientNetworkClock");
            if (clock != null) tick = clock.GetCurrentTick();
        }

        MonkeLogger.Info(
            $"[PERF] tick={tick} frames={_perfFramesThisWindow} fps={fps:F1} "
            + $"proc_ms={timeProcessMs:F2} phys_ms={timePhysicsMs:F2} worst_frame_ms={_perfWorstFrameMs:F1} "
            + $"phys_active={physActive:F0} phys_pairs={physPairs:F0} phys_islands={physIslands:F0} "
            + $"draw_calls={drawCalls:F0} rend_objs={objsInFrame:F0} vidmem_mb={videoMemMb:F1} "
            + $"obj={objCount:F0} nodes={nodeCount:F0} "
            + $"mispred_delta={mispredictDelta} rec_produced={produced} rec_dropped={dropped}");

        // Roll the window. Skipped-ticks happen if a frame took >1s; resync
        // _perfNextLogMs to now+1000 rather than chaining +1000 from stale.
        _perfNextLogMs = nowMs + 1000;
        _perfFramesThisWindow = 0;
        _perfWorstFrameMs = 0;
        _perfMispredictsAtWindowStart = mispredictNow;
        _perfRecorderFramesAtWindowStart = producedNow;
        _perfRecorderDroppedAtWindowStart = droppedNow;
    }

    private int ReadMispredictCountSafe()
    {
        var cm = ClientManager.Instance;
        if (cm == null) return 0;
        var pm = FindChildOfType<ClientPredictionManager>(cm);
        return pm?.MispredictionsCount ?? 0;
    }

    public override void _Process(double delta)
    {
        // Track per-window frame stats for the once-per-second [PERF] line.
        // delta is in seconds; keep the worst frame seen so a single hitch
        // shows up in the log even when the mean stays low.
        _perfFramesThisWindow++;
        double frameMs = delta * 1000.0;
        if (frameMs > _perfWorstFrameMs) _perfWorstFrameMs = frameMs;

        // Re-assert the observer camera as the active camera each frame. The
        // LocalRigidPlayer's FirstPersonCameraController sets itself Current
        // when the player spawns; without this re-assert the view would flip
        // to first-person mid-recording.
        if (_observerCamera != null && IsInstanceValid(_observerCamera) && !_observerCamera.Current)
            _observerCamera.Current = true;

        UpdateCameraFollow();

        // Re-assert the deterministic window title every frame so an external
        // recorder's window-title match (ffmpeg gdigrab `-i title=...`) keeps
        // working. Godot's main loop overwrites the title during startup (sets
        // it to e.g. "monke-net (DEBUG)") and intermittently on focus events,
        // so a one-shot WindowSetTitle in _Ready is silently undone.
        if (_desiredWindowTitle != null)
        {
            try { DisplayServer.WindowSetTitle(_desiredWindowTitle); } catch { }
        }

        UpdateHud();
        MaybeLogPerfSummary();

        // Push a freshly-rendered viewport frame to the recorder if active.
        // Non-blocking: if ffmpeg is behind, the frame is dropped rather than
        // delaying the render loop. Order matters — must be AFTER UpdateHud
        // so the captured frame includes the latest tick / mispredictions
        // overlay text.
        _videoRecorder?.TryCaptureFrame(GetViewport());

        if (_orchListener == null) return;

        try
        {
            if (_orchClient == null && _orchListener.Pending())
            {
                _orchClient = _orchListener.AcceptTcpClient();
                _orchStream = _orchClient.GetStream();
                GD.Print($"[harness {_label}] orchestrator connected");
            }

            if (_orchStream != null && _orchClient.Available > 0)
            {
                int n = _orchStream.Read(_readBuf, 0, _readBuf.Length);
                for (int i = 0; i < n; i++) _accumulator.Add(_readBuf[i]);
            }

            // Process any complete lines.
            while (_orchStream != null)
            {
                int newline = _accumulator.IndexOf((byte)'\n');
                if (newline < 0) break;
                string line = Encoding.UTF8.GetString(_accumulator.GetRange(0, newline).ToArray()).TrimEnd('\r');
                _accumulator.RemoveRange(0, newline + 1);
                string response = ProcessCommand(line);
                byte[] bytes = Encoding.UTF8.GetBytes(response + "\n");
                _orchStream.Write(bytes, 0, bytes.Length);
            }
        }
        catch (Exception e)
        {
            GD.PrintErr($"[harness {_label}] orch I/O error: {e.GetType().Name}: {e.Message}");
            // Tear down the broken connection — accept a fresh one next frame.
            try { _orchStream?.Dispose(); } catch { }
            try { _orchClient?.Dispose(); } catch { }
            _orchStream = null;
            _orchClient = null;
            _accumulator.Clear();
        }
    }

    private string ProcessCommand(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            string cmd = doc.RootElement.GetProperty("cmd").GetString() ?? "";
            return cmd switch
            {
                "ready" => Ok(new
                {
                    ready = true,
                    role = _role,
                    // PID inside the actual Godot process. The OS PID seen by
                    // Process.Start in the parent doesn't always match (e.g. the
                    // engine relaunches itself when --write-movie initialises a
                    // rendering context), so the orchestrator gets the truth here.
                    pid = System.Environment.ProcessId,
                    // Absolute path of MonkeLogger's current log file. The user-data
                    // dir varies between test-runner contexts (tests/ vs main repo)
                    // so the orchestrator can't reliably reconstruct it; reporting
                    // here lets it copy the live log into the artifact directory.
                    logPath = MonkeLogger.FilePath,
                    networkReady = (_role == "server") ? true : (ClientManager.Instance?.IsNetworkReady ?? false),
                    serverEntityCount = (_role == "server") ? EntitySpawner.Instance?.Entities.Count ?? 0 : 0,
                    clientEntityCount = (_role == "client") ? EntitySpawner.Instance?.ClientEntities.Count ?? 0 : 0,
                }),
                "tick-count" => Ok(new { ticks = (long)Engine.GetPhysicsFrames() }),
                "clock-state" => ClockState(),
                "get-network-id" => Ok(new
                {
                    networkId = (_role == "server")
                        ? (ServerManager.Instance?.GetNetworkId() ?? 0)
                        : (ClientManager.Instance?.GetNetworkId() ?? 0),
                }),
                "spawn-ball" => SpawnBall(doc.RootElement),
                "spawn-entity" => SpawnEntity(doc.RootElement),
                "spawn-ramp" => SpawnRamp(doc.RootElement),
                "teleport-entity" => TeleportEntity(doc.RootElement),
                "apply-impulse" => ApplyImpulse(doc.RootElement),
                "force-reconcile" => ForceReconcile(doc.RootElement),
                "set-entity-physics" => SetEntityPhysics(doc.RootElement),
                "set-authority" => SetAuthority(doc.RootElement),
                "send-input" => SendInput(doc.RootElement),
                "set-input" => SetInput(doc.RootElement),
                "set-input-schedule" => SetInputSchedule(doc.RootElement),
                "get-client-tick" => GetClientTick(),
                "get-all-entities" => GetAllEntities(),
                "get-entity" => GetEntity(doc.RootElement),
                "mispredict-count" => MispredictCount(),
                "mispredict-classification-counts" => MispredictClassificationCounts(),
                "rollback-depth-sample" => RollbackDepthSample(),
                "missed-input-count" => MissedInputCount(),
                "bandwidth-stats" => BandwidthStats(),
                "server-peer-count" => ServerPeerCount(),
                "pending-reclaim-for" => PendingReclaimFor(doc.RootElement),
                "sample-state" => SampleState(),
                "disconnect-client" => DisconnectClient(),
                "reconnect-client" => ReconnectClient(),
                "client-persistent-id" => ClientPersistentId(),
                "clear-input-schedule" => ClearInputSchedule(),
                "camera-follow-entity" => CameraFollowEntity(doc.RootElement),
                "set-camera" => SetCamera(doc.RootElement),
                "start-recording" => StartRecording(),
                "shutdown" => Shutdown(),
                _ => Err($"unknown cmd '{cmd}'"),
            };
        }
        catch (Exception e)
        {
            return Err($"{e.GetType().Name}: {e.Message}");
        }
    }

    // ── command handlers ──────────────────────────────────────────────────────

    // Snapshot of the network clock's internal state so a test orchestrator can
    // track sync convergence over time. Server reports its raw authoritative
    // tick + wall-clock time; client reports the same plus latency/offset/raw
    // tick so the orchestrator can compute the effective tick gap each side has.
    // Sampled at high frequency (~50 ms) it produces the clock-sync graphs.
    private string ClockState()
    {
        long wallMs = (long)Time.GetTicksMsec();
        long physFrames = (long)Engine.GetPhysicsFrames();
        if (_role == "server")
        {
            var sm = ServerManager.Instance;
            var clock = sm?.GetNodeOrNull<ServerNetworkClock>("ServerNetworkClock");
            return Ok(new
            {
                role = "server",
                wallMs,
                physFrames,
                serverTick = clock?.CurrentTick ?? 0,
            });
        }
        else
        {
            var cm = ClientManager.Instance;
            var clock = cm?.GetNodeOrNull<ClientNetworkClock>("ClientNetworkClock");
            return Ok(new
            {
                role = "client",
                wallMs,
                physFrames,
                rawTick = clock?.RawTick ?? 0,
                syncedTick = clock?.GetCurrentTick() ?? 0,
                averageLatencyTicks = clock?.AverageLatencyInTicks ?? 0,
                jitterTicks = clock?.JitterInTicks ?? 0,
                averageOffsetTicks = clock?.AverageOffsetInTicks ?? 0,
                syncWindowsApplied = clock?.SyncWindowsApplied ?? 0,
                networkReady = cm?.IsNetworkReady ?? false,
            });
        }
    }

    private string SpawnBall(JsonElement req)
    {
        if (_role != "server") return Err("spawn-ball is server-only");
        int authority = req.GetProperty("authority").GetInt32();
        var pos = ReadVec3(req.GetProperty("position"));
        var node = ServerManager.Instance.SpawnEntity<Node3D>(entityType: 1, authority: authority);
        // ServerBallStateSyncronizer.OnEntitySpawned forces (0,10,0); override after spawn.
        var lastEntity = EntitySpawner.Instance.Entities[EntitySpawner.Instance.Entities.Count - 1];
        EntitySpawner.Instance.GetEntityRoot(lastEntity)!.GlobalPosition = pos;
        return Ok(new { entityId = lastEntity.EntityId });
    }

    // Spawns any entity by type with explicit position. Used by the multi-process rigid-
    // player misprediction test to place player + cubes deterministically:
    //   type 0 = CharacterBody3D player, 1 = Ball, 2 = Vehicle, 3 = RigidBody3D player,
    //   type 4 = Cube. Position is set after spawn so the entity's OnEntitySpawned default
    //   position (e.g. ServerBallStateSyncronizer's (0,10,0) drop) doesn't override us.
    private string SpawnEntity(JsonElement req)
    {
        if (_role != "server") return Err("spawn-entity is server-only");
        byte entityType = (byte)req.GetProperty("entity_type").GetInt32();
        int authority = req.GetProperty("authority").GetInt32();
        var pos = ReadVec3(req.GetProperty("position"));
        // Thread position straight through SpawnEntity so the entity-event
        // broadcast carries the final position. The framework applies it after
        // OnEntitySpawned but before the broadcast, so the client never spawns
        // its dummy at a stale default (eliminating the one-frame teleport).
        ServerManager.Instance.SpawnEntity<Node3D>(entityType: entityType, authority: authority, position: pos);
        var lastEntity = EntitySpawner.Instance.Entities[EntitySpawner.Instance.Entities.Count - 1];
        return Ok(new { entityId = lastEntity.EntityId });
    }

    // Static-collider ramp injected at runtime, used by the multi-process ramp
    // motion tests to parameterise slope angle per scenario. Runs on BOTH server
    // and clients (each side adds its own StaticBody3D to its local World3D);
    // physics collisions resolve identically across processes because the ramp
    // is static (no integration, no per-process drift).
    //
    // Rotation is around the world X axis: positive angleDeg tilts the local +Z
    // end DOWN (toward -Y) and the local -Z end UP (toward +Y). A player walking
    // in the -Z direction with angleDeg > 0 walks UPHILL; the same player with
    // angleDeg < 0 walks DOWNHILL.
    //
    // Request fields:
    //   position:  [x,y,z]   world center of the ramp slab
    //   angleDeg:  float     tilt around the world X axis
    //   length:    float     long dimension along local Z (default 8.0)
    //   width:     float     width along local X (default 4.0)
    //   thickness: float     short dimension along local Y (default 0.5)
    //
    // The client side also adds a BoxMesh so the recorded video shows the
    // ramp geometry; the headless server skips the mesh.
    private string SpawnRamp(JsonElement req)
    {
        var pos = ReadVec3(req.GetProperty("position"));
        float angleDeg = (float)req.GetProperty("angleDeg").GetDouble();
        float length = req.TryGetProperty("length", out var lp) ? (float)lp.GetDouble() : 8f;
        float width = req.TryGetProperty("width", out var wp) ? (float)wp.GetDouble() : 4f;
        float thickness = req.TryGetProperty("thickness", out var tp) ? (float)tp.GetDouble() : 0.5f;

        var body = new StaticBody3D { Name = $"HarnessRamp_{_spawnedRamps.Count}" };
        var size = new Vector3(width, thickness, length);
        var shape = new CollisionShape3D { Shape = new BoxShape3D { Size = size } };
        body.AddChild(shape);
        if (_role == "client")
        {
            var mesh = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = size },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.6f, 0.6f, 0.7f) },
            };
            body.AddChild(mesh);
        }
        AddChild(body);
        // Setting GlobalPosition/RotationDegrees after AddChild because Godot
        // requires the node to be in the tree before global transforms apply.
        body.GlobalPosition = pos;
        body.RotationDegrees = new Vector3(angleDeg, 0, 0);
        _spawnedRamps.Add(body);
        return Ok(new
        {
            rampIndex = _spawnedRamps.Count - 1,
            role = _role,
            position = SerializeVec3(pos),
            angleDeg,
            length, width, thickness,
        });
    }

    // Forcibly relocates a server-owned entity's body to a new world position.
    // Used by tests that need to deterministically trigger a large client-side
    // reconcile (the next snapshot from the server arrives with the new pose,
    // the client mispredicts vs its own simulation, and HandleReconciliation
    // fires). Bypasses the prediction wrapper because this is a test-only
    // out-of-band teleport, not gameplay-driven motion.
    private string TeleportEntity(JsonElement req)
    {
        if (_role != "server") return Err("teleport-entity is server-only");
        int eid = req.GetProperty("entity_id").GetInt32();
        var pos = ReadVec3(req.GetProperty("position"));
        // Optional yaw (radians, world-Y rotation). When present, the entity is
        // rotated to that yaw in addition to being teleported. Used by tests
        // that need to place a long body at a specific orientation before any
        // input is driven (e.g. vehicle lying sideways at an angle to the
        // player's approach path).
        bool hasYaw = req.TryGetProperty("yaw", out var yawProp);
        float yaw = hasYaw ? (float)yawProp.GetDouble() : 0f;
        NetworkBehaviour target = null;
        foreach (var e in EntitySpawner.Instance.Entities)
        {
            if (e.EntityId == eid) { target = e; break; }
        }
        if (target == null) return Err($"entity {eid} not found");
        var root = EntitySpawner.Instance.GetEntityRoot(target);
        if (root == null) return Err($"entity {eid} has no root node");
        if (root is RigidBody3D rb)
        {
            rb.GlobalPosition = pos;
            if (hasYaw)
            {
                var rot = rb.Rotation;
                rot.Y = yaw;
                rb.Rotation = rot;
            }
            rb.LinearVelocity = Vector3.Zero;
            rb.AngularVelocity = Vector3.Zero;
            rb.ResetPhysicsInterpolation();
        }
        else
        {
            root.GlobalPosition = pos;
            if (hasYaw)
            {
                var rot = root.Rotation;
                rot.Y = yaw;
                root.Rotation = rot;
            }
        }
        return Ok(new { entityId = eid, teleportedTo = SerializeVec3(pos), yaw });
    }

    // Inject an out-of-band velocity delta into a body's RigidBody3D. Modelled
    // on the cross-process Jolt drift used by MultiProcessPushDriftToleranceTests —
    // the orchestrator adds a small per-tick velocity perturbation to the
    // server-side replica of a body so the test can observe whether the push
    // formula on the client compensates correctly.
    //
    // Accepts:
    //   deltaLinearVelocity:  [x, y, z]  — required when no angular delta given
    //   deltaAngularVelocity: [x, y, z]  — optional
    //   targetRole:           "server" | "client" — optional; defaults to whichever
    //                         role received the command. Required when a single
    //                         orchestrator wants to perturb only one side of a pair.
    // The command short-circuits with ok=false when targetRole is set and does
    // not match _role, so the caller can fire-and-forget at both processes and
    // only one applies.
    private string ApplyImpulse(JsonElement req)
    {
        if (req.TryGetProperty("targetRole", out var tr))
        {
            string want = tr.GetString();
            if (!string.IsNullOrEmpty(want) && want != _role)
                return Ok(new { entityId = (int?)null, skipped = true, role = _role });
        }
        int eid = req.GetProperty("entity_id").GetInt32();
        IEnumerable<NetworkBehaviour> collection =
            _role == "server" ? (IEnumerable<NetworkBehaviour>)EntitySpawner.Instance.Entities
                              : EntitySpawner.Instance.ClientEntities;
        NetworkBehaviour target = null;
        foreach (var e in collection)
        {
            if (e.EntityId == eid) { target = e; break; }
        }
        if (target == null) return Err($"entity {eid} not found on {_role}");
        var root = EntitySpawner.Instance.GetEntityRoot(target);
        if (root is not RigidBody3D rb) return Err($"entity {eid} root is not a RigidBody3D");

        if (req.TryGetProperty("deltaLinearVelocity", out var dlv))
        {
            rb.LinearVelocity += ReadVec3(dlv);
        }
        if (req.TryGetProperty("deltaAngularVelocity", out var dav))
        {
            rb.AngularVelocity += ReadVec3(dav);
        }
        return Ok(new
        {
            entityId = eid,
            role = _role,
            linearVelocity = SerializeVec3(rb.LinearVelocity),
            angularVelocity = SerializeVec3(rb.AngularVelocity),
        });
    }

    // Out-of-band write of the authoritative pose on the server side, designed
    // to deliberately trip the client's HasMisspredicted path so reconcile-replay
    // logic gets exercised in a cross-process test. Distinct from teleport-entity
    // in two ways: (1) does NOT auto-zero velocity (so a moving body can be
    // nudged), and (2) reports the server tick the change took effect on so the
    // test can correlate with the client's mispredict-count.
    private string ForceReconcile(JsonElement req)
    {
        if (_role != "server") return Err("force-reconcile is server-only");
        int eid = req.GetProperty("entity_id").GetInt32();
        NetworkBehaviour target = null;
        foreach (var e in EntitySpawner.Instance.Entities)
        {
            if (e.EntityId == eid) { target = e; break; }
        }
        if (target == null) return Err($"entity {eid} not found");
        var root = EntitySpawner.Instance.GetEntityRoot(target);
        if (root == null) return Err($"entity {eid} has no root node");

        var pos = ReadVec3(req.GetProperty("position"));
        if (root is RigidBody3D rb)
        {
            rb.GlobalPosition = pos;
            if (req.TryGetProperty("rotation", out var rotProp))
            {
                var q = new Quaternion(
                    (float)rotProp[0].GetDouble(), (float)rotProp[1].GetDouble(),
                    (float)rotProp[2].GetDouble(), (float)rotProp[3].GetDouble());
                rb.GlobalTransform = new Transform3D(new Basis(q), rb.GlobalPosition);
            }
            if (req.TryGetProperty("linearVelocity", out var lv))
                rb.LinearVelocity = ReadVec3(lv);
            if (req.TryGetProperty("angularVelocity", out var av))
                rb.AngularVelocity = ReadVec3(av);
            rb.ResetPhysicsInterpolation();
        }
        else
        {
            root.GlobalPosition = pos;
            if (req.TryGetProperty("rotation", out var rotProp))
            {
                var q = new Quaternion(
                    (float)rotProp[0].GetDouble(), (float)rotProp[1].GetDouble(),
                    (float)rotProp[2].GetDouble(), (float)rotProp[3].GetDouble());
                root.GlobalTransform = new Transform3D(new Basis(q), root.GlobalPosition);
            }
        }

        var clock = ServerManager.Instance?.GetNodeOrNull<ServerNetworkClock>("ServerNetworkClock");
        return Ok(new
        {
            entityId = eid,
            position = SerializeVec3(pos),
            atServerTick = clock?.CurrentTick ?? 0,
        });
    }

    // Runtime override of a body's friction + damping. Demo-cube physics tuning
    // (friction=0.6, damp=0.4/0.8) makes the cube come to rest within a few
    // ticks of contact — fine for gameplay but doesn't give us enough coast
    // time to demonstrate the visual smoother on a deterministic reconcile.
    // Tests call this on BOTH server and client right after spawn to push the
    // cube into a low-friction "ice puck" regime without modifying the shared
    // demo scenes. Friction lives on the PhysicsMaterial; damping is per-body.
    //
    // Applies to whichever role's local replica of the entity exists — server
    // sets it on the server-authoritative body, client sets it on its
    // predicted replica. Both must be called to keep the two simulations
    // running with identical parameters.
    private string SetEntityPhysics(JsonElement req)
    {
        int eid = req.GetProperty("entity_id").GetInt32();
        IEnumerable<NetworkBehaviour> collection =
            _role == "server" ? (IEnumerable<NetworkBehaviour>)EntitySpawner.Instance.Entities
                              : EntitySpawner.Instance.ClientEntities;
        NetworkBehaviour target = null;
        foreach (var e in collection)
        {
            if (e.EntityId == eid) { target = e; break; }
        }
        if (target == null) return Err($"entity {eid} not found on {_role}");
        var root = EntitySpawner.Instance.GetEntityRoot(target);
        if (root is not RigidBody3D rb) return Err($"entity {eid} root is not a RigidBody3D");

        if (req.TryGetProperty("friction", out var fr))
        {
            // Clone the existing PhysicsMaterial so this override doesn't leak
            // into other instances that share the same SubResource definition
            // (PackedScene shares sub_resources between instances by default).
            var src = rb.PhysicsMaterialOverride;
            var mat = new PhysicsMaterial
            {
                Friction = (float)fr.GetDouble(),
                Bounce = src?.Bounce ?? 0.2f,
                Rough = src?.Rough ?? false,
                Absorbent = src?.Absorbent ?? false,
            };
            rb.PhysicsMaterialOverride = mat;
        }
        if (req.TryGetProperty("linearDamp", out var ld)) rb.LinearDamp = (float)ld.GetDouble();
        if (req.TryGetProperty("angularDamp", out var ad)) rb.AngularDamp = (float)ad.GetDouble();
        return Ok(new
        {
            entityId = eid,
            role = _role,
            friction = rb.PhysicsMaterialOverride?.Friction ?? 0f,
            linearDamp = rb.LinearDamp,
            angularDamp = rb.AngularDamp,
        });
    }

    private string SetAuthority(JsonElement req)
    {
        if (_role != "server") return Err("set-authority is server-only");
        int eid = req.GetProperty("entity_id").GetInt32();
        int newAuth = req.GetProperty("new_authority").GetInt32();
        ServerManager.Instance.ChangeAuthority(eid, newAuth);
        return Ok(new { entityId = eid, newAuthority = newAuth });
    }

    // Stub kept for backwards compatibility with anything calling the old send-input cmd.
    // Real input-driving now goes through set-input + the latched FakeInputProducer.
    private string SendInput(JsonElement req)
    {
        if (_role != "client") return Err("send-input is client-only");
        return Ok(new { sent = true, note = "stub; use set-input" });
    }

    // The persistent per-client input source. Installed lazily on the first set-input
    // call so MonkeNet doesn't have to know about test-only types at startup. The
    // ClientInputManager polls MonkeNetConfig.Instance.InputProducer.GenerateCurrentInput()
    // each physics tick; setting NextInput here makes every subsequent tick use that
    // input until the orchestrator pushes a new value. We can't reference the test-only
    // FakeInputProducer (it lives in MonkeNetTests.csproj), so define the moral
    // equivalent inline as a nested class.
    private HarnessInputProducer _inputProducer;

    // Tick-scheduled input producer for deterministic test playback.
    //
    // Older versions latched whatever input the most recent `set-input` TCP
    // command set, which meant the *physics tick the input took effect on*
    // was a function of socket-arrival timing — varying by milliseconds run
    // to run, which translated into ±1-2 ticks of variance in when each
    // input change landed. For tests we want the same input to land on the
    // same client tick every run, so the schedule is anchored to the
    // client's ClientNetworkClock.GetCurrentTick() rather than to wall time.
    //
    // The orchestrator installs the full plan upfront via set-input-schedule;
    // GenerateCurrentInput() (called once per physics tick by the framework)
    // returns the schedule entry whose tick is the largest ≤ current tick.
    [GlobalClass]
    public partial class HarnessInputProducer : InputProducerComponent
    {
        // Fallback for the legacy set-input cmd. Used only if no schedule has
        // been installed yet; deprecated but kept for back-compat.
        public IPackableElement NextInput { get; set; }

        private readonly List<(int Tick, IPackableElement Input)> _schedule = new();
        private ClientNetworkClock _clock;

        public void ReplaceSchedule(List<(int Tick, IPackableElement Input)> entries)
        {
            _schedule.Clear();
            // Sort ascending by tick so the per-tick lookup is a simple linear
            // (or binary) scan, not a sort on the hot path.
            entries.Sort((a, b) => a.Tick.CompareTo(b.Tick));
            _schedule.AddRange(entries);
        }

        public override IPackableElement GenerateCurrentInput()
        {
            if (_schedule.Count == 0) return NextInput;

            // Resolve the clock lazily — ClientManager.Instance isn't
            // available during this node's _Ready (autoload ordering). Also
            // re-resolve if the previously cached clock node has been freed,
            // which happens across a disconnect-client → reconnect-client
            // cycle: MonkeNetManager.CreateClient drops the old ClientManager
            // (and with it the old ClientNetworkClock) before instantiating
            // a fresh one, so the cached reference would otherwise dangle.
            if ((_clock == null || !IsInstanceValid(_clock)) && ClientManager.Instance != null)
                _clock = ClientManager.Instance.GetNodeOrNull<ClientNetworkClock>("ClientNetworkClock");
            if (_clock == null) return NextInput;

            int now = _clock.GetCurrentTick();
            // Find the highest tick ≤ now. Linear scan from the end is fine
            // for the schedules tests produce (≤ a few hundred entries).
            for (int i = _schedule.Count - 1; i >= 0; i--)
            {
                if (_schedule[i].Tick <= now) return _schedule[i].Input;
            }
            // Schedule installed but no entry has reached its tick yet (current
            // tick < first scheduled tick). Fall through to NextInput so the
            // mover keeps emitting an idle input every tick — without this, the
            // server sees no input for this client until the first scheduled
            // tick, snapshots don't carry an Inputs[] entry for the entity, and
            // observer clients log [PRED-NO-INPUT] for the entire setup window.
            return NextInput;
        }
    }

    private string SetInput(JsonElement req)
    {
        if (_role != "client") return Err("set-input is client-only");
        if (MonkeNetConfig.Instance == null) return Err("set-input: MonkeNetConfig.Instance is null (scene not ready?)");

        EnsureInputProducer();

        _inputProducer.NextInput = new CharacterInputMessage
        {
            MoveX = req.TryGetProperty("moveX", out var mx) ? (float)mx.GetDouble() : 0f,
            MoveY = req.TryGetProperty("moveY", out var my) ? (float)my.GetDouble() : 0f,
            CameraYaw = req.TryGetProperty("yaw", out var yw) ? (float)yw.GetDouble() : 0f,
            Keys = req.TryGetProperty("keys", out var kk) ? (byte)kk.GetInt32() : (byte)0,
        };
        return Ok(new { installed = true });
    }

    // Installs an entire deterministic input plan keyed to client network ticks.
    // The schedule is consumed by HarnessInputProducer.GenerateCurrentInput:
    // each physics tick the framework polls the producer, which returns the
    // input from the highest-tick entry ≤ current client tick. This makes the
    // physics tick on which an input change takes effect a function of the
    // network clock, not of TCP socket arrival time — eliminating the
    // ±1-3 tick run-to-run jitter caused by per-iteration `set-input` calls.
    //
    // Request shape:
    //   { cmd: "set-input-schedule",
    //     entries: [ { tick:N, moveX:f, moveY:f, yaw:f, keys:i }, ... ] }
    // Entries may be in any order; the producer sorts them. Sending a new
    // schedule replaces the previous one entirely.
    private string SetInputSchedule(JsonElement req)
    {
        if (_role != "client") return Err("set-input-schedule is client-only");
        if (MonkeNetConfig.Instance == null) return Err("set-input-schedule: MonkeNetConfig.Instance is null (scene not ready?)");

        EnsureInputProducer();

        var list = new List<(int Tick, IPackableElement Input)>();
        if (req.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in entries.EnumerateArray())
            {
                int tick = e.GetProperty("tick").GetInt32();
                var msg = new CharacterInputMessage
                {
                    MoveX = e.TryGetProperty("moveX", out var mx) ? (float)mx.GetDouble() : 0f,
                    MoveY = e.TryGetProperty("moveY", out var my) ? (float)my.GetDouble() : 0f,
                    CameraYaw = e.TryGetProperty("yaw", out var yw) ? (float)yw.GetDouble() : 0f,
                    Keys = e.TryGetProperty("keys", out var kk) ? (byte)kk.GetInt32() : (byte)0,
                };
                list.Add((tick, msg));
            }
        }
        _inputProducer.ReplaceSchedule(list);
        return Ok(new { installed = true, entryCount = list.Count });
    }

    // Lightweight read of the client's current synced network tick (the same
    // value HarnessInputProducer keys off of). Cheaper than clock-state when
    // only the tick is needed; used by the orchestrator to convert wall-time
    // setup latencies into "anchor at clientTick + N" deterministic schedules.
    private string GetClientTick()
    {
        if (_role != "client") return Err("get-client-tick is client-only");
        var cm = ClientManager.Instance;
        var clock = cm?.GetNodeOrNull<ClientNetworkClock>("ClientNetworkClock");
        return Ok(new
        {
            syncedTick = clock?.GetCurrentTick() ?? 0,
            rawTick = clock?.RawTick ?? 0,
        });
    }

    private void EnsureInputProducer()
    {
        if (_inputProducer == null)
        {
            _inputProducer = new HarnessInputProducer();
            // Default to an all-zero idle CharacterInputMessage so the producer never
            // returns null. The ClientInputManager early-returns (sending nothing) when
            // the producer yields null — which used to mean every test client transmitted
            // ZERO inputs until set-input/set-input-schedule landed. The server therefore
            // had no input for this client to broadcast in GameSnapshotMessage.Inputs[],
            // and observer clients logged [PRED-NO-INPUT] for the entity until the first
            // real scheduled tick. Real player clients always stream an idle input from
            // connection, so defaulting matches production behavior.
            _inputProducer.NextInput = new CharacterInputMessage();
            // Add to the tree so _Ready / lifecycle hooks fire normally; parent under the
            // harness node itself (which is in MainScene). InputProducerComponent's
            // lifecycle wires it into MonkeNetConfig on AddChild, but we still assign
            // directly to guarantee replacement if a LocalPlayer producer is also active.
            AddChild(_inputProducer);
        }
        // Re-assert ownership of MonkeNetConfig.Instance.InputProducer every call.
        // Across a disconnect-client → reconnect-client cycle, the reclaimed player
        // entity spawns a fresh PlayerInputProducer (via the LocalRigidPlayer scene)
        // which calls Current=true in its _Ready and steals the InputProducer slot.
        // Test code re-invoking set-input-schedule after reconnect expects the
        // harness producer to be active again; idempotently re-pointing is the
        // cheapest way to guarantee that without coupling to spawn timing.
        MonkeNetConfig.Instance.InputProducer = _inputProducer;
    }

    // Returns ClientPredictionManager.MispredictionsCount — the running total of
    // mispredictions detected this session. The orchestrator polls this around input
    // bursts to compute "mispredictions per scenario."
    private string MispredictCount()
    {
        if (_role != "client") return Err("mispredict-count is client-only");
        var cm = ClientManager.Instance;
        if (cm == null) return Ok(new { count = 0 });
        var pm = FindChildOfType<ClientPredictionManager>(cm);
        return Ok(new { count = pm?.MispredictionsCount ?? 0 });
    }

    // Per-classification mispredict counts. The total mispredict count alone is
    // ambiguous — most "mispredicts" in MonkeNet are sub-millimeter Jolt FP
    // divergence (physics-nondeterminism class). The user-visible class is
    // external-force; degraded-network signals snapshots arriving past the
    // rollback window. See ClientPredictionManager.ClassifyMisprediction for
    // the classifier and the quantitative test plan for the threshold rationale.
    private string MispredictClassificationCounts()
    {
        if (_role != "client") return Err("mispredict-classification-counts is client-only");
        var cm = ClientManager.Instance;
        if (cm == null) return Ok(new { externalForce = 0, physicsNondeterminism = 0, degradedNetwork = 0 });
        var pm = FindChildOfType<ClientPredictionManager>(cm);
        return Ok(new
        {
            externalForce = pm?.MispredictsExternalForce ?? 0,
            physicsNondeterminism = pm?.MispredictsPhysicsNondeterminism ?? 0,
            degradedNetwork = pm?.MispredictsDegradedNetwork ?? 0,
        });
    }

    // Depth (in ticks) of the most recent rollback. Quantitative tests poll
    // this after each observed increase in MispredictionsCount to assemble a
    // rollback-depth distribution (P50, P95, P99) across a scenario.
    private string RollbackDepthSample()
    {
        if (_role != "client") return Err("rollback-depth-sample is client-only");
        var cm = ClientManager.Instance;
        if (cm == null) return Ok(new { depth = 0 });
        var pm = FindChildOfType<ClientPredictionManager>(cm);
        return Ok(new { depth = pm?.LastRollbackDepth ?? 0 });
    }

    // Cumulative count of (tick × entity) pairs where the predictor had to
    // fall back to a null input for a remote entity because no server-reported
    // input was cached. Quantitative-suite M9 metric.
    private string MissedInputCount()
    {
        if (_role != "client") return Err("missed-input-count is client-only");
        var cm = ClientManager.Instance;
        if (cm == null) return Ok(new { count = 0 });
        var pm = FindChildOfType<ClientPredictionManager>(cm);
        return Ok(new { count = pm?.MissedInputCount ?? 0 });
    }

    // Cumulative bandwidth counters (sent and received bytes/packets) since
    // the previous call. ENet's PopStatistic is destructive — the host
    // resets the counter on read — so each call returns the delta since the
    // previous read on this process. Quantitative-suite M10 metric.
    private string BandwidthStats()
    {
        if (_role != "client") return Err("bandwidth-stats is client-only");
        var cm = ClientManager.Instance;
        if (cm == null) return Ok(new { sentBytes = 0, recvBytes = 0, sentPackets = 0, recvPackets = 0 });
        return Ok(new
        {
            sentBytes   = cm.PopNetworkStatistic(INetworkManager.NetworkStatisticEnum.SentBytes),
            recvBytes   = cm.PopNetworkStatistic(INetworkManager.NetworkStatisticEnum.ReceivedBytes),
            sentPackets = cm.PopNetworkStatistic(INetworkManager.NetworkStatisticEnum.SentPackets),
            recvPackets = cm.PopNetworkStatistic(INetworkManager.NetworkStatisticEnum.ReceivedPackets),
        });
    }

    // Voluntary client disconnect via the same code path the demo's
    // "Disconnect" button uses (ClientManager.Disconnect). Sends
    // DisconnectNotificationMessage so the server treats this as a manual
    // disconnect (ManualDisconnectMode = KeepEntity by default) and parks the
    // client's entities in a reclaim entry keyed by the client's
    // ClientPersistentIdentity rather than destroying them.
    //
    // The ClientManager + ClientEntityManager survive this call — only the
    // ENet peer is torn down — so the persistent client identity (cached in
    // ClientPersistentIdentity, plus the "IsAwaitingReconnect" flag) carries
    // through to the next reconnect-client. The schedule installed via
    // set-input-schedule references the OLD clock's ticks so we also clear
    // it: the new ClientNetworkClock resets from zero on reconnect and the
    // orchestrator is expected to install a fresh schedule anchored to the
    // new client tick.
    private string DisconnectClient()
    {
        if (_role != "client") return Err("disconnect-client is client-only");
        var cm = ClientManager.Instance;
        if (cm == null) return Err("disconnect-client: no ClientManager");
        if (_inputProducer != null) _inputProducer.ReplaceSchedule(new List<(int, IPackableElement)>());
        cm.Disconnect();
        return Ok(new { disconnected = true });
    }

    // Counterpart to disconnect-client: re-establishes the ENet connection to
    // the same server address + port the harness was launched with. Uses
    // MonkeNetManager.CreateClient, which removes the old ClientManager and
    // brings up a fresh one — ClientEntityManager.OnNetworkReady then sends
    // ClientHelloMessage carrying our ClientPersistentIdentity, and the
    // server's ServerConnectionMonitor performs the keyed-by-persistent-id
    // reclaim lookup that reassigns each previously-orphaned entity's
    // Authority back to this peer.
    //
    // Returns immediately after kicking off the connect; callers should poll
    // the "ready" cmd with networkReady=true to wait for the new ENet peer to
    // come up + the clock to converge before driving more inputs.
    private string ReconnectClient()
    {
        if (_role != "client") return Err("reconnect-client is client-only");
        if (string.IsNullOrEmpty(_clientServerAddr) || _clientEnetPort == 0)
            return Err("reconnect-client: original server-addr/enet-port not captured at startup");
        MonkeNetManager.Instance.CreateClient(_clientServerAddr, _clientEnetPort);
        return Ok(new { reconnecting = true, serverAddr = _clientServerAddr, enetPort = _clientEnetPort });
    }

    // Reports the persistent client identity (the value sent in every
    // ClientHelloMessage on every connect, source-of-truth for reclaim
    // server-side) and whether we're in the post-disconnect /
    // pre-next-reconnect window. The persistent id NEVER rotates within a
    // process; reading this both before and after a reconnect should
    // return the same value, which is precisely what the persistence test
    // asserts. The 4-char suffix matches ServerConnectionMonitor's
    // [cid:XXXX] log tag so the test trace can be cross-referenced
    // against per-process MonkeLogger files.
    private string ClientPersistentId()
    {
        if (_role != "client") return Err("client-persistent-id is client-only");
        string id = ClientPersistentIdentity.Get();
        return Ok(new
        {
            id,
            idShort = ClientPersistentIdentity.Tail(id),
            source = ClientPersistentIdentity.SourceDescription,
            isAwaitingReconnect = ClientEntityManager.IsAwaitingReconnect,
        });
    }

    // Drops the active input schedule without sending a new one. Used by
    // tests across the disconnect/reconnect boundary so a stale schedule
    // (keyed to the previous ClientNetworkClock's ticks) isn't lingering
    // when the post-reconnect schedule is installed. set-input-schedule
    // already replaces the previous schedule, so this is only needed if the
    // caller wants a beat of zero-input between phases.
    private string ClearInputSchedule()
    {
        if (_role != "client") return Err("clear-input-schedule is client-only");
        if (_inputProducer != null)
            _inputProducer.ReplaceSchedule(new List<(int, IPackableElement)>());
        return Ok(new { cleared = true });
    }

    // Reports whether the server is holding a parked reclaim entry for the
    // given ClientPersistentId (i.e. that identity disconnected with
    // KeepEntity and hasn't reconnected yet). Used by the persistence test
    // to assert both that an entry exists immediately after disconnect AND
    // that it's been consumed once the matching client reconnects + sends
    // its hello.
    private string PendingReclaimFor(JsonElement req)
    {
        if (_role != "server") return Err("pending-reclaim-for is server-only");
        var sm = ServerManager.Instance;
        if (sm == null) return Err("pending-reclaim-for: no ServerManager");
        // ServerConnectionMonitor sits as a named child of ServerManager (see
        // ServerManager.tscn). Type-search would also work; this is a
        // diagnostic command so the simple name-based lookup is fine.
        var monitor = sm.GetNodeOrNull<ServerConnectionMonitor>("ServerConnectionMonitor");
        if (monitor == null) return Err("pending-reclaim-for: ServerConnectionMonitor not in scene");
        string id = req.GetProperty("client_id").GetString() ?? "";
        return Ok(new { hasPending = monitor.HasPendingReclaimFor(id), clientId = id });
    }

    // Number of currently-connected client peers, as tracked by ServerManager's
    // INetworkManager. Used by MultiProcessServerLifecycleTests to verify
    // stop/restart leaves no stale peer state.
    private string ServerPeerCount()
    {
        if (_role != "server") return Err("server-peer-count is server-only");
        var sm = ServerManager.Instance;
        return Ok(new { count = sm?.GetConnectedClientCount() ?? 0 });
    }

    // One-shot snapshot of the tick + every visible entity's position/velocity. Cheap
    // enough to call every 5-10 ticks during a scenario; the orchestrator stitches the
    // returned samples into a CSV trace for the SVG visualiser.
    private string SampleState()
    {
        var list = new List<object>();
        IEnumerable<NetworkBehaviour> source =
            _role == "server" ? (IEnumerable<NetworkBehaviour>)(EntitySpawner.Instance?.Entities)
                              : (IEnumerable<NetworkBehaviour>)(EntitySpawner.Instance?.ClientEntities);
        if (source != null)
        {
            foreach (var e in source)
            {
                var root = EntitySpawner.Instance.GetEntityRoot(e);
                Vector3 pos = root?.GlobalPosition ?? Vector3.Zero;
                // RigidBody3D exposes linear + angular velocity directly. Other
                // node types report zero so the trace still has a row per entity
                // (consumers don't need to special-case missing fields).
                Vector3 vel = Vector3.Zero;
                Vector3 angVel = Vector3.Zero;
                if (root is RigidBody3D rb)
                {
                    vel = rb.LinearVelocity;
                    angVel = rb.AngularVelocity;
                }
                // Rotation as a unit quaternion. Plot writers convert to Euler
                // or axis-angle as needed; sending the quat preserves the full
                // orientation without ambiguity around gimbal-aligned axes.
                Quaternion rot = root != null ? root.GlobalTransform.Basis.GetRotationQuaternion() : Quaternion.Identity;
                // If a PredictionVisualSmoothing3D is wired, report its Visual
                // node's world pose too — that's what the player actually sees.
                // Tests use the (visualPos, visualRot) pair to verify the
                // smoother's offset-decay hides body snaps from the renderer.
                Vector3 visualPos = pos;
                Quaternion visualRot = rot;
                if (root != null)
                {
                    var smoother = FindDescendantOfType<PredictionVisualSmoothing3D>(root);
                    if (smoother?.Visual != null)
                    {
                        visualPos = smoother.Visual.GlobalPosition;
                        visualRot = smoother.Visual.GlobalTransform.Basis.GetRotationQuaternion();
                    }
                }
                // Sleep state — only meaningful on RigidBody3D. Other body
                // types report false / false so the JSON shape stays uniform.
                bool sleeping = (root is RigidBody3D rbSleep) && rbSleep.Sleeping;
                bool canSleep = (root is RigidBody3D rbCanSleep) && rbCanSleep.CanSleep;
                list.Add(new
                {
                    id = e.EntityId,
                    type = e.EntityType,
                    authority = e.Authority,
                    position = SerializeVec3(pos),
                    velocity = SerializeVec3(vel),
                    angularVelocity = SerializeVec3(angVel),
                    rotation = new double[] { rot.X, rot.Y, rot.Z, rot.W },
                    visualPosition = SerializeVec3(visualPos),
                    visualRotation = new double[] { visualRot.X, visualRot.Y, visualRot.Z, visualRot.W },
                    sleeping,
                    canSleep,
                });
            }
        }
        int mispredicts = 0;
        if (_role == "client" && ClientManager.Instance != null)
        {
            var pm = FindChildOfType<ClientPredictionManager>(ClientManager.Instance);
            if (pm != null) mispredicts = pm.MispredictionsCount;
        }
        return Ok(new
        {
            tick = (long)Engine.GetPhysicsFrames(),
            mispredictionsCount = mispredicts,
            entities = list,
        });
    }

    private string GetAllEntities()
    {
        var list = new List<object>();
        if (_role == "server")
        {
            foreach (var e in EntitySpawner.Instance.Entities)
            {
                var root = EntitySpawner.Instance.GetEntityRoot(e);
                list.Add(new
                {
                    id = e.EntityId,
                    type = e.EntityType,
                    authority = e.Authority,
                    position = SerializeVec3(root?.GlobalPosition ?? Vector3.Zero),
                });
            }
        }
        else
        {
            foreach (var e in EntitySpawner.Instance.ClientEntities)
            {
                var root = EntitySpawner.Instance.GetEntityRoot(e);
                list.Add(new
                {
                    id = e.EntityId,
                    type = e.EntityType,
                    authority = e.Authority,
                    position = SerializeVec3(root?.GlobalPosition ?? Vector3.Zero),
                });
            }
        }
        return Ok(new { entities = list });
    }

    private string GetEntity(JsonElement req)
    {
        int eid = req.GetProperty("entity_id").GetInt32();
        var collection = _role == "server"
            ? (IEnumerable<NetworkBehaviour>)EntitySpawner.Instance.Entities
            : EntitySpawner.Instance.ClientEntities;
        foreach (var e in collection)
        {
            if (e.EntityId == eid)
            {
                var root = EntitySpawner.Instance.GetEntityRoot(e);
                return Ok(new
                {
                    id = e.EntityId,
                    type = e.EntityType,
                    authority = e.Authority,
                    position = SerializeVec3(root?.GlobalPosition ?? Vector3.Zero),
                });
            }
        }
        return Err($"entity {eid} not found");
    }

    // Make the observer camera track a specific entity each frame, looking at it
    // from a fixed offset. Used by tests where the action travels far enough to
    // leave the default fixed view; the test calls this once per client with the
    // vehicle's entity id and an offset that frames the run. Pass entity_id=0 to
    // clear the follow and return the camera to its installed pose.
    private string CameraFollowEntity(JsonElement req)
    {
        if (_observerCamera == null || !IsInstanceValid(_observerCamera))
            return Err("observer camera not installed (clients only)");

        int eid = req.GetProperty("entity_id").GetInt32();
        if (req.TryGetProperty("offset", out var off))
            _cameraFollowOffset = ReadVec3(off);
        if (req.TryGetProperty("lookOffset", out var lo))
            _cameraFollowLookOffset = ReadVec3(lo);
        _cameraFollowEntityId = eid;
        return Ok(new { followEntityId = eid, offset = SerializeVec3(_cameraFollowOffset) });
    }

    // Constructs the deferred video recorder using the params stashed in
    // _Ready when --defer-video-start was passed. Idempotent: returns the same
    // shape if already running or if no recording was requested for this
    // process, so callers don't need to know the harness's recording state.
    private string StartRecording()
    {
        if (_videoRecorder != null)
            return Ok(new { started = false, reason = "already running", path = _pendingVideoPath });
        if (string.IsNullOrEmpty(_pendingVideoPath))
            return Ok(new { started = false, reason = "no deferred recording configured" });
        try
        {
            _videoRecorder = new HarnessVideoRecorder(
                _pendingFfmpegBin, _pendingVideoPath,
                _pendingVideoWidth, _pendingVideoHeight, frameRate: 60);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[VIDEO] deferred recorder init failed: {ex.GetType().Name}: {ex.Message}");
            _videoRecorder = null;
            return Err($"recorder init failed: {ex.Message}");
        }
        return Ok(new { started = true, path = _pendingVideoPath });
    }

    // One-shot fixed-pose placement of the observer camera. Useful when the
    // recorded shot wants a static framing rather than chase-cam tracking; the
    // camera does NOT update per-frame the way camera-follow-entity does, so
    // there's no per-physics-frame jitter from sampling the entity pose. Also
    // clears any active follow so the two modes don't fight.
    private string SetCamera(JsonElement req)
    {
        if (_observerCamera == null || !IsInstanceValid(_observerCamera))
            return Err("observer camera not installed (clients only)");
        if (req.TryGetProperty("position", out var p))
            _observerCamera.GlobalPosition = ReadVec3(p);
        if (req.TryGetProperty("lookAt", out var la))
            _observerCamera.LookAt(ReadVec3(la), Vector3.Up);
        _cameraFollowEntityId = 0;
        return Ok(new
        {
            position = SerializeVec3(_observerCamera.GlobalPosition),
        });
    }

    private void UpdateCameraFollow()
    {
        if (_cameraFollowEntityId == 0) return;
        if (_observerCamera == null || !IsInstanceValid(_observerCamera)) return;
        if (EntitySpawner.Instance == null) return;
        var collection = _role == "server"
            ? (IEnumerable<NetworkBehaviour>)EntitySpawner.Instance.Entities
            : EntitySpawner.Instance.ClientEntities;
        NetworkBehaviour target = null;
        foreach (var e in collection)
        {
            if (e.EntityId == _cameraFollowEntityId) { target = e; break; }
        }
        if (target == null) return;
        var root = EntitySpawner.Instance.GetEntityRoot(target);
        if (root == null) return;

        // Camera-follow runs every _Process (render rate), which on a 144-Hz
        // monitor is 2-3× the 60-Hz physics tick. Reading the rigid body's
        // GlobalPosition directly here would show the same value across
        // 2-3 render frames in a row then jump — stair-stepping that the
        // recorded video shows as jitter. Prefer the smoothed visual node
        // (PredictionVisualSmoothing3D.Visual) whose pose is interpolated
        // between physics ticks via Engine.GetPhysicsInterpolationFraction(),
        // so successive render frames see a continuous-in-time pose.
        // Fall back to the body's pose when no smoother is wired (e.g. a
        // server-authoritative entity whose client representation is purely
        // snapshot-driven; we extrapolate one render frame's worth of velocity
        // so the camera doesn't pin to the snapshot tick).
        Vector3 worldPos;
        Vector3 lookTarget;
        var smoother = FindDescendantOfType<PredictionVisualSmoothing3D>(root);
        if (smoother?.Visual != null)
        {
            // GetGlobalTransformInterpolated() returns the FTI-interpolated
            // pose between the visual's previous and current physics-tick
            // transforms, so the camera tracks smoothly between physics ticks
            // even though the smoother only writes Visual.GlobalPosition once
            // per tick. Without this the camera sees the visual stair-step at
            // 60 Hz, and any rollback-induced one-frame discontinuity in
            // Visual.GlobalPosition shows up as a visible snap in the video.
            worldPos = smoother.Visual.GetGlobalTransformInterpolated().Origin;
            lookTarget = worldPos + _cameraFollowLookOffset;
        }
        else
        {
            worldPos = root.GlobalPosition;
            if (root is RigidBody3D rb)
            {
                // Frame-rate-aware velocity extrapolation: project from the
                // last physics-tick pose forward by the fraction of a tick
                // elapsed since that step. Matches the same interpolation
                // Godot would do internally if physics_interpolation_mode is
                // enabled on the node; doing it manually here keeps the
                // camera smooth even for entities that haven't opted into
                // Godot's built-in interpolation.
                float pif = (float)Engine.GetPhysicsInterpolationFraction();
                worldPos += rb.LinearVelocity * pif * (float)PhysicsUtils.DeltaTime;
            }
            lookTarget = worldPos + _cameraFollowLookOffset;
        }
        _observerCamera.GlobalPosition = worldPos + _cameraFollowOffset;
        _observerCamera.LookAt(lookTarget, Vector3.Up);
    }

    private string Shutdown()
    {
        var resp = Ok(new { goodbye = true });
        // Quit on a deferred call so the response actually flushes to the orchestrator.
        CallDeferred(nameof(QuitDeferred));
        return resp;
    }

    private void QuitDeferred()
    {
        // Finalise the recorder BEFORE shutting down the engine. StopAndFlush
        // drains the queued frames, closes ffmpeg's stdin so the container
        // header is written, and waits up to 15 s for the encoder to exit
        // cleanly. If we Quit first, the engine kills the ffmpeg child
        // before its `moov` atom is written and the mp4 is unreadable.
        try { _videoRecorder?.StopAndFlush(); } catch { }
        GetTree().Quit(0);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    // Look up a child node by component TYPE rather than by scene-graph name.
    // The internal MonkeNet scenes name nodes inconsistently with their class
    // (e.g. ClientManager.tscn has a "PredictionManager" node whose script is
    // ClientPredictionManager), so a name-based GetNodeOrNull silently returns
    // null when the names diverge — which previously zeroed out the harness's
    // misprediction counter. Type-based search is robust to renames.
    private static T FindChildOfType<T>(Node parent) where T : class
    {
        if (parent == null) return null;
        foreach (var child in parent.GetChildren())
        {
            if (child is T match) return match;
        }
        return null;
    }

    // Depth-first search for a node of the given type. Used to locate the
    // PredictionVisualSmoothing3D component sitting under an entity root —
    // a direct-child search misses it when the smoother is nested under an
    // intermediate node (or vice versa for different scene templates).
    private static T FindDescendantOfType<T>(Node parent) where T : class
    {
        if (parent == null) return null;
        foreach (var child in parent.GetChildren())
        {
            if (child is T match) return match;
            T deeper = FindDescendantOfType<T>(child);
            if (deeper != null) return deeper;
        }
        return null;
    }

    private static Dictionary<string, string> ParseArgs(string[] argv)
    {
        var dict = new Dictionary<string, string>();
        foreach (var a in argv)
        {
            if (!a.StartsWith("--")) continue;
            var trimmed = a.Substring(2);
            int eq = trimmed.IndexOf('=');
            if (eq < 0) dict[trimmed] = "true";
            else dict[trimmed.Substring(0, eq)] = trimmed.Substring(eq + 1);
        }
        return dict;
    }

    private static Vector3 ReadVec3(JsonElement el)
    {
        return new Vector3(
            (float)el[0].GetDouble(),
            (float)el[1].GetDouble(),
            (float)el[2].GetDouble());
    }

    private static double[] SerializeVec3(Vector3 v) => new double[] { v.X, v.Y, v.Z };

    private static string Ok(object payload)
    {
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            { "ok", true },
            { "data", payload },
        });
    }

    private static string Err(string msg)
    {
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            { "ok", false },
            { "error", msg },
        });
    }

    // Resolves ffmpeg.exe location. Tries FFMPEG_BIN env var, then PATH
    // lookup via `where` / `which`. Returns null if not found.
    private static string ResolveFfmpegBin()
    {
        string env = System.Environment.GetEnvironmentVariable("FFMPEG_BIN");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        bool isWindows = System.Environment.OSVersion.Platform == PlatformID.Win32NT;
        string finder = isWindows ? "where" : "which";
        try
        {
            var psi = new ProcessStartInfo(finder, "ffmpeg")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            string output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2_000);
            if (string.IsNullOrEmpty(output)) return null;
            string first = output.Split('\n')[0].Trim();
            return File.Exists(first) ? first : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Captures the harness viewport's rendered frame each <see cref="MultiClientHarness._Process"/>
    /// tick and pipes it as raw RGBA bytes into a child ffmpeg process which encodes
    /// to H.264/MP4. Capture happens INSIDE the Godot process — not from the OS
    /// compositor — so the test client window can be minimised, moved off-screen,
    /// or hidden behind any other window without affecting the recording.
    ///
    /// A bounded queue plus a background writer thread decouple capture (called on
    /// the engine's render thread) from encoding (CPU-bound ffmpeg child). If the
    /// encoder cannot keep up the queue fills and capture starts dropping frames,
    /// which manifests as jitter in the resulting video but never delays the
    /// engine's render loop.
    /// </summary>
    private sealed class HarnessVideoRecorder
    {
        private readonly Process _ffmpeg;
        private readonly Thread _writer;
        private readonly BlockingCollection<byte[]> _queue;
        private readonly int _width;
        private readonly int _height;
        private readonly string _outputPath;
        private long _produced;
        private long _dropped;
        private long _written;
        private volatile bool _stopped;

        public long Produced => _produced;
        public long Dropped => _dropped;
        public long Written => _written;

        public HarnessVideoRecorder(string ffmpegBin, string outputPath, int width, int height, int frameRate)
        {
            _width = width;
            _height = height;
            _outputPath = outputPath;
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

            var psi = new ProcessStartInfo(ffmpegBin)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("warning");
            // Raw RGBA input on stdin. Godot's Viewport.GetTexture().GetImage()
            // returns Rgba8 after Convert; we hand those bytes straight to ffmpeg.
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("rawvideo");
            psi.ArgumentList.Add("-pixel_format"); psi.ArgumentList.Add("rgba");
            psi.ArgumentList.Add("-video_size"); psi.ArgumentList.Add($"{width}x{height}");
            psi.ArgumentList.Add("-framerate"); psi.ArgumentList.Add(frameRate.ToString());
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("pipe:0");
            psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("libx264");
            psi.ArgumentList.Add("-preset"); psi.ArgumentList.Add("ultrafast");
            psi.ArgumentList.Add("-crf"); psi.ArgumentList.Add("23");
            psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("yuv420p");
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add(outputPath);

            _ffmpeg = Process.Start(psi)
                ?? throw new InvalidOperationException("failed to start ffmpeg for video recording");
            _ffmpeg.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) GD.PrintErr($"[VIDEO/ffmpeg] {e.Data}");
            };
            _ffmpeg.OutputDataReceived += (_, _) => { /* discard */ };
            _ffmpeg.BeginErrorReadLine();
            _ffmpeg.BeginOutputReadLine();

            // Bounded queue — if encoder falls behind, capture drops frames
            // rather than blocking the engine's render loop.
            _queue = new BlockingCollection<byte[]>(boundedCapacity: 8);
            _writer = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "HarnessVideoRecorder.Writer",
            };
            _writer.Start();
            GD.PrintErr($"[VIDEO] in-engine viewport recorder started → {outputPath} ({width}x{height} @ {frameRate} fps)");
        }

        /// <summary>Reads the viewport's framebuffer and enqueues it for encoding.
        /// Non-blocking: if the encoder queue is full the frame is dropped.</summary>
        public void TryCaptureFrame(Viewport viewport)
        {
            if (_stopped || viewport == null) return;
            ViewportTexture vt = viewport.GetTexture();
            if (vt == null) return;
            Image img = vt.GetImage();
            if (img == null) return;
            if (img.GetFormat() != Image.Format.Rgba8) img.Convert(Image.Format.Rgba8);
            if (img.GetWidth() != _width || img.GetHeight() != _height) return; // resolution drift, skip
            byte[] bytes = img.GetData();
            _produced++;
            if (!_queue.TryAdd(bytes)) _dropped++;
        }

        private void WriterLoop()
        {
            try
            {
                Stream stdin = _ffmpeg.StandardInput.BaseStream;
                foreach (byte[] bytes in _queue.GetConsumingEnumerable())
                {
                    if (_ffmpeg.HasExited) break;
                    stdin.Write(bytes, 0, bytes.Length);
                    _written++;
                }
                stdin.Flush();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[VIDEO] writer thread error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Drains the queue, closes ffmpeg's stdin so it finalises the
        /// container cleanly, and waits up to 15 s for the encoder to exit.</summary>
        public void StopAndFlush()
        {
            if (_stopped) return;
            _stopped = true;
            try { _queue.CompleteAdding(); } catch { }
            _writer.Join(TimeSpan.FromSeconds(10));
            try { _ffmpeg.StandardInput.Close(); } catch { }
            try
            {
                if (!_ffmpeg.WaitForExit(15_000))
                {
                    _ffmpeg.Kill(entireProcessTree: true);
                    GD.PrintErr($"[VIDEO] ffmpeg did not finalise within 15 s; killed");
                }
                else
                {
                    GD.PrintErr($"[VIDEO] recording finalised: {_produced} produced, {_dropped} dropped, {_written} written → {_outputPath}");
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[VIDEO] StopAndFlush: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
