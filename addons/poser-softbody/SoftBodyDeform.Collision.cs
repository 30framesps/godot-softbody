using System;
using System.Collections.Generic;
using Godot;

public partial class SoftBodyDeform : Node3D
{
	private void BuildColliders()
	{
		var sources = new List<CollisionSource>();
		var seen = new HashSet<ulong>();
		if (Colliders != null)
		{
			foreach (SoftBodyCollider entry in Colliders)
			{
				if (entry == null) continue;
				int bone = -1;
				if (!string.IsNullOrEmpty(entry.BoneName))
				{
					bone = _skeleton != null && IsInstanceValid(_skeleton) ? _skeleton.FindBone(entry.BoneName) : -1;
					if (bone < 0)
					{
						GD.PushWarning($"SoftBodyDeform: collider bone '{entry.BoneName}' was not found.");
						continue;
					}
				}
				sources.Add(new CollisionSource
				{
					IsNode = false,
					Resource = entry,
					Bone = bone
				});
			}
		}
		if (ColliderPaths != null)
		{
			foreach (NodePath path in ColliderPaths)
			{
				if (path.IsEmpty) continue;
				CollisionShape3D node = GetNodeOrNull<CollisionShape3D>(path);
				if (node == null) node = GetParent()?.GetNodeOrNull<CollisionShape3D>(path);
				if (node == null)
				{
					GD.PushWarning($"SoftBodyDeform: collider path '{path}' did not resolve to a CollisionShape3D.");
					continue;
				}
				AddCollisionNode(sources, seen, node);
			}
		}
		if (AutoCollectColliders)
		{
			CollectCollisionNodes(GetParent(), sources, seen);
		}
		if (AdditionalColliderRoots != null)
		{
			foreach (NodePath rootPath in AdditionalColliderRoots)
			{
				if (rootPath.IsEmpty) continue;
				Node root = GetNodeOrNull<Node>(rootPath) ?? GetParent()?.GetNodeOrNull<Node>(rootPath);
				if (root == null)
				{
					GD.PushWarning($"SoftBodyDeform: collider root '{rootPath}' did not resolve to a node.");
					continue;
				}
				CollectCollisionNodes(root, sources, seen);
			}
		}
		_collisionSources = sources.ToArray();
		_collisionProxies = new CollisionProxy[Mathf.Max(1, _collisionSources.Length)];
		_activeColliderCount = 0;
		RefreshCollisionProxies();
		if (_collisionSources.Length > 0)
			GD.Print($"SoftBodyDeform: {_activeColliderCount} active of {_collisionSources.Length} colliders.");
	}

	private static void AddCollisionNode(List<CollisionSource> sources, HashSet<ulong> seen, CollisionShape3D node)
	{
		if (node == null || node.Shape == null) return;
		if (!seen.Add(node.GetInstanceId())) return;
		sources.Add(new CollisionSource
		{
			IsNode = true,
			Node = node,
			Bone = -1
		});
	}

	private static void CollectCollisionNodes(Node node, List<CollisionSource> sources, HashSet<ulong> seen)
	{
		if (node == null) return;
		if (node is CollisionShape3D shape) AddCollisionNode(sources, seen, shape);
		foreach (Node child in node.GetChildren()) CollectCollisionNodes(child, sources, seen);
	}

	/// <summary>
	/// Queues a deferred collider rebuild. Call this after adding or removing
	/// collider nodes/primitives so the simulation picks them up next physics tick.
	/// </summary>
	public void QueueColliderRebuild()
	{
		if (!_initialized || _colliderRebuildQueued) return;
		_colliderRebuildQueued = true;
		CallDeferred(nameof(ProcessColliderRebuild));
	}

	private void ProcessColliderRebuild()
	{
		_colliderRebuildQueued = false;
		if (_initialized) BuildColliders();
	}

