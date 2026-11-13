using System;
using System.Collections.Generic;
using Godot;

[GlobalClass]
public partial class SoftBodyDeform : Node3D
{
	[Export] public bool Enabled { get; set; } = true;
	[Export] public bool SimulateWhenInactive { get; set; } = true;
	[Export] public NodePath TargetMeshPath { get; set; } = "";
	[Export] public string SurfaceName { get; set; } = "";
	[Export] public int SurfaceIndex { get; set; } = -1;
	[Export] public float Gravity { get; set; } = 9.8f;
	[Export] public Vector3 ExternalForce { get; set; } = Vector3.Zero;
	[Export] public float Damping { get; set; } = 0.99f;
	[Export] public int EdgeIterations { get; set; } = 4;
	[Export] public float EdgeStiffness { get; set; } = 1f;
	[Export] public int GoalFalloffEdges { get; set; } = 4;
	[Export] public float GoalPull { get; set; } = 0.2f;
	[Export] public bool HideSourceSurface { get; set; } = true;
	[Export] public bool TargetOnly { get; set; } = false;
	[Export] public bool DebugGoalWeights { get; set; } = false;
	[Export] public bool LogPerformance { get; set; } = false;
	[Export] public bool UseFinalSkeletonPose { get; set; } = true;
	[Export(PropertyHint.Range, "0,0.1,0.001,or_greater")]
	public float SimulationSpacing { get; set; } = 0f;
	[Export] public bool SmoothReconstruction { get; set; } = true;
	[Export(PropertyHint.Range, "0.25,3,0.05,or_greater")]
	public float SmoothRadius { get; set; } = 1f;
	[Export] public bool SmoothOutput { get; set; } = true;
	[Export(PropertyHint.Range, "0,1,0.01")]
	public float OutputSmoothing { get; set; } = 0.5f;
	[Export(PropertyHint.Range, "0,4,1")]
	public int OutputSmoothingIterations { get; set; } = 2;
	[Export] public bool CollisionEnabled { get; set; } = true;
	[Export] public Godot.Collections.Array<SoftBodyCollider> Colliders { get; set; } = new();
	[Export] public Godot.Collections.Array<NodePath> ColliderPaths { get; set; } = new();
	[Export] public bool AutoCollectColliders { get; set; } = true;
	[Export] public Godot.Collections.Array<NodePath> AdditionalColliderRoots { get; set; } = new();
	[Export(PropertyHint.Range, "0,0.1,0.0005,or_greater")]
	public float CollisionMargin { get; set; } = 0.005f;
	[Export(PropertyHint.Range, "0,1,0.01")]
	public float CollisionFriction { get; set; } = 0f;
	[Export(PropertyHint.Range, "0,1,0.01")]
	public float CollisionSpread { get; set; } = 0.6f;
	[Export(PropertyHint.Range, "0,4,1")]
	public int CollisionSmoothingIterations { get; set; } = 3;
	[Export(PropertyHint.Range, "0,1,0.01")]
	public float CollisionStrength { get; set; } = 1f;
	[Export] public bool DebugColliders { get; set; } = false;

	private enum ColliderKind
	{
		Sphere,
		Capsule,
		Box,
		Cylinder
	}

	private struct CollisionSource
	{
		public bool IsNode;
		public SoftBodyCollider Resource;
		public CollisionShape3D Node;
		public int Bone;
	}

	private struct CollisionProxy
	{
		public ColliderKind Kind;
		public Transform3D Transform;
		public Vector3 HalfExtents;
		public float Radius;
		public float Height;
	}

	private CollisionSource[] _collisionSources = Array.Empty<CollisionSource>();
	private CollisionProxy[] _collisionProxies = Array.Empty<CollisionProxy>();
	private int _activeColliderCount;
	private Vector3[] _contactNormals;
	private bool[] _contacted;
	private Vector3[] _collisionOffsets;
	private Vector3[] _collisionOffsetBuffer;
	private bool _colliderRebuildQueued;
	private MeshInstance3D _colliderDebugMesh;
	private ArrayMesh _colliderDebugMeshResource;
	private StandardMaterial3D _colliderDebugMaterial;
	private readonly List<Vector3> _colliderDebugVertices = new();

	private MeshInstance3D _sourceMesh;
	private Skeleton3D _skeleton;
	private Skin _skin;
	private MeshInstance3D _outputMesh;
	private ArrayMesh _outputArrayMesh;
	private Material _surfaceMaterial;
	private Material _originalOverride;
	private ShaderMaterial _hiddenMaterial;
	private int _surfaceIndex = -1;

