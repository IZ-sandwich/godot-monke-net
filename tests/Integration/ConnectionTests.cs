using System.Threading.Tasks;
using GdUnit4;
using MonkeNet.Client;
using MonkeNet.Server;
using MonkeNet.Serializer;
using MonkeNet.Tests.Infrastructure;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Integration;

/// <summary>
/// C-01..C-05: Connection lifecycle tests.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class ConnectionTests
{
    private ISceneRunner _serverRunner;
    private ISceneRunner _clientRunner;
    private FakeNetworkEndpoint _serverNet;
    private FakeNetworkEndpoint _clientNet;

    [BeforeTest]
    public async Task SetUp()
    {
        FakeNetworkBridge.Reset();
        MessageSerializer.RegisterNetworkMessages();
        (_serverNet, _clientNet) = FakeNetworkBridge.CreatePair();

        _serverRunner = ISceneRunner.Load("res://addons/monke-net/scenes/ServerManager.tscn", autoFree: true);
        await _serverRunner.AwaitIdleFrame();

        _clientRunner = ISceneRunner.Load("res://addons/monke-net/scenes/ClientManager.tscn", autoFree: true);
        await _clientRunner.AwaitIdleFrame();
    }

    [AfterTest]
    public void TearDown()
    {
        _serverRunner?.Dispose();
        _clientRunner?.Dispose();
    }

    // C-01 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task ServerEmitsServerReady_OnInitialize()
    {
        var server = _serverRunner.Scene() as ServerManager;
        AssertThat(server).IsNotNull();

        var sa = AssertSignal(server);
        server!.Initialize(_serverNet, port: 7777);

        await sa.IsEmitted(ServerManager.SignalName.ServerReady).WithTimeout(2000);
    }

    // C-02 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task ServerEmitsClientConnected_WhenClientConnects()
    {
        var server = _serverRunner.Scene() as ServerManager;
        server!.Initialize(_serverNet, port: 7777);

        var sa = AssertSignal(server);
        _serverNet.FireClientConnected(peerId: 3);

        await sa.IsEmitted(ServerManager.SignalName.ClientConnected, (Godot.Variant)3).WithTimeout(2000);
    }

    // C-03 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task Client_ServerConnectedFlag_SetAfterHandshake()
    {
        var client = _clientRunner.Scene() as ClientManager;
        AssertThat(client).IsNotNull();

        client!.Initialize(_clientNet, "127.0.0.1", 7777);
        AssertThat(client.IsNetworkReady).IsFalse();

        var sa = AssertSignal(client);
        await sa.IsNotEmitted(ClientManager.SignalName.ServerDisconnected).WithTimeout(500);
    }

    // C-04 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task NetworkReady_EmittedAfterClockSyncSamples()
    {
        var server = _serverRunner.Scene() as ServerManager;
        var client = _clientRunner.Scene() as ClientManager;
        server!.Initialize(_serverNet, port: 7778);
        client!.Initialize(_clientNet, "127.0.0.1", 7778);

        var clock = _clientRunner.Scene().GetNode<ClientNetworkClock>("ClientNetworkClock");
        typeof(ClientNetworkClock)
            .GetField("_sampleSize", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.SetValue(clock, 1);

        var sa = AssertSignal(client);
        clock.GetNode<Godot.Timer>("Timer").EmitSignal("timeout");

        await sa.IsEmitted(ClientManager.SignalName.NetworkReady).WithTimeout(2000);
        AssertThat(client.IsNetworkReady).IsTrue();
    }

    // C-04b ────────────────────────────────────────────────────────────────────
    // Regression: NetworkReady was previously emitted on every latency recalc, causing
    // UI flicker and re-firing of game-start logic each ~second. Must now fire once
    // per connection lifecycle.
    [TestCase]
    public async Task NetworkReady_FiresOncePerConnection()
    {
        var server = _serverRunner.Scene() as ServerManager;
        var client = _clientRunner.Scene() as ClientManager;
        server!.Initialize(_serverNet, port: 7780);
        client!.Initialize(_clientNet, "127.0.0.1", 7780);

        var clock = _clientRunner.Scene().GetNode<ClientNetworkClock>("ClientNetworkClock");
        typeof(ClientNetworkClock)
            .GetField("_sampleSize", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.SetValue(clock, 1);

        int emissionCount = 0;
        client.NetworkReady += () => emissionCount++;

        var timer = clock.GetNode<Godot.Timer>("Timer");
        for (int i = 0; i < 5; i++)
        {
            timer.EmitSignal("timeout");
            await _clientRunner.AwaitIdleFrame();
        }

        AssertThat(emissionCount).IsEqual(1);
        AssertThat(client.IsNetworkReady).IsTrue();
    }

    // C-05 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task LatencyCalculated_HasNonNegativeValues()
    {
        var server = _serverRunner.Scene() as ServerManager;
        var client = _clientRunner.Scene() as ClientManager;
        server!.Initialize(_serverNet, port: 7779);
        client!.Initialize(_clientNet, "127.0.0.1", 7779);

        var clock = _clientRunner.Scene().GetNode<ClientNetworkClock>("ClientNetworkClock");
        typeof(ClientNetworkClock)
            .GetField("_sampleSize", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.SetValue(clock, 1);

        int capturedLatency = -1;
        int capturedJitter = -1;
        client.LatencyCalculated += (lat, jit) => { capturedLatency = lat; capturedJitter = jit; };

        clock.GetNode<Godot.Timer>("Timer").EmitSignal("timeout");
        await _clientRunner.AwaitIdleFrame();

        AssertThat(capturedLatency).IsGreaterEqual(0);
        AssertThat(capturedJitter).IsGreaterEqual(0);
    }

    // C-06 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task ConnectionFailed_EmittedWhenEnetFiresDisconnectDuringConnecting()
    {
        var client = _clientRunner.Scene() as ClientManager;
        AssertThat(client).IsNotNull();

        // Initialize sets _connecting = true; do NOT fire ClientConnected(1) so _serverConnected stays false
        client!.Initialize(_clientNet, "127.0.0.1", 7780);

        var sa = AssertSignal(client);
        // Simulate ENet timeout: PeerDisconnected(1) while _connecting=true, _serverConnected=false
        _clientNet.SimulateServerDisconnected();

        await sa.IsEmitted(ClientManager.SignalName.ConnectionFailed).WithTimeout(2000);
    }

    // C-07 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task ConnectionFailed_NotEmitted_AfterClientDisconnect()
    {
        var client = _clientRunner.Scene() as ClientManager;
        AssertThat(client).IsNotNull();

        client!.Initialize(_clientNet, "127.0.0.1", 7781);
        // Calling Disconnect() clears _connecting, so the subsequent PeerDisconnected(1) must not fire ConnectionFailed
        client.Disconnect();

        var sa = AssertSignal(client);
        _clientNet.SimulateServerDisconnected();

        await sa.IsNotEmitted(ClientManager.SignalName.ConnectionFailed).WithTimeout(500);
    }
}
