# 3D pathfinding for `MoveTo(x, z, y)` — design

Status: approved, not yet implemented.

## Purpose

`ClientControlledEntity.MoveTo` currently has two overloads (already implemented): a synchronous
straight-line, terrain-following mover that walks face-first into obstacles. This document adds
real obstacle-aware pathfinding — both ground-walking and flying — as new async, callback-based
overloads, resolving Open Research Item #1 from the mod's main `README.md`.

## Verified starting point

Vanilla's own creature pathfinder (`AStar` in `VSEssentials.dll`, an assembly this project already
references) is pure block-graph search — no server-only physics or network state involved. Its
supporting types are all public and reusable as-is from client code:

- `Vintagestory.Essentials.PathNode` / `PathNodeSet` — open/closed set and node bookkeeping.
- `Vintagestory.API.MathTools.Cardinal` — the 8 horizontal movement directions.
- `Vintagestory.API.MathTools.CollisionTester` — entity-vs-block collision checks.
- `ICachingBlockAccessor`, obtained via `IWorldAccessor.GetCachingBlockAccessor(synchronize, relight)`
  — declared on the shared `IWorldAccessor` interface (not server-only), and backed by the same
  `BlockAccessorCaching` implementation on both sides (`GameMain.GetCachingBlockAccessor`, the
  shared base class of `ClientMain`/`ServerMain`). The `synchronize: true` flag is exactly what
  vanilla's own off-thread pathfinding instance (`PathfindingAsync.astar_offthread`) relies on to
  read chunk data safely off the main thread — the same guarantee applies to the client's copy.

The outer `AStar` class itself is **not** directly reusable — its constructor and a few call sites
hardcode `ICoreServerAPI` (logging, RNG). This mod ports the outer search loop
(`FindPathOrEscapePath`/`traversable`) to `ICoreClientAPI`, while reusing the public pieces above
verbatim.

Vanilla's `WaypointsTraverser` (the class that walks a real creature along a found path) is **not**
reused — it's wired into server-side AI task infrastructure (`PathfindingAsync`,
`IPhysicsTickable`-based controls) the same way the original spawn/move code already ruled out
elsewhere in this mod. Waypoint-following instead extends this mod's own existing manual mover
(`OnGameTick`/`PushPosition`/`SetMoving`).

Vanilla has **no flying creature type** — `EnumAICreatureType` is `Default` / `LandCreature` /
`Humanoid` / `HeatProofCreature` / `SeaCreature` only. The flying pathfinding profile below is
therefore original design, built on vanilla's proven low-level primitives (`CollisionTester`,
`Block.GetTraversalCost`) but not a port of any existing vanilla algorithm. This is called out
explicitly in the code comments per this repo's verification policy.

## Public API additions

```csharp
public class ClientControlledEntity : IDisposable
{
    // NEW constructor parameters; existing single-arg constructor behavior unchanged when both are omitted.
    public ClientControlledEntity(ICoreClientAPI capi, bool isFlying = false, int? pathfindingSearchDepth = null);

    // Existing overloads - unchanged, still synchronous straight-line + terrain-follow.
    public bool SpawnClient(string entityCode, Vec3d spawnPos);
    public bool MoveTo(double x, double z, double? y = null);
    public void Despawn();

    // NEW: real pathfinding, target Y auto-resolved via a ground/terrain scan at (x, z).
    public void MoveTo(double x, double z, Action<bool> onComplete);

    // NEW: real pathfinding to an exact 3D point.
    public void MoveTo(double x, double z, double y, Action<bool> onComplete);
}
```

`isFlying` selects the traversal profile (`GroundWalkingProfile` vs `FlyingProfile`, see below) used
by both new overloads for this handle's lifetime. It is not a per-call choice.

`pathfindingSearchDepth`, when `null`, resolves to a type-appropriate default:

| `isFlying` | Default node budget |
|---|---|
| `false` | 4000 |
| `true`  | 8000 |

An explicit value is always used as-is, regardless of `isFlying` — it is never silently doubled on
top of a caller's explicit choice.

