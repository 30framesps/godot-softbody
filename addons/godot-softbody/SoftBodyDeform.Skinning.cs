using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;

public partial class SoftBodyDeform : Node3D
{
	private void ResetState()
	{
		if (RefreshBlendShapeWeights()) RebuildCapturedPositions();
		if (_contactNormals == null || _contactNormals.Length != _pointCount)
		{
			_contactNormals = new Vector3[_pointCount];
			_contacted = new bool[_pointCount];
			_collisionOffsets = new Vector3[_pointCount];
			_collisionOffsetBuffer = new Vector3[_pointCount];
		}
		_points = new Vector3[_pointCount];
		_previous = new Vector3[_pointCount];
		_targets = new Vector3[_pointCount];
		_targetBasises = new Basis[_pointCount];
		UpdateTargets();
		Array.Copy(_targets, _points, _pointCount);
		Array.Copy(_targets, _previous, _pointCount);
		Vector3 scale = _skeleton != null && IsInstanceValid(_skeleton)
			? _skeleton.GlobalTransform.Basis.Scale
			: _sourceMesh.GlobalTransform.Basis.Scale;
		float edgeScale = (scale.X + scale.Y + scale.Z) / 3f;
		for (int i = 0; i < _edgeA.Length; i++)
			_edgeRest[i] = _capturedPositions[_edgeA[i]].DistanceTo(_capturedPositions[_edgeB[i]]) * edgeScale;
	}

	private void ComputeNormalSign()
	{
		_normalSign = 1f;
		if (_sourceNormals == null || _sourceNormals.Length != _renderCount) return;
		Vector3[] normals = AccumulateNormals(_renderRestPositions);
		float dot = 0f;
		for (int r = 0; r < _renderCount; r++)
			dot += normals[_renderPoint[r]].Dot(_sourceNormals[r]);
		if (dot < 0f) _normalSign = -1f;
	}

	private void CacheRenderBindMatrices()
	{
		if (_bindCount <= 0) return;
		if (_renderBindMatrices == null || _renderBindMatrices.Length != _bindCount)
			_renderBindMatrices = new Transform3D[_bindCount];
		Rid renderSkeleton = default;
		bool useRenderPose = false;
		if (_sourceMesh != null && IsInstanceValid(_sourceMesh))
		{
			SkinReference reference = _sourceMesh.GetSkinReference();
			if (reference != null)
			{
				renderSkeleton = reference.GetSkeleton();
				if (renderSkeleton.IsValid && RenderingServer.SkeletonGetBoneCount(renderSkeleton) >= _bindCount)
					useRenderPose = true;
			}
		}
		for (int i = 0; i < _bindCount; i++)
		{
			if (useRenderPose)
			{
				_renderBindMatrices[i] = RenderingServer.SkeletonBoneGetTransform(renderSkeleton, i);
				continue;
			}
			int bone = _bindBones[i];
			_renderBindMatrices[i] = bone >= 0 && _skeleton != null
				? _skeleton.GetBoneGlobalPose(bone) * _bindMatrices[i]
				: Transform3D.Identity;
		}
	}

