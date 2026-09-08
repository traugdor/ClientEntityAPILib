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

        private List<Vec3d> activeWaypoints;
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

        public void Despawn()
        {
            if (entity == null) return;

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
