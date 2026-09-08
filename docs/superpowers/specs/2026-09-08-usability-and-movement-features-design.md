# Public README, slow/fast movement, custom-entity spawning, and inter-entity collision — design

Status: approved, not yet implemented.

## Scope

This document covers five related additions on top of the existing `ClientControlledEntity` API
(spawn/despawn, straight-line move, and the ground/flying pathfinding already implemented per
`2026-09-08-3d-pathfinding-design.md`):

1. A public-facing `README.md` rewrite (usage doc for consuming mod authors).
2. Explicit slow/fast movement (`MoveToSlow`/`MoveToFast`), with per-entity speeds derived from
   the spawned entity's own AI-task JSON where possible.
3. `SpawnClientCustom` — spawning an arbitrary entity code with caller-specified animation names.
4. Terrain-aware collision avoidance between this library's own entities, and drift
   detection/recovery for idle entities that get pushed off their last commanded destination.
5. Composable movement capabilities (walk / fly / swim / climb) and a terrain preference, replacing
   the constructor's `bool isFlying` parameter from the prior pathfinding design — this supersedes
   that part of the prior design; see section 6.

## 1. README rewrite

`README.md` becomes a pure public usage document for mod authors consuming this library:
installing/referencing the built `ClientEntityAILib.dll` (the same pattern this project's own
`.csproj` already uses to reference `VSSurvivalMod.dll`/`VSEssentials.dll` — a `Reference` with a
`HintPath`, plus declaring `cliententityailib` under `dependencies` in the consuming mod's
`modinfo.json`, exactly as any inter-mod dependency in this engine works), the full public API
surface, and a minimal worked example.

The existing content (verification rationale against decompiled/real vanilla source, the
"Open research items" punch list, implementation-detail explanations like the interpolation trap
and `FindGroundY` technique) moves to `docs/design-notes.md` — preserved, but no longer the front
door for someone who just wants to use the library.

**Example:** spawn a `game:drifter-normal` a few blocks from the player, walk it through a
triangle, a square, and a circle (each shape as a chain of `MoveToFast(...)` calls linked via their
`onComplete` callbacks — showcasing both the callback pathfinding API and explicit speed
selection), then despawn it.

## 2. Explicit slow/fast movement

Verified against the actual Remedy & Ruin source this library is based on
(`GameEngineTweaks/Hallucination/DrifterBehavior.cs`): wandering uses a real `WanderSpeed = 1.2`
blocks/sec with `host.SetMoving(true, running: false)` (→ `walk`/`standwalk`), beelining toward the
player uses `BeelineSpeed = 3.0` with `running: true` (→ `run`/`standrun`) — two distinct actual
speeds, explicitly chosen by the calling AI code, never inferred from distance.

### Public API

Every existing `MoveTo` overload gets `Slow`/`Fast`-suffixed twins:

```csharp
public bool MoveToSlow(double x, double z, double? y = null);
public bool MoveToFast(double x, double z, double? y = null);
public void MoveToSlow(double x, double z, Action<bool> onComplete);
public void MoveToFast(double x, double z, Action<bool> onComplete);
public void MoveToSlow(double x, double z, double y, Action<bool> onComplete);
public void MoveToFast(double x, double z, double y, Action<bool> onComplete);
```

The existing plain `MoveTo(...)` overloads become thin wrappers that call the matching
`MoveToSlow(...)` overload — unchanged behavior for any existing caller (they already move at
whatever the "slow" tier resolves to for that entity), with no separate third speed tier to
maintain.

### Per-entity speed derivation (verified formula)

Vanilla's own creature movement speed for a given AI task (e.g. `wander`, `seekentity`) is stored
as `movespeed` in that task's JSON config — but it is **not** itself a blocks/sec figure. It is fed
into `Controls.WalkVector`, which the server's per-tick physics module (`PModuleOnGround.DoApply`)
converts into real velocity through a damped exponential-approach recurrence:

```
motionDelta += (walkX - motionDelta) × belowBlockDragMultiplier   // per 1/60s substep
motion = (motion + motionDelta) × groundDrag                      // per 1/60s substep, groundDrag = 1 - groundDragFactor
```

This recurrence's steady state is `motion* = walkX × groundDrag / (1 - groundDrag)`. The final
piece — easy to miss, and the actual source of an earlier 60× error during this design's own
derivation — is that position updates use `dtFactor = dt × 60`, not `dt` directly
(`EntityBehaviorControlledPhysics.cs`), so the true real-world rate is `motion* × 60`. Combined and
simplified, with `walkX = movespeed × OverallSpeedMultiplier` (`OverallSpeedMultiplier = 1.0`
by default) and the standard-case `GetWalkSpeedMultiplier() = 1.0` (not sneaking/sprinting/in
liquid):

