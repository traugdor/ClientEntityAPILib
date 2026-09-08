# ClientEntityAI — design & implementation handoff

Status: **not yet implemented.** This document is the complete spec for building ClientEntityAI as
its own standalone Vintage Story mod. Everything in it is either verified against the actual
shipped game files/decompiled source, or is a proven, already-working implementation pulled
directly out of the Remedy & Ruin mod's Hallucination apparition system (`GameEngineTweaks/
Hallucination/` in that mod) — the same trick, generalized into a reusable library instead of one
mod's private feature. Anything that is *not* yet proven is called out explicitly under "Open
research items" rather than presented as settled.

## What this mod is for

A general-purpose library other mods can depend on to spawn and drive **client-only** entities —
entities that render, animate, and move like real creatures, but exist only in one player's local
client memory. They are never sent to the server, never seen by other players, and are not real
gameplay entities (no server-side health, AI, or combat). Typical use case: ambient/cosmetic
creatures, illusions, ghosts, previews, or any other visual-only actor a mod wants full manual
control over without writing a real server-side creature.

## Public API

Exactly two public methods, both on one class, `ClientControlledEntity`. A calling mod constructs
one instance per entity it wants to control.

```csharp
public class ClientControlledEntity
{
    public ClientControlledEntity(ICoreClientAPI capi);

    /// <summary>
    /// Registers and spawns a client-only entity of the given entity code at spawnPos. Returns
    /// true if the entity type was found and the entity was created and rendered successfully;
    /// false otherwise (bad entity code, or this instance already has an active entity - call
    /// Despawn first to reuse the handle).
    /// </summary>
    public bool SpawnClient(string entityCode, Vec3d spawnPos);

    /// <summary>
    /// Moves the entity toward (x, z), following the real terrain surface vertically. If y is
    /// given, the target is a full 3D point and the entity paths to it without moving through
    /// terrain (walls, floors, ceilings) rather than just following the ground under a straight
    /// line. Returns true if pathing to the destination is possible (or, for the 2D overload,
    /// simply that an entity is active to move); false if no entity is active, or - only when y
    /// is given - no path to the destination could be found.
    /// </summary>
    public bool MoveTo(double x, double z, double? y = null);
}
```

Everything else (registering the entity type lookup, animation switching, facing, despawning) is
internal detail the calling mod never has to touch. A `Despawn()`/`Dispose()` method is also
needed for correct cleanup (see "Lifecycle" below) — the user's spec named only these two as the
*driving* API, but a handle that owns a live client entity still needs a way to clean it up, so
this document treats that as required plumbing rather than a third feature.

```csharp
public void Despawn(); // or IDisposable.Dispose() - not part of "the two", but required for cleanup
```

### Design decisions made to fill gaps in the spec

- **`SpawnClient` takes the spawn position as a parameter.** There's no reasonable way to spawn an
  entity without saying where — Remedy & Ruin's own apparition system derives spawn position from
  camera direction, which is specific to *that* mod's "in front of the player" use case, not a
  general default. The calling mod decides the position.
- **One `ClientControlledEntity` instance = one entity, for its whole lifetime (until despawned).**
  `SpawnClient` returning `bool` rather than a handle only makes sense if the object it was called
  on *is* the handle — so the calling mod constructs the object first, then calls `SpawnClient` on
  it, then calls `MoveTo` on the same object. This matches the "entity.SpawnClient(entityname)"
  phrasing in the original spec.

## Why the normal spawn/move APIs don't work here

Verified against the decompiled client source (`ClientMain`, in `Vintagestory.Client.NoObf`):

- `IWorldAccessor.SpawnEntity(entity)` is an **empty method body** on the client. It does nothing.
- `IWorldAccessor.LoadEntity(entity, chunkIndex)` **throws** `InvalidOperationException("Cannot use LoadEntity on the Client side")` on the client.
- There is no client-side AI/pathfinding tick at all: `EntityBehaviorControlledPhysics` implements
  `IPhysicsTickable`, and the only thing that ever ticks `IPhysicsTickable` is
  `Vintagestory.Server.PhysicsManager` — physics, and by extension any AI built on top of it, is
  **100% server-only**. A client-only entity gets none of that for free; this mod has to drive
  position, facing, and animation by hand, every frame.

So this mod has to do two independent things itself: (1) get an entity object registered and
rendering at all, and (2) manually simulate its movement, since nothing else will.

## Part 1: spawning (`SpawnClient`)

The real, verified working path — pulled directly from Remedy & Ruin's
`HallucinationApparition.cs` constructor, which currently ships and works:

```csharp
// Step 1: build the entity object. Use a negative EntityId - real server-assigned IDs are
// always positive, so negative values are a safe, collision-free convention for fake/local-only
// entities. Keep a shared decrementing counter across every spawned handle in the mod.
EntityProperties props = capi.World.GetEntityType(new AssetLocation(entityCode));
if (props == null) return false; // bad entity code

