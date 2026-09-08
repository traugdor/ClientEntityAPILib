# Usability, Movement, and Collision Features Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Public README, explicit slow/fast movement with per-entity derived speeds, `SpawnClientCustom` for arbitrary entities, terrain-aware inter-entity collision avoidance with drift recovery, and composable movement capabilities (walk/fly/swim/climb) with a terrain preference — per `docs/superpowers/specs/2026-09-08-usability-and-movement-features-design.md`.

**Architecture:** New small data/enum types (`AnimationKeycodes`, `MovementType`, `TerrainPreference`) plus a new `EntitySpeedDerivation` helper feed into an extended `ClientControlledEntity`. The pathfinding `Pathfinding/` folder gains climbing support in `GroundWalkingProfile` and a new capability-aware `ThreeDMovementProfile` (replacing the single-purpose `FlyingProfile`). `ClientControlledEntity` itself is rewritten to add `MoveToSlow`/`MoveToFast`, a static cross-instance registry for collision push-apart, and idle-only drift detection/recovery.

**Tech Stack:** C#, `net10.0`, Vintage Story modding API. No automated test harness (same constraint as the rest of this mod) — `dotnet build` substitutes for test-driven verification, ending in a manual in-game checklist.

**Continuation of branch:** This plan continues on the existing `feature/3d-pathfinding` worktree/branch (not yet merged) — all prior pathfinding work (`Pathfinding/ITraversalProfile.cs`, `ClientAStar.cs`, etc.) is already present and this plan builds directly on it, including one breaking change to the constructor (`bool isFlying` → `MovementType`/`TerrainPreference`), accepted because nothing has shipped yet.

---

### Task 1: `AnimationKeycodes`

**Files:**
- Create: `ClientEntityAILib/ClientEntityAILib/AnimationKeycodes.cs`

- [ ] **Step 1: Write the class**

```csharp
namespace ClientEntityAILib
{
    /// <summary>
    /// Animation codes to attempt for an entity's three movement states. AnimManager.StartAnimation
    /// no-ops on an unrecognized code rather than throwing, so a name that doesn't match the spawned
    /// entity's actual animations is safe - the entity simply doesn't animate for that state.
    /// MoveFast falls back to MoveSlow's code when left null or empty.
    /// </summary>
    public class AnimationKeycodes
    {
        public string Idle;
        public string MoveSlow;
        public string MoveFast;
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run:
```bash
dotnet build "ClientEntityAILib/ClientEntityAILib/ClientEntityAILib.csproj" -v minimal
```
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add ClientEntityAILib/ClientEntityAILib/AnimationKeycodes.cs
git commit -m "add AnimationKeycodes for caller-defined per-state animations"
```

---

### Task 2: `MovementType` and `TerrainPreference`

**Files:**
- Create: `ClientEntityAILib/ClientEntityAILib/Pathfinding/MovementCapabilities.cs`

- [ ] **Step 1: Write the enums**

```csharp
using System;

namespace ClientEntityAILib.Pathfinding
{
    /// <summary>
    /// Hard legality gate for what an entity can physically do - not a preference. An entity with
    /// only CanSwim set is already incapable of leaving water; there is no need for
    /// TerrainPreference to enforce that separately.
    /// </summary>
    [Flags]
    public enum MovementType
    {
        CanWalk = 1,
        CanFly = 2,
        CanSwim = 4,
        CanClimb = 8
    }

    /// <summary>
    /// Soft cost bias between terrain types, applied only when an entity's MovementType makes more
    /// than one terrain type legal (e.g. CanWalk | CanSwim). Both means no bias.
    /// </summary>
    public enum TerrainPreference
    {
        Water,
        Land,
        Both
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run:
```bash
dotnet build "ClientEntityAILib/ClientEntityAILib/ClientEntityAILib.csproj" -v minimal
```
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add ClientEntityAILib/ClientEntityAILib/Pathfinding/MovementCapabilities.cs
git commit -m "add MovementType and TerrainPreference enums"
```

---

### Task 3: `EntitySpeedDerivation`

**Files:**
- Create: `ClientEntityAILib/ClientEntityAILib/EntitySpeedDerivation.cs`

Derives per-entity slow/fast blocks/sec from the entity's own AI-task JSON where present, per the
verified formula in the design doc (`blocks/sec = movespeed × 60 × (1 − groundDragFactor) / groundDragFactor`),
falling back to the Remedy & Ruin constants (1.2 / 3.0) independently per tier when the JSON schema
isn't found. `JsonObject`'s indexer never returns null (it returns a JsonObject wrapping a missing
token, safe to keep indexing or call `.AsX()` on), but its `GetEnumerator()` throws if the token
itself doesn't exist — `.Exists` must be checked before iterating an array-valued JsonObject.

- [ ] **Step 1: Write the derivation helper**

```csharp
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace ClientEntityAILib
{
    internal static class EntitySpeedDerivation
    {
        // Remedy & Ruin's own shipped, hand-tuned DrifterBehavior speeds - used whenever an
        // entity's JSON doesn't define a matching AI task to derive a speed from.
        internal const double FallbackSlowSpeed = 1.2;
        internal const double FallbackFastSpeed = 3.0;

        private const string SlowTaskCode = "wander";
        private const string FastTaskCode = "seekentity";

        public static (double slowSpeed, double fastSpeed) Derive(EntityProperties props)
        {
            double groundDragFactor = DeriveGroundDragFactor(props);
            if (groundDragFactor <= 0.0)
            {
                return (FallbackSlowSpeed, FallbackFastSpeed);
            }

            double? slowMoveSpeed = FindTaskMoveSpeed(props, SlowTaskCode);
            double? fastMoveSpeed = FindTaskMoveSpeed(props, FastTaskCode);

            double slowSpeed = slowMoveSpeed.HasValue ? ToBlocksPerSecond(slowMoveSpeed.Value, groundDragFactor) : FallbackSlowSpeed;
            double fastSpeed = fastMoveSpeed.HasValue ? ToBlocksPerSecond(fastMoveSpeed.Value, groundDragFactor) : FallbackFastSpeed;

            return (slowSpeed, fastSpeed);
        }

        private static double DeriveGroundDragFactor(EntityProperties props)
        {
            JsonObject physics = props?.Attributes?["physics"];
            double multiplier = (physics != null && physics.Exists) ? physics["groundDragFactor"].AsDouble(1.0) : 1.0;
            return 0.3 * multiplier;
        }

        /// <summary>
        /// Vanilla's own creature "movespeed" isn't a blocks/sec figure - it's fed into
        /// Controls.WalkVector, which the server's per-tick physics module (PModuleOnGround.DoApply)
        /// converts into real velocity through a damped exponential-approach recurrence whose
        /// steady state is walkX * groundDrag / (1 - groundDrag). Position updates then use
        /// dtFactor = dt * 60 (EntityBehaviorControlledPhysics), not dt directly, so the real rate
        /// is that steady state times 60. groundDragFactor's 0.3 base matches PModuleOnGround's own
        /// default.
        /// </summary>
        private static double ToBlocksPerSecond(double moveSpeed, double groundDragFactor)
        {
            return moveSpeed * 60.0 * (1.0 - groundDragFactor) / groundDragFactor;
        }

        private static double? FindTaskMoveSpeed(EntityProperties props, string taskCode)
        {
            JsonObject[] behaviors = props?.Server?.BehaviorsAsJsonObj;
            if (behaviors == null) return null;

            for (int i = 0; i < behaviors.Length; i++)
            {
                JsonObject behavior = behaviors[i];
                if (behavior["code"].AsString() != "taskai") continue;

                JsonObject aitasks = behavior["aitasks"];
                if (!aitasks.Exists) continue;

                foreach (JsonObject task in aitasks)
                {
                    if (task["code"].AsString() == taskCode && task["movespeed"].Exists)
                    {
                        return task["movespeed"].AsDouble();
                    }
                }
            }

            return null;
        }
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run:
```bash
dotnet build "ClientEntityAILib/ClientEntityAILib/ClientEntityAILib.csproj" -v minimal
```
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add ClientEntityAILib/ClientEntityAILib/EntitySpeedDerivation.cs
git commit -m "add EntitySpeedDerivation: verified movespeed-to-blocks/sec conversion"
```

