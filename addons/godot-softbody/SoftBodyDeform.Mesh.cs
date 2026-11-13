using System;
using System.Collections.Generic;
using Godot;

public partial class SoftBodyDeform : Node3D
{
	private MeshInstance3D ResolveMesh()
	{
		if (!TargetMeshPath.IsEmpty)
		{
			Node node = GetNodeOrNull<Node>(TargetMeshPath);
			if (node is MeshInstance3D mesh && mesh.Mesh != null) return mesh;
		}
		return FindMesh(GetParent());
	}

	private static MeshInstance3D FindMesh(Node node)
	{
		if (node == null) return null;
		if (node is MeshInstance3D mesh && mesh.Mesh != null && mesh.Mesh.GetSurfaceCount() > 0) return mesh;
		foreach (Node child in node.GetChildren())
		{
			MeshInstance3D found = FindMesh(child);
			if (found != null) return found;
		}
		return null;
	}

	private int ResolveSurface(Mesh mesh)
	{
		if (mesh is ArrayMesh arrayMesh && !string.IsNullOrEmpty(SurfaceName))
		{
			int byName = arrayMesh.SurfaceFindByName(SurfaceName);
			if (byName >= 0) return byName;
		}
		if (!string.IsNullOrEmpty(SurfaceName))
		{
			for (int i = 0; i < mesh.GetSurfaceCount(); i++)
			{
				Material material = _sourceMesh.GetSurfaceOverrideMaterial(i) ?? mesh.SurfaceGetMaterial(i);
				if (material != null && material.ResourceName == SurfaceName) return i;
			}
		}
		return SurfaceIndex >= 0 && SurfaceIndex < mesh.GetSurfaceCount() ? SurfaceIndex : -1;
	}

	private static Skeleton3D ResolveSkeleton(MeshInstance3D mesh)
	{
		if (!mesh.Skeleton.IsEmpty)
		{
			Skeleton3D skeleton = mesh.GetNodeOrNull<Skeleton3D>(mesh.Skeleton);
			if (skeleton != null) return skeleton;
		}
		return FindSkeleton(mesh.GetParent());
	}

	private static Skeleton3D FindSkeleton(Node node)
	{
		if (node == null) return null;
		if (node is Skeleton3D skeleton) return skeleton;
		foreach (Node child in node.GetChildren())
		{
			Skeleton3D found = FindSkeleton(child);
			if (found != null) return found;
		}
		return null;
	}

	private static int[] ReadIndices(Godot.Collections.Array arrays, int renderCount)
	{
		Variant indexData = arrays[(int)Mesh.ArrayType.Index];
		if (indexData.VariantType != Variant.Type.Nil)
		{
			int[] indices = indexData.AsInt32Array();
			if (indices != null && indices.Length > 0) return indices;
		}
		int[] generated = new int[renderCount];
		for (int i = 0; i < renderCount; i++) generated[i] = i;
		return generated;
	}

	private void BuildPoints(Vector3[] vertices, Godot.Collections.Array arrays)
	{
		var pointByPosition = new Dictionary<Vector3I, int>();
		_renderPoint = new int[_renderCount];
		var rest = new List<Vector3>();
		for (int r = 0; r < _renderCount; r++)
		{
			Vector3 position = vertices[r];
			var key = new Vector3I(
				Mathf.RoundToInt(position.X * 10000f),
				Mathf.RoundToInt(position.Y * 10000f),
				Mathf.RoundToInt(position.Z * 10000f));
			if (!pointByPosition.TryGetValue(key, out int point))
			{
				point = rest.Count;
				pointByPosition[key] = point;
				rest.Add(position);
			}
			_renderPoint[r] = point;
		}
		_pointCount = rest.Count;
		_restPositions = rest.ToArray();
		_capturedPositions = (Vector3[])_restPositions.Clone();
		_offsets = new Vector3[_pointCount];

		Variant boneData = arrays[(int)Mesh.ArrayType.Bones];
		Variant weightData = arrays[(int)Mesh.ArrayType.Weights];
		int[] sourceBones = boneData.VariantType == Variant.Type.Nil ? null : boneData.AsInt32Array();
		float[] sourceWeights = weightData.VariantType == Variant.Type.Nil ? null : weightData.AsFloat32Array();
		int bonesPerVertex = sourceBones != null && sourceWeights != null && sourceBones.Length == sourceWeights.Length && sourceBones.Length % _renderCount == 0
			? sourceBones.Length / _renderCount
			: 0;
		_influencesPerPoint = Mathf.Clamp(bonesPerVertex, 0, 8);
		_influenceBinds = new int[_pointCount * _influencesPerPoint];
		_influenceWeights = new float[_pointCount * _influencesPerPoint];
		if (_influencesPerPoint == 0) return;

		bool[] assigned = new bool[_pointCount];
		for (int r = 0; r < _renderCount; r++)
		{
			int point = _renderPoint[r];
			if (assigned[point]) continue;
			bool found = false;
			for (int k = 0; k < bonesPerVertex && k < _influencesPerPoint; k++)
			{
				int bind = sourceBones[r * bonesPerVertex + k];
				float weight = sourceWeights[r * bonesPerVertex + k];
				if (weight <= 0f || bind < 0 || bind >= _bindCount || _bindBones[bind] < 0) continue;
				_influenceBinds[point * _influencesPerPoint + k] = bind;
				_influenceWeights[point * _influencesPerPoint + k] = weight;
				found = true;
			}
			assigned[point] = found;
		}

		for (int p = 0; p < _pointCount; p++)
		{
			float total = 0f;
			for (int k = 0; k < _influencesPerPoint; k++) total += _influenceWeights[p * _influencesPerPoint + k];
			if (total <= 0f) continue;
			for (int k = 0; k < _influencesPerPoint; k++) _influenceWeights[p * _influencesPerPoint + k] /= total;
		}
	}

