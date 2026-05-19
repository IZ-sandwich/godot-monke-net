using GameDemo;
using Godot;
using MonkeNet.Client;
using MonkeNet.Serializer;
using MonkeNet.Shared;

public partial class PlayerInputProducer : InputProducerComponent
{
    [Export] private FirstPersonCameraController _cameraController;

    // The NetworkBehaviour at the entity root that owns this input producer.
    // Cached at _Ready, used per-tick to decide whether we should currently
    // be acting as MonkeNetConfig.Instance.InputProducer.
    private NetworkBehaviour _owner;

    public override void _Ready()
    {
        _owner = FindOwningNetworkBehaviour();

        // Under the unified-prediction model EVERY client instantiates this
        // scene for every player entity — including the dummies for OTHER
        // clients' players. If we let every spawned PlayerInputProducer call
        // base._Ready() (which assigns MonkeNetConfig.Instance.InputProducer
        // = this), the most recently joined player's input producer would
        // hijack the global slot, and the local player's keyboard inputs
        // would be read through the WRONG camera's yaw / sent for the wrong
        // entity. We only let the LOCAL player's producer take the global
        // slot; the per-tick _Process re-checks ownership so the slot also
        // tracks authority changes (e.g. reclaim flipping Authority to us
        // after the entity was spawned with Authority=0).
        //
        // Test-harness mode (MONKENET_TEST=1) opts out entirely: the
        // multi-process / in-process test rigs install their own
        // deterministic input producer (HarnessInputProducer / FakeInputProducer)
        // and we must NOT race them. Without this opt-out, the few physics
        // ticks between entity spawn and the test's set-input-schedule call
        // would let PlayerInputProducer poll Input.GetAxis on the windowed
        // test instance — reading real OS keyboard state — and send
        // non-deterministic inputs that bleed into the test's assertions.
        if (IsInTestMode()) return;

        if (IsForLocalPlayer())
        {
            base._Ready();
        }
    }

    private static bool IsInTestMode() =>
        !string.IsNullOrEmpty(OS.GetEnvironment("MONKENET_TEST"));

    public override void _Process(double delta)
    {
        if (IsInTestMode()) return;
        if (MonkeNetConfig.Instance == null) return;
        bool isLocal = IsForLocalPlayer();
        var current = MonkeNetConfig.Instance.InputProducer;

        if (isLocal && current == null)
        {
            // Slot is empty: this happens after authority transitions to us
            // mid-game (reclaim spawns the entity with Authority=0, then a
            // followup AuthorityChangedMessage flips it to our netId — at
            // _Ready time we were not yet the owner, so we skipped
            // base._Ready and the slot stayed null). Take it.
            //
            // We deliberately DO NOT steal a non-null slot: an integration
            // test or game-mode swap may install a different producer
            // (e.g. HarnessInputProducer, a replay-driver, a remote-control
            // overlay) and that producer needs to remain active. The slot
            // only goes back to null when the previous producer is freed
            // (its _ExitTree clears the slot), which is the signal we wait
            // for.
            MonkeNetConfig.Instance.InputProducer = this;
        }
        else if (!isLocal && (GodotObject)current == this)
        {
            // We hold the slot but our entity is no longer (or never was)
            // ours — e.g. authority was transferred away from us via
            // AuthorityChangedMessage. Surrender the slot; the new owner's
            // producer (or anything else) will take over.
            MonkeNetConfig.Instance.InputProducer = null;
        }
    }

    // Walks up the scene tree looking for an ancestor whose children include a
    // NetworkBehaviour — that's the entity root. The NetworkBehaviour is a
    // SIBLING node inside the entity (see LocalRigidPlayer.tscn:
    // ClientPredictedEntity is a "Node" child of the rigid-body root, not on
    // the root itself), so a straight `is NetworkBehaviour` walk would never
    // find it. Mirrors how MonkeNetComponents.GetComponent locates components
    // elsewhere in the framework.
    private NetworkBehaviour FindOwningNetworkBehaviour()
    {
        Node n = GetParent();
        while (n != null)
        {
            foreach (Node child in n.GetChildren())
            {
                if (child is NetworkBehaviour nb) return nb;
            }
            n = n.GetParent();
        }
        return null;
    }

    private bool IsForLocalPlayer()
    {
        if (_owner == null) return false;
        var cm = ClientManager.Instance;
        if (cm == null) return false;
        int myId = cm.GetNetworkId();
        if (myId == 0) return false;
        return _owner.Authority == myId;
    }

    public override IPackableElement GenerateCurrentInput()
    {
        // Hold at zero movement until the clock is synced; movement before that causes a
        // snap-back to spawn position when the first authoritative snapshot is reconciled.
        if (!ClientManager.Instance.IsNetworkReady)
            return new CharacterInputMessage { CameraYaw = _cameraController.GetLateralRotationAngle() };

        float moveX = Input.GetAxis("left", "right");
        float moveY = Input.GetAxis("forward", "backward");
        byte keys = GetCurrentPressedKeys();
        // if (moveX != 0 || moveY != 0 || keys != 0)
        //     MonkeLogger.Info($"[InputProducer] MoveX={moveX:0.00} MoveY={moveY:0.00} Keys={keys} Yaw={_cameraController.GetLateralRotationAngle():0.00}");
        return new CharacterInputMessage
        {
            MoveX = moveX,
            MoveY = moveY,
            Keys = keys,
            CameraYaw = _cameraController.GetLateralRotationAngle()
        };
    }

    private static byte GetCurrentPressedKeys()
    {
        byte keys = 0;
        if (Input.IsActionPressed("space")) keys |= (byte)InputFlags.Space;
        if (Input.IsActionPressed("shift")) keys |= (byte)InputFlags.Shift;
        // F = interact (claim/release a nearby vehicle). No mapped action — the demo
        // reads the key directly. LocalRigidPlayerPrediction edge-triggers on it.
        if (Input.IsKeyPressed(Key.F)) keys |= (byte)InputFlags.Interact;
        return keys;
    }
}