```
blocks/sec = movespeed × 60 × (1 − groundDragFactor) / groundDragFactor
```

Verified numerically (a direct tick-by-tick simulation of the real recurrence, not just the closed
form) and cross-checked against observed in-game drifter movement.

**Where the inputs come from, generically (same schema for any entity, vanilla or modded):**
- `groundDragFactor`: `EntityProperties.Attributes["physics"]["groundDragFactor"]`, read as a
  multiplier on `0.3` (defaulting to `1.0`, i.e. effectively `0.3`) — matches
  `PModuleOnGround.Initialize`'s own `groundDragFactor = 0.3 * config["groundDragFactor"].AsDouble(1.0)`.
- `movespeed`: found by walking `EntityProperties.Server.BehaviorsAsJsonObj` for the behavior with
  `code == "taskai"`, then its `aitasks` array — **SlowSpeed** from the first task with
  `code == "wander"`, **FastSpeed** from the first task with `code == "seekentity"`.
- **Fallback, independently per tier:** if `Server`, `BehaviorsAsJsonObj`, the `taskai` behavior, or
  a matching task is missing, that tier falls back to the Remedy & Ruin constants
  (`SlowSpeed = 1.2`, `FastSpeed = 3.0`) rather than failing the spawn. A minimal custom entity
  with no AI-task JSON at all still works exactly as before this feature existed.

This derivation happens once, at spawn time (`SpawnClient`/`SpawnClientCustom`), and is stored as
per-instance `slowSpeed`/`fastSpeed` fields — replacing the single global `DefaultSpeed` constant
the movement-stepping code previously used uniformly for every entity.

## 3. `SpawnClientCustom` — arbitrary entity codes with caller-defined animations

```csharp
public class AnimationKeycodes
{
    public string Idle;
    public string MoveSlow;
    public string MoveFast; // falls back to MoveSlow's code when left null/empty
}

public bool SpawnClientCustom(string entityCode, Vec3d spawnPos, AnimationKeycodes animKeycodes);
```

`SpawnClient(entityCode, spawnPos)` (the existing method) becomes a thin wrapper calling
`SpawnClientCustom` with the vanilla-convention defaults it already used
(`Idle = "idle"`, `MoveSlow = "walk"`, `MoveFast = "walk"`) — no duplicated spawn logic, no
behavior change for existing callers.

Everything else needed to drive an arbitrary entity code (`EntityAgent` detection for
`Controls.WalkVector`/`BodyYawServer`, `GetBehavior<EntityBehaviorInterpolatePosition>()`,
`entity.CollisionBox`, the generic step-height/fall-height defaults `GroundWalkingProfile` already
uses) is already entity-agnostic in the existing code — nothing else needed changing for "any
entity, including ones this library has never seen before" to work.

### Animation state machine

`SetMoving(bool moving)` becomes a three-state selector driven by which move tier is currently
active (idle / slow / fast), picking the matching `AnimationKeycodes` field, with the same
best-effort `AnimManager.StartAnimation` no-op-on-failure behavior already in place — an
entity type missing one of the three named animations just doesn't animate for that state, it
doesn't error.

## 4. Collision avoidance (push-apart) between library entities

