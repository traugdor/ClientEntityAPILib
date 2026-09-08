# 3D Pathfinding Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add real, obstacle-aware ground and flying pathfinding to `ClientControlledEntity.MoveTo`, per `docs/superpowers/specs/2026-09-08-3d-pathfinding-design.md`.

**Architecture:** Port vanilla's proven `AStar` search loop (from `VSEssentials.dll`, already referenced) to run on `ICoreClientAPI`, reusing its public `PathNode`/`PathNodeSet`/`Cardinal`/`CollisionTester` types verbatim. Neighbor expansion and traversability rules are pulled behind an `ITraversalProfile` so ground (8-directional, ported from vanilla) and flying (26-directional, original design) share one search loop. Search runs on a background `Task`, result is marshaled back to the main thread via `capi.Event.EnqueueMainThreadTask`, and consumed by extending the entity's existing per-tick mover to walk an ordered waypoint list instead of a single point.

**Tech Stack:** C#, `net10.0`, Vintage Story modding API (`VintagestoryAPI.dll`, `VSEssentials.dll`), no test framework available (see "Testing" below and the design doc's own note on this).

**No automated tests:** This mod has no unit-test harness — `IWorldAccessor`/`ICoreClientAPI` have no mocking layer and require a live game instance (documented in the design doc and the mod's own README). Every task below substitutes `dotnet build` for the usual "write failing test / make it pass" cycle, and the plan ends with a manual in-game verification checklist instead of an automated test run.

---

### Task 1: `ITraversalProfile` interface

**Files:**
- Create: `ClientEntityAILib/ClientEntityAILib/Pathfinding/ITraversalProfile.cs`

- [ ] **Step 1: Write the interface**

```csharp
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Essentials;

namespace ClientEntityAILib.Pathfinding
{
    /// <summary>
    /// Pluggable neighbor-expansion and traversability rules for ClientAStar. GroundWalkingProfile
    /// and FlyingProfile are the two implementations - see their own doc comments for what each
    /// ports from vanilla versus builds fresh.
    /// </summary>
    internal interface ITraversalProfile
    {
        /// <summary>Candidate neighbor nodes reachable from one search-graph move starting at <paramref name="from"/>.</summary>
        IEnumerable<PathNode> GetNeighbors(PathNode from);

        /// <summary>
        /// Whether <paramref name="node"/> can be reached from <paramref name="from"/> in one move.
        /// May mutate <paramref name="node"/>'s Y (ground step-up/fall-down) and adds any extra
        /// traversal cost (hazard blocks, liquids) to <paramref name="extraCost"/>.
        /// </summary>
        bool IsTraversable(PathNode from, PathNode node, Cuboidf entityCollBox, ICachingBlockAccessor blockAccess, ref float extraCost);
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run:
```bash
dotnet build "ClientEntityAILib/ClientEntityAILib/ClientEntityAILib.csproj" -v minimal
```
Expected: `Build succeeded. 0 Error(s)` (the interface has no implementations yet, but a lone interface file always compiles).

- [ ] **Step 3: Commit**

```bash
git add ClientEntityAILib/ClientEntityAILib/Pathfinding/ITraversalProfile.cs
git commit -m "add ITraversalProfile seam for ground/flying pathfinding"
```

(Skip this step if the repo has no `.git` yet — run `git init` first if the user wants history for this work, otherwise proceed without committing.)

---

### Task 2: `GroundWalkingProfile` — ported vanilla ground traversal

**Files:**
- Create: `ClientEntityAILib/ClientEntityAILib/Pathfinding/GroundWalkingProfile.cs`

This is a direct port of `AStar.traversable()` from `Vintagestory.Essentials.AStar`
(`VSEssentials.dll`, source verified against the decompiled game at
`../VSDecompile/VSEssentials/Vintagestory.Essentials/AStar.cs`), with `Cardinal fromDir` replaced
by directly-computed `dx`/`dz` (equivalent information, since `GetNeighbors` already used `Cardinal`
to build each candidate node) and the per-search random center-offset jitter removed (applied once
to the final waypoints in `ClientAStar` instead, not per-traversability-check).

- [ ] **Step 1: Write the profile**

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

        public GroundWalkingProfile(EnumAICreatureType creatureType = EnumAICreatureType.Default)
        {
            this.creatureType = creatureType;
        }

        public IEnumerable<PathNode> GetNeighbors(PathNode from)
        {
            for (int i = 0; i < Cardinal.ALL.Length; i++)
            {
                yield return new PathNode(from, Cardinal.ALL[i]);
            }
        }

        public bool IsTraversable(PathNode from, PathNode node, Cuboidf entityCollBox, ICachingBlockAccessor blockAccess, ref float extraCost)
        {
            int dx = node.X - from.X;
            int dz = node.Z - from.Z;
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
git commit -m "port vanilla AStar ground traversal to GroundWalkingProfile"
```

---

### Task 3: `FlyingProfile` — original 3D traversal

**Files:**
- Create: `ClientEntityAILib/ClientEntityAILib/Pathfinding/FlyingProfile.cs`

Unlike `GroundWalkingProfile`, this is **not** a port of any vanilla algorithm — vanilla has no
flying creature type (`EnumAICreatureType` is `Default`/`LandCreature`/`Humanoid`/
`HeatProofCreature`/`SeaCreature` only, verified against
`../VSDecompile/VintagestoryAPI/Vintagestory.API.Common/EnumAICreatureType.cs`). It's original
design built on the same proven `CollisionTester`/`Block.GetTraversalCost` primitives ground
pathing uses, generalized to a 3D grid.

- [ ] **Step 1: Write the profile**

```csharp
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Essentials;

namespace ClientEntityAILib.Pathfinding
{
    /// <summary>
    /// Flying traversal: full 3D (26-directional) grid connectivity with a 3D generalization of
    /// vanilla ground pathing's single-sample diagonal corner-cutting check. This is original
    /// design, not a port of any vanilla algorithm - see the class remarks in the design doc
    /// (docs/superpowers/specs/2026-09-08-3d-pathfinding-design.md) for why. Built on the same
    /// CollisionTester/Block.GetTraversalCost primitives GroundWalkingProfile uses.
    /// </summary>
    internal class FlyingProfile : ITraversalProfile
    {
        private const double Center = 0.5;

        private static readonly (int dx, int dy, int dz)[] Offsets = BuildOffsets();

        private readonly CollisionTester collTester = new CollisionTester();
        private readonly Vec3d tmpVec = new Vec3d();
        private readonly BlockPos tmpPos = new BlockPos(0);
        private readonly EnumAICreatureType creatureType;

        public FlyingProfile(EnumAICreatureType creatureType = EnumAICreatureType.Default)
        {
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
                // a block on every axis actually being moved diagonally, and re-check for a
                // collision there.
                tmpVec.Add(-dx / 2.0, -dy / 2.0, -dz / 2.0);
                if (collTester.IsColliding(blockAccess, entityCollBox, tmpVec, alsoCheckTouch: false)) return false;
            }

            tmpPos.Set(node.X, node.Y, node.Z);
            tmpPos.dimension = node.dimension;
            float cost = blockAccess.GetBlock(tmpPos, 2).GetTraversalCost(tmpPos, creatureType);
            if (cost > 10000f) return false;
            extraCost += cost;

            return true;
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
git add ClientEntityAILib/ClientEntityAILib/Pathfinding/FlyingProfile.cs
git commit -m "add original FlyingProfile for 3D pathfinding"
```

---

### Task 4: `ClientAStar` — the ported search loop

**Files:**
- Create: `ClientEntityAILib/ClientEntityAILib/Pathfinding/ClientAStar.cs`

Ported from `AStar.FindPathOrEscapePath`/`FindPathAsWaypoints`
(`../VSDecompile/VSEssentials/Vintagestory.Essentials/AStar.cs`), with `ICoreServerAPI` replaced by
`ICoreClientAPI`, the flee-mode (`modeMinFleeDistance`) and Manhattan-distance-tolerance
(`mhdistanceTolerance`) parameters dropped (this mod only needs point-to-point pathing, not escape
paths), and neighbor expansion/traversability delegated to an `ITraversalProfile`. Uses a private
`System.Random` for the waypoint center-offset jitter rather than `capi.World.Rand`, since this runs
on a background thread and `System.Random` is not thread-safe against concurrent use from the main
thread.

- [ ] **Step 1: Write the search loop**

```csharp
using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Essentials;

namespace ClientEntityAILib.Pathfinding
{
    /// <summary>
    /// Client-side port of vanilla's AStar.FindPathOrEscapePath (Vintagestory.Essentials.AStar,
    /// VSEssentials.dll), adapted for ICoreClientAPI and an injectable ITraversalProfile so ground
    /// and flying search share this one loop. searchDepth is the node-count budget that bounds
    /// worst-case computation time - the search aborts and returns null once exceeded, exactly
    /// like vanilla's own algorithm.
    /// </summary>
    internal class ClientAStar
    {
        private readonly ICoreClientAPI capi;
        private readonly ICachingBlockAccessor blockAccess;
        private readonly ITraversalProfile profile;
        private readonly int searchDepth;
        private readonly Random rand = new Random();

        private readonly PathNodeSet openSet = new PathNodeSet();
        private readonly HashSet<PathNode> closedSet = new HashSet<PathNode>();

        public ClientAStar(ICoreClientAPI capi, ICachingBlockAccessor blockAccess, ITraversalProfile profile, int searchDepth)
        {
            this.capi = capi;
            this.blockAccess = blockAccess;
            this.profile = profile;
            this.searchDepth = searchDepth;
        }

        public List<Vec3d> FindPath(BlockPos start, BlockPos target, Cuboidf entityCollBox)
        {
            if (entityCollBox.XSize > 100f || entityCollBox.YSize > 100f || entityCollBox.ZSize > 100f)
            {
                capi.Logger.Warning("ClientAStar.FindPath() called with an entity box larger than 100 ({0}). Algorithm not designed for such sizes; ignoring.", entityCollBox);
                return null;
            }

            blockAccess.Begin();

            int nodesChecked = 0;
            PathNode startNode = new PathNode(start);
            PathNode targetNode = new PathNode(target);

            openSet.Clear();
            closedSet.Clear();
            openSet.Add(startNode);

            while (openSet.Count > 0)
            {
                if (nodesChecked++ > searchDepth) return null;

                PathNode nearestNode = openSet.RemoveNearest();
                closedSet.Add(nearestNode);

                if (nearestNode == targetNode)
                {
                    return ToWaypoints(RetracePath(startNode, nearestNode));
                }

                foreach (PathNode neighbourNode in profile.GetNeighbors(nearestNode))
                {
                    float extraCost = 0f;
                    PathNode existingNeighbourNode = openSet.TryFindValue(neighbourNode);
                    if (existingNeighbourNode != null)
                    {
                        float baseCostToNeighbour = nearestNode.gCost + nearestNode.distanceTo(neighbourNode);
                        if (existingNeighbourNode.gCost > baseCostToNeighbour + 0.0001f
                            && profile.IsTraversable(nearestNode, neighbourNode, entityCollBox, blockAccess, ref extraCost)
                            && existingNeighbourNode.gCost > baseCostToNeighbour + extraCost + 0.0001f)
                        {
                            UpdateNode(nearestNode, existingNeighbourNode, extraCost);
                        }
                    }
                    else if (!closedSet.Contains(neighbourNode) && profile.IsTraversable(nearestNode, neighbourNode, entityCollBox, blockAccess, ref extraCost))
                    {
                        UpdateNode(nearestNode, neighbourNode, extraCost);
                        neighbourNode.hCost = neighbourNode.distanceTo(targetNode);
                        openSet.Add(neighbourNode);
                    }
                }
            }

            return null;
        }

        private static void UpdateNode(PathNode nearestNode, PathNode neighbourNode, float extraCost)
        {
            neighbourNode.gCost = nearestNode.gCost + nearestNode.distanceTo(neighbourNode) + extraCost;
            neighbourNode.Parent = nearestNode;
            neighbourNode.pathLength = nearestNode.pathLength + 1;
        }

        private static List<PathNode> RetracePath(PathNode startNode, PathNode endNode)
        {
            int length = endNode.pathLength;
            List<PathNode> path = new List<PathNode>(length);
            for (int i = 0; i < length; i++) path.Add(null);

            PathNode currentNode = endNode;
            for (int i = length - 1; i >= 0; i--)
            {
                path[i] = currentNode;
                currentNode = currentNode.Parent;
            }
            return path;
        }

        // NOTE: unlike vanilla's own ToWaypoints (which skips path[0] - see the design doc's
        // "Verified starting point" section for why that's a deliberate deviation, not an oversight),
        // this includes every step, since the caller here is this mod's own waypoint-following
        // mover rather than vanilla's WaypointsTraverser, and has no equivalent assumption to rely on.
        private List<Vec3d> ToWaypoints(List<PathNode> path)
        {
            double offsetX = 0.3 + rand.NextDouble() * 0.4;
            double offsetZ = 0.3 + rand.NextDouble() * 0.4;

            List<Vec3d> waypoints = new List<Vec3d>(path.Count);
            for (int i = 0; i < path.Count; i++)
            {
                waypoints.Add(path[i].ToWaypoint().Add(offsetX, 0.0, offsetZ));
            }
            return waypoints;
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
git add ClientEntityAILib/ClientEntityAILib/Pathfinding/ClientAStar.cs
git commit -m "add ClientAStar: client-side port of vanilla's AStar search loop"
```

---

### Task 5: Wire pathfinding into `ClientControlledEntity`

**Files:**
- Modify: `ClientEntityAILib/ClientEntityAILib/ClientControlledEntity.cs` (full-file replacement — the diff touches the constructor, adds fields, adds two `MoveTo` overloads, extends `OnGameTick`, and updates `Despawn`/the existing `MoveTo`, so replacing the whole file is clearer than a scattered patch)

- [ ] **Step 1: Replace the file with the following content**

```csharp
using System;
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
        // Real server-assigned entity IDs are always positive, so negative IDs are a safe,
        // collision-free convention for these fake local-only entities. Shared across every
        // handle in the mod so two handles never collide.
        private static long nextFakeEntityId = -1;

        // Push OnReceivedServerPos at ~15/sec to match EntityBehaviorInterpolatePosition's own
        // hardcoded interval; feeding it faster floods its queue and forces a jarring catch-up snap.
        private const float PositionPushInterval = 1f / 15f;

        // Not a verified vanilla value - a reasonable default walking speed in blocks/sec.
        private const double DefaultSpeed = 1.5;

        private const double ArriveDistance = 0.05;

        private readonly ICoreClientAPI capi;
        private readonly bool isFlying;
        private readonly int pathfindingSearchDepth;

        private Entity entity;
        private Vec3d logicalPos;
        private Vec3d moveTarget;
        private bool hasMoveTarget;
        private bool isMoving;
        private string activeAnim;
        private float pushAccum;
        private long tickListenerId = -1;

        private System.Collections.Generic.List<Vec3d> activeWaypoints;
        private int waypointIndex;
        private Action<bool> pendingCallback;
        private int moveGeneration;

        /// <param name="isFlying">
        /// Selects the pathfinding traversal profile used by the callback-based MoveTo overloads
        /// for this handle's whole lifetime (not a per-call choice). Ground pathing (false,
        /// default) is an 8-directional search ported from vanilla's own creature pathfinder.
        /// Flying pathing (true) is a 26-directional search - roughly 3x the branching factor per
        /// node - so expect noticeably longer resolve times for an equivalent search depth.
        /// </param>
        /// <param name="pathfindingSearchDepth">
        /// Node budget for the callback-based MoveTo overloads; null resolves to a type-appropriate
        /// default (4000 ground, 8000 flying). An explicit value is always used as-is.
        /// Each node is roughly one block-step. In the best case (straight open terrain) nodes
        /// explored is close to path length in blocks; every obstacle/elevation change/dead-end
        /// the search has to route around multiplies that past the direct-line distance. Rough
        /// real-world range at the ground default of 4000: ~1300-4000 blocks in open flat terrain,
        /// ~400-800 in rolling/lightly obstructed terrain, ~100-270 (sometimes less) in dense
        /// forest/caves/buildings. Flying's 8000-node default lands in a similar practical range
        /// to ground's 4000, not double it - the larger budget compensates for the 26-directional
        /// search's bigger branching factor rather than extending range past ground's.
        /// </param>
        public ClientControlledEntity(ICoreClientAPI capi, bool isFlying = false, int? pathfindingSearchDepth = null)
        {
            this.capi = capi;
            this.isFlying = isFlying;
            this.pathfindingSearchDepth = pathfindingSearchDepth ?? (isFlying ? 8000 : 4000);

            if (isFlying)
            {
                capi.Logger.Warning("ClientControlledEntity created with isFlying=true: 26-directional 3D pathfinding has roughly 3x the branching factor of ground pathfinding per node, so equivalent search depths take noticeably longer to resolve.");
            }
        }

        /// <summary>
        /// Registers and spawns a client-only entity of the given entity code at spawnPos. Returns
        /// true if the entity type was found and the entity was created and rendered successfully;
        /// false otherwise (bad entity code, or this instance already has an active entity - call
        /// Despawn first to reuse the handle).
        /// </summary>
        public bool SpawnClient(string entityCode, Vec3d spawnPos)
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
            isMoving = false;
            activeAnim = null;
            pushAccum = 0f;
            activeWaypoints = null;
            waypointIndex = 0;
            pendingCallback = null;

            tickListenerId = capi.Event.RegisterGameTickListener(OnGameTick, 30);

            return true;
        }

        /// <summary>
        /// Moves the entity toward (x, z), following the real terrain surface vertically. If y is
        /// given, the target is a full 3D point. This overload does not avoid obstacles - it walks
        /// a straight line and will walk into a wall it can't pass. Returns true if pathing to the
        /// destination is possible (or, for the 2D overload, simply that an entity is active to
        /// move); false if no entity is active. For real obstacle-avoiding pathfinding, use one of
        /// the callback-based MoveTo overloads instead.
        /// </summary>
        public bool MoveTo(double x, double z, double? y = null)
        {
            if (entity == null) return false;

            CancelPendingPathfind();

            moveTarget = new Vec3d(x, y ?? logicalPos.Y, z);
            hasMoveTarget = true;
            return true;
        }

        /// <summary>
        /// Real, obstacle-aware pathfinding to (x, z); the target Y is auto-resolved from the
        /// terrain at that column. <paramref name="onComplete"/> fires exactly once: true if the
        /// entity reached the destination, false if no path was found within the configured node
        /// budget, no entity is active, the handle was despawned before arrival, or a newer MoveTo
        /// call superseded this one first (in the superseded case, only the newer call's callback
        /// ever fires - this one gets nothing).
        /// </summary>
        public void MoveTo(double x, double z, Action<bool> onComplete)
        {
            if (entity == null)
            {
                onComplete?.Invoke(false);
                return;
            }

            double targetY = FindGroundY(capi, x, logicalPos.Y, z);
            MoveTo(x, z, targetY, onComplete);
        }

        /// <summary>
        /// Real, obstacle-aware pathfinding to the exact 3D point (x, y, z). See the (x, z, callback)
        /// overload's doc comment for the callback contract.
        /// </summary>
        public void MoveTo(double x, double z, double y, Action<bool> onComplete)
        {
            if (entity == null)
            {
                onComplete?.Invoke(false);
                return;
            }

            CancelPendingPathfind();
            hasMoveTarget = false;
            pendingCallback = onComplete;

            int myGeneration = moveGeneration;
            BlockPos startPos = new BlockPos((int)Math.Floor(logicalPos.X), (int)Math.Floor(logicalPos.Y), (int)Math.Floor(logicalPos.Z), entity.Pos.Dimension);
            BlockPos targetPos = new BlockPos((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z), entity.Pos.Dimension);
            Cuboidf entityCollBox = entity.CollisionBox.Clone();
            // A fresh profile instance per search call, not a shared field - GroundWalkingProfile/
            // FlyingProfile hold mutable scratch state (tmpVec/tmpPos/etc.) that isn't safe to touch
            // from two overlapping background searches at once (a superseding MoveTo call doesn't
            // cancel the previous search, it just discards its result later - see CancelPendingPathfind).
            ITraversalProfile profile = isFlying ? (ITraversalProfile)new FlyingProfile() : new GroundWalkingProfile();
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
                System.Collections.Generic.List<Vec3d> waypoints = null;
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
            isMoving = false;
            activeAnim = null;

            moveGeneration++;
            activeWaypoints = null;
            waypointIndex = 0;

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

            if (!moving && entity is EntityAgent stoppedAgent)
            {
                stoppedAgent.Controls.WalkVector.Set(0, 0, 0);
            }

            SetMoving(moving);

            pushAccum += dt;
            if (pushAccum >= PositionPushInterval)
            {
                pushAccum -= PositionPushInterval;
                PushPosition(yaw);
            }
        }

        // Horizontal-only stepping with terrain-following Y (FindGroundY) - the existing
        // synchronous MoveTo(x,z,y?) overload's behavior, unchanged from before this feature.
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

            double step = Math.Min(DefaultSpeed * dt, dist);
            logicalPos.X += toTarget.X / dist * step;
            logicalPos.Z += toTarget.Z / dist * step;
            logicalPos.Y = FindGroundY(capi, logicalPos.X, logicalPos.Y, logicalPos.Z);

            yaw = (float)Math.Atan2(toTarget.X, toTarget.Z);

            if (entity is EntityAgent agent)
            {
                agent.Controls.WalkVector.Set(toTarget.X / dist * DefaultSpeed, 0, toTarget.Z / dist * DefaultSpeed);
            }

            return true;
        }

        // Full 3D stepping toward the current waypoint - each waypoint already carries its own
        // valid Y from the search, so (unlike StepDirectTarget) this doesn't re-derive Y via
        // FindGroundY, which would be wrong for a flown path or a ground path mid step-up/fall.
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

            double step = Math.Min(DefaultSpeed * dt, dist);
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
                agent.Controls.WalkVector.Set(toTarget.X / dist * DefaultSpeed, toTarget.Y / dist * DefaultSpeed, toTarget.Z / dist * DefaultSpeed);
            }

            return true;
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

        private void SetMoving(bool moving)
        {
            if (moving == isMoving) return;
            isMoving = moving;

            if (activeAnim != null)
            {
                entity.AnimManager.StopAnimation(activeAnim);
                activeAnim = null;
            }
            entity.AnimManager.StopAnimation(moving ? "idle" : "walk");

            string code = moving ? "walk" : "idle";
            if (entity.AnimManager.StartAnimation(code)) activeAnim = code;
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
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add ClientEntityAILib/ClientEntityAILib/ClientControlledEntity.cs
git commit -m "wire ground/flying pathfinding into ClientControlledEntity.MoveTo"
```

---

### Task 6: Update `README.md`

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Replace the "Part 3" `MoveTo(x, z, y)` pathfinding section**

Find the section headed `### The \`MoveTo(x, z, y)\` overload — real pathfinding, NOT YET DESIGNED HERE` (and its surrounding "Open research items" item #1) and replace it with the current, implemented state:

```markdown
### Real pathfinding: the callback-based `MoveTo` overloads

Beyond the straight-line overloads above, `ClientControlledEntity` also exposes real,
obstacle-aware pathfinding:

```csharp
public void MoveTo(double x, double z, Action<bool> onComplete);
public void MoveTo(double x, double z, double y, Action<bool> onComplete);
```

These port vanilla's own creature pathfinder (`AStar` in `VSEssentials.dll`) to run client-side -
its supporting types (`PathNode`, `PathNodeSet`, `Cardinal`, `CollisionTester`) are public and
reused as-is; only the outer search loop, which vanilla hardcodes to `ICoreServerAPI`, is ported.
The search runs on a background `Task` (never the render thread) and the result is marshaled back
via `capi.Event.EnqueueMainThreadTask`; `onComplete` fires exactly once with `true` (reached the
destination) or `false` (no path found within the configured node budget, no entity active,
despawned before arrival, or superseded by a newer `MoveTo` call).

`ClientControlledEntity`'s constructor takes an `isFlying` flag (default `false`) that selects the
whole handle's traversal profile for these overloads:

- **Ground** (`isFlying: false`) - 8-directional, ported verbatim from vanilla's own
  `AStar.traversable()` (step-height/fall-height handling, liquid costs, diagonal corner-cutting).
- **Flying** (`isFlying: true`) - 26-directional (full 3D grid connectivity). Vanilla has no flying
  creature type to port from, so this is original design built on the same proven
  `CollisionTester`/`Block.GetTraversalCost` primitives ground pathing uses, generalized to 3D
  (including a 3D corner-cutting guard). Roughly 3x the branching factor per node versus ground.

Both use a configurable node-count search budget (`pathfindingSearchDepth`, default 4000 ground /
8000 flying) as the bounded-computation-time mechanism - the search aborts and returns "no path"
once the budget is exceeded, exactly like vanilla's own algorithm. Each node is roughly one
block-step; in open terrain that's close to 1:1 with real range, but obstacles, elevation changes,
and dead-ends multiply nodes explored well past the direct-line distance. See the constructor's XML
doc comment for the full range table.
```

- [ ] **Step 2: Replace "Open research items" item #1**

Find:
```markdown
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
```

Replace with:
```markdown
1. ~~Real pathfinding for `MoveTo(x, z, y)`~~ - **done.** Vanilla's own `AStar`
   (`Vintagestory.Essentials.AStar`, `VSEssentials.dll`) turned out to be pure block-graph search
   with no server-only dependency; its supporting types are public and reused directly. See "Real
   pathfinding: the callback-based `MoveTo` overloads" above and
   `docs/superpowers/specs/2026-09-08-3d-pathfinding-design.md` for the full design. Swimming
   pathfinding (a third profile) remains unimplemented - not requested yet.
```

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "document real ground/flying pathfinding in README"
```

---

### Task 7: Manual in-game verification (no automated test harness exists)

Not automatable - requires the actual game. Perform these checks after building and deploying the
mod (see `build.ps1`/`build.sh`), from a small test mod or the game's mod-dev console that
constructs a `ClientControlledEntity` and calls the API below.

- [ ] **Ground obstacle routing:** Spawn a ground entity (`isFlying: false`) behind a wall or small
  building. Call `MoveTo(targetX, targetZ, y => Console.WriteLine($"reached: {y}"))` with a target
  on the far side. Confirm the entity routes around the obstacle (not a straight line through it,
  unlike the existing 2-arg/3-arg overloads) and the callback fires `true` on arrival.

- [ ] **Flying vertical routing:** Spawn a flying entity (`isFlying: true`). Call
  `MoveTo(x, z, y, callback)` with a target that requires climbing over a wall or through a gap at
  a different height. Confirm the entity moves vertically as part of the path, not just
  horizontally, and the callback fires `true` on arrival.

- [ ] **Callback fires exactly once - success:** From the two checks above, confirm the callback
  printed exactly once each.

- [ ] **Callback fires exactly once - superseded:** Call `MoveTo(x1, z1, cb1)` immediately followed
  by `MoveTo(x2, z2, cb2)` before the first can resolve. Confirm only `cb2` ever fires, and `cb1`
  never fires.

- [ ] **Callback fires exactly once - despawn:** Call `MoveTo(x, z, callback)` on a distant target,
  then call `Despawn()` before it can resolve. Confirm `callback(false)` fires.

- [ ] **Unreachable target:** Call `MoveTo(...)` targeting a point inside a fully sealed room (no
  reachable path) or a point requiring more than `pathfindingSearchDepth` nodes. Confirm
  `callback(false)` fires within a few seconds rather than hanging.

- [ ] **No frame hitches:** While a search is in flight (especially a large/near-budget one),
  confirm no visible stutter - the search runs on a background `Task`, not the render thread.

---

## Self-review notes

- **Spec coverage:** every section of `2026-09-08-3d-pathfinding-design.md` maps to a task above -
  public API (Task 5), node budget/conversion math (Task 5's constructor doc comment), ported
  ground search (Tasks 2 & 4), original flying search (Tasks 3 & 4), cancellation/error handling
  (Task 5's `CancelPendingPathfind`/`Despawn`), README update (Task 6), manual testing (Task 7).
- **Resolved spec ambiguity:** the design doc's "Public API additions" section lists "despawned
  before arrival" as a `false`-callback case, while its "Error handling and cancellation" section
  only says the in-flight search "gets discarded" without specifying callback behavior. Task 5's
  `Despawn()` resolves this in favor of the more explicit statement: it invokes `pendingCallback`
  with `false` before clearing state. Superseding `MoveTo` calls (any overload) invoke nothing, per
  the unambiguous "no callback fires for a call that gets superseded" sentence.
- **Type consistency checked:** `ITraversalProfile.GetNeighbors`/`IsTraversable` signatures match
  across Task 1 (interface), Task 2 (`GroundWalkingProfile`), Task 3 (`FlyingProfile`), and Task 4
  (`ClientAStar`'s call sites). `ClientAStar` constructor signature matches its call site in Task 5.
  `pathfindingSearchDepth`/`isFlying` field names match between the constructor and their use sites.