	private void UpdateTargets()
	{
		bool skinned = _skeleton != null && _skin != null && _influencesPerPoint > 0;
		Transform3D skeletonTransform = skinned ? _skeleton.GlobalTransform : Transform3D.Identity;
		Transform3D meshTransform = _sourceMesh.GlobalTransform;
		bool needBasis = SimulationSpacing > 0f;
		if (skinned) CacheRenderBindMatrices();
		if (_targetBasises == null || _targetBasises.Length != _pointCount) _targetBasises = new Basis[_pointCount];
		for (int p = 0; p < _pointCount; p++)
		{
			Vector3 restPosition = _capturedPositions[p];
			if (!skinned)
			{
				_targets[p] = meshTransform * restPosition;
				if (needBasis) _targetBasises[p] = meshTransform.Basis;
				continue;
			}
			Vector3 sum = Vector3.Zero;
			Vector3 basisX = Vector3.Zero;
			Vector3 basisY = Vector3.Zero;
			Vector3 basisZ = Vector3.Zero;
			float total = 0f;
			for (int k = 0; k < _influencesPerPoint; k++)
			{
				float weight = _influenceWeights[p * _influencesPerPoint + k];
				if (weight <= 0f) continue;
				int bind = _influenceBinds[p * _influencesPerPoint + k];
				Transform3D boneMatrix = _renderBindMatrices != null && _renderBindMatrices.Length == _bindCount
					? _renderBindMatrices[bind]
					: _skeleton.GetBoneGlobalPose(_bindBones[bind]) * _bindMatrices[bind];
				sum += boneMatrix * restPosition * weight;
				if (needBasis)
				{
					basisX += boneMatrix.Basis.Column0 * weight;
					basisY += boneMatrix.Basis.Column1 * weight;
					basisZ += boneMatrix.Basis.Column2 * weight;
				}
				total += weight;
			}
			if (total > 0f)
			{
				_targets[p] = skeletonTransform * (sum / total);
				if (needBasis)
				{
					Basis weighted = new(basisX / total, basisY / total, basisZ / total);
					_targetBasises[p] = skeletonTransform.Basis * weighted;
				}
			}
			else
			{
				_targets[p] = meshTransform * restPosition;
				_targetBasises[p] = meshTransform.Basis;
			}
		}
	}

	private void OnSkeletonUpdated()
	{
		if (!_initialized || _points == null || _targets == null || _outputMesh == null) return;
		if (_skeletonUpdateQueued) return;
		_skeletonUpdateQueued = true;
		CallDeferred(nameof(ProcessSkeletonUpdate));
	}

	private void ProcessSkeletonUpdate()
	{
		_skeletonUpdateQueued = false;
		if (!_initialized || _points == null || _targets == null || _outputMesh == null) return;
		ulong started = LogPerformance ? Time.GetTicksUsec() : 0UL;
		UpdateTargets();
		if (TargetOnly)
		{
			Array.Copy(_targets, _points, _pointCount);
			Array.Copy(_targets, _previous, _pointCount);
		}
		else
		{
			Step((float)SkeletonDelta());
		}
		UpdateOutput();
		LogTick(started);
	}

	private double SkeletonDelta()
	{
		if (_skeleton != null && IsInstanceValid(_skeleton)
			&& _skeleton.ModifierCallbackModeProcess == Skeleton3D.ModifierCallbackModeProcessEnum.Physics)
			return GetPhysicsProcessDeltaTime();
		return GetProcessDeltaTime();
	}

	private Vector3[] AccumulateNormals(Vector3[] positions)
	{
		if (_pointNormals == null || _pointNormals.Length != _pointCount)
			_pointNormals = new Vector3[_pointCount];
		else
			Array.Clear(_pointNormals, 0, _pointCount);
		Vector3[] pointNormals = _pointNormals;
		for (int i = 0; i + 2 < _indices.Length; i += 3)
		{
			int va = _indices[i];
			int vb = _indices[i + 1];
			int vc = _indices[i + 2];
			Vector3 normal = (positions[vc] - positions[va]).Cross(positions[vb] - positions[va]) * _normalSign;
			int a = _renderPoint[va];
			int b = _renderPoint[vb];
			int c = _renderPoint[vc];
			pointNormals[a] += normal;
			pointNormals[b] += normal;
			pointNormals[c] += normal;
		}
		for (int i = 0; i < _pointCount; i++)
		{
			if (pointNormals[i].LengthSquared() > 0.00000001f) pointNormals[i] = pointNormals[i].Normalized();
			else pointNormals[i] = Vector3.Up;
		}
		return pointNormals;
	}