Scoped to this library's own entities only — real vanilla entities, the player, and blocks are
never involved. A static registry (`List<ClientControlledEntity>`, added on successful spawn,
removed as part of `Despawn()`'s already-established reentrancy-safe teardown ordering) that every
handle's `OnGameTick` checks against.

Each tick, for every *other* active handle, if the horizontal distance between the two entities'
`logicalPos` is less than the sum of their `CollisionBox` XZ half-extents (real per-entity-type
data already available, not a guess), a small separation vector pushes both apart along the line
between them, added directly to `logicalPos`.

Runs for both idle and actively-moving entities: for a mover, it's a minor perpendicular
correction it naturally keeps re-correcting for as it continues stepping toward its current
target every tick; for an idle entity, it is the *only* thing that can displace it, which is why
section 5 exists.

**Terrain-aware, via axis-separated sliding.** The push is not applied blindly. The X and Z push
components are tested independently against terrain (`CollisionTester.IsColliding` on the entity's
own `CollisionBox`, the same primitive `GroundWalkingProfile`/`FlyingProfile` already use for
traversability): each axis is applied only if moving along it alone doesn't collide; an axis whose
push would collide is zeroed out instead, so an entity pushed toward a wall slides along it rather
than clipping through. If *both* axes are individually blocked, the push is skipped entirely for
that tick (the other entity involved may still move away, naturally resolving the overlap once
there's room). This reuses the exact same collision primitive already proven throughout this
library, not a new mechanism.

## 5. Drift detection and recovery

Every `MoveTo*`/`MoveToSlow`/`MoveToFast` call (any overload) records `lastCommandedDestination`.
A new, separate low-frequency poll (~1 second interval, not every game tick) runs **only while the
entity is idle** (no active `hasMoveTarget`/`activeWaypoints`) and compares current `logicalPos`
against `lastCommandedDestination` via full 3D distance. Past a **1.5-block threshold** (within
the requested 1–2 block range), it triggers a corrective move back to
`lastCommandedDestination`, via `MoveToSlow(..., callback)` (pathfinding, not the straight-line
overload, since the entity may have been pushed near an obstacle) — a minor corrective nudge, not
an alarmed beeline, hence the slow tier.

This mechanism only matters in combination with section 4: an actively-moving entity is already
correcting toward its own target every tick regardless of small pushes, so drift detection would
never trigger for it; it exists specifically for entities section 4 can otherwise strand with no
other force acting on them.

## 6. Composable movement capabilities and terrain preference

Replaces the prior pathfinding design's single `bool isFlying` with two independent, composable
inputs — a bitmask of physical capabilities, and a soft preference between terrain types that only
matters when capabilities overlap:

```csharp
[Flags]
public enum MovementType
{
    CanWalk = 1,
    CanFly = 2,
    CanSwim = 4,
    CanClimb = 8
}

public enum TerrainPreference { Water, Land, Both }

public ClientControlledEntity(ICoreClientAPI capi, MovementType movementType, TerrainPreference terrainPreference = TerrainPreference.Both, int? pathfindingSearchDepth = null);
```

`movementType` is **required** (no default) — a caller must state what this entity can physically
do. `terrainPreference` defaults to `Both` (no bias). This is a breaking change to the constructor
introduced by the prior pathfinding design, accepted because that design hasn't shipped/merged yet.

### What each flag means, and how they compose

`MovementType` is a **hard legality gate**: it determines which node types are physically possible
for this entity to occupy at all, not a preference. A `MovementType.CanSwim`-only entity (a fish)
is already incapable of leaving water — dry land is never traversable, with no need for
`terrainPreference` to enforce it. `TerrainPreference` only ever does anything for an entity whose
`MovementType` makes *multiple* terrain types legal (e.g. `CanWalk | CanSwim`, amphibious) — it
biases search cost toward routes through the preferred terrain when a comparably-short route
through either exists; `Both` leaves cost unbiased.

**Which graph shape the search uses:**
- `CanFly` or `CanSwim` set → the full 26-directional 3D grid (`FlyingProfile`'s existing
  neighbor/corner-cutting logic) is the base, since either capability needs free vertical movement
  the 8-directional ground graph doesn't offer.
- Neither set (only `CanWalk` and/or `CanClimb`) → the 8-directional ground graph
  (`GroundWalkingProfile`'s existing step-up/fall-down logic) is the base.

**Per-node legality when the 3D grid is the base** (this is what makes amphibious/mixed
combinations behave correctly rather than just "flying that happens to touch water"):
- A liquid destination node is legal only if `CanSwim` is set.
- A non-liquid, open-air destination node is legal only if `CanFly` is set.
- A non-liquid destination node resting directly on solid ground is legal if `CanFly` **or**
  `CanWalk` is set — this is what lets a `CanWalk | CanSwim` entity (no `CanFly`) still cross a
  short stretch of dry land between two water bodies by walking along the ground plane within the
  3D grid, rather than being just as air-restricted as a pure swimmer.
- `TerrainPreference` adds cost bias only between nodes that are legal by more than one of the
  above rules for this entity's capability set.

**`CanClimb`** is a small additive extension on top of whichever base graph is active: two extra
vertical-only neighbor offsets (straight up, straight down, same X/Z), legal when the destination
cell is clear (`CollisionTester.IsColliding`, the same primitive used everywhere else in this
library) **and** at least one horizontally-adjacent cell is solid — something to cling to.

**`pathfindingSearchDepth` default:** `8000` if `CanFly` or `CanSwim` is set (26-directional
branching factor), else `4000` (8- or 10-directional). `CanClimb` alone does not trigger the higher
default — it only adds 2 neighbors, not a whole extra dimension of branching. The constructor's
search-cost warning (previously tied to `isFlying: true`) fires whenever the 8000 default applies.

## Out of scope

- Auto-deriving animation names from each AI task's own `animation` field (confirmed present in
  the same JSON schema, e.g. `wander`'s `"animation": "standwalk"`) — `AnimationKeycodes` stays a
  caller-provided value per this session's request; this is a natural future extension, not built
  now.
- Per-call profile override (the movement profile is still fixed per handle, chosen at
  construction) — unchanged from the prior pathfinding design's own "out of scope" list.