	private const int MaxRenderWeights = 6;
	private Vector3[] _restPositions;
	private Vector3[] _capturedPositions;
	private Vector3[] _renderRestOffset;
	private Vector3[] _renderBlendOffset;
	private int[] _renderWeightPoints;
	private float[] _renderWeights;
	private int[] _renderWeightCounts;
	private bool[] _reducedBorder;
	private Vector3[] _offsets;
	private int[] _renderPoint;
	private Vector3[] _points;
	private Vector3[] _previous;
	private Vector3[] _targets;
	private Basis[] _targetBasises;
	private float[] _goalWeights;
	private bool[] _border;
	private int[] _edgeA;
	private int[] _edgeB;
	private float[] _edgeRest;
	private int[] _neighborStart;
	private int[] _neighborPoints;
	private int[] _outputNeighborStart;
	private int[] _outputNeighborPoints;
	private Vector3[] _renderOffsets;
	private Vector3[] _renderOffsetBuffer;
	private Vector3[] _renderBasePositions;
	private Vector3[] _outputPositions;
	private Vector3[] _outputNormals;
	private Color[] _outputColors;
	private Vector3[] _pointNormals;
	private byte[] _outputNormalTangentBytes;
	private uint _outputNormalOffset;
	private int _outputNormalStride;
	private bool _outputSurfaceBuilt;
	private bool _outputSurfaceDebugColors;
	private int _pointCount;
	private int _renderCount;
	private int[] _indices;
	private Vector2[] _uvs;
	private Vector3[] _sourceNormals;
	private Vector3[] _blendedSourceNormals;
	private Vector3[][] _blendShapeNormalDeltas = Array.Empty<Vector3[]>();
	private Vector3[] _renderRestPositions;
	private int[] _influenceBinds;
	private float[] _influenceWeights;
	private int _influencesPerPoint;
	private Transform3D[] _bindMatrices;
	private Transform3D[] _renderBindMatrices;
	private int[] _bindBones;
	private int _bindCount;
	private Vector3[][] _blendShapeDeltas = Array.Empty<Vector3[]>();
	private Vector3[][] _blendShapeRenderDeltas = Array.Empty<Vector3[]>();
	private float[] _blendShapeWeights = Array.Empty<float>();
	private bool _relativeBlendShapes;
	private bool _initialized;
	private bool _skeletonUpdateQueued;
	private Vector3 _lastSkeletonScale = Vector3.One;
	private float _normalSign = 1f;
	private StandardMaterial3D _debugMaterial;
	private ulong _tickUsec;
	private int _tickCount;
	private bool _skeletonPoseConnected;

	public override void _EnterTree()
	{
		ProcessPhysicsPriority = 2000;
	}

	public override void _Ready()
	{
		if (!Enabled)
		{
			SetPhysicsProcess(false);
			return;
		}
		if (!Initialize())
		{
			SetPhysicsProcess(false);
			GD.PushWarning("SoftBodyDeform could not initialize.");
			return;
		}
		_lastSkeletonScale = _skeleton?.GlobalTransform.Basis.Scale ?? Vector3.One;
	}

	public override void _ExitTree()
	{
		if (_skeletonPoseConnected && _skeleton != null && IsInstanceValid(_skeleton))
		{
			_skeleton.SkeletonUpdated -= OnSkeletonUpdated;
			_skeletonPoseConnected = false;
		}
		RestoreSourceSurface();
		if (_outputMesh != null && IsInstanceValid(_outputMesh)) _outputMesh.QueueFree();
	}

	public override void _PhysicsProcess(double delta)
	{
		if (!_initialized || _sourceMesh == null || !IsInstanceValid(_sourceMesh)) return;
		if (_skeleton != null && IsInstanceValid(_skeleton))
		{
			Vector3 scale = _skeleton.GlobalTransform.Basis.Scale;
			if (scale.DistanceSquaredTo(_lastSkeletonScale) > 0.000001f)
			{
				_lastSkeletonScale = scale;
				ResetState();
			}
		}
		if (RefreshBlendShapeWeights())
		{
			RebuildCapturedPositions();
			ResetState();
		}
		if (UseFinalSkeletonPose && _skeletonPoseConnected && _skeleton != null && IsInstanceValid(_skeleton) && _skeleton.CanProcess()) return;
		ulong started = LogPerformance ? Time.GetTicksUsec() : 0UL;
		UpdateTargets();
		if (TargetOnly)
		{
			Array.Copy(_targets, _points, _pointCount);
			Array.Copy(_targets, _previous, _pointCount);
		}
		else
		{
			Step((float)delta);
		}
		UpdateOutput();
		LogTick(started);
	}

	public void ResetSimulation()
	{
		if (_initialized) ResetState();
	}

	public void SetActorActive(bool active)
	{
		ProcessMode = SimulateWhenInactive && !active ? ProcessModeEnum.Always : ProcessModeEnum.Inherit;
	}

	public void RebuildColliders()
	{
		if (_initialized) BuildColliders();
	}

	public int ActiveColliderCount => _activeColliderCount;