	private void RefreshCollisionProxies()
	{
		_activeColliderCount = 0;
		if (!CollisionEnabled || _collisionSources.Length == 0)
		{
			UpdateColliderDebug();
			return;
		}
		if (_collisionProxies.Length < _collisionSources.Length)
			_collisionProxies = new CollisionProxy[_collisionSources.Length];
		for (int i = 0; i < _collisionSources.Length; i++)
		{
			CollisionSource source = _collisionSources[i];
			Transform3D transform;
			Shape3D shape;
			if (source.IsNode)
			{
				CollisionShape3D node = source.Node;
				if (node == null || !IsInstanceValid(node) || node.Disabled || node.Shape == null) continue;
				transform = node.GlobalTransform;
				shape = node.Shape;
			}
			else
			{
				SoftBodyCollider entry = source.Resource;
				if (entry == null || !entry.Enabled || entry.Shape == null) continue;
				Transform3D baseTransform = source.Bone >= 0 && _skeleton != null && IsInstanceValid(_skeleton)
					? _skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(source.Bone)
					: _sourceMesh.GlobalTransform;
				transform = baseTransform * entry.LocalTransform;
				shape = entry.Shape;
			}
			if (!TryReadShape(shape, out ColliderKind kind, out float radius, out float height, out Vector3 halfExtents)) continue;
			_collisionProxies[_activeColliderCount] = new CollisionProxy
			{
				Kind = kind,
				Transform = transform,
				HalfExtents = halfExtents,
				Radius = radius,
				Height = height
			};
			_activeColliderCount++;
		}
		UpdateColliderDebug();
	}

	private static bool TryReadShape(Shape3D shape, out ColliderKind kind, out float radius, out float height, out Vector3 halfExtents)
	{
		kind = ColliderKind.Sphere;
		radius = 0f;
		height = 0f;
		halfExtents = Vector3.Zero;
		switch (shape)
		{
			case SphereShape3D sphere:
				kind = ColliderKind.Sphere;
				radius = Mathf.Max(0f, sphere.Radius);
				return true;
			case CapsuleShape3D capsule:
				kind = ColliderKind.Capsule;
				radius = Mathf.Max(0f, capsule.Radius);
				height = Mathf.Max(radius * 2f, capsule.Height);
				return true;
			case BoxShape3D box:
				kind = ColliderKind.Box;
				halfExtents = box.Size.Abs() * 0.5f;
				return true;
			case CylinderShape3D cylinder:
				kind = ColliderKind.Cylinder;
				radius = Mathf.Max(0f, cylinder.Radius);
				height = Mathf.Max(0f, cylinder.Height);
				return true;
			default:
				return false;
		}
	}

	private static float AverageScale(in Basis basis)
	{
		Vector3 scale = basis.Scale;
		return Mathf.Max(0.0001f, (Mathf.Abs(scale.X) + Mathf.Abs(scale.Y) + Mathf.Abs(scale.Z)) / 3f);
	}

	private static float RadialScale(in Basis basis)
	{
		Vector3 scale = basis.Scale;
		return Mathf.Max(0.0001f, (Mathf.Abs(scale.X) + Mathf.Abs(scale.Z)) * 0.5f);
	}

	private static float AxialScale(in Basis basis)
	{
		return Mathf.Max(0.0001f, Mathf.Abs(basis.Scale.Y));
	}

	private void ResolveCollisions()
	{
		float margin = Mathf.Max(0f, CollisionMargin);
		float strength = Mathf.Clamp(CollisionStrength, 0f, 1f);
		if (strength <= 0f || _collisionOffsets == null) return;
		Array.Clear(_collisionOffsets, 0, _pointCount);
		bool anyContact = false;
		for (int p = 0; p < _pointCount; p++)
		{
			if (_goalWeights[p] >= 0.999f) continue;
			Vector3 point = _points[p];
			Vector3 total = Vector3.Zero;
			Vector3 contactNormal = Vector3.Zero;
			bool contacted = false;
			for (int c = 0; c < _activeColliderCount; c++)
			{
				if (!ResolveCollision(_collisionProxies[c], point, margin, out Vector3 correction, out Vector3 normal)) continue;
				point += correction;
				total += correction;
				contactNormal = normal;
				contacted = true;
			}
			if (!contacted) continue;
			_collisionOffsets[p] = total;
			_contactNormals[p] = contactNormal;
			_contacted[p] = true;
			anyContact = true;
		}
		if (!anyContact) return;
		SmoothCollisionOffsets();
		for (int p = 0; p < _pointCount; p++)
		{
			Vector3 offset = _collisionOffsets[p];
			if (offset.LengthSquared() <= 0.0000000001f) continue;
			_points[p] += offset * strength;
		}
	}

