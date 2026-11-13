using Godot;

[GlobalClass]
public partial class SoftBodyCollider : Resource
{
	[Export] public bool Enabled { get; set; } = true;
	[Export] public string BoneName { get; set; } = "";
	[Export] public Transform3D LocalTransform { get; set; } = Transform3D.Identity;
	[Export] public Shape3D Shape { get; set; }
}
