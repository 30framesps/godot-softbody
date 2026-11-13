# godot-softbody

A CPU softbody deformation addon for **Godot 4 (C#)** built for skinned characters. It re-simulates a mesh surface with Verlet integration and edge constraints, keeps vertices pinned to their skinned/blend-shaped rest pose via goal weights, collides the softbody against primitive proxies, and reconstructs a smooth render mesh each frame.

It is project-agnostic — any Godot .NET project can use it for soft-tissue, cloth or secondary-motion deformation on skinned meshes.

## Tested on

Developed and battle-tested in **PoserApp**, a Godot 4 (.NET) character posing application (skinned characters, `Skeleton3D`-driven rest poses, Jolt Physics), where it drives deforming soft surfaces on animated actors. It is not tied to PoserApp, however, and contains no application-specific code.

## Features

- **Verlet integration** with damping, gravity and a global external force (`SoftBodyEnvironment.SetForce`, e.g. wind) plus a per-node `ExternalForce` export.
- **Edge-constraint solver** with configurable iterations and stiffness, plus goal-pull toward the skinned rest pose with distance-falloff border weighting (`GoalFalloffEdges`, `GoalPull`).
- **Skinned rest pose**: targets are computed from the skin bind poses, so the softbody follows bone animation. Runs after the skeleton is posed via `Skeleton3D.SkeletonUpdated` (`UseFinalSkeletonPose`, physics priority `2000`).
- **Blend shape deltas** are captured from the source mesh and re-applied on top of the simulation.
- **Optional reduced simulation grid** (`SimulationSpacing`) re-projects render vertices onto a coarser set of simulation points.
- **Smooth output reconstruction**: neighbor-averaged normals, radius-based smoothing and Laplacian-style output smoothing (`SmoothReconstruction`, `SmoothOutput`, `OutputSmoothing*`).
- **Collision against primitives**: sphere, capsule, box and cylinder proxies sourced from `SoftBodyCollider` resources, explicit `CollisionShape3D` node paths, automatic collection under the parent subtree (`AutoCollectColliders`), and additional roots (`AdditionalColliderRoots`). Includes friction, spread, contact smoothing, a solver collision pass per iteration and a debug wireframe (`DebugColliders`).
- **Surface selection** by name (`SurfaceName`, matched against `ArrayMesh` surface names or material resource names) or index (`SurfaceIndex`).
- Source surface is hidden with a discard shader while an `ArrayMesh` output is generated and attached next to it (`HideSourceSurface`, `TargetOnly` passthrough mode).

## Requirements

- Godot **4.4+** with **.NET** support (developed against Godot 4.7 / `net8.0`).
- A project using C# (`Godot.NET.Sdk`). Any physics engine works; the PoserApp test project uses Jolt Physics.

## Installation

1. Copy `addons/godot-softbody` into your project's `addons/` directory.
2. Build your C# project (the classes compile into your game assembly).
3. Optionally enable **Godot SoftBody** under *Project → Project Settings → Plugins*. (Not strictly required for runtime use — the nodes are `[GlobalClass]` and register on compile — but it makes the addon visible in the plugin list.)

## Quick start

1. Add a `SoftBodyDeform` node as a **sibling or child of the skinned `MeshInstance3D`** whose surface should deform (e.g. breasts, belly, hair, cape cloth on a character).
2. Set `TargetMeshPath` (or leave empty to auto-find a mesh under the parent) and pick the surface via `SurfaceName` or `SurfaceIndex` — this is the surface that will be replaced by the softbody output.
3. The node simulates in physics process; make sure the skeleton node referenced by the mesh's skin actually drives the rest pose before the deform node runs (default `ProcessPhysicsPriority = 2000` and `UseFinalSkeletonPose = true` handle this for typical setups).
4. Optionally assign colliders (see below) and press play.

Typical look-and-feel comes from: `EdgeIterations = 4`, `EdgeStiffness = 1`, `GoalPull = 0.2`, `GoalFalloffEdges = 4`, `Damping = 0.99`, `CollisionSpread = 0.6`, `OutputSmoothingIterations = 2`.

## Colliders

Three ways to feed colliders, combinable:

| Mechanism | Description |
|-----------|-------------|
| `Colliders` | Array of `SoftBodyCollider` resources. Set `Shape` (any `Shape3D`), optional `BoneName` (follows a skeleton bone) and `LocalTransform` relative to the bone or mesh. |
| `ColliderPaths` | Array of `NodePath`s to `CollisionShape3D` nodes (resolved relative to the deform node, then its parent). |
| `AutoCollectColliders` | Recursively collects every `CollisionShape3D` under the parent subtree. |
| `AdditionalColliderRoots` | Array of `NodePath`s to extra subtrees to scan (resolved relative to the deform node, then its parent). |

Supported shapes: `SphereShape3D`, `CapsuleShape3D`, `BoxShape3D`, `CylinderShape3D`. Others are ignored with a warning.

When colliders are added/removed at runtime, call `RebuildColliders()` (immediate) or `QueueColliderRebuild()` (deferred).

## Global environment force

`SoftBodyEnvironment.SetForce(new Vector3(x, y, z))` sets a global acceleration added to every `SoftBodyDeform` in the process (wind, custom gravity, impulse fields). Pass `Vector3.Zero` to clear it. The change is immediate on the next physics tick; per-node `ExternalForce` is added on top.

## Public API

| Member | Description |
|--------|-------------|
| `ResetSimulation()` | Re-pins the simulation to the current rest pose and clears velocities. |
| `RebuildColliders()` / `QueueColliderRebuild()` | Rebuild the collider proxy list immediately / deferred. |
| `ActiveColliderCount` | Number of collider proxies currently in effect. |
| `SetActorActive(bool active)` | Combined with `SimulateWhenInactive`, controls whether the node keeps simulating when the owning actor/host is disabled. |
| `SoftBodyEnvironment.Force` / `SetForce()` / `ForceChanged` | Global external force shared by all instances. |

## Migrating from an inline copy (e.g. the original PoserApp scripts)

The addon is API-compatible except for three intentional decouplings:

1. **App-specific global force → `SoftBodyEnvironment.Force`**
   If your app had its own global physics-force singleton (in PoserApp: `EnvironmentPhysics.Force`, which also fed `SpringBoneSimulator3D`s), call `SoftBodyEnvironment.SetForce` alongside it during the transition. The addon deliberately only affects softbodies.
2. **Actor collider auto-collection removed.**
   The PoserApp original scanned its `ActorManager` for "primitive" actor colliders. Use `AdditionalColliderRoots` pointing at your primitives root, or call `RebuildColliders()` whenever your own collider set changes (e.g. on actor add/remove).
3. **Output mesh parenting.**
   The generated output `MeshInstance3D` is attached to the deform node's parent (or the deform node itself if parentless) instead of a custom owning-actor node type.

`.uid` files are kept from the original project, so scenes serialized against old script paths can be re-pointed at `res://addons/godot-softbody/SoftBodyDeform.cs` without losing script references.

## Notes & caveats

- Simulation cost scales with `EdgeIterations × edges` and the output-smoothing passes; use `SimulationSpacing` and `LogPerformance` to tune heavy meshes.
- `TargetOnly = true` copies targets directly to points (no dynamics) — useful for debugging the skinned/goal solve.
- Bone-scaled skeletons trigger an automatic `ResetSimulation()`.
- Collision response is positional (projection + friction), not impulse-based; it is designed for soft tissue contact, not rigid-body accuracy.

## License

MIT — see [LICENSE](LICENSE). If you publish changes, keep the copyright notice.
