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

    // Straight-line, terrain-following - no obstacle avoidance.
    public bool MoveTo(double x, double z, double? y = null);      // alias for MoveToSlow
    public bool MoveToSlow(double x, double z, double? y = null);
    public bool MoveToFast(double x, double z, double? y = null);

    // Real, obstacle-aware pathfinding - runs on a background thread, callback fires on arrival/failure.
    public void MoveToSlow(double x, double z, Action<bool> onComplete);
    public void MoveToFast(double x, double z, Action<bool> onComplete);
    public void MoveToSlow(double x, double z, double y, Action<bool> onComplete);
    public void MoveToFast(double x, double z, double y, Action<bool> onComplete);

    public void Despawn();
}

[Flags]
public enum MovementType { CanWalk = 1, CanFly = 2, CanSwim = 4, CanClimb = 8 }
public enum TerrainPreference { Water, Land, Both }

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
- **`SpawnClientCustom`** works for any entity code — vanilla or a custom mod-added one — since it
  takes the animation names to use directly rather than assuming vanilla's `"idle"`/`"walk"`
  convention (which `SpawnClient` still uses as a shorthand for the common case).

See `docs/design-notes.md` for implementation details, verification notes against the decompiled
game source, and known limitations.
