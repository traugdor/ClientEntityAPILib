# ClientEntityAILib

A library other Vintage Story mods can depend on to spawn and drive **client-only** entities —
entities that render, animate, and move like real creatures, but exist only in one player's local
client memory. Never sent to the server, never seen by other players, no server-side health, AI,
or combat. Useful for ambient/cosmetic creatures, illusions, ghosts, previews, or any other
visual-only actor a mod wants full manual control over.

## Installing

Reference the built `ClientEntityAILib.dll` the same way this project references the game's own
mod DLLs — a `Reference` with a `HintPath` in your mod's `.csproj`:

```xml
<Reference Include="ClientEntityAILib">
  <HintPath>path\to\ClientEntityAILib.dll</HintPath>
  <Private>False</Private>
</Reference>
```

And declare it as a dependency in your mod's `modinfo.json` so the game loads it first:

```json
{
  "dependencies": {
    "cliententityailib": "1.0.0"
  }
}
```

## Quick example

Spawn a drifter near the player, walk it through a triangle, a square, and a circle, then despawn
it — chaining each leg via the pathfinding callback:

```csharp
using System;
using ClientEntityAILib;
using ClientEntityAILib.Pathfinding;
using Vintagestory.API.MathTools;

ICoreClientAPI capi = /* from your mod's StartClientSide */;
Vec3d origin = capi.World.Player.Entity.Pos.XYZ.Add(4, 0, 0);

var entity = new ClientControlledEntity(capi, MovementType.CanWalk);
entity.SpawnClient("game:drifter-normal", origin);

Vec3d[] triangle =
{
    origin.AddCopy(3, 0, 0),
    origin.AddCopy(1.5, 0, 3),
    origin
};
Vec3d[] square =
{
    origin.AddCopy(3, 0, 0),
    origin.AddCopy(3, 0, 3),
    origin.AddCopy(0, 0, 3),
    origin
};

int circleSteps = 12;
Vec3d[] circle = new Vec3d[circleSteps + 1];
for (int i = 0; i <= circleSteps; i++)
{
    double angle = i * (2 * Math.PI / circleSteps);
    circle[i] = origin.AddCopy(Math.Cos(angle) * 3, 0, Math.Sin(angle) * 3);
}

void WalkShape(Vec3d[] points, int index, Action onShapeComplete)
{
    if (index >= points.Length) { onShapeComplete(); return; }
    Vec3d p = points[index];
    entity.MoveToFast(p.X, p.Z, ok => WalkShape(points, index + 1, onShapeComplete));
}

WalkShape(triangle, 0, () => WalkShape(square, 0, () => WalkShape(circle, 0, () => entity.Despawn())));
```

## Public API

```csharp
public class ClientControlledEntity : IDisposable
{
    public ClientControlledEntity(ICoreClientAPI capi, MovementType movementType, TerrainPreference terrainPreference = TerrainPreference.Both, int? pathfindingSearchDepth = null);

    public bool SpawnClient(string entityCode, Vec3d spawnPos);
    public bool SpawnClientCustom(string entityCode, Vec3d spawnPos, AnimationKeycodes animKeycodes);

    // Real, obstacle-aware pathfinding - runs on a background thread, callback fires on arrival/failure.
    public void MoveToSlow(double x, double z, Action<bool> onComplete);
    public void MoveToFast(double x, double z, Action<bool> onComplete);
    public void MoveToSlow(double x, double z, double y, Action<bool> onComplete);
    public void MoveToFast(double x, double z, double y, Action<bool> onComplete);

    // Generic, entity-agnostic building blocks for a caller's own AI logic - this library has no
    // built-in concept of "attack" or "in range"; these just play whatever animation and report
    // whatever distance the caller asks for.
    public bool PlayOneShotAnimation(string animationCode);
    public Vec3d GetPosition();
    public double DistanceTo(double x, double y, double z);
    public bool PlaySound(string soundLocation, bool relativeToEntity = true, SoundDirection direction = SoundDirection.Front, double distance = 0, double heightOffset = 0, bool randomizePitch = true, float range = 32f, float volume = 1f);

    public void Despawn();
}

[Flags]
public enum MovementType { CanWalk = 1, CanFly = 2, CanSwim = 4, CanClimb = 8 }
public enum TerrainPreference { Water, Land, Both }
public enum SoundDirection { Front, FrontRight, Right, BackRight, Back, BackLeft, Left, FrontLeft }

public class AnimationKeycodes
{
    public string Idle;
    public string MoveSlow;
    public string MoveFast; // falls back to MoveSlow's code when left null/empty
}
```

- **`MovementType`** is what the entity can physically do (a hard legality gate for pathfinding,
  not a preference) — combine flags freely, e.g. `MovementType.CanWalk | MovementType.CanSwim` for
  an amphibious creature, or `MovementType.CanSwim` alone for a fish that can never leave water.
- **`TerrainPreference`** only matters when `MovementType` makes more than one terrain type legal —
  it biases pathfinding cost toward `Water` or `Land` when a comparable route through either
  exists; `Both` (the default) is unbiased.
- **Speeds** for `MoveToSlow`/`MoveToFast` are derived automatically from the spawned entity's own
  AI-task JSON where present, falling back to fixed defaults otherwise — no configuration needed.
  If the entity's JSON has no matching `wander`/`seekentity` movement tasks to derive a speed from
  (a minimal custom entity, for example), both tiers fall back to fixed constants (1.2 / 3.0
  blocks/sec) taken from Remedy & Ruin's own shipped, hand-tuned drifter apparition behavior —
  proven-good values, not the derived speed of vanilla's actual drifter.
- **`SpawnClientCustom`** works for any entity code — vanilla or a custom mod-added one — since it
  takes the animation names to use directly rather than assuming vanilla's `"idle"`/`"walk"`
  convention (which `SpawnClient` still uses as a shorthand for the common case).
- **`PlayOneShotAnimation`/`GetPosition`/`DistanceTo`** are the entity-agnostic building blocks for
  writing your own AI on top of this library — this library has no built-in idea of "attack" or
  "in range." Poll `DistanceTo(x, y, z)` against whatever threshold your entity cares about, call
  `PlayOneShotAnimation("whatever-that-entity-calls-it")` when you decide it's time, and `Despawn()`
  on your own timer. `GetPosition()` returns the real (`logicalPos`) position, not `entity.Pos`,
  which becomes a smoothed/lagging value once movement starts — reading `entity.Pos` directly (or
  calling `AnimManager` yourself) would get stale answers or fight this library's own bookkeeping,
  which is why the `Entity` object itself isn't exposed. `PlayOneShotAnimation` works for any real
  animation clip regardless of whether the entity's own JSON happened to also expose it as a named
  `client.animations` code (many don't — e.g. the real drifter's `"standattack"` is only ever
  played by its own AI task constructing the animation data directly, the same fallback this
  method uses).
- **`PlaySound`** plays a one-shot sound using the engine's own real 3D positional audio — no
  custom panning trick, just computing the right world position and letting the engine's audio
  listener do the rest. `relativeToEntity: true` (the default) anchors it to the entity's own
  position and facing; `false` anchors it to the player's instead. `direction`/`distance` offset
  it from that anchor along a fixed 45°-step angle relative to whichever facing applies — e.g.
  `PlaySound("game:creature/drifter/hurt", relativeToEntity: false, direction: SoundDirection.Back, distance: 5)`
  plays a sound 5 blocks behind wherever the player is currently facing, regardless of where the
  entity actually is. `distance: 0` (the default) means no offset at all — just play at the anchor.

See `docs/design-notes.md` for implementation details, verification notes against the decompiled
game source, and known limitations.