---

### Task 4: `GroundWalkingProfile` — add `CanClimb`

**Files:**
- Modify: `ClientEntityAILib/ClientEntityAILib/Pathfinding/GroundWalkingProfile.cs` (full-file replacement)

Adds two extra vertical-only neighbors (straight up, straight down) when `canClimb` is true, legal
when the destination cell is clear and at least one horizontally-adjacent cell is solid. This is
original design (vanilla has no generic climbing pathfinding to port), reusing the same
`BlockFacing.HORIZONTALS`/`IsSideSolid` pattern already verified and used by vanilla's own
`AiTaskWander.cs` for an analogous "is there a wall here" check. The existing 8-directional ground
logic is untouched.

- [ ] **Step 1: Replace the file with the following content**

```csharp
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Essentials;

namespace ClientEntityAILib.Pathfinding
{
    /// <summary>
    /// Ground-walking traversal: the 8 horizontal Cardinal directions, with per-step vertical
    /// step-up/fall-down handling. Ported from vanilla's own AStar.traversable()
    /// (Vintagestory.Essentials.AStar, VSEssentials.dll) - verified against the decompiled source.
    /// StepHeight/MaxFallHeight use vanilla's own generic fallback defaults (0.6 / 8, from
    /// WaypointsTraverser's "no EntityBehaviorControlledPhysics" / "no fall damage" case), since
    /// this general-purpose library has no per-entity physics data to draw from for an arbitrary
    /// spawned entity code.
    ///
    /// When canClimb is true, two extra vertical-only neighbors (straight up, straight down) are
    /// available whenever the destination cell is clear and at least one horizontally-adjacent
    /// cell is solid - something to cling to. This is original design (vanilla has no generic
    /// climbing pathfinding to port), layered on top of the otherwise-verified ground logic.
    /// </summary>
    internal class GroundWalkingProfile : ITraversalProfile
    {
        private const float StepHeight = 0.6f;
        private const int MaxFallHeight = 8;
        private const double CenterOffset = 0.5;

        private readonly CollisionTester collTester = new CollisionTester();
        private readonly Vec3d tmpVec = new Vec3d();
        private readonly BlockPos tmpPos = new BlockPos(0);
        private Cuboidd tmpCub = new Cuboidd();
        private readonly EnumAICreatureType creatureType;
        private readonly bool canClimb;

        public GroundWalkingProfile(bool canClimb = false, EnumAICreatureType creatureType = EnumAICreatureType.Default)
        {
            this.canClimb = canClimb;
            this.creatureType = creatureType;
        }

        public IEnumerable<PathNode> GetNeighbors(PathNode from)
        {
            for (int i = 0; i < Cardinal.ALL.Length; i++)
            {
                yield return new PathNode(from, Cardinal.ALL[i]);
            }

            if (canClimb)
            {
                yield return new PathNode(new BlockPos(from.X, from.Y + 1, from.Z, from.dimension));
                yield return new PathNode(new BlockPos(from.X, from.Y - 1, from.Z, from.dimension));
            }
        }

        public bool IsTraversable(PathNode from, PathNode node, Cuboidf entityCollBox, ICachingBlockAccessor blockAccess, ref float extraCost)
        {
            int dx = node.X - from.X;
            int dz = node.Z - from.Z;

            if (canClimb && dx == 0 && dz == 0 && node.Y != from.Y)
            {
                return IsClimbTraversable(node, entityCollBox, blockAccess);
            }

            bool isDiagonal = dx != 0 && dz != 0;

            tmpVec.Set((double)node.X + CenterOffset, node.Y, (double)node.Z + CenterOffset);
            tmpPos.dimension = node.dimension;

            int maxFallHeight = MaxFallHeight;

            Block block;
            if (!collTester.IsColliding(blockAccess, entityCollBox, tmpVec, alsoCheckTouch: false))
            {
                while (true)
                {
                    tmpPos.Set(node.X, node.Y - 1, node.Z);
                    block = blockAccess.GetBlock(tmpPos, 1);
                    if (!block.CanStep) return false;

                    Block fluidLayerBlock = blockAccess.GetBlock(tmpPos, 2);
                    if (fluidLayerBlock.IsLiquid())
                    {
                        float cost = fluidLayerBlock.GetTraversalCost(tmpPos, creatureType);
                        if (cost > 10000f) return false;
                        extraCost += cost;
                        break;
                    }
                    if (fluidLayerBlock.BlockMaterial == EnumBlockMaterial.Ice) block = fluidLayerBlock;

                    Cuboidf[] hitboxBelow = block.GetCollisionBoxes(blockAccess, tmpPos);
                    if (hitboxBelow != null && hitboxBelow.Length != 0)
                    {
                        float cost2 = block.GetTraversalCost(tmpPos, creatureType);
                        if (cost2 > 10000f) return false;
                        extraCost += cost2;
                        break;
                    }

                    tmpVec.Y -= 1.0;
                    if (collTester.IsColliding(blockAccess, entityCollBox, tmpVec, alsoCheckTouch: false)) return false;

                    node.Y--;
                    maxFallHeight--;
                    if (maxFallHeight < 0) return false;
                }

                if (isDiagonal)
                {
                    tmpVec.Add(-dx / 2f, 0.0, -dz / 2f);
                    if (collTester.IsColliding(blockAccess, entityCollBox, tmpVec, alsoCheckTouch: false)) return false;
                }

                tmpPos.Set(node.X, node.Y, node.Z);
                float ucost = blockAccess.GetBlock(tmpPos, 2).GetTraversalCost(tmpPos, creatureType);
                if (ucost > 10000f) return false;
                extraCost += ucost;

                if (isDiagonal && creatureType == EnumAICreatureType.Humanoid)
                {
                    tmpPos.Set(node.X - dx, node.Y, node.Z);
                    ucost = blockAccess.GetBlock(tmpPos, 2).GetTraversalCost(tmpPos, creatureType);
                    extraCost += ucost - 1f;
                    if (ucost > 10000f) return false;

                    tmpPos.Set(node.X, node.Y, node.Z - dz);
                    ucost = blockAccess.GetBlock(tmpPos, 2).GetTraversalCost(tmpPos, creatureType);
                    extraCost += ucost - 1f;
                    if (ucost > 10000f) return false;
                }

                return true;
            }

            tmpPos.Set(node.X, node.Y, node.Z);
            block = blockAccess.GetBlock(tmpPos, 4);
            if (!block.CanStep) return false;

            float upcost = block.GetTraversalCost(tmpPos, creatureType);
            if (upcost > 10000f) return false;
            if (block.Id != 0) extraCost += upcost;

            Block lblock = blockAccess.GetBlock(tmpPos, 2);
            upcost = lblock.GetTraversalCost(tmpPos, creatureType);
            if (upcost > 10000f) return false;
            if (lblock.Id != 0) extraCost += upcost;

            float steponHeightAdjust = -1f;
            Cuboidf[] collboxes = block.GetCollisionBoxes(blockAccess, tmpPos);
            if (collboxes != null && collboxes.Length != 0)
            {
                steponHeightAdjust += collboxes.Max(cuboid => cuboid.Y2);
            }

            tmpVec.Set((double)node.X + CenterOffset, (float)node.Y + StepHeight + steponHeightAdjust, (double)node.Z + CenterOffset);
            if (!collTester.GetCollidingCollisionBox(blockAccess, entityCollBox, tmpVec, ref tmpCub, alsoCheckTouch: false, node.dimension))
            {
                if (!isDiagonal)
                {
                    node.Y += (int)(1f + steponHeightAdjust);
                    return true;
                }
                if (collboxes != null && collboxes.Length != 0)
                {
                    tmpVec.Add(-dx / 2f, 0.0, -dz / 2f);
                    if (collTester.IsColliding(blockAccess, entityCollBox, tmpVec, alsoCheckTouch: false)) return false;
                    node.Y += (int)(1f + steponHeightAdjust);
                    return true;
                }
            }
            return false;
        }

        private bool IsClimbTraversable(PathNode node, Cuboidf entityCollBox, ICachingBlockAccessor blockAccess)
        {
            tmpVec.Set((double)node.X + CenterOffset, node.Y, (double)node.Z + CenterOffset);
            if (collTester.IsColliding(blockAccess, entityCollBox, tmpVec, alsoCheckTouch: false)) return false;

            for (int i = 0; i < BlockFacing.HORIZONTALS.Length; i++)
            {
                BlockFacing face = BlockFacing.HORIZONTALS[i];
                int wallX = node.X + face.Normali.X;
                int wallZ = node.Z + face.Normali.Z;
                if (blockAccess.IsSideSolid(wallX, node.Y, wallZ, face.Opposite)) return true;
            }

            return false;
        }
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run:
```bash
dotnet build "ClientEntityAILib/ClientEntityAILib/ClientEntityAILib.csproj" -v minimal
```
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add ClientEntityAILib/ClientEntityAILib/Pathfinding/GroundWalkingProfile.cs
git commit -m "add CanClimb vertical-move support to GroundWalkingProfile"
```

