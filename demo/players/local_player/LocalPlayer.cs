using Godot;
using ImGuiNET;

namespace GameDemo;

public partial class LocalPlayer : CharacterBody3D
{
    public override void _Process(double delta)
    {
        if (ImGui.Begin("Player Information"))
        {
            ImGui.Text("Use `C` to free/capture your cursor.");
            ImGui.Text($"Position ({GlobalPosition.X:0.00}, {GlobalPosition.Y:0.00}, {GlobalPosition.Z:0.00})");
            ImGui.End();
        }
    }
}