Entity entity = capi.World.ClassRegistry.CreateEntity(props);
entity.EntityId = nextFakeEntityId--;
entity.Pos.SetPos(spawnPos);
entity.Initialize(props, capi.World.Api, 0);

// Step 2: add it to the client's real entity list. IClientWorldAccessor.LoadedEntities is a
// public dictionary - most mod code never touches it directly, but it's public.
((IClientWorldAccessor)capi.World).LoadedEntities[entity.EntityId] = entity;

// Step 3: tell the renderer system the entity is ready. capi.Event's concrete type is
// ClientEventAPI, a thin wrapper that does NOT expose TriggerEntityLoaded - the real event
// manager is a public field, `eventManager`, on ClientMain itself, reached via a cast.
ClientMain game = (ClientMain)capi.World;
game.eventManager.TriggerEntityLoaded(entity);
```

After step 3, `ClientSystemEntities` (which listens for this event) builds a real `EntityRenderer`
for the entity and registers it with the client's normal rendering pipeline. From that point on,
the entity renders every frame automatically — correct mesh, animation, lighting, shading, frustum
culling — with zero manual GL/render code needed.

**Why other players never see it:** it's never added to any server-side chunk entity list, and no
network packet is ever sent about it. No other client's `LoadedEntities` ever hears about it. This
is structural privacy, not a visibility flag.

**Failure conditions for `SpawnClient` (return `false`):**
- `capi.World.GetEntityType(entityCode)` returns null (bad/unknown entity code).
- This handle already has a live entity (caller must `Despawn()` first).

**Caveat:** this depends on `Vintagestory.Client.NoObf` (`ClientMain`, `ClientEventManager`) —
public classes, but not part of the documented/supported modding API. Re-verify after game
updates. (Confirmed still correct as of game version 1.22.7 at the time this document was written.)

## Part 2: despawning (needed for `Despawn`/cleanup, not one of the two public methods but required)

Mirrors the real despawn-packet handler, in the same order:

```csharp
EntityDespawnData despawnData = new EntityDespawnData { Reason = EnumDespawnReason.Removed };
game.eventManager.TriggerEntityDespawn(entity, despawnData);
game.RemoveEntityRenderer(entity); // public method on ClientMain
entity.OnEntityDespawn(despawnData);
((IClientWorldAccessor)capi.World).LoadedEntities.Remove(entity.EntityId);
```

Call this from the handle's `Despawn()`/`Dispose()`, and from the owning `ModSystem.Dispose()` for
every handle still alive when the mod unloads (don't leak client entities across a mod reload).

## Part 3: movement (`MoveTo`)

### The interpolation trap (read this first)

Most vanilla creatures (check the entity's own JSON for `"code": "interpolateposition"` under
`client.behaviors`) have `EntityBehaviorInterpolatePosition` attached automatically. It's a
client-only behavior whose whole job is smoothing an entity's rendered position/rotation between
real server updates. It runs every frame, at `EnumRenderStage.Before`, **before** anything else
touches the entity that frame, and it overwrites `Pos.Yaw`, `Pos.Pitch`, `Pos.Roll`, and a
separate field, `agent.BodyYaw` (not `Pos.Yaw` — this is the field that actually drives rendered
body-facing for `EntityAgent`s), toward whatever target its own `OnReceivedServerPos` was last
given.

**Do not write `Pos.Yaw` directly every frame** — this behavior will silently overwrite it on the
very next frame, and the entity will appear frozen or turn incorrectly. Instead, feed it, the same
way a real server position update would:

```csharp
entity.Pos.SetPos(targetPos);
entity.Pos.Yaw = targetYaw;
if (entity is EntityAgent agent) agent.BodyYawServer = targetYaw;