---

### Task 5: `ThreeDMovementProfile` — replaces `FlyingProfile`

**Files:**
- Delete: `ClientEntityAILib/ClientEntityAILib/Pathfinding/FlyingProfile.cs`
- Create: `ClientEntityAILib/ClientEntityAILib/Pathfinding/ThreeDMovementProfile.cs`

Generalizes the prior `FlyingProfile` (same 26-directional neighbor set, same corner-cutting
collision guard) with capability-aware per-node legality (`MovementType`) and a `TerrainPreference`
cost bias, per the design doc's section 6 rules.

- [ ] **Step 1: Delete the old file**

```bash
git rm ClientEntityAILib/ClientEntityAILib/Pathfinding/FlyingProfile.cs
```

- [ ] **Step 2: Create the new file**

```csharp
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Essentials;

namespace ClientEntityAILib.Pathfinding
{
    /// <summary>
    /// Full 3D (26-directional) grid traversal for entities that can fly and/or swim, with
    /// per-node legality gated by MovementType and a cost bias from TerrainPreference. Original
    /// design (not a port of any vanilla algorithm - vanilla has no flying/amphibious creature
    /// pathfinding), built on the same CollisionTester/Block.GetTraversalCost primitives
    /// GroundWalkingProfile uses. CanClimb has nothing to add within this grid - a pure vertical
    /// move next to a wall is already a legal air-node move whenever CanFly is set; CanClimb only
    /// changes GroundWalkingProfile's 8-directional graph, which has no vertical moves of its own.
    /// </summary>
    internal class ThreeDMovementProfile : ITraversalProfile
    {
        private const double Center = 0.5;

        private static readonly (int dx, int dy, int dz)[] Offsets = BuildOffsets();

        private readonly CollisionTester collTester = new CollisionTester();
        private readonly Vec3d tmpVec = new Vec3d();
        private readonly BlockPos tmpPos = new BlockPos(0);
        private readonly EnumAICreatureType creatureType;
        private readonly MovementType movementType;
        private readonly TerrainPreference terrainPreference;

        public ThreeDMovementProfile(MovementType movementType, TerrainPreference terrainPreference, EnumAICreatureType creatureType = EnumAICreatureType.Default)
        {
            this.movementType = movementType;
            this.terrainPreference = terrainPreference;
            this.creatureType = creatureType;
        }

        private static (int, int, int)[] BuildOffsets()
        {
            List<(int, int, int)> list = new List<(int, int, int)>(26);
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dy == 0 && dz == 0) continue;
                        list.Add((dx, dy, dz));
                    }
                }
            }
            return list.ToArray();
        }

        public IEnumerable<PathNode> GetNeighbors(PathNode from)
        {
            foreach ((int dx, int dy, int dz) in Offsets)
            {
                yield return new PathNode(new BlockPos(from.X + dx, from.Y + dy, from.Z + dz, from.dimension));
            }
        }

        public bool IsTraversable(PathNode from, PathNode node, Cuboidf entityCollBox, ICachingBlockAccessor blockAccess, ref float extraCost)
        {
            tmpVec.Set((double)node.X + Center, node.Y, (double)node.Z + Center);
            if (collTester.IsColliding(blockAccess, entityCollBox, tmpVec, alsoCheckTouch: false)) return false;

            int dx = node.X - from.X;
            int dy = node.Y - from.Y;
            int dz = node.Z - from.Z;
            int axesUsed = (dx != 0 ? 1 : 0) + (dy != 0 ? 1 : 0) + (dz != 0 ? 1 : 0);

            if (axesUsed >= 2)
            {
                // 3D corner-cutting guard, generalizing vanilla ground pathing's own single-sample
                // check: pull the sample point back from the destination toward the source by half
                // a block on every axis actually being moved diagonally, and re-check collision.
                tmpVec.Add(-dx / 2.0, -dy / 2.0, -dz / 2.0);
                if (collTester.IsColliding(blockAccess, entityCollBox, tmpVec, alsoCheckTouch: false)) return false;
            }

            tmpPos.Set(node.X, node.Y, node.Z);
            tmpPos.dimension = node.dimension;

            bool destinationIsLiquid = blockAccess.GetBlock(tmpPos, 2).IsLiquid();
            bool destinationRestsOnGround = !destinationIsLiquid && blockAccess.IsSideSolid(node.X, node.Y - 1, node.Z, BlockFacing.UP);

            bool canSwim = (movementType & MovementType.CanSwim) != 0;
            bool canFly = (movementType & MovementType.CanFly) != 0;
            bool canWalk = (movementType & MovementType.CanWalk) != 0;

            bool legal = (destinationIsLiquid && canSwim)
                || (!destinationIsLiquid && !destinationRestsOnGround && canFly)
                || (destinationRestsOnGround && (canFly || canWalk));

            if (!legal) return false;

            float cost = blockAccess.GetBlock(tmpPos, 2).GetTraversalCost(tmpPos, creatureType);
            if (cost > 10000f) return false;
            extraCost += cost;

            bool multipleTerrainTypesLegal = canSwim && (canFly || canWalk);
            if (multipleTerrainTypesLegal && terrainPreference != TerrainPreference.Both)
            {
                bool preferWater = terrainPreference == TerrainPreference.Water;
                if (preferWater && !destinationIsLiquid) extraCost += 1f;
                if (!preferWater && destinationIsLiquid) extraCost += 1f;
            }

            return true;
        }
    }
}
```