**Callback contract:** `onComplete(bool)` fires exactly once per `MoveTo(...,callback)` call:
- `true` — the entity reached the destination.
- `false` — no path was found within the node budget (indistinguishable from "genuinely
  unreachable," matching vanilla's own behavior), the handle was despawned before arrival, or a
  newer `MoveTo` call (any overload) superseded this one before it completed. No callback fires
  for a call that gets superseded by a later one — only the most recent call's callback ever
  fires.
- If no entity is currently spawned when `MoveTo(...,callback)` is called, `onComplete(false)`
  fires synchronously and no search is started — mirrors the existing overloads' `false` return
  for "no entity active."

## Node budget → real-world range (conversion math)

Each A* node roughly corresponds to one block-step in the search graph. In the *best case* — a
straight, unobstructed line across open terrain — the heuristic keeps the search tight along the
direct path, so nodes explored ≈ path length in blocks. That best case is not typical: every
obstacle, elevation change, or dead-end the search has to route around multiplies nodes explored
past the direct-line distance (an **exploration factor**):

```
nodesNeeded ≈ pathLengthInBlocks × explorationFactor
```

Ground-walking (8-directional), default 4000 nodes:

| Terrain | Exploration factor | Realistic range |
|---|---|---|
| Open flat ground, few obstacles | ~1–3× | ~1300–4000 blocks |
| Rolling hills / scattered trees / mild terrain | ~5–10× | ~400–800 blocks |
| Dense forest, cave systems, buildings/indoors | ~15–40×+ | ~100–270 blocks, sometimes far less |

Flying (26-directional) roughly triples the branching factor per node versus ground's 8-directional
search, so for an *equivalent* terrain-complexity exploration factor, real-world range at the same
node count is roughly a third of the ground table above. The 8000-node flying default therefore
lands in a similar practical range to the 4000-node ground default, not double it — the doubled
budget compensates for the larger branching factor rather than extending range beyond ground's.

This table (and the branching-factor note) lives in the XML doc comment on the constructor's
`pathfindingSearchDepth` parameter, and in the mod's `README.md`.

## Architecture and data flow

```
MoveTo(x, z, [y,] callback)
  │
  ├─ if entity == null: callback(false) synchronously, return
  ├─ resolve target Y if omitted (ground scan at column x,z, reusing the existing FindGroundY technique)
  ├─ moveGeneration++; capture myGeneration = moveGeneration
  ├─ pendingCallback = callback   (replaces any previous pending callback without invoking it)
  ├─ Task.Run(() => {
  │       var blockAccess = capi.World.GetCachingBlockAccessor(synchronize: true, relight: true);
  │       var astar = new ClientAStar(capi, blockAccess, profile, pathfindingSearchDepth);
  │       return astar.FindPath(startBlockPos, targetBlockPos, entityCollisionBox);
  │   })
  └─ .ContinueWith(result => {
         if myGeneration != moveGeneration: discard (stale - a newer call has already taken over)
         else if result == null: pendingCallback?.Invoke(false); pendingCallback = null
         else: activeWaypoints = result; waypointIndex = 0
               // pendingCallback now fires later, from OnGameTick, when the last waypoint is reached
     })
```

`OnGameTick` (already the per-tick mover for the existing straight-line overloads) is extended to
optionally chase an ordered waypoint list instead of a single point: advance to the next waypoint
once within arrival distance of the current one; on reaching the last waypoint, invoke
`pendingCallback?.Invoke(true)` and clear it. No duplication of the interpolation-feed or animation
logic — a waypoint list is just a queue of `(x,z,y)` targets fed through the same per-segment
stepping code the existing overloads already use.

Any `MoveTo` call (including the existing synchronous overloads) and `Despawn()` both clear
`activeWaypoints` and bump `moveGeneration`, cancelling any in-flight search's eventual effect and
any pending callback that hasn't fired yet.

## `ClientAStar` — the ported search

New file, `Pathfinding/ClientAStar.cs`. Structurally a direct port of `AStar.FindPathOrEscapePath` /
`traversable` from `VSEssentials`, with `ICoreServerAPI` replaced by `ICoreClientAPI` and RNG/logging
call sites adjusted accordingly. Reuses `PathNode`, `PathNodeSet`, `Cardinal`, `CollisionTester`
directly rather than reimplementing them. The `searchDepth` node-count cap (already present in
vanilla's algorithm — the loop aborts and returns "no path" once nodes-checked exceeds it) is the
bounded-computation mechanism, sourced from `ClientControlledEntity`'s `pathfindingSearchDepth`.

The neighbor-expansion and per-node traversability rules are pulled behind a small profile
interface so ground and flying search share the same outer loop:

```csharp
internal interface ITraversalProfile
{
    IEnumerable<PathNode> GetNeighbors(PathNode from);
    bool IsTraversable(PathNode from, PathNode node, Cuboidf entityBox,
                        ICachingBlockAccessor blockAccess, ref float extraCost);
}
```

### `GroundWalkingProfile` (ported vanilla logic)

- Neighbors: the 8 `Cardinal` directions (horizontal only).
- Traversability: vanilla's `traversable()` verbatim — step-height/fall-height handling, liquid
  cost via `Block.GetTraversalCost`, and the existing horizontal diagonal corner-cutting guard.

### `FlyingProfile` (original design — see "Verified starting point" above)

- Neighbors: all 26 combinations of `{-1,0,1}` in X/Y/Z except `(0,0,0)`.
- Traversability:
  1. Entity collision box must not collide with the world at the destination cell
     (`CollisionTester.IsColliding`, `alsoCheckTouch: false`).
  2. **3D corner-cutting guard:** for any diagonal move (2 or 3 axes nonzero), every cell sharing a
     face with both the start and end cell along the move must also be clear — generalizes the
     single horizontal-corner check `GroundWalkingProfile` already makes.
  3. Cost: `Block.GetTraversalCost` applied the same way ground movement uses it (hazard blocks
     such as lava still return a cost `>10000` and block the node).
  - No step-height/fall-height logic — a flying node is either passable or it isn't.

`ClientControlledEntity` always constructs `ClientAStar` with `GroundWalkingProfile` unless
`isFlying: true`, in which case it uses `FlyingProfile` and logs a one-time
`Mod.Logger.Warning(...)` noting that 26-directional search has roughly 3x the branching factor of
ground search per node, so equivalent search depths take noticeably longer to resolve.

## Error handling and cancellation

- **Superseding calls:** see "Architecture and data flow" — generation counter, last call wins.
- **Despawn() mid-search:** bumps `moveGeneration`, clears `activeWaypoints`, and clears `entity`
  and every other bit of instance state *before* invoking any pending callback with `false` — a
  pending callback is arbitrary caller code and may call back into this same handle (e.g.
  `Despawn()` or `MoveTo(...)` again); by the time it runs, the handle already looks fully
  despawned, so a reentrant call safely no-ops via each method's own `entity == null` guard
  instead of racing the outer `Despawn()` call's own teardown. A search still running on the
  background thread finishes normally but its result is discarded on completion since
  `moveGeneration` no longer matches.
- **No path found:** `ClientAStar.FindPath` returns `null` (budget exceeded or genuinely
  unreachable — indistinguishable, matching vanilla) → `callback(false)`, entity holds position.
- **No entity spawned:** `callback(false)` synchronously, no search started.

## Testing

No unit-test harness exists for this mod (hard dependency on live `ICoreClientAPI`/chunk data, no
mocking layer for `IWorldAccessor` — same constraint as the rest of the mod). Verification is
manual, in-game:

1. Spawn a ground entity behind an obstacle (wall, hill, small building); confirm
   `MoveTo(x,z,y,callback)` routes around it instead of the straight-line walk-into-wall behavior
   the existing overloads have.
2. Spawn a flying entity (`isFlying: true`) and path it through a gap that requires vertical
   movement (e.g. over a wall, through a window); confirm it climbs/descends as part of the path
   rather than only moving horizontally.
3. Confirm the callback fires exactly once per call, including: normal success, superseded by a
   later `MoveTo` call, and `Despawn()` mid-search.
4. Target a deliberately unreachable point (sealed room, or a point requiring more than
   `pathfindingSearchDepth` nodes) and confirm `callback(false)` fires within a bounded time rather
   than hanging.
5. Confirm no frame hitches occur while a search is in flight (search runs on a background
   `Task`, not the main/render thread).

## Out of scope

- Swimming pathfinding (a third profile) — not requested this session.
- Per-call profile override (`isFlying` is fixed per handle, set at construction).
- Any change to the existing synchronous `MoveTo(x,z)` / `MoveTo(x,z,y)` overloads' documented
  behavior.