	private Vector3 SkinnedSourceNormal(int renderIndex, int point, Transform3D skeletonTransform)
	{
		Vector3 sourceNormal = _blendedSourceNormals != null && _blendedSourceNormals.Length == _renderCount
			? _blendedSourceNormals[renderIndex]
			: _sourceNormals[renderIndex];
		Vector3 sum = Vector3.Zero;
		float total = 0f;
		for (int k = 0; k < _influencesPerPoint; k++)
		{
			float weight = _influenceWeights[point * _influencesPerPoint + k];
			if (weight <= 0f) continue;
			int bind = _influenceBinds[point * _influencesPerPoint + k];
			Transform3D boneMatrix = _renderBindMatrices != null && _renderBindMatrices.Length == _bindCount
				? _renderBindMatrices[bind]
				: _skeleton.GetBoneGlobalPose(_bindBones[bind]) * _bindMatrices[bind];
			sum += (boneMatrix.Basis * sourceNormal) * weight;
			total += weight;
		}
		return total > 0f ? skeletonTransform.Basis * (sum / total) : Vector3.Zero;
	}

	private void UpdateOutput()
	{
		EnsureOutputBuffers();
		Vector3[] positions = _outputPositions;
		Vector3[] normals = _outputNormals;
		bool skinned = _skeleton != null && IsInstanceValid(_skeleton) && _skin != null && _influencesPerPoint > 0;
		bool hasTargetBasis = _targetBasises != null && _targetBasises.Length == _pointCount;
		Basis fallbackBasis = skinned ? _skeleton.GlobalTransform.Basis : _sourceMesh.GlobalTransform.Basis;
		bool hasRestOffset = _renderRestOffset != null && _renderRestOffset.Length == _renderCount;
		bool blended = SmoothReconstruction && _renderWeightCounts != null && _renderWeightCounts.Length == _renderCount;
		bool smoothOutput = SmoothOutput && OutputSmoothing > 0f && OutputSmoothingIterations > 0
			&& _outputNeighborStart != null && _renderOffsets != null && _renderOffsets.Length == _renderCount;
		for (int p = 0; p < _pointCount; p++) _offsets[p] = _points[p] - _targets[p];
		for (int r = 0; r < _renderCount; r++)
		{
			int point = _renderPoint[r];
			Vector3 offset = _offsets[point];
			if (blended)
			{
				int slot = r * MaxRenderWeights;
				int count = _renderWeightCounts[r];
				Vector3 sum = Vector3.Zero;
				for (int k = 0; k < count; k++)
					sum += _offsets[_renderWeightPoints[slot + k]] * _renderWeights[slot + k];
				offset = sum;
			}
			Basis pointBasis = hasTargetBasis ? _targetBasises[point] : fallbackBasis;
			Vector3 restOffset = hasRestOffset ? pointBasis * _renderRestOffset[r] : Vector3.Zero;
			Vector3 blendOffset = _renderBlendOffset != null && _renderBlendOffset.Length == _renderCount
				? pointBasis * _renderBlendOffset[r]
				: Vector3.Zero;
			Vector3 basePosition = _targets[point] + restOffset + blendOffset;
			if (smoothOutput)
			{
				_renderBasePositions[r] = basePosition;
				_renderOffsets[r] = offset;
			}
			positions[r] = basePosition + offset;
		}
		if (smoothOutput)
		{
			SmoothRenderOffsets();
			for (int r = 0; r < _renderCount; r++)
				positions[r] = _renderBasePositions[r] + _renderOffsets[r];
		}
		Vector3[] pointNormals = AccumulateNormals(positions);
		bool blendNormals = skinned && _sourceNormals != null && _sourceNormals.Length == _renderCount;
		Transform3D skeletonTransform = skinned ? _skeleton.GlobalTransform : Transform3D.Identity;
		for (int r = 0; r < _renderCount; r++)
		{
			int point = _renderPoint[r];
			normals[r] = pointNormals[point];
			if (!blendNormals) continue;
			float blend = Mathf.Clamp(_goalWeights[point], 0f, 1f);
			if (blend <= 0f) continue;
			Vector3 skinnedNormal = SkinnedSourceNormal(r, point, skeletonTransform);
			if (skinnedNormal.LengthSquared() <= 0.00000001f) continue;
			normals[r] = normals[r].Lerp(skinnedNormal.Normalized(), blend);
			if (normals[r].LengthSquared() > 0.00000001f) normals[r] = normals[r].Normalized();
		}

		UpdateOutputSurface(positions);
	}