	private void ReduceSimulationPoints(Vector3[] vertices)
	{
		int fullCount = _pointCount;
		float spacing = Mathf.Max(0.0001f, SimulationSpacing);
		var cellSum = new Dictionary<Vector3I, Vector3>();
		var cellCount = new Dictionary<Vector3I, int>();
		for (int p = 0; p < fullCount; p++)
		{
			if (_border[p]) continue;
			Vector3 position = _restPositions[p];
			Vector3I cell = SimulationCell(position, spacing);
			cellSum.TryGetValue(cell, out Vector3 sum);
			cellSum[cell] = sum + position;
			cellCount.TryGetValue(cell, out int count);
			cellCount[cell] = count + 1;
		}

		int[] fullToSim = new int[fullCount];
		var cellToSim = new Dictionary<Vector3I, int>();
		var rest = new List<Vector3>();
		var border = new List<bool>();
		var representatives = new List<int>();
		for (int p = 0; p < fullCount; p++)
		{
			Vector3 position = _restPositions[p];
			if (_border[p])
			{
				fullToSim[p] = rest.Count;
				rest.Add(position);
				border.Add(true);
				representatives.Add(p);
				continue;
			}
			Vector3I cell = SimulationCell(position, spacing);
			if (!cellToSim.TryGetValue(cell, out int sim))
			{
				sim = rest.Count;
				cellToSim[cell] = sim;
				rest.Add(position);
				border.Add(false);
				representatives.Add(p);
			}
			else
			{
				Vector3 centroid = cellSum[cell] / cellCount[cell];
				int representative = representatives[sim];
				if (position.DistanceSquaredTo(centroid) < _restPositions[representative].DistanceSquaredTo(centroid))
				{
					representatives[sim] = p;
					rest[sim] = position;
				}
			}
			fullToSim[p] = sim;
		}

		int simCount = rest.Count;
		_restPositions = rest.ToArray();
		_capturedPositions = (Vector3[])_restPositions.Clone();
		_offsets = new Vector3[simCount];
		_reducedBorder = border.ToArray();

		int per = _influencesPerPoint;
		int[] binds = new int[simCount * per];
		float[] weights = new float[simCount * per];
		for (int sim = 0; sim < simCount; sim++)
		{
			if (per <= 0) continue;
			int representative = representatives[sim];
			Array.Copy(_influenceBinds, representative * per, binds, sim * per, per);
			Array.Copy(_influenceWeights, representative * per, weights, sim * per, per);
		}
		_influenceBinds = binds;
		_influenceWeights = weights;

		_renderRestOffset = new Vector3[_renderCount];
		for (int r = 0; r < _renderCount; r++)
		{
			int sim = fullToSim[_renderPoint[r]];
			_renderPoint[r] = sim;
			_renderRestOffset[r] = vertices[r] - _restPositions[sim];
		}
		_pointCount = simCount;
	}

	private static Vector3I SimulationCell(Vector3 position, float spacing)
	{
		return new Vector3I(
			Mathf.FloorToInt(position.X / spacing),
			Mathf.FloorToInt(position.Y / spacing),
			Mathf.FloorToInt(position.Z / spacing));
	}

	private void ApplyReducedBorders()
	{
		if (_reducedBorder == null) return;
		int count = Mathf.Min(_pointCount, _reducedBorder.Length);
		for (int p = 0; p < count; p++)
			if (_reducedBorder[p]) _border[p] = true;
	}

