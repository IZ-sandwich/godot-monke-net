using Godot;

[GlobalClass, Icon("res://addons/monke-net/resources/circle_nodes_solid.png")]
public partial class EntitySpawnConfiguration : Resource
{
    [Export] public byte EntityType { get; set; }
    [Export] public PackedScene ClientAuthorityScene { get; set; }
    [Export] public PackedScene ClientDummyScene { get; set; }
    [Export] public PackedScene ServerScene { get; set; }
}