EnumHandling handling = EnumHandling.PassThrough;
var interp = entity.GetBehavior<EntityBehaviorInterpolatePosition>();
interp?.OnReceivedServerPos(isTeleport: false, ref handling);
```

Call this at roughly **15 times per second**, not every render frame. The behavior assumes a
~15-updates/sec cadence internally (its own hardcoded `interval` constant, `1f/15f`) and queues one
`PositionSnapshot` per call. Feeding it at 60/sec (every frame) floods its internal queue faster
than it drains, and once the queue exceeds 20 entries its own safety valve forces a jarring
catch-up snap. Accumulate `dt` and only push once the accumulator crosses `1f/15f`.

If `EntityBehaviorInterpolatePosition` isn't present on this entity type, fall back to writing
`Pos.Yaw`/`Pos.SetPos` directly (an instant snap, no smoothing) — harmless, just less polished.

### Facing

```csharp
float desiredYaw = (float)Math.Atan2(toTarget.X, toTarget.Z);
```

Verified against the real vanilla movement code (`StraightLineTraverser.cs:72`,
`desiredYaw = Atan2(targetVec.X, targetVec.Z)`) — this exact axis order, not `Atan2(Z, X)`.

### Walk animation direction

`entity.Controls.WalkVector` (on `EntityAgent`) is what vanilla's own movement/animation-adjacent
code reads to judge movement direction and speed for blending the walk cycle. Setting only
`Pos`/`logicalPos` is not enough — without also setting this, the legs animate independently of
the body's actual movement direction ("moonwalking"):

```csharp
if (entity is EntityAgent agent)
{
    agent.Controls.WalkVector.Set(toTarget.X / dist * speed, 0, toTarget.Z / dist * speed);
}
```

### Track your own ground-truth position

Once you're feeding `OnReceivedServerPos`, `entity.Pos` becomes a *smoothed, lagging* value, not
the true current position — don't read it back as your movement source of truth. Keep your own
plain `Vec3d logicalPos` field per handle, update *that* each step, and push it into the
interpolator. This is exactly what `HallucinationApparition.logicalPos` does.

### Terrain following (vertical) — verified, not a heightmap

**Do not use `IBlockAccessor.GetTerrainMapheightAt`** — verified against the engine's own XML
docs: it's "the topmost solid surface position... as it was during world generation. This map is
not updated after placing/removing blocks." Useless in any already-modified world.

**Do not use `IBlockAccessor.GetRainMapHeightAt` either**, despite it being live-updated — it's
still a column heightmap, so it returns the topmost *sky-facing* surface. Underground or indoors,
that's a cave roof or building roof, not the actual floor the entity should be walking on.

**The correct technique**, verified against vanilla's own real `AiTaskWander.MoveDownToFloor`
(used by real drifters/shivers/bowtorns server-side) — a short local downward scan anchored near a
*known* Y, not a top-down heightmap scan from the sky:

```csharp
internal static double FindGroundY(ICoreClientAPI capi, double x, double aroundY, double z)
{
    int bx = (int)x, bz = (int)z;
    int y = (int)Math.Ceiling(aroundY) + 1; // allow stepping up slightly
    int tries = 8; // vanilla's own MoveDownToFloor uses 5; a little extra margin here is a deliberate, undocumented tuning choice, not a verified vanilla value
    while (tries-- > 0)
    {
        if (capi.World.BlockAccessor.IsSideSolid(bx, y, bz, BlockFacing.UP)) return y + 1;
        y--;
    }
    return aroundY; // nothing solid found nearby - hold position rather than snapping somewhere wrong
}
```

Call this every step, anchored at the entity's own current logical Y (not the sky, not a fixed
spawn height) — this is what makes it work correctly both outdoors on hills and underground/indoors.

### Putting a movement step together

```csharp
Vec3d toTarget = new Vec3d(targetX - logicalPos.X, 0, targetZ - logicalPos.Z); // horizontal only
double dist = toTarget.Length();
float desiredYaw = dist > 0.01 ? (float)Math.Atan2(toTarget.X, toTarget.Z) : entity.Pos.Yaw;