	private void SmoothCollisionOffsets()
	{
		int iterations = Mathf.Clamp(CollisionSmoothingIterations, 0, 4);
		float spread = Mathf.Clamp(CollisionSpread, 0f, 1f);
		if (iterations <= 0 || spread <= 0f || _neighborStart == null || _neighborPoints == null) return;
		Vector3[] source = _collisionOffsets;
		Vector3[] target = _collisionOffsetBuffer;
		for (int iteration = 0; iteration < iterations; iteration++)
		{
			for (int p = 0; p < _pointCount; p++)
			{
				int start = _neighborStart[p];
				int end = _neighborStart[p + 1];
				if (end <= start)
				{
					target[p] = source[p];
					continue;
				}
				Vector3 sum = Vector3.Zero;
				for (int i = start; i < end; i++) sum += source[_neighborPoints[i]];
				Vector3 average = sum / (end - start);
				target[p] = source[p].Lerp(average, spread);
			}
			(source, target) = (target, source);
		}
		if (!ReferenceEquals(source, _collisionOffsets))
			Array.Copy(source, _collisionOffsets, _pointCount);
	}

	private void ApplyContactFriction()
	{
		float friction = Mathf.Clamp(CollisionFriction, 0f, 1f);
		if (friction <= 0f || _contacted == null) return;
		for (int p = 0; p < _pointCount; p++)
		{
			if (!_contacted[p] || _goalWeights[p] >= 0.999f) continue;
			Vector3 normal = _contactNormals[p];
			Vector3 velocity = _points[p] - _previous[p];
			Vector3 tangential = velocity - normal * velocity.Dot(normal);
			_previous[p] += tangential * friction;
		}
	}

	private static bool ResolveCollision(in CollisionProxy collider, Vector3 point, float margin, out Vector3 correction, out Vector3 normal)
	{
		correction = Vector3.Zero;
		normal = Vector3.Up;
		switch (collider.Kind)
		{
			case ColliderKind.Sphere:
				return ResolveSphere(collider, point, margin, out correction, out normal);
			case ColliderKind.Capsule:
				return ResolveCapsule(collider, point, margin, out correction, out normal);
			case ColliderKind.Box:
				return ResolveBox(collider, point, margin, out correction, out normal);
			case ColliderKind.Cylinder:
				return ResolveCylinder(collider, point, margin, out correction, out normal);
			default:
				return false;
		}
	}

	private static bool ResolveSphere(in CollisionProxy collider, Vector3 point, float margin, out Vector3 correction, out Vector3 normal)
	{
		correction = Vector3.Zero;
		normal = Vector3.Up;
		float radius = collider.Radius * AverageScale(collider.Transform.Basis) + margin;
		if (radius <= 0f) return false;
		Vector3 delta = point - collider.Transform.Origin;
		float distanceSquared = delta.LengthSquared();
		if (distanceSquared >= radius * radius) return false;
		float distance = Mathf.Sqrt(distanceSquared);
		if (distance <= 0.000001f)
		{
			correction = normal * radius;
			return true;
		}
		normal = delta / distance;
		correction = normal * (radius - distance);
		return true;
	}

	private static bool ResolveCapsule(in CollisionProxy collider, Vector3 point, float margin, out Vector3 correction, out Vector3 normal)
	{
		correction = Vector3.Zero;
		normal = Vector3.Up;
		Basis basis = collider.Transform.Basis;
		float radialScale = RadialScale(basis);
		float axialScale = AxialScale(basis);
		float radius = collider.Radius * radialScale + margin;
		if (radius <= 0f) return false;
		float halfSegment = Mathf.Max(0f, collider.Height * 0.5f * axialScale - collider.Radius * radialScale);
		Vector3 axis = basis.Y;
		float axisLength = axis.Length();
		axis = axisLength > 0.000001f ? axis / axisLength : Vector3.Up;
		Vector3 center = collider.Transform.Origin;
		float along = Mathf.Clamp((point - center).Dot(axis), -halfSegment, halfSegment);
		Vector3 closest = center + axis * along;
		Vector3 delta = point - closest;
		float distanceSquared = delta.LengthSquared();
		if (distanceSquared >= radius * radius) return false;
		float distance = Mathf.Sqrt(distanceSquared);
		if (distance <= 0.000001f)
		{
			Vector3 reference = Mathf.Abs(axis.Dot(Vector3.Up)) < 0.9f ? Vector3.Up : Vector3.Right;
			Vector3 radial = reference - axis * reference.Dot(axis);
			normal = radial.LengthSquared() > 0.000001f ? radial.Normalized() : Vector3.Right;
			correction = normal * radius;
			return true;
		}
		normal = delta / distance;
		correction = normal * (radius - distance);
		return true;
	}

