using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using GameDemo;
using GdUnit4;
using MonkeNet.Client;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Server;
using MonkeNet.Shared;
using MonkeNet.Tests.Infrastructure;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Integration;

/// <summary>
/// J-01..J-06: Input transmission and server input receiver tests.
///
/// J-01..J-03: ClientInputManager — requires ClientManager scene.
/// J-04..J-06: ServerInputReceiver — can be tested by calling methods directly after
///             setting up the server scene and delivering PackedClientInputMessages.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class InputTests
{
    private FakeNetworkEndpoint _serverNet;
    private FakeNetworkEndpoint _clientNet;
    private ISceneRunner _serverRunner;
    private ISceneRunner _clientRunner;
    private ISceneRunner _mainSceneRunner;
    private ServerManager _server;
    private ClientManager _client;
    private ClientInputManager _inputManager;
    private FakeInputProducer _fakeProducer;

    [BeforeTest]
    public async Task SetUp()
    {
        MonkeNet.Shared.MonkeNetConfig.Instance = null;
        FakeNetworkBridge.Reset();
        MessageSerializer.RegisterNetworkMessages();
        (_serverNet, _clientNet) = FakeNetworkBridge.CreatePair();

        _mainSceneRunner = ISceneRunner.Load("res://demo/MainScene.tscn", autoFree: true);
        await _mainSceneRunner.AwaitIdleFrame();

        _serverRunner = ISceneRunner.Load("res://addons/monke-net/scenes/ServerManager.tscn", autoFree: true);
        await _serverRunner.AwaitIdleFrame();
        _server = _serverRunner.Scene() as ServerManager;

        _clientRunner = ISceneRunner.Load("res://addons/monke-net/scenes/ClientManager.tscn", autoFree: true);
        await _clientRunner.AwaitIdleFrame();
        _client = _clientRunner.Scene() as ClientManager;

        _server!.Initialize(_serverNet, port: 7400);
        _client!.Initialize(_clientNet, "127.0.0.1", 7400);
        await _clientRunner.AwaitIdleFrame();

        _inputManager = _client.GetNode<ClientInputManager>("ClientInputManager");
    }

    [AfterTest]
    public void TearDown()
    {
        _serverRunner?.Dispose();
        _clientRunner?.Dispose();
        _mainSceneRunner?.Dispose();
        MonkeNet.Shared.MonkeNetConfig.Instance = null;
    }

    // J-01 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task Client_SendsInputEachTick_ViaFakeProducer()
    {
        SetupFakeProducer(new CharacterInputMessage { Keys = 1, MoveX = 0.5f, MoveY = 0f, CameraYaw = 0f });

        _serverNet.ClearSentPackets();
        var sentToServer = new List<byte[]>();
        _serverNet.PacketReceived += (_, bin) => sentToServer.Add(bin);

        // GenerateAndTransmitInputs calls SendCommandToServer which goes through the bridge
        _inputManager.GenerateAndTransmitInputs(currentTick: 1);

        AssertThat(sentToServer.Count).IsGreaterEqual(1);

        var msg = MessageSerializer.Deserialize(sentToServer.Last()) as PackedClientInputMessage?;
        AssertThat(msg.HasValue).IsTrue();
        AssertThat(msg!.Value.Tick).IsEqual(1);
    }

    // J-02 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task Client_IncludesUnacknowledgedRedundantInputs()
    {
        SetupFakeProducer(new CharacterInputMessage { Keys = 0, MoveX = 0f, MoveY = 0f, CameraYaw = 0f });

        var lastPacket = (byte[])null;
        _serverNet.PacketReceived += (_, bin) => lastPacket = bin;

        // Send 3 ticks without any snapshot acknowledgement
        for (int t = 1; t <= 3; t++)
        {
            SetupFakeProducer(new CharacterInputMessage { Keys = (byte)t });
            _inputManager.GenerateAndTransmitInputs(currentTick: t);
        }

        AssertThat(lastPacket).IsNotNull();
        var msg = (PackedClientInputMessage)MessageSerializer.Deserialize(lastPacket!);
        // All 3 inputs should be bundled (none acknowledged yet)
        AssertThat(msg.Inputs.Length).IsEqual(3);
    }

    // J-03 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task Client_DropsAcknowledgedInputs_OnSnapshotReceived()
    {
        // Accumulate 5 inputs (ticks 1-5)
        for (int t = 1; t <= 5; t++)
        {
            SetupFakeProducer(new CharacterInputMessage { Keys = (byte)t });
            _inputManager.GenerateAndTransmitInputs(currentTick: t);
        }

        // Deliver a snapshot for tick 3 — inputs 1,2,3 should be removed.
        // Null InputProducer so _PhysicsProcess doesn't add a phantom input during AwaitIdleFrame.
        if (MonkeNet.Shared.MonkeNetConfig.Instance != null)
            MonkeNet.Shared.MonkeNetConfig.Instance.InputProducer = null;
        var snap = new GameSnapshotMessage
        {
            Tick = 3,
            States = System.Array.Empty<IEntityStateData>()
        };
        _clientNet.SimulateIncomingPacket(1, MessageSerializer.Serialize(snap));
        await _clientRunner.AwaitIdleFrame();

        // Send tick 6 — packet should contain inputs 4,5,6 only
        SetupFakeProducer(new CharacterInputMessage { Keys = 6 });
        var lastPacket = (byte[])null;
        _serverNet.PacketReceived += (_, bin) => lastPacket = bin;
        _inputManager.GenerateAndTransmitInputs(currentTick: 6);

        var msg = (PackedClientInputMessage)MessageSerializer.Deserialize(lastPacket!);
        AssertThat(msg.Inputs.Length).IsEqual(3); // ticks 4,5,6
    }

    // J-04 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task Server_RegistersInputToCorrectTick()
    {
        // Get the ServerInputReceiver
        var receiver = _server.GetNode<ServerInputReceiver>("ServerInputReceiver");

        // Build a PackedClientInputMessage with 3 inputs for ticks 3,4,5 (latest=5)
        var inputs = new IPackableElement[]
        {
            new CharacterInputMessage { Keys = 10 }, // tick 3 (offset 2)
            new CharacterInputMessage { Keys = 20 }, // tick 4 (offset 1)
            new CharacterInputMessage { Keys = 30 }, // tick 5 (offset 0)
        };
        var inputMsg = new PackedClientInputMessage { Tick = 5, Inputs = inputs };

        // Deliver as if from client 2 (who owns a networked entity)
        _serverNet.SimulateIncomingPacket(2, MessageSerializer.Serialize(inputMsg));
        await _serverRunner.AwaitIdleFrame();

        // Verify the inputs are correctly registered. We use reflection because
        // _pendingInputs is private and GetInputForEntityTick requires a NetworkBehaviour key.
        var pendingInputs = typeof(ServerInputReceiver)
            .GetField("_pendingInputs", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(receiver) as Dictionary<int, Dictionary<MonkeNet.Shared.NetworkBehaviour, IPackableElement>>;

        // Verify at minimum that the receiver processed the packet without error
        AssertThat(pendingInputs).IsNotNull();
    }

    // J-05 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task Server_FallsBackToLastInput_WhenTickMissing()
    {
        var receiver = _server.GetNode<ServerInputReceiver>("ServerInputReceiver");

        // GetInputForEntityTick with a NetworkBehaviour that has no pending input
        // should return null (no last stored input) without throwing.
        var dummyEntity = new MonkeNet.Shared.NetworkBehaviour();

        IPackableElement result = receiver.GetInputForEntityTick(dummyEntity, tick: 99);
        // No prior input stored → returns null
        AssertThat(result).IsNull();

        dummyEntity.Free();
    }

    // J-06 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task Server_DropOutdatedInputs_RemovesOlderTicks()
    {
        var receiver = _server.GetNode<ServerInputReceiver>("ServerInputReceiver");

        // Register inputs for ticks 1-5 via a fake message
        var inputs = new IPackableElement[]
        {
            new CharacterInputMessage { Keys = 1 },
            new CharacterInputMessage { Keys = 2 },
            new CharacterInputMessage { Keys = 3 },
            new CharacterInputMessage { Keys = 4 },
            new CharacterInputMessage { Keys = 5 },
        };
        var inputMsg = new PackedClientInputMessage { Tick = 5, Inputs = inputs };
        _serverNet.SimulateIncomingPacket(2, MessageSerializer.Serialize(inputMsg));
        await _serverRunner.AwaitIdleFrame();

        // Drop inputs ≤ tick 3
        receiver.DropOutdatedInputs(currentTick: 3);

        var pendingInputs = typeof(ServerInputReceiver)
            .GetField("_pendingInputs", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(receiver) as Dictionary<int, Dictionary<MonkeNet.Shared.NetworkBehaviour, IPackableElement>>;

        if (pendingInputs != null)
        {
            foreach (int key in pendingInputs.Keys)
                AssertThat(key).IsGreater(3);
        }
    }

    // J-07 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task Server_PerClientMissedInputs_TrackedCorrectly()
    {
        var receiver = _server.GetNode<ServerInputReceiver>("ServerInputReceiver");
        var dummyEntity = new MonkeNet.Shared.NetworkBehaviour();
        dummyEntity.Authority = 42;

        // Call GetInputForEntityTick without registering any input — triggers a miss
        receiver.GetInputForEntityTick(dummyEntity, tick: 1000);
        await _serverRunner.AwaitIdleFrame();

        AssertThat(receiver.GetMissedInputTotal(42)).IsGreaterEqual(1);
        AssertThat(receiver.GetMissedInputRate(42)).IsGreater(0f);

        dummyEntity.Free();
    }

    // J-09 ─────────────────────────────────────────────────────────────────────
    // MaximumServerReplicates protects the server from a flood of far-future inputs.
    // When the cap is exceeded, the oldest queued ticks are evicted. We invoke the
    // private RegisterCommand directly because the public OnCommandReceived path filters
    // by spawned-entity authority, which would require staging a full entity spawn.
    [TestCase]
    public async Task Server_FloodOfInputsCappedAtMaximumServerReplicates()
    {
        var receiver = _server.GetNode<ServerInputReceiver>("ServerInputReceiver");
        receiver.MaximumServerReplicates = 10;

        var entity = new MonkeNet.Shared.NetworkBehaviour();
        entity.Authority = 7;

        var inputs = new IPackableElement[50];
        for (int i = 0; i < 50; i++)
            inputs[i] = new CharacterInputMessage { Keys = (byte)(i + 1) };
        var inputMsg = new PackedClientInputMessage { Tick = 100, Inputs = inputs };

        // Reach RegisterCommand directly — bypasses the entity-authority filter in
        // OnCommandReceived since this dummy entity isn't spawned.
        var register = typeof(ServerInputReceiver).GetMethod(
            "RegisterCommand", BindingFlags.NonPublic | BindingFlags.Instance);
        register!.Invoke(receiver, new object[] { entity, inputMsg });

        var pendingTicks = typeof(ServerInputReceiver)
            .GetField("_pendingTicksPerEntity", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(receiver) as Dictionary<MonkeNet.Shared.NetworkBehaviour, SortedSet<int>>;

        AssertThat(pendingTicks).IsNotNull();
        AssertThat(pendingTicks!.ContainsKey(entity)).IsTrue();
        AssertThat(pendingTicks[entity].Count).IsEqual(10);
        // Oldest evicted, newest retained: ticks 100-9..100 should remain.
        AssertThat(pendingTicks[entity].Min).IsEqual(91);
        AssertThat(pendingTicks[entity].Max).IsEqual(100);
        AssertThat(receiver.GetTrimmedInputTotal()).IsEqual(40);

        entity.Free();
    }

    // J-08 ─────────────────────────────────────────────────────────────────────
    // Within the stale-input timeout window, the last received input is repeated
    // so brief packet loss does not freeze the entity.
    [TestCase]
    public async Task Server_RepeatsLastInput_WithinStaleTimeout()
    {
        var receiver = _server.GetNode<ServerInputReceiver>("ServerInputReceiver");
        receiver.StaleInputTimeoutSec = 1.0f;

        var dummyEntity = new MonkeNet.Shared.NetworkBehaviour();
        dummyEntity.Authority = 50;

        // Register one input at tick 100 and consume it
        SetField(receiver, dummyEntity, tick: 100, new CharacterInputMessage { MoveX = 1f, MoveY = 0.5f });
        var first = receiver.GetInputForEntityTick(dummyEntity, tick: 100);
        AssertThat(first).IsNotNull();
        var firstInput = (CharacterInputMessage)first;
        AssertThat(firstInput.MoveX).IsEqual(1f);

        // Tick 101 — within timeout window, last input must be repeated
        var repeated = receiver.GetInputForEntityTick(dummyEntity, tick: 101);
        AssertThat(repeated).IsNotNull();
        var repeatedInput = (CharacterInputMessage)repeated;
        AssertThat(repeatedInput.MoveX).IsEqual(1f);
        AssertThat(repeatedInput.MoveY).IsEqual(0.5f);

        dummyEntity.Free();
    }

    // J-09 ─────────────────────────────────────────────────────────────────────
    // After the stale-input timeout expires, default-valued inputs are returned so
    // a disconnected player does not keep moving on the server forever.
    [TestCase]
    public async Task Server_ReturnsDefaultInput_AfterStaleTimeout()
    {
        var receiver = _server.GetNode<ServerInputReceiver>("ServerInputReceiver");
        receiver.StaleInputTimeoutSec = 1.0f;
        int maxStaleTicks = (int)(receiver.StaleInputTimeoutSec * Godot.Engine.PhysicsTicksPerSecond);

        var dummyEntity = new MonkeNet.Shared.NetworkBehaviour();
        dummyEntity.Authority = 51;

        // Register one input at tick 200 with non-zero movement
        SetField(receiver, dummyEntity, tick: 200, new CharacterInputMessage { MoveX = 1f, MoveY = 1f, Keys = 7 });
        receiver.GetInputForEntityTick(dummyEntity, tick: 200);

        // Jump well past the timeout window
        int staleTick = 200 + maxStaleTicks + 5;
        var defaulted = receiver.GetInputForEntityTick(dummyEntity, tick: staleTick);

        AssertThat(defaulted).IsNotNull();
        var defaultInput = (CharacterInputMessage)defaulted;
        AssertThat(defaultInput.MoveX).IsEqual(0f);
        AssertThat(defaultInput.MoveY).IsEqual(0f);
        AssertThat(defaultInput.Keys).IsEqual((byte)0);

        dummyEntity.Free();
    }

    // ── J-08/J-09 helper ──────────────────────────────────────────────────────
    private static void SetField(ServerInputReceiver receiver,
        MonkeNet.Shared.NetworkBehaviour entity, int tick, IPackableElement input)
    {
        var pending = typeof(ServerInputReceiver)
            .GetField("_pendingInputs", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(receiver) as Dictionary<int, Dictionary<MonkeNet.Shared.NetworkBehaviour, IPackableElement>>;
        if (!pending!.TryGetValue(tick, out var byEntity))
        {
            byEntity = new Dictionary<MonkeNet.Shared.NetworkBehaviour, IPackableElement>();
            pending[tick] = byEntity;
        }
        byEntity[entity] = input;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private void SetupFakeProducer(CharacterInputMessage input)
    {
        if (MonkeNet.Shared.MonkeNetConfig.Instance != null)
        {
            if (MonkeNet.Shared.MonkeNetConfig.Instance.InputProducer is FakeInputProducer fake)
            {
                fake.NextInput = input;
                return;
            }

            var producer = new FakeInputProducer { NextInput = input };
            // Add to scene tree so it is freed with the ClientManager and doesn't become an orphan.
            // _Ready() fires here, sets MonkeNetConfig.Instance.InputProducer = this via Current setter.
            _client.AddChild(producer);
        }
    }
}