if (dist > 0.01)
{
    double step = Math.Min(speed * dt, dist);
    logicalPos.X += toTarget.X / dist * step;
    logicalPos.Z += toTarget.Z / dist * step;
    logicalPos.Y = FindGroundY(capi, logicalPos.X, logicalPos.Y, logicalPos.Z);

    if (entity is EntityAgent agent)
        agent.Controls.WalkVector.Set(toTarget.X / dist * speed, 0, toTarget.Z / dist * speed);
}

// push logicalPos + desiredYaw through OnReceivedServerPos, throttled to ~15/sec (see above)
```

**Important:** `toTarget` must stay horizontal (Y = 0) here. If Y is left non-zero, `dist` becomes
a 3D distance that gets inflated by however far the entity's Y has drifted from the target's
stated Y — which corrupts both the "have I arrived" check and the normalized step direction. Do
the horizontal delta and the vertical (terrain-follow) grounding as two separate concerns.

### The `MoveTo(x, z)` overload (no y given)

This is exactly the loop above — horizontal target, terrain-height-following, straight-line. It
does **not** avoid obstacles (walls, cliffs it can't climb) — it will walk face-first into a wall
and keep trying. Always returns `true` once an entity is active (there's no reachability check in
this overload — see the next section for why).

### The `MoveTo(x, z, y)` overload — real pathfinding, NOT YET DESIGNED HERE

This is the one place where this document does **not** hand over a proven implementation, because
none exists yet anywhere in this project. "Paths to it without moving through terrain" requires
genuine obstacle-aware pathfinding (walls, ceilings, drops), which the straight-line + terrain-
follow technique above does not provide. See "Open research items" below — this needs to be solved
before `MoveTo`'s 3-argument overload can honestly return `false` for an unreachable point rather
than just walking into a wall and stalling.

## Part 4: animation

This mod cannot assume specific animation names — the concrete implementation this document is
based on (Remedy & Ruin) hardcodes `"walk"`/`"run"`/`"idle"` and per-creature attack codes because
it only ever spawns three known creature types. A general-purpose library controlling *arbitrary*
entity codes can't do that safely.

**Recommended approach:** attempt the vanilla-convention codes only (`"idle"`, `"walk"`, `"run"`)
via `entity.AnimManager.StartAnimation(code)` — this method returns `false` rather than throwing on
an unrecognized code, so trying and silently no-op'ing on failure is safe. Document plainly that
animation switching is **best-effort**, not guaranteed correct for arbitrary custom creatures with
non-standard animation names (e.g. the real drifter's crawl-state remapping table is
creature-specific and this library has no general way to discover or replicate that).

```csharp
private void SetMoving(bool moving)
{
    if (moving == isMoving) return;
    isMoving = moving;
    if (activeAnim != null) { entity.AnimManager.StopAnimation(activeAnim); activeAnim = null; }
    entity.AnimManager.StopAnimation(moving ? "idle" : "walk"); // stop whichever's live going the other way; harmless no-op if not
    string code = moving ? "walk" : "idle";
    if (entity.AnimManager.StartAnimation(code)) activeAnim = moving ? code : null;
}
```

## Lifecycle

- One `ClientControlledEntity` = one live client entity at a time. `SpawnClient` fails (`false`)
  if called again while already spawned.
- The owning `ModSystem` (or whatever object owns the `ClientControlledEntity` instances) must
  track every live handle and call `Despawn()` on all of them from its own `Dispose()` — otherwise
  a mod reload leaks entities that were never told to unregister.
- Nothing about this system persists across a game restart or reconnect — that's expected and
  correct; these entities were never meant to be real, persistent objects.

## Known limitations (be upfront about these to consumers of this mod)

- **No real collision.** These entities don't push against blocks, other entities, or the player -
  there's no server physics tick driving that. `MoveTo` without pathfinding can walk an entity
  into/through a wall visually.
- **No gravity/falling by itself.** `FindGroundY` re-grounds the entity to the nearest solid
  surface each step, which *looks* like it respects terrain, but nothing will make the entity fall
  if you stop calling `MoveTo` while it's over a ledge - it simply stays at its last logical
  position until told to move again.
- **Multiplayer:** entirely local to one client. If a mod wants something other players can see
  too, this is the wrong tool - that requires a real server-spawned entity.
- **Depends on undocumented engine internals** (`Vintagestory.Client.NoObf`). Re-verify
  `ClientMain`/`ClientEventManager`'s shape after any game update before shipping an update.

## Open research items (not solved in this document — investigate in the new session)

1. **Real pathfinding for `MoveTo(x, z, y)`.** Two candidate directions, neither verified yet:
   - **Build a self-contained client-safe pathfinder** (e.g. A*/BFS over a walkable-cell graph,
     testing candidate cells with the same `IsSideSolid` technique `FindGroundY` uses). Pure block
     reads, no server-only systems involved, so this is guaranteed safe to run client-side - the
     open question is just the algorithm/performance work, not feasibility.
   - **Investigate reusing vanilla's own pathfinding code** (`Entity/Pathfinding` in the decompiled
     `vsessentialsmod` source tree, e.g. `AiTaskGotoEntity` and whatever navigation graph solver it
     calls into). This is normally invoked only from server-side AI tasks; it is **not yet verified**
     whether its core solver has any dependency on server-only ticking/physics or whether it's pure
     graph-search math that could be called directly from client code. Check this before assuming
     it's reusable — if it turns out to depend on server-only state, the self-built option above is
     the fallback.
2. **Rock-throw / ranged-attack style effects**, if a future consumer of this mod wants them (this
   came up in Remedy & Ruin's own drifter behavior). The real server task (`AiTaskShootAtEntityR` /
   the older `throwatentity` it evolved from) spawns a real, server-physics-ticked projectile
   entity and solves a closed-form ballistic arc (`SolveBallisticArc`) for its launch velocity.
   Since a real `IProjectile` entity spawned client-side won't move on its own (nothing ticks it),
   a client-only equivalent would need to: (a) reuse the same closed-form ballistic-arc math
   (pure algebra, no server dependency), then (b) manually drive a second fake entity's position
   each frame along that precomputed arc, the same way `MoveTo` already drives the main entity -
   purely cosmetic, no real collision/damage, which is fine for a visual-only effect but should be
   documented as such to any consuming mod.
3. **Multi-entity performance.** This document's techniques were only exercised with a handful of
   concurrent entities (Remedy & Ruin's Hallucination system caps at 5). Whether the per-frame
   `IsSideSolid` terrain scan needs caching/throttling at larger counts is untested.

## Reference implementation to adapt from

Everything proven in this document currently ships, working, in Remedy & Ruin
(`Remedy And Ruin/Remedy And Ruin/Remedy And Ruin/GameEngineTweaks/Hallucination/`):
- `HallucinationApparition.cs` — spawn/despawn, movement primitives, interpolation feeding.
- `HallucinationManager.cs` — spawn placement, per-frame tick loop.
- `ApparitionTerrain.cs` — the `FindGroundY` local-scan technique.
- `DrifterBehavior.cs` / `ShiverBehavior.cs` / `BowtornBehavior.cs` — example AI flows built on top
  of the primitives above; not part of this mod's own scope (this mod only needs to expose
  `SpawnClient`/`MoveTo`), but useful worked examples of how a consuming mod would drive them.

These were built and live-tested over an extended session in that project; treat them as the
proven baseline rather than re-deriving the same techniques from scratch.
