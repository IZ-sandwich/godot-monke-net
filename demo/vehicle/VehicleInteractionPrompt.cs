using Godot;
using MonkeNet.Client;
using MonkeNet.NetworkMessages;
using MonkeNet.Shared;

namespace GameDemo;

/// <summary>
/// Visual-only prompt for the vehicle interact UX. Drops into LocalVehicle.tscn next
/// to an Area3D + Label3D. Shows "[F] Drive Vehicle" / "[F] Exit Vehicle" when the
/// local player overlaps the area. The actual claim/release happens in
/// <see cref="LocalRigidPlayerPrediction"/>, which edge-triggers on the
/// <c>InputFlags.Interact</c> bit of the player's <see cref="CharacterInputMessage"/>.
/// </summary>
public partial class VehicleInteractionPrompt : Node
{
    [Export] public Area3D Area { get; set; }
    [Export] public Label3D Label { get; set; }
    [Export] public NetworkBehaviour Vehicle { get; set; }

    /// <summary>How close the local player must be to interact (server validates the same).</summary>
    [Export] public float MaxInteractionDistance { get; set; } = 4f;

    public override void _Process(double delta)
    {
        if (Vehicle == null || Area == null || Label == null) return;
        if (ClientManager.Instance == null) { Label.Visible = false; return; }

        if (!IsLocalPlayerInArea())
        {
            Label.Visible = false;
            return;
        }

        bool ownedByMe = Vehicle.Authority == ClientManager.Instance.GetNetworkId();
        Label.Text = ownedByMe ? "[F] Exit Vehicle" : "[F] Drive Vehicle";
        Label.Visible = true;
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