	private void BuildRenderWeights()
	{
		if (SimulationSpacing <= 0f) return;
		float spacing = Mathf.Max(0.0001f, SimulationSpacing);
		float sigma = spacing * Mathf.Max(0.05f, SmoothRadius);
		float radius = sigma * 3f;
		float radiusSquared = radius * radius;
		float falloff = 1f / (2f * sigma * sigma);
		_renderWeightPoints = new int[_renderCount * MaxRenderWeights];
		_renderWeights = new float[_renderCount * MaxRenderWeights];
		_renderWeightCounts = new int[_renderCount];
		int[] bestPoints = new int[MaxRenderWeights];
		float[] bestWeights = new float[MaxRenderWeights];
		for (int r = 0; r < _renderCount; r++)
		{
			int own = _renderPoint[r];
			int slot = r * MaxRenderWeights;
			if (_border[own])
			{
				_renderWeightCounts[r] = 1;
				_renderWeightPoints[slot] = own;
				_renderWeights[slot] = 1f;
				continue;
			}

			Vector3 rest = _renderRestPositions[r];
			int bestCount = 0;
			for (int p = 0; p < _pointCount; p++)
			{
				float distanceSquared = rest.DistanceSquaredTo(_restPositions[p]);
				if (p != own && distanceSquared > radiusSquared) continue;
				float weight = Mathf.Exp(-distanceSquared * falloff);
				int insert = bestCount;
				if (bestCount < MaxRenderWeights)
				{
					bestCount++;
				}
				else if (weight <= bestWeights[bestCount - 1])
				{
					continue;
				}
				else
				{
					insert = bestCount - 1;
				}
				while (insert > 0 && bestWeights[insert - 1] < weight)
				{
					bestWeights[insert] = bestWeights[insert - 1];
					bestPoints[insert] = bestPoints[insert - 1];
					insert--;
				}
				bestWeights[insert] = weight;
				bestPoints[insert] = p;
			}

			float total = 0f;
			for (int k = 0; k < bestCount; k++) total += bestWeights[k];
			if (total <= 0f) continue;
			for (int k = 0; k < bestCount; k++)
			{
				float weight = bestWeights[k] / total;
				_renderWeightPoints[slot + k] = bestPoints[k];
				_renderWeights[slot + k] = weight;
			}
			_renderWeightCounts[r] = bestCount;
		}
	}





	private void BuildEdges()
	{
		var uses = new Dictionary<long, int>();
		for (int i = 0; i + 2 < _indices.Length; i += 3)
		{
			int a = _renderPoint[_indices[i]];
			int b = _renderPoint[_indices[i + 1]];
			int c = _renderPoint[_indices[i + 2]];
			AddEdge(uses, a, b);
			AddEdge(uses, b, c);
			AddEdge(uses, c, a);
		}
		_border = new bool[_pointCount];
		_edgeA = new int[uses.Count];
		_edgeB = new int[uses.Count];
		int edge = 0;
		foreach (KeyValuePair<long, int> pair in uses)
		{
			int a = (int)(pair.Key >> 32);
			int b = (int)(pair.Key & 0xffffffffL);
			_edgeA[edge] = a;
			_edgeB[edge] = b;
			if (pair.Value == 1)
			{
				_border[a] = true;
				_border[b] = true;
			}
			edge++;
		}
		_edgeRest = new float[_edgeA.Length];
	}

	private static void AddEdge(Dictionary<long, int> uses, int a, int b)
	{
		if (a == b) return;
		int low = Mathf.Min(a, b);
		int high = Mathf.Max(a, b);
		long key = ((long)low << 32) | (uint)high;
		uses.TryGetValue(key, out int count);
		uses[key] = count + 1;
	}

	private void BuildPointNeighbors()
	{
		_neighborStart = new int[_pointCount + 1];
		for (int i = 0; i < _edgeA.Length; i++)
		{
			_neighborStart[_edgeA[i] + 1]++;
			_neighborStart[_edgeB[i] + 1]++;
		}
		for (int i = 0; i < _pointCount; i++) _neighborStart[i + 1] += _neighborStart[i];
		_neighborPoints = new int[_edgeA.Length * 2];
		int[] cursor = new int[_pointCount];
		Array.Copy(_neighborStart, cursor, _pointCount);
		for (int i = 0; i < _edgeA.Length; i++)
		{
			int a = _edgeA[i];
			int b = _edgeB[i];
			_neighborPoints[cursor[a]++] = b;
			_neighborPoints[cursor[b]++] = a;
		}
	}

