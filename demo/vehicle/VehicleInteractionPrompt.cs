using Godot;
using MonkeNet.Client;
using MonkeNet.NetworkMessages;
using MonkeNet.Shared;

namespace GameDemo;

/// <summary>
/// Drop into LocalVehicle.tscn and DummyVehicle.tscn alongside an Area3D + Label3D.
/// Detects when the local player is overlapping the area, shows a prompt above the
/// vehicle ("[F] Drive" / "[F] Exit"), and on F press either claims authority via
/// the framework's <see cref="ClientManager.RequestAuthority"/> or releases it via
/// the demo's <see cref="ReleaseVehicleMessage"/>.
///
/// The script lives on both Local and Dummy variants so non-owning clients see the
/// prompt for vehicles owned by someone else (or the server). When ownership flips
/// the framework destroys+respawns the entity into the other scene, which carries
/// its own copy of this prompt — no state-transfer needed.
/// </summary>
public partial class VehicleInteractionPrompt : Node
{
    [Export] public Area3D Area { get; set; }
    [Export] public Label3D Label { get; set; }
    [Export] public NetworkBehaviour Vehicle { get; set; }

    /// <summary>How close the local player must be to interact (server validates the same).</summary>
    [Export] public float MaxInteractionDistance { get; set; } = 4f;

    private bool _wasInteractPressed;
    private bool _initialized;

    public override void _Process(double delta)
    {
        if (Vehicle == null || Area == null || Label == null) return;
        if (ClientManager.Instance == null) { Label.Visible = false; return; }

        // On the very first tick, seed _wasInteractPressed from the current key state.
        // Authority flips destroy+respawn the entity, which gives this prompt a fresh
        // instance — without seeding, a key held across the swap looks like a new press
        // and immediately fires the opposite action (claim → release → claim → ...).
        if (!_initialized)
        {
            _wasInteractPressed = Input.IsKeyPressed(Key.F);
            _initialized = true;
        }

        // Always track key state regardless of proximity. Otherwise leaving and re-entering
        // the area while F is held would look like a fresh edge on re-entry.
        bool pressed = Input.IsKeyPressed(Key.F);
        bool isEdge = pressed && !_wasInteractPressed;
        _wasInteractPressed = pressed;

        bool nearby = IsLocalPlayerInArea();
        if (!nearby)
        {
            Label.Visible = false;
            return;
        }

        bool ownedByMe = Vehicle.Authority == ClientManager.Instance.GetNetworkId();
        Label.Text = ownedByMe ? "[F] Exit Vehicle" : "[F] Drive Vehicle";
        Label.Visible = true;

        if (isEdge)
        {
            if (ownedByMe)
                ClientManager.Instance.SendCommandToServer(
                    new ReleaseVehicleMessage { EntityId = Vehicle.EntityId },
                    INetworkManager.PacketModeEnum.Reliable, (int)ChannelEnum.GameReliable);
            else
                ClientManager.Instance.RequestAuthority(Vehicle.EntityId);
        }
    }

    private bool IsLocalPlayerInArea()
    {
        // EntitySpawner renames every spawned root to its entity ID, so we identify
        // the local player by its NetworkBehaviour authority rather than by node name.
        int myId = ClientManager.Instance.GetNetworkId();
        foreach (var body in Area.GetOverlappingBodies())
        {
            var nb = MonkeNetComponents.GetComponent<NetworkBehaviour>(body);
            if (nb != null && nb.Authority == myId) return true;
        }
        return false;
    }
}
