using System;
using Godot;

/// <summary>
/// Global environment forces applied to every <see cref="SoftBodyDeform"/> instance.
/// Useful for wind, gravity overrides or impulse fields shared across a scene.
/// The <see cref="SoftBodyDeform"/> simulation reads <see cref="Force"/> once per step; the per-node
/// <see cref="SoftBodyDeform.ExternalForce"/> export is added on top of it.
/// </summary>
public static class SoftBodyEnvironment
{
	/// <summary>Current global external force in m/s² (or any unit consistent with your gravity setting).</summary>
	public static Vector3 Force { get; private set; } = Vector3.Zero;

	/// <summary>Raised whenever <see cref="SetForce"/> changes the global force.</summary>
	public static event Action<Vector3> ForceChanged;

	public static void SetForce(Vector3 force)
	{
		Force = force;
		ForceChanged?.Invoke(force);
	}
}
