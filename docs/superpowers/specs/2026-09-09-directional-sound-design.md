# Directional sound playback — design

Status: approved, not yet implemented.

## Purpose

Let a consuming mod trigger a one-shot sound tied to a `ClientControlledEntity` handle, with
control over whether it's anchored to the entity's real position or the player's, and where it's
offset to from there — e.g. a sound that comes from directly behind the player regardless of which
way they're currently facing, independent of where the entity visually is (matching Remedy & Ruin's
own horror-apparition use case this library originated from).

## Verified starting point

Vintage Story has real, engine-handled 3D positional audio:

```csharp
void PlaySoundAt(AssetLocation? location, double posx, double posy, double posz, IPlayer? dualCallByPlayer = null, bool randomizePitch = true, float range = 32f, float volume = 1f);
```

(`Vintagestory.API.Common.IWorldAccessor`, verified against the decompiled source — declared on
the shared `IWorldAccessor` interface `capi.World` implements, same as every other engine API this
library already calls through `capi.World`). The engine's own audio listener handles panning,
distance attenuation, and direction relative to the player automatically once given a world
position — no custom spatialization trick needed. This mod only needs to compute the right world
position to hand it.

## Public API

```csharp
public bool PlaySound(
    string soundLocation,
    bool relativeToEntity = true,
    SoundDirection direction = SoundDirection.Front,
    double distance = 0,
    double heightOffset = 0,
    bool randomizePitch = true,
    float range = 32f,
    float volume = 1f);

public enum SoundDirection
{
    Front, FrontRight, Right, BackRight, Back, BackLeft, Left, FrontLeft
}
```

- **`soundLocation`** — the sound asset code (e.g. `"game:creature/drifter/hurt"`), converted to an
  `AssetLocation` the same way `SpawnClient`/`SpawnClientCustom` already convert `entityCode`.
- **`relativeToEntity`** (default `true`) — selects the anchor position and facing used below:
  the entity's own real position (`logicalPos`, not `entity.Pos` — same "ground truth" reasoning
  `GetPosition()` already documents) and `entity.Pos.Yaw` when `true`; the player's position and
  the player's own current `Yaw` (`capi.World.Player.Entity.Pos`) when `false`.
- **`direction`/`distance`** — `direction` is a fixed 45°-step angle offset from "straight ahead"
  relative to whichever facing applies (`Front` = 0°, `FrontRight` = 45°, `Right` = 90°,
  `BackRight` = 135°, `Back` = 180°, `BackLeft` = 225°, `Left` = 270°, `FrontLeft` = 315°);
  `distance` projects a world-space offset out along the resulting combined angle
  (`facingYaw + directionAngle`), using the same `Atan2(X, Z)` yaw convention already verified and
  used throughout this library's movement code (`sin`/`cos` for the X/Z components respectively).
  `distance: 0` (the default) means no offset at all regardless of `direction` — the sound plays
  exactly at the anchor position, same as omitting directionality entirely.
- **`heightOffset`** — added straight to the anchor's Y; no rotation involved.
- **`randomizePitch`/`range`/`volume`** — passed straight through to `PlaySoundAt`, with the same
  defaults the engine's own overload uses.
- **Returns** `true` if a sound was found at that location and started playing; `false` if no
  entity is currently spawned, or the sound location wasn't found (mirrors `PlayOneShotAnimation`'s
  honest-return-value pattern rather than throwing).

## Position computation

```csharp
Vec3d anchorPos = relativeToEntity ? logicalPos : capi.World.Player.Entity.Pos.XYZ;
float anchorYaw = relativeToEntity ? entity.Pos.Yaw : capi.World.Player.Entity.Pos.Yaw;

float directionAngle = direction switch
{
    SoundDirection.Front => 0f,
    SoundDirection.FrontRight => GameMath.PIHALF / 2f,
    SoundDirection.Right => GameMath.PIHALF,
    SoundDirection.BackRight => GameMath.PIHALF + GameMath.PIHALF / 2f,
    SoundDirection.Back => GameMath.PI,
    SoundDirection.BackLeft => -(GameMath.PIHALF + GameMath.PIHALF / 2f),
    SoundDirection.Left => -GameMath.PIHALF,
    SoundDirection.FrontLeft => -(GameMath.PIHALF / 2f),
    _ => 0f
};

float combinedYaw = anchorYaw + directionAngle;
double worldX = anchorPos.X + Math.Sin(combinedYaw) * distance;
double worldZ = anchorPos.Z + Math.Cos(combinedYaw) * distance;
double worldY = anchorPos.Y + heightOffset;
```

(`GameMath.PIHALF`/`GameMath.PI` are real, already-available constants in
`Vintagestory.API.MathTools` - exact float values to be confirmed against source at
implementation time rather than hardcoded here, consistent with this repo's verification policy.)

## Error handling

- No entity spawned (`entity == null`) → return `false` immediately, no sound played.
- Empty/null `soundLocation` → return `false` immediately (mirrors `PlayOneShotAnimation`'s
  `string.IsNullOrEmpty` guard).
- Sound asset not found → `PlaySoundAt` itself handles this (engine-side); this method reports it
  via its own `false` return using `PlaySoundAt`'s return value where available, or a direct
  `AssetLocation`/asset-existence check beforehand if the specific overload used doesn't expose a
  success signal - confirmed against the exact `PlaySoundAt` overload signature during
  implementation.

## Out of scope

- Looping/ambient sounds, or a repositionable sound handle - this is one-shot only, matching
  `PlayOneShotAnimation`'s scope (one trigger, not a state machine).
- Any custom spatialization beyond what `PlaySoundAt` already provides - the engine's own audio
  listener does the real panning/attenuation work once given a world position.