	private void BuildOutputNeighbors()
	{
		if (_renderCount <= 0 || _indices == null)
		{
			_outputNeighborStart = null;
			_outputNeighborPoints = null;
			return;
		}
		var edges = new HashSet<long>();
		for (int i = 0; i + 2 < _indices.Length; i += 3)
		{
			int a = _indices[i];
			int b = _indices[i + 1];
			int c = _indices[i + 2];
			AddOutputEdge(edges, a, b);
			AddOutputEdge(edges, b, c);
			AddOutputEdge(edges, c, a);
		}
		if (_pointCount > 0)
		{
			int[] firstRender = new int[_pointCount];
			Array.Fill(firstRender, -1);
			for (int r = 0; r < _renderCount; r++)
			{
				int point = _renderPoint[r];
				if (firstRender[point] < 0) firstRender[point] = r;
				else AddOutputEdge(edges, firstRender[point], r);
			}
		}
		_outputNeighborStart = new int[_renderCount + 1];
		foreach (long key in edges)
		{
			int a = (int)(key >> 32);
			int b = (int)(key & 0xffffffffL);
			_outputNeighborStart[a + 1]++;
			_outputNeighborStart[b + 1]++;
		}
		for (int i = 0; i < _renderCount; i++) _outputNeighborStart[i + 1] += _outputNeighborStart[i];
		_outputNeighborPoints = new int[edges.Count * 2];
		int[] cursor = new int[_renderCount];
		Array.Copy(_outputNeighborStart, cursor, _renderCount);
		foreach (long key in edges)
		{
			int a = (int)(key >> 32);
			int b = (int)(key & 0xffffffffL);
			_outputNeighborPoints[cursor[a]++] = b;
			_outputNeighborPoints[cursor[b]++] = a;
		}
		_renderOffsets = new Vector3[_renderCount];
		_renderOffsetBuffer = new Vector3[_renderCount];
		_renderBasePositions = new Vector3[_renderCount];
	}

	private static void AddOutputEdge(HashSet<long> edges, int a, int b)
	{
		if (a == b) return;
		int low = Mathf.Min(a, b);
		int high = Mathf.Max(a, b);
		edges.Add(((long)low << 32) | (uint)high);
	}

	private void BuildGoalWeights()
	{
		_goalWeights = new float[_pointCount];
		var adjacency = new List<int>[_pointCount];
		for (int i = 0; i < _pointCount; i++) adjacency[i] = new List<int>();
		for (int i = 0; i < _edgeA.Length; i++)
		{
			adjacency[_edgeA[i]].Add(_edgeB[i]);
			adjacency[_edgeB[i]].Add(_edgeA[i]);
		}

		int falloff = Mathf.Max(1, GoalFalloffEdges);
		int[] distance = new int[_pointCount];
		Array.Fill(distance, -1);
		var queue = new Queue<int>();
		for (int i = 0; i < _pointCount; i++)
		{
			if (!_border[i]) continue;
			distance[i] = 0;
			queue.Enqueue(i);
		}
		while (queue.Count > 0)
		{
			int point = queue.Dequeue();
			if (distance[point] >= falloff) continue;
			foreach (int neighbor in adjacency[point])
			{
				if (distance[neighbor] >= 0) continue;
				distance[neighbor] = distance[point] + 1;
				queue.Enqueue(neighbor);
			}
		}
		for (int i = 0; i < _pointCount; i++)
		{
			if (_border[i]) _goalWeights[i] = 1f;
			else if (distance[i] >= 0) _goalWeights[i] = Mathf.Clamp(1f - distance[i] / (float)falloff, 0f, 1f);
			else _goalWeights[i] = 0f;
		}
	}

	private void CreateOutput()
	{
		_outputArrayMesh = new ArrayMesh();
		_outputMesh = new MeshInstance3D
		{
			Name = "SoftBodyOutput",
			Mesh = _outputArrayMesh,
			TopLevel = true,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.On
		};
		Callable.From(AttachOutput).CallDeferred();
		if (HideSourceSurface)
		{
			Shader shader = new()
			{
				Code = "shader_type spatial;\nrender_mode unshaded, cull_disabled, depth_draw_never;\nvoid fragment() { discard; }\n"
			};
			_hiddenMaterial = new ShaderMaterial { Shader = shader };
			_sourceMesh.SetSurfaceOverrideMaterial(_surfaceIndex, _hiddenMaterial);
		}
	}

	private void AttachOutput()
	{
		if (_outputMesh == null || !IsInstanceValid(_outputMesh)) return;
		Node owner = GetParent() ?? this;
		if (!IsInstanceValid(owner)) owner = this;
		owner.AddChild(_outputMesh);
		_outputMesh.GlobalTransform = Transform3D.Identity;
	}

	private void RestoreSourceSurface()
	{
		if (_sourceMesh == null || !IsInstanceValid(_sourceMesh) || _surfaceIndex < 0) return;
		if (!HideSourceSurface) return;
		Material restore = _originalOverride ?? _sourceMesh.Mesh?.SurfaceGetMaterial(_surfaceIndex);
		if (restore != null) _sourceMesh.SetSurfaceOverrideMaterial(_surfaceIndex, restore);
	}

}