- [ ] **Step 3: Build to verify it compiles**

Run:
```bash
dotnet build "ClientEntityAILib/ClientEntityAILib/ClientEntityAILib.csproj" -v minimal
```
Expected: build FAILS at this point — `ClientControlledEntity.cs` (Task 6, not yet done) still
references the now-deleted `FlyingProfile` and the old `bool isFlying` constructor. This is
expected; do not attempt to fix it here. Confirm the failure specifically names `FlyingProfile` or
`ClientControlledEntity.cs` as the source (not `ThreeDMovementProfile.cs` or `GroundWalkingProfile.cs`
themselves) before proceeding — those two new/modified files should show no errors of their own.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "replace FlyingProfile with capability-aware ThreeDMovementProfile"
```

---

### Task 6: Rewrite `ClientControlledEntity`

**Files:**
- Modify: `ClientEntityAILib/ClientEntityAILib/ClientControlledEntity.cs` (full-file replacement)

This is the integration task — wires every new piece (Tasks 1-5) into the entity-control class:
`MovementType`/`TerrainPreference` constructor, `SpawnClientCustom`, `MoveToSlow`/`MoveToFast` (both
straight-line and pathfinding variants), the static cross-instance registry for terrain-aware
collision push-apart, and idle-only drift detection/recovery.

- [ ] **Step 1: Replace the file with the following content**

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using ClientEntityAILib.Pathfinding;

namespace ClientEntityAILib
{
    /// <summary>
    /// Drives one client-only entity: spawns it directly into the client's render/entity
    /// pipeline without ever touching the server, and manually simulates its position/facing/
    /// animation every tick since nothing else will (client-side entities get no server physics,
    /// no AI tick, and IWorldAccessor.SpawnEntity/LoadEntity are unusable on the client).
    /// </summary>
    public class ClientControlledEntity : IDisposable
    {
        private enum EnumMoveTier { Idle, Slow, Fast }

        // Real server-assigned entity IDs are always positive, so negative IDs are a safe,
        // collision-free convention for these fake local-only entities. Shared across every
        // handle in the mod so two handles never collide.
        private static long nextFakeEntityId = -1;

        // Every live handle, for cross-instance collision push-apart. Main-thread only - every
        // read/write happens from SpawnClientCustom/Despawn/OnGameTick, never a background thread.
        private static readonly List<ClientControlledEntity> activeHandles = new List<ClientControlledEntity>();

        // Push OnReceivedServerPos at ~15/sec to match EntityBehaviorInterpolatePosition's own
        // hardcoded interval; feeding it faster floods its queue and forces a jarring catch-up snap.
        private const float PositionPushInterval = 1f / 15f;

        private const double ArriveDistance = 0.05;
        private const float DriftCheckInterval = 1f;
        private const double DriftThreshold = 1.5;

        private readonly ICoreClientAPI capi;
        private readonly MovementType movementType;
        private readonly TerrainPreference terrainPreference;
        private readonly int pathfindingSearchDepth;
        private readonly CollisionTester separationCollTester = new CollisionTester();

        private Entity entity;
        private Vec3d logicalPos;
        private Vec3d moveTarget;
        private bool hasMoveTarget;
        private EnumMoveTier currentTier = EnumMoveTier.Idle;
        private EnumMoveTier requestedTier = EnumMoveTier.Slow;
        private double currentMoveSpeed;
        private string activeAnim;
        private float pushAccum;
        private long tickListenerId = -1;

        private double slowSpeed;
        private double fastSpeed;
        private AnimationKeycodes animKeycodes;

        private List<Vec3d> activeWaypoints;
        private int waypointIndex;
        private Action<bool> pendingCallback;
        private int moveGeneration;

        private Vec3d lastCommandedDestination;
        private float driftCheckAccum;

        /// <param name="movementType">
        /// Required - what this entity can physically do. A hard legality gate, not a preference:
        /// MovementType.CanSwim alone (no CanWalk/CanFly) already makes an entity incapable of
        /// leaving water, with no need for terrainPreference to enforce that separately.
        /// </param>
        /// <param name="terrainPreference">
        /// Soft cost bias between terrain types; only does anything when movementType makes more
        /// than one terrain type legal (e.g. CanWalk | CanSwim). Defaults to Both (no bias).
        /// </param>
        /// <param name="pathfindingSearchDepth">
        /// Node budget for the callback-based MoveTo/MoveToSlow/MoveToFast overloads; null resolves
        /// to a type-appropriate default (4000 if neither CanFly nor CanSwim is set, 8000 if
        /// either is - matching the 26-directional grid's larger branching factor). An explicit
        /// value is always used as-is. Each node is roughly one block-step; obstacles, elevation
        /// changes, and dead-ends multiply nodes explored well past the direct-line distance - see
        /// the design doc's conversion table for realistic ranges at the defaults.
        /// </param>
        public ClientControlledEntity(ICoreClientAPI capi, MovementType movementType, TerrainPreference terrainPreference = TerrainPreference.Both, int? pathfindingSearchDepth = null)
        {
            this.capi = capi;
            this.movementType = movementType;
            this.terrainPreference = terrainPreference;

            bool uses3D = (movementType & (MovementType.CanFly | MovementType.CanSwim)) != 0;
            this.pathfindingSearchDepth = pathfindingSearchDepth ?? (uses3D ? 8000 : 4000);

            if (uses3D)
            {
                capi.Logger.Warning("ClientControlledEntity created with CanFly and/or CanSwim: 26-directional 3D pathfinding has roughly 3x the branching factor of ground pathfinding per node, so equivalent search depths take noticeably longer to resolve.");
            }
        }

        /// <summary>
        /// Registers and spawns any entity code at spawnPos, using the given animation names for
        /// its idle/slow-move/fast-move states. Returns true if the entity type was found and the
        /// entity was created and rendered successfully; false otherwise (bad entity code, or this
        /// instance already has an active entity - call Despawn first to reuse the handle).
        /// animKeycodes.MoveFast falls back to MoveSlow's code when left null/empty.
        /// </summary>
        public bool SpawnClientCustom(string entityCode, Vec3d spawnPos, AnimationKeycodes animKeycodes)
        {
            if (entity != null) return false;

            EntityProperties props = capi.World.GetEntityType(new AssetLocation(entityCode));
            if (props == null) return false;

            Entity newEntity = capi.World.ClassRegistry.CreateEntity(props);
            newEntity.EntityId = nextFakeEntityId--;
            newEntity.Pos.SetPos(spawnPos);
            newEntity.Initialize(props, capi.World.Api, 0);

            ((IClientWorldAccessor)capi.World).LoadedEntities[newEntity.EntityId] = newEntity;

            ClientMain game = (ClientMain)capi.World;
            game.eventManager.TriggerEntityLoaded(newEntity);

            entity = newEntity;
            logicalPos = spawnPos.Clone();
            hasMoveTarget = false;
            currentTier = EnumMoveTier.Idle;
            activeAnim = null;
            pushAccum = 0f;
            activeWaypoints = null;
            waypointIndex = 0;
            pendingCallback = null;
            lastCommandedDestination = null;
            driftCheckAccum = 0f;

            (double derivedSlow, double derivedFast) = EntitySpeedDerivation.Derive(props);
            slowSpeed = derivedSlow;
            fastSpeed = derivedFast;

            this.animKeycodes = new AnimationKeycodes
            {
                Idle = animKeycodes?.Idle,
                MoveSlow = animKeycodes?.MoveSlow,
                MoveFast = string.IsNullOrEmpty(animKeycodes?.MoveFast) ? animKeycodes?.MoveSlow : animKeycodes.MoveFast
            };

            activeHandles.Add(this);

            tickListenerId = capi.Event.RegisterGameTickListener(OnGameTick, 30);

            return true;
        }

        /// <summary>
        /// Registers and spawns a client-only entity of the given entity code at spawnPos, using
        /// the vanilla-convention animation names ("idle"/"walk" for both move tiers). Same return
        /// contract as SpawnClientCustom. Equivalent to calling SpawnClientCustom with those names.
        /// </summary>
        public bool SpawnClient(string entityCode, Vec3d spawnPos)
        {
            return SpawnClientCustom(entityCode, spawnPos, new AnimationKeycodes { Idle = "idle", MoveSlow = "walk", MoveFast = "walk" });
        }

        /// <summary>
        /// Moves the entity toward (x, z) at the slow tier, following the real terrain surface
        /// vertically. Alias for MoveToSlow(x, z, y) - see its doc comment. Kept for backward
        /// compatibility with callers written before MoveToSlow/MoveToFast existed.
        /// </summary>
        public bool MoveTo(double x, double z, double? y = null)
        {
            return MoveToSlow(x, z, y);
        }

        /// <summary>
        /// Moves the entity toward (x, z) at the slow tier, following the real terrain surface
        /// vertically. If y is given, the target is a full 3D point. Does not avoid obstacles - it
        /// walks a straight line and will walk into a wall it can't pass. Returns true if an
        /// entity is active; false otherwise. For real obstacle-avoiding pathfinding, use one of
        /// the callback-based overloads instead.
        /// </summary>
        public bool MoveToSlow(double x, double z, double? y = null)
        {
            return MoveToDirect(x, z, y, EnumMoveTier.Slow);
        }

        /// <summary>Same as MoveToSlow, but at the fast tier. See MoveToSlow's doc comment.</summary>
        public bool MoveToFast(double x, double z, double? y = null)
        {
            return MoveToDirect(x, z, y, EnumMoveTier.Fast);
        }

        private bool MoveToDirect(double x, double z, double? y, EnumMoveTier tier)
        {
            if (entity == null) return false;

            CancelPendingPathfind();

            moveTarget = new Vec3d(x, y ?? logicalPos.Y, z);
            hasMoveTarget = true;
            requestedTier = tier;
            currentMoveSpeed = tier == EnumMoveTier.Fast ? fastSpeed : slowSpeed;
            lastCommandedDestination = moveTarget.Clone();
            return true;
        }

        /// <summary>
        /// Real, obstacle-aware pathfinding to (x, z) at the slow tier; the target Y is
        /// auto-resolved from the terrain at that column. onComplete fires exactly once: true if
        /// the entity reached the destination, false if no path was found within the configured
        /// node budget, no entity is active, or the handle was despawned before arrival. A call
        /// superseded by a newer MoveTo*/MoveToSlow/MoveToFast call gets no callback at all - only
        /// the newest call's callback ever fires.
        /// </summary>
        public void MoveToSlow(double x, double z, Action<bool> onComplete)
        {
            MoveToTierWithGroundY(x, z, onComplete, EnumMoveTier.Slow);
        }

        /// <summary>Same as MoveToSlow, but at the fast tier. See MoveToSlow's doc comment.</summary>
        public void MoveToFast(double x, double z, Action<bool> onComplete)
        {
            MoveToTierWithGroundY(x, z, onComplete, EnumMoveTier.Fast);
        }

        private void MoveToTierWithGroundY(double x, double z, Action<bool> onComplete, EnumMoveTier tier)
        {
            if (entity == null)
            {
                onComplete?.Invoke(false);
                return;
            }

            double targetY = FindGroundY(capi, x, logicalPos.Y, z);
            MoveToPathfind(x, z, targetY, onComplete, tier);
        }

        /// <summary>
        /// Real, obstacle-aware pathfinding to the exact 3D point (x, y, z) at the slow tier. See
        /// the (x, z, callback) overload's doc comment for the callback contract.
        /// </summary>
        public void MoveToSlow(double x, double z, double y, Action<bool> onComplete)
        {
            MoveToPathfind(x, z, y, onComplete, EnumMoveTier.Slow);
        }

        /// <summary>Same as MoveToSlow, but at the fast tier. See MoveToSlow's doc comment.</summary>
        public void MoveToFast(double x, double z, double y, Action<bool> onComplete)
        {
            MoveToPathfind(x, z, y, onComplete, EnumMoveTier.Fast);
        }

        private void MoveToPathfind(double x, double z, double y, Action<bool> onComplete, EnumMoveTier tier)
        {
            if (entity == null)
            {
                onComplete?.Invoke(false);
                return;
            }

            CancelPendingPathfind();
            hasMoveTarget = false;
            pendingCallback = onComplete;
            requestedTier = tier;
            currentMoveSpeed = tier == EnumMoveTier.Fast ? fastSpeed : slowSpeed;
            lastCommandedDestination = new Vec3d(x, y, z);

            int myGeneration = moveGeneration;
            BlockPos startPos = new BlockPos((int)Math.Floor(logicalPos.X), (int)Math.Floor(logicalPos.Y), (int)Math.Floor(logicalPos.Z), entity.Pos.Dimension);
            BlockPos targetPos = new BlockPos((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z), entity.Pos.Dimension);
            Cuboidf entityCollBox = entity.CollisionBox.Clone();
            ITraversalProfile profile = CreateProfile();
            int searchDepth = pathfindingSearchDepth;

            Task.Run(() =>
            {
                ICachingBlockAccessor blockAccess = capi.World.GetCachingBlockAccessor(synchronize: true, relight: true);
                try
                {
                    ClientAStar astar = new ClientAStar(capi, blockAccess, profile, searchDepth);
                    return astar.FindPath(startPos, targetPos, entityCollBox);
                }
                finally
                {
                    blockAccess.Dispose();
                }
            }).ContinueWith(task =>
            {
                List<Vec3d> waypoints = null;
                if (task.Status == TaskStatus.RanToCompletion)
                {
                    waypoints = task.Result;
                }
                else if (task.Exception != null)
                {
                    capi.Logger.Error("ClientEntityAILib pathfinding failed: {0}", task.Exception.GetBaseException());
                }

                capi.Event.EnqueueMainThreadTask(() =>
                {
                    if (myGeneration != moveGeneration) return; // superseded - the newer call owns pendingCallback now

                    if (waypoints == null || waypoints.Count == 0)
                    {
                        Action<bool> callback = pendingCallback;
                        pendingCallback = null;
                        callback?.Invoke(false);
                        return;
                    }

                    activeWaypoints = waypoints;
                    waypointIndex = 0;
                }, "clientEntityAiLibPathResult");
            });
        }

        // A fresh profile instance per search call, not a shared field - GroundWalkingProfile/
        // ThreeDMovementProfile hold mutable scratch state that isn't safe to touch from two
        // overlapping background searches at once (a superseding MoveTo call doesn't cancel the
        // previous search, it just discards its result later - see CancelPendingPathfind).
        private ITraversalProfile CreateProfile()
        {
            bool uses3D = (movementType & (MovementType.CanFly | MovementType.CanSwim)) != 0;
            if (uses3D) return new ThreeDMovementProfile(movementType, terrainPreference);

            bool canClimb = (movementType & MovementType.CanClimb) != 0;
            return new GroundWalkingProfile(canClimb);
        }

        public void Despawn()
        {
            if (entity == null) return;

            // entity (and every other bit of state a reentrant call would check) is nulled out
            // BEFORE pendingCallback fires, since that callback is arbitrary caller code that may
            // call back into this instance (e.g. Despawn() again, or MoveTo(...) again) - firing
            // it while entity was still non-null let a reentrant Despawn() null it out from
            // underneath this call, crashing on the despawn-packet-mirroring calls below once
            // control returned here.
            Entity entityToRemove = entity;
            entity = null;
            hasMoveTarget = false;
            currentTier = EnumMoveTier.Idle;
            activeAnim = null;
            lastCommandedDestination = null;

            moveGeneration++;
            activeWaypoints = null;
            waypointIndex = 0;

            activeHandles.Remove(this);

            if (tickListenerId != -1)
            {
                capi.Event.UnregisterGameTickListener(tickListenerId);
                tickListenerId = -1;
            }

            Action<bool> callback = pendingCallback;
            pendingCallback = null;

            ClientMain game = (ClientMain)capi.World;
            EntityDespawnData despawnData = new EntityDespawnData { Reason = EnumDespawnReason.Removed };
            game.eventManager.TriggerEntityDespawn(entityToRemove, despawnData);
            game.RemoveEntityRenderer(entityToRemove);
            entityToRemove.OnEntityDespawn(despawnData);
            ((IClientWorldAccessor)capi.World).LoadedEntities.Remove(entityToRemove.EntityId);

            callback?.Invoke(false);
        }

        public void Dispose()
        {
            Despawn();
        }

        // Cancels whatever the previous MoveTo call was doing (waypoint-following or a background
        // search still in flight) without invoking its callback - per this mod's documented
        // contract, a call superseded by a newer one gets no callback at all; only despawn (see
        // Despawn() above) explicitly resolves a pending callback with false.
        private void CancelPendingPathfind()
        {
            moveGeneration++;
            activeWaypoints = null;
            waypointIndex = 0;
            pendingCallback = null;
        }

        private void OnGameTick(float dt)
        {
            if (entity == null) return;

            bool moving = false;
            float yaw = entity.Pos.Yaw;

            if (activeWaypoints != null)
            {
                moving = StepWaypoints(dt, ref yaw);
            }
            else if (hasMoveTarget)
            {
                moving = StepDirectTarget(dt, ref yaw);
            }

            ApplySeparation();
            RunDriftCheck(dt, moving);

            SetMoving(moving ? requestedTier : EnumMoveTier.Idle);

            pushAccum += dt;
            if (pushAccum >= PositionPushInterval)
            {
                pushAccum -= PositionPushInterval;
                PushPosition(yaw);
            }
        }

        // Horizontal-only stepping with terrain-following Y (FindGroundY).
        private bool StepDirectTarget(float dt, ref float yaw)
        {
            // Horizontal delta only - mixing in a non-zero Y here would turn dist into a 3D
            // distance inflated by however far logicalPos.Y has drifted from the target's Y,
            // corrupting both the arrival check and the normalized step direction.
            Vec3d toTarget = new Vec3d(moveTarget.X - logicalPos.X, 0, moveTarget.Z - logicalPos.Z);
            double dist = toTarget.Length();

            if (dist <= ArriveDistance)
            {
                hasMoveTarget = false;
                return false;
            }

            double step = Math.Min(currentMoveSpeed * dt, dist);
            logicalPos.X += toTarget.X / dist * step;
            logicalPos.Z += toTarget.Z / dist * step;
            logicalPos.Y = FindGroundY(capi, logicalPos.X, logicalPos.Y, logicalPos.Z);

            yaw = (float)Math.Atan2(toTarget.X, toTarget.Z);

            if (entity is EntityAgent agent)
            {
                agent.Controls.WalkVector.Set(toTarget.X / dist * currentMoveSpeed, 0, toTarget.Z / dist * currentMoveSpeed);
            }

            return true;
        }

        // Full 3D stepping toward the current waypoint - each waypoint already carries its own
        // valid Y from the search, so (unlike StepDirectTarget) this doesn't re-derive Y via
        // FindGroundY, which would be wrong for a flown/swum path or a ground path mid step-up/fall.
        private bool StepWaypoints(float dt, ref float yaw)
        {
            Vec3d target = activeWaypoints[waypointIndex];
            Vec3d toTarget = new Vec3d(target.X - logicalPos.X, target.Y - logicalPos.Y, target.Z - logicalPos.Z);
            double dist = toTarget.Length();

            if (dist <= ArriveDistance)
            {
                waypointIndex++;
                if (waypointIndex >= activeWaypoints.Count)
                {
                    activeWaypoints = null;
                    waypointIndex = 0;
                    Action<bool> callback = pendingCallback;
                    pendingCallback = null;
                    callback?.Invoke(true);
                    return false;
                }

                target = activeWaypoints[waypointIndex];
                toTarget = new Vec3d(target.X - logicalPos.X, target.Y - logicalPos.Y, target.Z - logicalPos.Z);
                dist = toTarget.Length();
                if (dist <= ArriveDistance) return true; // next waypoint is essentially here too; pick it up next tick
            }

            double step = Math.Min(currentMoveSpeed * dt, dist);
            logicalPos.X += toTarget.X / dist * step;
            logicalPos.Y += toTarget.Y / dist * step;
            logicalPos.Z += toTarget.Z / dist * step;

            double horizLen = Math.Sqrt(toTarget.X * toTarget.X + toTarget.Z * toTarget.Z);
            if (horizLen > 0.0001)
            {
                yaw = (float)Math.Atan2(toTarget.X, toTarget.Z);
            }

            if (entity is EntityAgent agent)
            {
                agent.Controls.WalkVector.Set(toTarget.X / dist * currentMoveSpeed, toTarget.Y / dist * currentMoveSpeed, toTarget.Z / dist * currentMoveSpeed);
            }

            return true;
        }

        // Terrain-aware push-apart against every other live handle. Each instance only ever
        // mutates its OWN logicalPos - mutual separation emerges from every handle doing this
        // independently in its own OnGameTick, not from reaching into another instance's fields.
        private void ApplySeparation()
        {
            if (entity == null || activeHandles.Count <= 1) return;

            double pushX = 0.0;
            double pushZ = 0.0;
            double myRadius = HalfExtentXZ(entity.CollisionBox);

            for (int i = 0; i < activeHandles.Count; i++)
            {
                ClientControlledEntity other = activeHandles[i];
                if (other == this || other.entity == null) continue;

                double dx = logicalPos.X - other.logicalPos.X;
                double dz = logicalPos.Z - other.logicalPos.Z;
                double dist = Math.Sqrt(dx * dx + dz * dz);

                double minDist = myRadius + HalfExtentXZ(other.entity.CollisionBox);
                if (dist >= minDist || dist < 0.0001) continue;

                double overlap = minDist - dist;
                pushX += dx / dist * overlap * 0.5;
                pushZ += dz / dist * overlap * 0.5;
            }

            if (pushX == 0.0 && pushZ == 0.0) return;

            // Axis-separated sliding: an axis whose push would collide with terrain is zeroed out
            // instead of applied, so an entity pushed toward a wall slides along it rather than
            // clipping through. If both axes are blocked, the push is skipped entirely this tick.
            if (!IsPositionColliding(logicalPos.X + pushX, logicalPos.Y, logicalPos.Z)) logicalPos.X += pushX;
            if (!IsPositionColliding(logicalPos.X, logicalPos.Y, logicalPos.Z + pushZ)) logicalPos.Z += pushZ;
        }

        private bool IsPositionColliding(double x, double y, double z)
        {
            return separationCollTester.IsColliding(capi.World.BlockAccessor, entity.CollisionBox, new Vec3d(x, y, z), alsoCheckTouch: false);
        }

        private static double HalfExtentXZ(Cuboidf box)
        {
            return Math.Max(box.XSize, box.ZSize) / 2.0;
        }

        // Only runs while idle (no active move) - an actively-moving entity is already correcting
        // toward its own target every tick regardless of small pushes, so this would never trigger
        // for it. Exists specifically for entities ApplySeparation can otherwise strand with no
        // other force acting on them. A recovery search that's still in flight when this fires
        // again a second later just restarts (CancelPendingPathfind), which is harmless and cheap
        // for a 1.5-block correction - not worth guarding against for this edge case.
        private void RunDriftCheck(float dt, bool moving)
        {
            if (moving || lastCommandedDestination == null)
            {
                driftCheckAccum = 0f;
                return;
            }

            driftCheckAccum += dt;
            if (driftCheckAccum < DriftCheckInterval) return;
            driftCheckAccum = 0f;

            Vec3d delta = new Vec3d(logicalPos.X - lastCommandedDestination.X, logicalPos.Y - lastCommandedDestination.Y, logicalPos.Z - lastCommandedDestination.Z);
            if (delta.Length() <= DriftThreshold) return;

            Vec3d target = lastCommandedDestination;
            MoveToPathfind(target.X, target.Z, target.Y, null, EnumMoveTier.Slow);
        }

        // Feeds EntityBehaviorInterpolatePosition the same way a real server position update
        // would. Writing Pos.Yaw/Pos.SetPos directly every frame doesn't work: this behavior runs
        // at EnumRenderStage.Before, ahead of everything else, and silently overwrites both toward
        // whatever OnReceivedServerPos was last given.
        private void PushPosition(float yaw)
        {
            entity.Pos.SetPos(logicalPos);
            entity.Pos.Yaw = yaw;
            if (entity is EntityAgent agent) agent.BodyYawServer = yaw;

            EnumHandling handling = EnumHandling.PassThrough;
            EntityBehaviorInterpolatePosition interp = entity.GetBehavior<EntityBehaviorInterpolatePosition>();
            interp?.OnReceivedServerPos(isTeleport: false, ref handling);
        }

        private void SetMoving(EnumMoveTier tier)
        {
            if (tier == currentTier) return;
            currentTier = tier;

            if (activeAnim != null)
            {
                entity.AnimManager.StopAnimation(activeAnim);
                activeAnim = null;
            }

            string code = tier == EnumMoveTier.Slow ? animKeycodes.MoveSlow
                : tier == EnumMoveTier.Fast ? animKeycodes.MoveFast
                : animKeycodes.Idle;

            if (!string.IsNullOrEmpty(code) && entity.AnimManager.StartAnimation(code)) activeAnim = code;
        }

        // A short local downward scan anchored near a known Y, not a top-down heightmap scan from
        // the sky - this is what makes it work both outdoors on hills and underground/indoors.
        // GetTerrainMapheightAt/GetRainMapHeightAt are both column heightmaps and unusable here
        // (worldgen-time snapshot, or topmost sky-facing surface respectively).
        private static double FindGroundY(ICoreClientAPI capi, double x, double aroundY, double z)
        {
            int bx = (int)x, bz = (int)z;
            int y = (int)Math.Ceiling(aroundY) + 1; // allow stepping up slightly
            int tries = 8; // vanilla's own MoveDownToFloor uses 5; extra margin is an undocumented tuning choice
            while (tries-- > 0)
            {
                if (capi.World.BlockAccessor.IsSideSolid(bx, y, bz, BlockFacing.UP)) return y + 1;
                y--;
            }
            return aroundY; // nothing solid found nearby - hold position rather than snapping somewhere wrong
        }
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run:
```bash
dotnet build "ClientEntityAILib/ClientEntityAILib/ClientEntityAILib.csproj" -v minimal
```
Expected: `Build succeeded. 0 Error(s)` — this should now also resolve Task 5's expected failure.

- [ ] **Step 3: Commit**

```bash
git add ClientEntityAILib/ClientEntityAILib/ClientControlledEntity.cs
git commit -m "add MoveToSlow/MoveToFast, SpawnClientCustom, collision push-apart, drift recovery"
```

---

### Task 7: README rewrite

**Files:**
- Create: `docs/design-notes.md`
- Modify: `README.md` (full-file replacement)

- [ ] **Step 1: Move the existing README content to `docs/design-notes.md`**

Read the current `README.md` in full and copy its entire content, verbatim, into a new file
`docs/design-notes.md`, with exactly one edit: find the paragraph (added by the prior pathfinding
work) that reads:

```markdown
`ClientControlledEntity`'s constructor takes an `isFlying` flag (default `false`) that selects the
whole handle's traversal profile for these overloads:

