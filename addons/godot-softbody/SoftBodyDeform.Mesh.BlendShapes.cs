using System;
using System.Collections.Generic;
using Godot;

public partial class SoftBodyDeform : Node3D
{
	private void BuildBlendShapeDeltas(Mesh mesh, Vector3[] vertices)
	{
		if (mesh is not ArrayMesh arrayMesh) return;
		int shapeCount = _sourceMesh.GetBlendShapeCount();
		if (shapeCount == 0) return;
		var shapeArrays = mesh.SurfaceGetBlendShapeArrays(_surfaceIndex);
		if (shapeArrays.Count != shapeCount) return;
		_relativeBlendShapes = arrayMesh.BlendShapeMode == Mesh.BlendShapeMode.Relative;
		_blendShapeDeltas = new Vector3[shapeCount][];
		_blendShapeRenderDeltas = new Vector3[shapeCount][];
		_blendShapeNormalDeltas = new Vector3[shapeCount][];
		_renderBlendOffset = new Vector3[_renderCount];
		_blendShapeWeights = new float[shapeCount];
		_blendedSourceNormals = _sourceNormals != null && _sourceNormals.Length == _renderCount
			? (Vector3[])_sourceNormals.Clone()
			: null;
		int[] pointRenderCounts = new int[_pointCount];
		for (int r = 0; r < _renderCount; r++) pointRenderCounts[_renderPoint[r]]++;
		for (int shape = 0; shape < shapeCount; shape++)
		{
			Godot.Collections.Array shapeData = shapeArrays[shape];
			Variant vertexData = shapeData[(int)Mesh.ArrayType.Vertex];
			if (vertexData.VariantType == Variant.Type.Nil) continue;
			Vector3[] shapeVertices = vertexData.AsVector3Array();
			if (shapeVertices == null || shapeVertices.Length != _renderCount) continue;
			Vector3[] deltas = new Vector3[_pointCount];
			Vector3[] renderDeltas = new Vector3[_renderCount];
			for (int r = 0; r < _renderCount; r++)
			{
				int point = _renderPoint[r];
				Vector3 delta = _relativeBlendShapes ? shapeVertices[r] : shapeVertices[r] - vertices[r];
				renderDeltas[r] = delta;
				deltas[point] += delta / pointRenderCounts[point];
			}
			_blendShapeDeltas[shape] = deltas;
			_blendShapeRenderDeltas[shape] = renderDeltas;

			Vector3[] normalDeltas = new Vector3[_renderCount];
			Variant normalData = shapeData[(int)Mesh.ArrayType.Normal];
			if (normalData.VariantType != Variant.Type.Nil && _sourceNormals != null && _sourceNormals.Length == _renderCount)
			{
				Vector3[] shapeNormals = normalData.AsVector3Array();
				if (shapeNormals != null && shapeNormals.Length == _renderCount)
				{
					for (int r = 0; r < _renderCount; r++)
						normalDeltas[r] = _relativeBlendShapes ? shapeNormals[r] : shapeNormals[r] - _sourceNormals[r];
				}
			}
			_blendShapeNormalDeltas[shape] = normalDeltas;
		}
		RefreshBlendShapeWeights();
		RebuildCapturedPositions();
	}

	private bool RefreshBlendShapeWeights()
	{
		bool changed = false;
		for (int i = 0; i < _blendShapeWeights.Length; i++)
		{
			float value = _sourceMesh.GetBlendShapeValue(i);
			if (Mathf.Abs(value - _blendShapeWeights[i]) <= 0.0001f) continue;
			_blendShapeWeights[i] = value;
			changed = true;
		}
		return changed;
	}

	private void RebuildCapturedPositions()
	{
		if (_restPositions == null) return;
		Array.Copy(_restPositions, _capturedPositions, _pointCount);
		for (int shape = 0; shape < _blendShapeDeltas.Length; shape++)
		{
			Vector3[] deltas = _blendShapeDeltas[shape];
			if (deltas == null || Mathf.Abs(_blendShapeWeights[shape]) <= 0.0001f) continue;
			float weight = _blendShapeWeights[shape];
			for (int point = 0; point < _pointCount; point++)
				_capturedPositions[point] += deltas[point] * weight;
		}
		if (_renderBlendOffset != null)
		{
			Array.Clear(_renderBlendOffset, 0, _renderCount);
			for (int shape = 0; shape < _blendShapeRenderDeltas.Length; shape++)
			{
				Vector3[] renderDeltas = _blendShapeRenderDeltas[shape];
				Vector3[] deltas = shape < _blendShapeDeltas.Length ? _blendShapeDeltas[shape] : null;
				if (renderDeltas == null || deltas == null) continue;
				float weight = _blendShapeWeights[shape];
				if (Mathf.Abs(weight) <= 0.0001f) continue;
				for (int r = 0; r < _renderCount; r++)
					_renderBlendOffset[r] += (renderDeltas[r] - deltas[_renderPoint[r]]) * weight;
			}
		}
		RebuildBlendedSourceNormals();
	}

	private void RebuildBlendedSourceNormals()
	{
		if (_blendedSourceNormals == null || _sourceNormals == null || _blendedSourceNormals.Length != _renderCount) return;
		Array.Copy(_sourceNormals, _blendedSourceNormals, _renderCount);
		for (int shape = 0; shape < _blendShapeNormalDeltas.Length; shape++)
		{
			Vector3[] deltas = _blendShapeNormalDeltas[shape];
			if (deltas == null || deltas.Length != _renderCount) continue;
			float weight = _blendShapeWeights[shape];
			if (Mathf.Abs(weight) <= 0.0001f) continue;
			for (int r = 0; r < _renderCount; r++)
				_blendedSourceNormals[r] += deltas[r] * weight;
		}
		for (int r = 0; r < _renderCount; r++)
		{
			Vector3 normal = _blendedSourceNormals[r];
			_blendedSourceNormals[r] = normal.LengthSquared() > 0.00000001f ? normal.Normalized() : _sourceNormals[r];
		}
	}

}
