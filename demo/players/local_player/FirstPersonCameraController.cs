using Godot;
using MonkeNet.Client;
using MonkeNet.Shared;

namespace GameDemo;

public partial class FirstPersonCameraController : Node3D
{
    [Export] private float _mouseSensitivity = 0.05f;
    [Export] private float _maxVerticalAngle = 90;

    private Node3D _rotationHelperY;
    // Owning entity — cached at spawn time so per-tick ownership checks don't
    // walk the tree. Authority on this NetworkBehaviour can mutate in-place
    // (AuthorityChangedMessage on reclaim, vehicle claim/release, etc.), so
    // we read .Authority each tick rather than caching a bool.
    private NetworkBehaviour _owner;
    // Mirrors the most recent IsForLocalPlayer() result, used so _Process
    // only acts on the transition frame (don't reset MouseMode every frame
    // when the user has manually toggled it via C).
    private bool _wasForLocalPlayer;
    private ClientManager _subscribedManager;
    private Callable _networkReadyCallable;
    private bool _networkReadySubscribed;
    private bool _capturedMouseOnce;

    // Camera yaw and pitch are held on RotationHelperY/Camera and are not part of any
    // synced entity state, so a fresh LocalPlayer spawn after a reclaim would otherwise
    // start facing the default direction. Saved on _ExitTree only when a reclaim is
    // pending (so an ordinary scene close doesn't pollute the next session) and applied
    // one-shot in _Ready of the next LocalPlayer instance.
    private static float _savedYaw;
    private static float _savedPitch;
    private static bool _hasSavedRotation;

    public override void _Ready()
    {
        _rotationHelperY = GetParent<Node3D>();

        // Walk up to the NetworkBehaviour at the entity root. Authority is
        // assigned in EntitySpawner.InitializeEntity BEFORE AddChild fires
        // the children's _Ready chain, so this value is already meaningful
        // here — though it can change later via AuthorityChangedMessage (see
        // _Process for the reactive path).
        _owner = FindOwningNetworkBehaviour();

        if (_hasSavedRotation)
        {
            _hasSavedRotation = false;
            var helperRot = _rotationHelperY.Rotation;
            helperRot.Y = _savedYaw;
            _rotationHelperY.Rotation = helperRot;

            var cameraRot = Rotation;
            cameraRot.X = _savedPitch;
            Rotation = cameraRot;
        }

        // If we're NOT the local player's camera, immediately surrender the
        // Current flag the .tscn set on us. Without this, the second player
        // joining causes their LocalRigidPlayer scene to be instantiated on
        // the FIRST player's client too — and that scene's Camera3D
        // (current=true in the .tscn) would steal first-person view from
        // the local player.
        //
        // The script's class extends Node3D for historical reasons, but
        // it's always attached to a Camera3D node — so we set "current"
        // through Godot's property bag rather than via C# casting (which
        // wouldn't compile because Node3D is the static type).
        if (!IsForLocalPlayer())
        {
            SetCurrent(false);
        }
        _wasForLocalPlayer = IsForLocalPlayer();

        var clientMgr = ClientManager.Instance;
        // Only the local player's camera should grab the OS mouse; the
        // observer copy of someone else's player must keep the cursor free.
        if (IsForLocalPlayer())
        {
            if (clientMgr?.IsNetworkReady == true)
            {
                CaptureMouseUnlessTest();
                _capturedMouseOnce = true;
            }
            else if (clientMgr != null)
            {
                _networkReadyCallable = Callable.From(OnNetworkReady);
                _networkReadySubscribed = true;
                _subscribedManager = clientMgr;
                clientMgr.Connect(ClientManager.SignalName.NetworkReady, _networkReadyCallable);
            }
        }
    }

    // The gdUnit4 test runner spawns a real Godot window, so an unconditional
    // Captured grab steals the user's pointer for the whole test run. run-tests.ps1
    // sets MONKENET_TEST=1 to opt out; production / F5-from-editor leaves it unset.
    private static void CaptureMouseUnlessTest()
    {
        if (!string.IsNullOrEmpty(OS.GetEnvironment("MONKENET_TEST"))) return;
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    // Walks up the scene tree looking for an ancestor whose children include
    // a NetworkBehaviour — that's the entity root. The NetworkBehaviour is a
    // SIBLING node inside the entity (see LocalRigidPlayer.tscn: the
    // ClientPredictedEntity script is on a "Node" child of the rigid-body
    // root, not on the root itself). Direct-ancestor `is NetworkBehaviour`
    // checks miss it. This mirrors how
    // <see cref="MonkeNetComponents.GetComponent{T}"/> locates components on
    // an entity root throughout the framework.
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
        // Pre-handshake or post-disconnect: NetworkId is 0, no entity belongs to us.
        // Treat as non-local rather than asserting — the next frame will re-check.
        if (myId == 0) return false;
        return _owner.Authority == myId;
    }