	private bool Initialize()
	{
		_sourceMesh = ResolveMesh();
		if (_sourceMesh == null || _sourceMesh.Mesh == null) return false;
		Mesh mesh = _sourceMesh.Mesh;
		_surfaceIndex = ResolveSurface(mesh);
		if (_surfaceIndex < 0) return false;

		Godot.Collections.Array arrays = mesh.SurfaceGetArrays(_surfaceIndex);
		Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
		if (vertices == null || vertices.Length == 0) return false;
		_renderCount = vertices.Length;
		_renderRestPositions = (Vector3[])vertices.Clone();
		_sourceNormals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
		Variant uvData = arrays[(int)Mesh.ArrayType.TexUV];
		_uvs = uvData.VariantType == Variant.Type.Nil ? Array.Empty<Vector2>() : uvData.AsVector2Array();
		_indices = ReadIndices(arrays, _renderCount);

		_skeleton = ResolveSkeleton(_sourceMesh);
		if (UseFinalSkeletonPose && _skeleton != null)
		{
			_skeleton.SkeletonUpdated += OnSkeletonUpdated;
			_skeletonPoseConnected = true;
		}
		_skin = _sourceMesh.Skin;
		_bindCount = _skin?.GetBindCount() ?? 0;
		_bindBones = new int[_bindCount];
		_bindMatrices = new Transform3D[_bindCount];
		for (int i = 0; i < _bindCount; i++)
		{
			int bone = _skin.GetBindBone(i);
			if (bone < 0) bone = _skeleton?.FindBone(_skin.GetBindName(i).ToString()) ?? -1;
			_bindBones[i] = bone;
			_bindMatrices[i] = _skin.GetBindPose(i);
		}

		BuildPoints(vertices, arrays);
		BuildEdges();
		if (SimulationSpacing > 0f) ReduceSimulationPoints(vertices);
		BuildBlendShapeDeltas(mesh, vertices);
		BuildEdges();
		ApplyReducedBorders();
		BuildRenderWeights();
		BuildGoalWeights();
		BuildPointNeighbors();
		BuildOutputNeighbors();
		BuildColliders();
		_originalOverride = _sourceMesh.GetSurfaceOverrideMaterial(_surfaceIndex);
		_surfaceMaterial = _originalOverride ?? mesh.SurfaceGetMaterial(_surfaceIndex);
		CreateOutput();
		UpdateColliderDebug();
		ResetState();
		ComputeNormalSign();
		_initialized = true;
		UpdateOutput();
		int borderCount = 0;
		float maxGoal = 0f;
		for (int i = 0; i < _pointCount; i++)
		{
			if (_border[i]) borderCount++;
			maxGoal = Mathf.Max(maxGoal, _goalWeights[i]);
		}
		GD.Print($"SoftBodyDeform: {_renderCount} vertices, {_pointCount} points, {_edgeA.Length} edges, {borderCount} border points, max goal {maxGoal}.");
		return true;
	}

















































	private void LogTick(ulong started)
	{
		if (!LogPerformance) return;
		_tickUsec += Time.GetTicksUsec() - started;
		_tickCount++;
		if (_tickCount < 60) return;
		double average = _tickUsec / 1000.0 / _tickCount;
		GD.Print($"SoftBodyDeform: {_pointCount} points, {_edgeA.Length} edges, {average:0.000} ms/tick.");
		_tickUsec = 0UL;
		_tickCount = 0;
	}

	private void Step(float delta)
	{
		if (delta <= 0f) return;
		RefreshCollisionProxies();
		if (_contacted != null) Array.Clear(_contacted, 0, _pointCount);
		delta = Mathf.Min(delta, 1f / 30f);
		Vector3 acceleration = new Vector3(0f, -Gravity, 0f) + ExternalForce + SoftBodyEnvironment.Force;
		float damping = Mathf.Clamp(Damping, 0f, 0.9999f);
		for (int i = 0; i < _pointCount; i++)
		{
			Vector3 velocity = (_points[i] - _previous[i]) * damping;
			_previous[i] = _points[i];
			_points[i] += velocity + acceleration * (delta * delta);
		}

		int iterations = Mathf.Max(1, EdgeIterations);
		float stiffness = Mathf.Clamp(EdgeStiffness, 0f, 1f);
		float goalPull = Mathf.Clamp(GoalPull, 0f, 1f);
		SnapBorder();
		for (int iteration = 0; iteration < iterations; iteration++)
		{
			for (int i = 0; i < _edgeA.Length; i++)
			{
				int a = _edgeA[i];
				int b = _edgeB[i];
				Vector3 deltaVector = _points[b] - _points[a];
				float length = deltaVector.Length();
				if (length <= 0.000001f) continue;
				Vector3 correction = deltaVector * (0.5f * stiffness * (length - _edgeRest[i]) / length);
				_points[a] += correction;
				_points[b] -= correction;
			}
			if (goalPull > 0f)
			{
				for (int i = 0; i < _pointCount; i++)
				{
					float weight = Mathf.Clamp(goalPull * _goalWeights[i], 0f, 1f);
					if (weight > 0f) _points[i] = _points[i].Lerp(_targets[i], weight);
				}
			}
			if (_activeColliderCount > 0) ResolveCollisions();
			SnapBorder();
		}
		ApplyContactFriction();
	}

	private void SnapBorder()
	{
		for (int i = 0; i < _pointCount; i++)
		{
			if (!_border[i]) continue;
			_points[i] = _targets[i];
			_previous[i] = _targets[i];
		}
	}






}