	private static bool ResolveBox(in CollisionProxy collider, Vector3 point, float margin, out Vector3 correction, out Vector3 normal)
	{
		correction = Vector3.Zero;
		normal = Vector3.Up;
		Basis basis = collider.Transform.Basis;
		Vector3 scale = basis.Scale;
		float scaleX = Mathf.Max(0.0001f, Mathf.Abs(scale.X));
		float scaleY = Mathf.Max(0.0001f, Mathf.Abs(scale.Y));
		float scaleZ = Mathf.Max(0.0001f, Mathf.Abs(scale.Z));
		Vector3 half = collider.HalfExtents + new Vector3(margin / scaleX, margin / scaleY, margin / scaleZ);
		Vector3 local = collider.Transform.AffineInverse() * point;
		if (Mathf.Abs(local.X) >= half.X || Mathf.Abs(local.Y) >= half.Y || Mathf.Abs(local.Z) >= half.Z) return false;
		float penetrationX = half.X - Mathf.Abs(local.X);
		float penetrationY = half.Y - Mathf.Abs(local.Y);
		float penetrationZ = half.Z - Mathf.Abs(local.Z);
		Vector3 localAxis;
		float penetration;
		if (penetrationX <= penetrationY && penetrationX <= penetrationZ)
		{
			localAxis = Vector3.Right * (local.X >= 0f ? 1f : -1f);
			penetration = penetrationX;
		}
		else if (penetrationY <= penetrationZ)
		{
			localAxis = Vector3.Up * (local.Y >= 0f ? 1f : -1f);
			penetration = penetrationY;
		}
		else
		{
			localAxis = Vector3.Back * (local.Z >= 0f ? 1f : -1f);
			penetration = penetrationZ;
		}
		correction = basis * (localAxis * penetration);
		Vector3 worldNormal = basis * localAxis;
		if (worldNormal.LengthSquared() > 0.000001f) normal = worldNormal.Normalized();
		return true;
	}

	private static bool ResolveCylinder(in CollisionProxy collider, Vector3 point, float margin, out Vector3 correction, out Vector3 normal)
	{
		correction = Vector3.Zero;
		normal = Vector3.Up;
		Basis basis = collider.Transform.Basis;
		float radialScale = Mathf.Max(0.0001f, (Mathf.Abs(basis.Scale.X) + Mathf.Abs(basis.Scale.Z)) * 0.5f);
		float axialScale = AxialScale(basis);
		float radius = collider.Radius + margin / radialScale;
		float halfHeight = collider.Height * 0.5f + margin / axialScale;
		if (radius <= 0f || halfHeight <= 0f) return false;
		Vector3 local = collider.Transform.AffineInverse() * point;
		float radial = Mathf.Sqrt(local.X * local.X + local.Z * local.Z);
		if (radial >= radius || Mathf.Abs(local.Y) >= halfHeight) return false;
		float radialPenetration = radius - radial;
		float capPenetration = halfHeight - Mathf.Abs(local.Y);
		Vector3 localNormal;
		float penetration;
		if (radialPenetration <= capPenetration)
		{
			Vector3 radialDirection = radial > 0.000001f ? new Vector3(local.X / radial, 0f, local.Z / radial) : Vector3.Right;
			localNormal = radialDirection;
			penetration = radialPenetration;
		}
		else
		{
			localNormal = Vector3.Up * (local.Y >= 0f ? 1f : -1f);
			penetration = capPenetration;
		}
		correction = basis * (localNormal * penetration);
		Vector3 worldNormal = basis * localNormal;
		if (worldNormal.LengthSquared() > 0.000001f) normal = worldNormal.Normalized();
		return true;
	}





}
