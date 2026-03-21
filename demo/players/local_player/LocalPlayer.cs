using Godot;
using ImGuiNET;
using MonkeNet.Client;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace GameDemo;

public partial class LocalPlayer : CharacterBody3D, IPredictableEntity
{
    [Export] private float _maxDeviationAllowedSquared = 0.001f;
    [Export] private SharedPlayerMovement _playerMovement;

    public int EntityId { get; set; }
    public byte EntityType { get; set; }
    public int Authority { get; set; }
    public string Metadata { get; set; }
    public void EntitySpawned() { }

    // Called every physics tick (but synced to network clock)
    public void OnProcessTick(int tick, IPackableElement input)
    {
        _playerMovement.AdvancePhysics((CharacterInputMessage)input);
    }

    // We have misspredicted, return player back to authoritative position
    public void HandleReconciliation(IEntityStateData receivedState)
    {
        EntityStateMessage state = (EntityStateMessage)receivedState;
        this.Position = state.Position;
        this.Velocity = state.Velocity;
    }

    // Check if we have misspredicted
    public bool HasMisspredicted(IEntityStateData receivedState, Vector3 savedPosition)
    {
        EntityStateMessage state = (EntityStateMessage)receivedState;
        return (state.Position - savedPosition).LengthSquared() > _maxDeviationAllowedSquared;
    }

    // When the client is re-simulating inputs, what should we do with it? usually the same we do on process tick
    public void ResimulateTick(IPackableElement input)
    {
        _playerMovement.AdvancePhysics((CharacterInputMessage)input);
    }

    public override void _Process(double delta)
    {
        if (ImGui.Begin("Player Information"))
        {
            ImGui.Text("Use `C` to free/capture your cursor.");
            ImGui.Text($"Position ({GlobalPosition.X:0.00}, {GlobalPosition.Y:0.00}, {GlobalPosition.Z:0.00})");
            ImGui.End();
        }
    }

    public void OnReceivedState(IEntityStateData receivedState)
    {

    }
}
