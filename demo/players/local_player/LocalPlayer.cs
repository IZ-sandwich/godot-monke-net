using Godot;
using ImGuiNET;

namespace GameDemo;

public partial class LocalPlayer : CharacterBody3D
{
    public override void _Process(double delta)
    {
        if (!GetTree().Root.HasNode("ImGuiRoot")) return;
        var displaySize = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(10f, displaySize.Y - 10f), ImGuiCond.Always, new System.Numerics.Vector2(0f, 1f));
        if (ImGui.Begin("Player Information"))
        {
            ImGui.Text("Use `C` to free/capture your cursor.");
            ImGui.Text($"Position ({GlobalPosition.X:0.00}, {GlobalPosition.Y:0.00}, {GlobalPosition.Z:0.00})");
            ImGui.End();
        }
    }
}