    // Re-asserts the desired Camera3D.Current state every frame. This is
    // load-bearing in two ways and absolutely cannot early-out on a "did
    // anything change?" check:
    //
    // 1. When a SECOND player joins later, that player's LocalRigidPlayer
    //    scene enters the tree with the .tscn's `current=true` on the
    //    Camera3D — Godot's Viewport sets the new (non-local-to-this-client)
    //    camera as its current. The new controller's own _Ready then
    //    clears its Current, leaving the viewport with NO current camera;
    //    the viewport then falls back to whatever first Camera3D it finds
    //    (in MainScene, that's the orthographic MenuCamera = top-down
    //    view). Re-asserting SetCurrent(true) here brings the local
    //    player's camera back to current on the very next frame — without
    //    this, the local player's view stays stuck on the top-down camera
    //    forever after a second player joins.
    //
    // 2. AuthorityChangedMessage mutates _owner.Authority in place after
    //    spawn (reclaim flips a Authority=0 spawn to our netId; vehicle
    //    claim/release transfers authority back and forth). Per-frame
    //    re-evaluation keeps Current aligned with whoever currently owns
    //    the entity without needing a separate authority-changed signal.
    //
    // SetCurrent is cheap (a property-bag write that routes to Camera3D's
    // _make_current/_clear_current), and the Viewport's internal
    // current-camera pointer is no-op-assigned when already in that state.
    public override void _Process(double delta)
    {
        bool isLocal = IsForLocalPlayer();
        SetCurrent(isLocal);

        if (isLocal && !_capturedMouseOnce
            && ClientManager.Instance?.IsNetworkReady == true)
        {
            CaptureMouseUnlessTest();
            _capturedMouseOnce = true;
        }

        _wasForLocalPlayer = isLocal;
    }

    public override void _ExitTree()
    {
        // Capture camera state only when we're between a server-disconnect and a
        // not-yet-completed reconnect — the same window in which the demo's Reconnect
        // button is shown. Only the local player's controller has a meaningful
        // rotation to preserve; the dummy-side controller's helper rotation reflects
        // the OBSERVED other player and must not pollute the next session.
        if (ClientEntityManager.IsAwaitingReconnect
            && _wasForLocalPlayer
            && _rotationHelperY != null && IsInstanceValid(_rotationHelperY))
        {
            _savedYaw = _rotationHelperY.Rotation.Y;
            _savedPitch = Rotation.X;
            _hasSavedRotation = true;
        }

        // Releasing the mouse on exit is only correct if WE were the controller
        // that captured it. A dummy-player controller exiting (e.g. that player
        // disconnected) must not free the cursor of the local-player controller
        // still running.
        if (_wasForLocalPlayer) Input.MouseMode = Input.MouseModeEnum.Visible;
        if (_networkReadySubscribed && _subscribedManager != null && IsInstanceValid(_subscribedManager))
        {
            _subscribedManager.Disconnect(ClientManager.SignalName.NetworkReady, _networkReadyCallable);
            _networkReadySubscribed = false;
        }
        _subscribedManager = null;
    }

    private void OnNetworkReady()
    {
        if (IsForLocalPlayer() && !_capturedMouseOnce)
        {
            CaptureMouseUnlessTest();
            _capturedMouseOnce = true;
        }
        if (_networkReadySubscribed && _subscribedManager != null)
        {
            _subscribedManager.Disconnect(ClientManager.SignalName.NetworkReady, _networkReadyCallable);
            _networkReadySubscribed = false;
            _subscribedManager = null;
        }
    }

    public override void _Input(InputEvent @event)
    {
        // Non-local controllers must not consume input. Without this gate the
        // second player's LocalRigidPlayer scene (instantiated on the FIRST
        // player's client because every client predicts every entity) would
        // also receive InputEventKey events — pressing C would fire toggle in
        // BOTH controllers, netting zero change, which is exactly the
        // "C does nothing on the second player's client" symptom.
        if (!IsForLocalPlayer()) return;

        if (Input.MouseMode == Input.MouseModeEnum.Captured && @event is InputEventMouseMotion mouseMotionEvent)
        {
            RotateX(-Mathf.DegToRad(mouseMotionEvent.Relative.Y * _mouseSensitivity));
            _rotationHelperY.RotateY(Mathf.DegToRad(-mouseMotionEvent.Relative.X * _mouseSensitivity));

            Vector3 cameraRot = RotationDegrees;
            cameraRot.X = Mathf.Clamp(cameraRot.X, -_maxVerticalAngle, _maxVerticalAngle);
            RotationDegrees = cameraRot;

            // Camera is transformed outside _PhysicsProcess; reset so Godot's built-in
            // physics interpolation doesn't lerp back to the previous physics-frame pose.
            ResetPhysicsInterpolation();
            _rotationHelperY.ResetPhysicsInterpolation();
        }

        if (@event is InputEventKey keyEvent)
        {
            if (keyEvent.Keycode == Key.C && keyEvent.Pressed && ClientManager.Instance?.IsNetworkReady == true)
            {
                if (Input.MouseMode == Input.MouseModeEnum.Visible)
                    CaptureMouseUnlessTest();
                else
                    Input.MouseMode = Input.MouseModeEnum.Visible;
            }
        }

    }

    // Toggles the Camera3D "current" property without requiring the script's
    // static type to be Camera3D. The script is attached to a Camera3D node in
    // LocalRigidPlayer.tscn but declared as Node3D, so a direct C# cast won't
    // compile; setting via the Godot property bag does what we need.
    private void SetCurrent(bool current)
    {
        Set(Camera3D.PropertyName.Current, current);
    }

    public float GetLateralRotationAngle()
    {
        return _rotationHelperY.Rotation.Y;
    }

    public void RotateCameraYaw(float amount)
    {
        _rotationHelperY.RotateY(amount);
    }
}
