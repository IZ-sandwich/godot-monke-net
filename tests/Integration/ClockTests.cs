using System.Reflection;
using System.Threading.Tasks;
using GdUnit4;
using MonkeNet.Client;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Server;
using MonkeNet.Tests.Infrastructure;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Integration;

/// <summary>
/// B-01..B-05: ClientNetworkClock — latency calculation and sample management.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class ClockTests
{
    private ISceneRunner _serverRunner;
    private ISceneRunner _clientRunner;
    private FakeNetworkEndpoint _serverNet;
    private FakeNetworkEndpoint _clientNet;
    private ServerManager _server;
    private ClientManager _client;
    private ClientNetworkClock _clock;

    [BeforeTest]
    public async Task SetUp()
    {
        FakeNetworkBridge.Reset();
        MessageSerializer.RegisterNetworkMessages();
        (_serverNet, _clientNet) = FakeNetworkBridge.CreatePair();

        _serverRunner = ISceneRunner.Load("res://addons/monke-net/scenes/ServerManager.tscn", autoFree: true);
        await _serverRunner.AwaitIdleFrame();
        _server = _serverRunner.Scene() as ServerManager;

        _clientRunner = ISceneRunner.Load("res://addons/monke-net/scenes/ClientManager.tscn", autoFree: true);
        await _clientRunner.AwaitIdleFrame();
        _client = _clientRunner.Scene() as ClientManager;

        _server!.Initialize(_serverNet, port: 7300);
        _client!.Initialize(_clientNet, "127.0.0.1", 7300);
        await _clientRunner.AwaitIdleFrame();

        _clock = _clientRunner.Scene().GetNode<ClientNetworkClock>("ClientNetworkClock");
    }

    [AfterTest]
    public void TearDown()
    {
        _serverRunner?.Dispose();
        _clientRunner?.Dispose();
    }

    // B-01 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task Clock_NoLatencyCalculated_BeforeSampleSize()
    {
        SetSampleSize(3);

        bool signalFired = false;
        _client.LatencyCalculated += (_, __) => signalFired = true;

        // Send 2 clock syncs (one short of _sampleSize = 3)
        SendClockSync();
        SendClockSync();
        await _clientRunner.AwaitIdleFrame();

        AssertThat(signalFired).IsFalse();
    }

    // B-02 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task Clock_EmitsLatencyCalculated_OnNthSample()
    {
        SetSampleSize(3);

        int emitCount = 0;
        _client.LatencyCalculated += (_, __) => emitCount++;

        SendClockSync();
        SendClockSync();
        SendClockSync(); // 3rd sample → should emit
        await _clientRunner.AwaitIdleFrame();

        AssertThat(emitCount).IsEqual(1);
    }

    // B-03 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task Clock_SampleBuffer_ClearsAfterEmit()
    {
        SetSampleSize(3);

        int emitCount = 0;
        _client.LatencyCalculated += (_, __) => emitCount++;

        // Fill first batch → emit
        SendClockSync(); SendClockSync(); SendClockSync();
        await _clientRunner.AwaitIdleFrame();

        AssertThat(emitCount).IsEqual(1);

        // Send 2 more (incomplete second batch)
        SendClockSync(); SendClockSync();
        await _clientRunner.AwaitIdleFrame();

        AssertThat(emitCount).IsEqual(1); // second batch not complete yet
    }

    // B-04 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task Server_EchoesClockSync_WithServerTime()
    {
        // Advance server tick by driving _PhysicsProcess indirectly
        for (int i = 0; i < 10; i++)
            _server.EmitSignal(ServerManager.SignalName.ServerTick, i);

        // Send a ClockSyncMessage from the client side; server echoes back with ServerTime set
        var sync = new ClockSyncMessage { ClientTime = 9999, ServerTime = 0 };
        _serverNet.SimulateIncomingPacket(2, MessageSerializer.Serialize(sync));
        await _serverRunner.AwaitIdleFrame();

        // The server delivers its echo via the bridge; capture it
        ClockSyncMessage? echoReceived = null;
        _clientNet.PacketReceived += (_, bin) =>
        {
            if (MessageSerializer.Deserialize(bin) is ClockSyncMessage echo)
                echoReceived = echo;
        };

        // Re-trigger via the server clock's OnCommandReceived
        _serverNet.SimulateIncomingPacket(2, MessageSerializer.Serialize(sync));
        await _serverRunner.AwaitIdleFrame();

        if (echoReceived.HasValue)
        {
            AssertThat(echoReceived.Value.ClientTime).IsEqual(9999);
            AssertThat(echoReceived.Value.ServerTime).IsGreaterEqual(0);
        }
    }

    // B-06 ─────────────────────────────────────────────────────────────────────
    // Fast-correction path: a sync sample whose offset estimate exceeds
    // ClientNetworkClock._immediateCorrectionMinAbsTicks should pump _lastOffset
    // immediately (not wait for the averaged-window buffer to fill), so the
    // next ProcessTick advances _currentTick by the offset. This is the
    // Photon-Fusion-2-style "first-packet correction" that brings the clock
    // to within a few ticks of the server within ~0.5 s of connecting.
    [TestCase]
    public void Clock_AppliesImmediateCorrection_OnLargeOffsetSample()
    {
        SetSampleSize(11); // make the windowed path inert for this test

        // Inject a sync echo as if the server already replied with a far-future
        // ServerTime. Bypasses the server-side echo path, which on the fake
        // bridge would otherwise reflect the server's _currentTick (0).
        // Read _lastOffset BEFORE awaiting an idle frame so the engine's
        // _PhysicsProcess can't consume it before we observe it.
        InjectClockSyncEchoToClient(serverTime: 80, clientTime: (int)Godot.Time.GetTicksMsec());

        int lastOffset = GetLastOffset();

        // _lastOffset should equal the immediate offset estimate ≈ 80
        // (allowing for the (latency_ticks) component and any raw ticks the
        // client has done during scene setup).
        AssertThat(lastOffset).IsGreaterEqual(70);
    }

    // B-07 ─────────────────────────────────────────────────────────────────────
    // After an immediate correction the per-sample offset buffers must be
    // cleared so the next averaged window doesn't re-add the same pre-
    // correction offset on top of the immediate one (which would yank the
    // clock backwards right as the window completes).
    //
    // The post-correction syncs must keep the offset BELOW the immediate-
    // correction threshold; otherwise they'd take the fast-path again and
    // re-clear the buffer, masking the bug this test is meant to catch. We
    // capture the raw tick afresh at each injection so the synthesised
    // serverTime tracks _currentTick — any ProcessTicks that fire during the
    // awaits don't push the inferred offset outside the windowed-path
    // tolerance band. A single cached alignedServerTick would let the offset
    // grow with each ProcessTick and made the test flaky in the suite (where
    // physics-frame catch-up can advance _currentTick by 5–10 ticks per idle
    // frame), even though it passed in isolation.
    [TestCase]
    public async Task Clock_ImmediateCorrection_ClearsBuffersToAvoidWindowDoubleCount()
    {
        SetSampleSize(3);

        // Big-offset sync → immediate path, clears buffers.
        InjectClockSyncEchoToClient(serverTime: 80, clientTime: (int)Godot.Time.GetTicksMsec());
        await _clientRunner.AwaitIdleFrame();

        // After the immediate correction the buffer should be empty, so two
        // more small-offset samples shouldn't complete the window of 3.
        bool fired = false;
        _client.LatencyCalculated += (_, __) => fired = true;

        // Each post-correction sync uses the current raw tick as ServerTime,
        // so the inferred offset stays ≈0 regardless of how many ProcessTicks
        // the engine has run since the immediate correction.
        InjectAlignedSync();
        InjectAlignedSync();
        await _clientRunner.AwaitIdleFrame();
        AssertThat(fired).IsFalse(); // buffer still only has 2 post-correction samples

        InjectAlignedSync(); // 3rd → window emits
        await _clientRunner.AwaitIdleFrame();
        AssertThat(fired).IsTrue();
    }

    // Injects a clock-sync echo whose ServerTime equals the client's current
    // raw tick. The resulting offset estimate is ≈0 ticks (well below
    // _immediateCorrectionMinAbsTicks=10), so the sync takes the windowed-
    // average path rather than re-triggering the fast-correction path.
    private void InjectAlignedSync() =>
        InjectClockSyncEchoToClient(GetCurrentRawTick(), (int)Godot.Time.GetTicksMsec());

    // B-05 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task Clock_GetCurrentTick_ExceedsRawTickByAtLeastFixedMargin()
    {
        int fixedMargin = GetFixedTickMargin();

        // ProcessTick twice to advance raw tick
        _clock.ProcessTick();
        _clock.ProcessTick();

        // GetCurrentTick = rawTick + latency + jitter + fixedMargin
        // At minimum it equals rawTick + fixedMargin (latency/jitter start at 0)
        int rawTick = GetCurrentRawTick();
        int reported = _clock.GetCurrentTick();

        AssertThat(reported).IsGreaterEqual(rawTick + fixedMargin);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private void SetSampleSize(int n)
    {
        typeof(ClientNetworkClock)
            .GetField("_sampleSize", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.SetValue(_clock, n);
    }

    private int GetFixedTickMargin()
    {
        return (int)(typeof(ClientNetworkClock)
            .GetField("_fixedTickMargin", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(_clock) ?? 3);
    }

    private int GetCurrentRawTick()
    {
        return (int)(typeof(ClientNetworkClock)
            .GetField("_currentTick", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(_clock) ?? 0);
    }

    private int GetLastOffset()
    {
        return (int)(typeof(ClientNetworkClock)
            .GetField("_lastOffset", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(_clock) ?? 0);
    }

    private void SendClockSync()
    {
        // Trigger the clock's timer-driven method to send a sync, then deliver the echo.
        _clock.GetNode<Godot.Timer>("Timer").EmitSignal("timeout");
        // The bridge synchronously delivers the server's echo to the client.
    }

    // Injects a ClockSyncMessage echo directly into the client's network
    // endpoint as if the server had already replied with the given ServerTime.
    // Bypasses the server-echo path (where ServerTime would reflect the
    // server's actual _currentTick = 0 since we don't pump physics here).
    private void InjectClockSyncEchoToClient(int serverTime, int clientTime)
    {
        var sync = new ClockSyncMessage { ClientTime = clientTime, ServerTime = serverTime };
        _clientNet.SimulateIncomingPacket(1, MessageSerializer.Serialize(sync));
    }
}