- **Ground** (`isFlying: false`) - 8-directional, ported verbatim from vanilla's own
  `AStar.traversable()` (step-height/fall-height handling, liquid costs, diagonal corner-cutting).
- **Flying** (`isFlying: true`) - 26-directional (full 3D grid connectivity). Vanilla has no flying
  creature type to port from, so this is original design built on the same proven
  `CollisionTester`/`Block.GetTraversalCost` primitives ground pathing uses, generalized to 3D
  (including a 3D corner-cutting guard). Roughly 3x the branching factor per node versus ground.
```

and replace it with:

```markdown
`ClientControlledEntity`'s constructor takes a `MovementType` flags bitmask (`CanWalk`/`CanFly`/
`CanSwim`/`CanClimb`, required) and a `TerrainPreference` (`Water`/`Land`/`Both`, default `Both`)
that together select the whole handle's traversal profile for these overloads. `MovementType` is a
hard legality gate - which node types are physically possible, not a preference (a
`CanSwim`-only entity is already incapable of leaving water). `TerrainPreference` only biases cost
when more than one terrain type is legal for that entity (e.g. `CanWalk | CanSwim`, amphibious).
`CanFly`/`CanSwim` select the 26-directional 3D grid (roughly 3x the branching factor of ground
pathing per node); `CanWalk`-only (optionally with `CanClimb`) stays on the 8-directional ground
graph. See `docs/superpowers/specs/2026-09-08-usability-and-movement-features-design.md` for the
full composition rules.
```

- [ ] **Step 2: Replace `README.md` with the following content**

```markdown
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
```

- [ ] **Step 3: Build to verify nothing else changed**

Run:
```bash
dotnet build "ClientEntityAILib/ClientEntityAILib/ClientEntityAILib.csproj" -v minimal
```
Expected: `Build succeeded. 0 Error(s)` (this task only touches Markdown files, but confirms the
branch is still in a good state).

- [ ] **Step 4: Commit**

```bash
git add README.md docs/design-notes.md
git commit -m "rewrite README as public usage doc, move design rationale to docs/design-notes.md"
```

---

### Task 8: Manual in-game verification (no automated test harness exists)

Not automatable — requires the actual game. Perform after building and deploying the mod.

- [ ] **Composable capabilities:** Spawn one entity per `MovementType` combination you care about
  testing (at minimum: `CanWalk` alone, `CanFly` alone, `CanSwim` alone near a body of water,
  `CanWalk | CanSwim` amphibious spanning land and water, `CanWalk | CanClimb` near a wall).
  Confirm each moves only through terrain its capabilities permit — in particular, confirm a
  `CanSwim`-only entity never leaves water even when given a dry-land target.

- [ ] **TerrainPreference:** For an amphibious (`CanWalk | CanSwim`) entity with a route where both
  a land path and a water path exist at comparable length, confirm `TerrainPreference.Water` routes
  it through water and `TerrainPreference.Land` routes it over land, while `Both` picks whichever is
  naturally shorter.

- [ ] **CanClimb:** Confirm a `CanClimb` entity can path up a vertical wall face it's adjacent to,
  and does not attempt to climb when no adjacent solid face exists.

- [ ] **MoveToSlow vs MoveToFast:** Confirm visibly different movement speeds and different
  animations play for the two tiers, and that a custom entity's own JSON-derived speed (verify via
  a real vanilla creature like `game:drifter-normal`) differs from the Remedy & Ruin fallback
  constants used by an entity with no matching AI tasks.

- [ ] **SpawnClientCustom on a non-vanilla-convention entity:** Spawn something whose animation
  names aren't `"idle"`/`"walk"` (or a custom mod entity, if available) with matching
  `AnimationKeycodes`; confirm the correct animations play for each state and that leaving
  `MoveFast` null falls back to `MoveSlow`'s animation.

- [ ] **Collision push-apart:** Spawn two or more entities and command them to overlapping
  destinations; confirm they push each other apart rather than passing through, and that a push
  toward a wall slides along it instead of clipping through.

- [ ] **Drift recovery:** With two entities positioned so one gets pushed by the other while idle
  at its destination, confirm the pushed entity notices the drift (~1 second later) and paths back
  to its last commanded destination once it exceeds ~1.5 blocks of drift.

- [ ] **Backward compatibility:** Confirm the plain `MoveTo(x, z, y?)` overload still works exactly
  as before this feature (straight-line, terrain-following, no obstacle avoidance) for an entity
  constructed with `MovementType.CanWalk`.

---

## Self-review notes

- **Spec coverage:** every section of `2026-09-08-usability-and-movement-features-design.md` maps
  to a task above — README (Task 7), speed derivation (Task 3), `SpawnClientCustom`/animation
  state machine (Task 6), collision avoidance + drift recovery (Task 6), composable movement
  capabilities (Tasks 2, 4, 5, 6).
- **Type consistency checked:** `ITraversalProfile.GetNeighbors`/`IsTraversable` signatures match
  across `GroundWalkingProfile` (Task 4) and `ThreeDMovementProfile` (Task 5), matching the
  existing `ClientAStar` call sites (unchanged from the prior pathfinding plan). `MovementType`/
  `TerrainPreference` (Task 2) are referenced identically in `ThreeDMovementProfile` (Task 5) and
  `ClientControlledEntity`'s constructor/`CreateProfile` (Task 6). `AnimationKeycodes` field names
  (Task 1) match their use in `SpawnClient`/`SpawnClientCustom`/`SetMoving` (Task 6).
- **Placeholder scan:** no TBD/TODO markers; every step has complete code.
