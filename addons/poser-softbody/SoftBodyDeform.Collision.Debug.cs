using System;
using System.Collections.Generic;
using Godot;

public partial class SoftBodyDeform : Node3D
{
	private void UpdateColliderDebug()
	{
		if (!DebugColliders)
		{
			if (_colliderDebugMesh != null && IsInstanceValid(_colliderDebugMesh)) _colliderDebugMesh.Visible = false;
			return;
		}
		if (_colliderDebugMesh == null || !IsInstanceValid(_colliderDebugMesh)) CreateColliderDebug();
		if (_colliderDebugMesh == null) return;
		_colliderDebugMesh.Visible = true;
		_colliderDebugVertices.Clear();
		for (int i = 0; i < _activeColliderCount; i++) AppendColliderDebug(_collisionProxies[i]);
		_colliderDebugMeshResource.ClearSurfaces();
		if (_colliderDebugVertices.Count == 0) return;
		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = Variant.From(_colliderDebugVertices.ToArray());
		_colliderDebugMeshResource.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
	}

	private void CreateColliderDebug()
	{
		if (_outputMesh == null || !IsInstanceValid(_outputMesh)) return;
		_colliderDebugMeshResource = new ArrayMesh();
		_colliderDebugMaterial = new StandardMaterial3D
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			AlbedoColor = new Color(1f, 0.2f, 0.6f),
			NoDepthTest = true
		};
		_colliderDebugMaterial.ResourceLocalToScene = true;
		_colliderDebugMesh = new MeshInstance3D
		{
			Name = "SoftBodyColliders",
			Mesh = _colliderDebugMeshResource,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			MaterialOverride = _colliderDebugMaterial
		};
		_outputMesh.AddChild(_colliderDebugMesh);
	}

	private void AppendColliderDebug(in CollisionProxy collider)
	{
		Basis basis = collider.Transform.Basis;
		Vector3 center = collider.Transform.Origin;
		Vector3 axisX = basis.X;
		Vector3 axisY = basis.Y;
		Vector3 axisZ = basis.Z;
		axisX = axisX.LengthSquared() > 0.000001f ? axisX.Normalized() : Vector3.Right;
		axisY = axisY.LengthSquared() > 0.000001f ? axisY.Normalized() : Vector3.Up;
		axisZ = axisZ.LengthSquared() > 0.000001f ? axisZ.Normalized() : Vector3.Back;
		switch (collider.Kind)
		{
			case ColliderKind.Sphere:
			{
				float radius = collider.Radius * AverageScale(basis);
				AppendCircle(center, axisX, axisY, radius, 16);
				AppendCircle(center, axisX, axisZ, radius, 16);
				AppendCircle(center, axisY, axisZ, radius, 16);
				break;
			}
			case ColliderKind.Capsule:
			{
				float radialScale = RadialScale(basis);
				float radius = collider.Radius * radialScale;
				float halfSegment = Mathf.Max(0f, collider.Height * 0.5f * AxialScale(basis) - collider.Radius * radialScale);
				Vector3 top = center + axisY * halfSegment;
				Vector3 bottom = center - axisY * halfSegment;
				AppendCircle(top, axisX, axisZ, radius, 16);
				AppendCircle(bottom, axisX, axisZ, radius, 16);
				for (int i = 0; i < 4; i++)
				{
					float angle = Mathf.Pi * 0.5f * i;
					Vector3 offset = (axisX * Mathf.Cos(angle) + axisZ * Mathf.Sin(angle)) * radius;
					_colliderDebugVertices.Add(top + offset);
					_colliderDebugVertices.Add(bottom + offset);
				}
				break;
			}
			case ColliderKind.Box:
			{
				Vector3 half = collider.HalfExtents;
				Vector3[] corners = new Vector3[8];
				for (int i = 0; i < 8; i++)
				{
					Vector3 local = new(
						(i & 1) == 0 ? -half.X : half.X,
						(i & 2) == 0 ? -half.Y : half.Y,
						(i & 4) == 0 ? -half.Z : half.Z);
					corners[i] = collider.Transform * local;
				}
				int[] edges = { 0, 1, 0, 2, 0, 4, 1, 3, 1, 5, 2, 3, 2, 6, 3, 7, 4, 5, 4, 6, 5, 7, 6, 7 };
				for (int i = 0; i < edges.Length; i += 2)
				{
					_colliderDebugVertices.Add(corners[edges[i]]);
					_colliderDebugVertices.Add(corners[edges[i + 1]]);
				}
				break;
			}
			case ColliderKind.Cylinder:
			{
				float radialScale = Mathf.Max(0.0001f, (Mathf.Abs(basis.Scale.X) + Mathf.Abs(basis.Scale.Z)) * 0.5f);
				float radius = collider.Radius * radialScale;
				float halfHeight = collider.Height * 0.5f * AxialScale(basis);
				Vector3 top = center + axisY * halfHeight;
				Vector3 bottom = center - axisY * halfHeight;
				AppendCircle(top, axisX, axisZ, radius, 16);
				AppendCircle(bottom, axisX, axisZ, radius, 16);
				for (int i = 0; i < 4; i++)
				{
					float angle = Mathf.Pi * 0.5f * i;
					Vector3 offset = (axisX * Mathf.Cos(angle) + axisZ * Mathf.Sin(angle)) * radius;
					_colliderDebugVertices.Add(top + offset);
					_colliderDebugVertices.Add(bottom + offset);
				}
				break;
			}
		}
	}

	private void AppendCircle(Vector3 center, Vector3 axisA, Vector3 axisB, float radius, int segments)
	{
		if (radius <= 0f || segments < 3) return;
		Vector3 previous = center + axisA * radius;
		for (int i = 1; i <= segments; i++)
		{
			float angle = Mathf.Tau * i / segments;
			Vector3 point = center + (axisA * Mathf.Cos(angle) + axisB * Mathf.Sin(angle)) * radius;
			_colliderDebugVertices.Add(previous);
			_colliderDebugVertices.Add(point);
			previous = point;
		}
	}

}