	private void EnsureOutputBuffers()
	{
		if (_outputPositions != null && _outputPositions.Length == _renderCount) return;
		_outputPositions = new Vector3[_renderCount];
		_outputNormals = new Vector3[_renderCount];
		_outputColors = new Color[_renderCount];
	}

	private void UpdateOutputSurface(Vector3[] positions)
	{
		if (!_outputSurfaceBuilt || _outputSurfaceDebugColors != DebugGoalWeights)
			RebuildOutputSurface(positions);
		else
			UpdateOutputVertexBuffers(positions);
		_outputMesh.CustomAabb = ComputeBounds(positions);
	}

	private void RebuildOutputSurface(Vector3[] positions)
	{
		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = Variant.From(positions);
		arrays[(int)Mesh.ArrayType.Normal] = Variant.From(_outputNormals);
		arrays[(int)Mesh.ArrayType.Index] = Variant.From(_indices);
		if (_uvs != null && _uvs.Length == _renderCount)
			arrays[(int)Mesh.ArrayType.TexUV] = Variant.From(_uvs);
		if (DebugGoalWeights)
		{
			for (int r = 0; r < _renderCount; r++)
				_outputColors[r] = GoalWeightColor(_goalWeights[_renderPoint[r]]);
			arrays[(int)Mesh.ArrayType.Color] = Variant.From(_outputColors);
			_outputMesh.MaterialOverride = DebugMaterial();
		}
		else
		{
			_outputMesh.MaterialOverride = null;
		}

		_outputArrayMesh.ClearSurfaces();
		_outputArrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, Mesh.ArrayFormat.FlagUseDynamicUpdate);
		if (_surfaceMaterial != null) _outputArrayMesh.SurfaceSetMaterial(0, _surfaceMaterial);
		_outputSurfaceBuilt = true;
		_outputSurfaceDebugColors = DebugGoalWeights;
		CacheOutputLayout();
	}

	private void CacheOutputLayout()
	{
		Mesh.ArrayFormat format = _outputArrayMesh.SurfaceGetFormat(0);
		RenderingServer.ArrayFormat renderFormat = (RenderingServer.ArrayFormat)(long)format;
		_outputNormalOffset = RenderingServer.MeshSurfaceGetFormatOffset(renderFormat, _renderCount, (int)Mesh.ArrayType.Normal);
		_outputNormalStride = (int)RenderingServer.MeshSurfaceGetFormatNormalTangentStride(renderFormat, _renderCount);
		if (_outputNormalStride != 4 && _outputNormalStride != 8)
		{
			_outputNormalStride = 0;
			_outputNormalTangentBytes = null;
			return;
		}
		_outputNormalTangentBytes = new byte[_renderCount * _outputNormalStride];
		if (_outputNormalStride < 8) return;
		Godot.Collections.Array arrays = _outputArrayMesh.SurfaceGetArrays(0);
		Variant tangentData = arrays[(int)Mesh.ArrayType.Tangent];
		if (tangentData.VariantType == Variant.Type.Nil) return;
		float[] tangents = tangentData.AsFloat32Array();
		if (tangents == null || tangents.Length < _renderCount * 4) return;
		Span<ushort> packed = MemoryMarshal.Cast<byte, ushort>(_outputNormalTangentBytes.AsSpan());
		int stride = _outputNormalStride / 2;
		for (int r = 0; r < _renderCount; r++)
		{
			Vector3 tangent = new(tangents[r * 4], tangents[r * 4 + 1], tangents[r * 4 + 2]);
			Vector2 encoded = EncodeTangent(tangent, tangents[r * 4 + 3]);
			ushort x = EncodeUnit(encoded.X);
			ushort y = EncodeUnit(encoded.Y);
			if (x == 0 && y == 65535) x = 65535;
			packed[r * stride + 2] = x;
			packed[r * stride + 3] = y;
		}
	}

	private void UpdateOutputVertexBuffers(Vector3[] positions)
	{
		if (_renderCount == 0) return;
		if (_outputNormalStride == 0 || _outputNormalTangentBytes == null)
		{
			RebuildOutputSurface(positions);
			return;
		}
		_outputArrayMesh.SurfaceUpdateVertexRegion(0, 0, MemoryMarshal.AsBytes(positions.AsSpan()));
		Span<ushort> packed = MemoryMarshal.Cast<byte, ushort>(_outputNormalTangentBytes.AsSpan());
		int stride = _outputNormalStride / 2;
		for (int r = 0; r < _renderCount; r++)
		{
			Vector2 encoded = _outputNormals[r].OctahedronEncode();
			packed[r * stride] = EncodeUnit(encoded.X);
			packed[r * stride + 1] = EncodeUnit(encoded.Y);
		}
		_outputArrayMesh.SurfaceUpdateVertexRegion(0, (int)_outputNormalOffset, _outputNormalTangentBytes);
	}

	private static ushort EncodeUnit(float value)
	{
		return (ushort)Mathf.Clamp(value * 65535f, 0f, 65535f);
	}

	private static Vector2 EncodeTangent(Vector3 tangent, float sign)
	{
		if (tangent.LengthSquared() <= 0.00000001f) tangent = Vector3.Right;
		Vector2 encoded = tangent.Normalized().OctahedronEncode();
		encoded.Y = Mathf.Max(encoded.Y, 1f / 32767f);
		encoded.Y = encoded.Y * 0.5f + 0.5f;
		encoded.Y = sign >= 0f ? encoded.Y : 1f - encoded.Y;
		return encoded;
	}

	private void SmoothRenderOffsets()
	{
		int iterations = Mathf.Clamp(OutputSmoothingIterations, 0, 4);
		float smoothing = Mathf.Clamp(OutputSmoothing, 0f, 1f);
		if (iterations <= 0 || smoothing <= 0f || _outputNeighborStart == null || _outputNeighborPoints == null) return;
		Vector3[] source = _renderOffsets;
		Vector3[] target = _renderOffsetBuffer;
		for (int iteration = 0; iteration < iterations; iteration++)
		{
			for (int r = 0; r < _renderCount; r++)
			{
				int point = _renderPoint[r];
				if (_goalWeights[point] >= 0.999f)
				{
					target[r] = source[r];
					continue;
				}
				int start = _outputNeighborStart[r];
				int end = _outputNeighborStart[r + 1];
				if (end <= start)
				{
					target[r] = source[r];
					continue;
				}
				Vector3 sum = Vector3.Zero;
				for (int i = start; i < end; i++) sum += source[_outputNeighborPoints[i]];
				Vector3 average = sum / (end - start);
				target[r] = source[r].Lerp(average, smoothing);
			}
			(source, target) = (target, source);
		}
		if (!ReferenceEquals(source, _renderOffsets))
			Array.Copy(source, _renderOffsets, _renderCount);
	}

	private static Color GoalWeightColor(float weight)
	{
		float clamped = Mathf.Clamp(weight, 0f, 1f);
		return new Color(0.15f + 0.85f * clamped, 0.2f * (1f - clamped), 1f - 0.85f * clamped);
	}

	private StandardMaterial3D DebugMaterial()
	{
		if (_debugMaterial != null) return _debugMaterial;
		_debugMaterial = new StandardMaterial3D
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			VertexColorUseAsAlbedo = true,
			CullMode = BaseMaterial3D.CullModeEnum.Disabled
		};
		return _debugMaterial;
	}

	private static Aabb ComputeBounds(Vector3[] positions)
	{
		if (positions == null || positions.Length == 0) return new Aabb();
		Vector3 min = positions[0];
		Vector3 max = positions[0];
		for (int i = 1; i < positions.Length; i++)
		{
			Vector3 point = positions[i];
			min = new Vector3(Mathf.Min(min.X, point.X), Mathf.Min(min.Y, point.Y), Mathf.Min(min.Z, point.Z));
			max = new Vector3(Mathf.Max(max.X, point.X), Mathf.Max(max.Y, point.Y), Mathf.Max(max.Z, point.Z));
		}
		Vector3 padding = Vector3.One * 1.5f;
		return new Aabb(min - padding, max - min + padding * 2f);
	}

}
