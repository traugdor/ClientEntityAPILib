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

        /// <summary>
        /// Plays an arbitrary, caller-defined one-shot animation (attack, eat, wave, whatever the
        /// consuming mod decides an "action" means for this entity) - not tied to any built-in
        /// concept of combat or interaction. Interrupts whatever movement animation is currently
        /// active the same way SetMoving does internally, and reuses that same activeAnim
        /// bookkeeping rather than a second parallel animation-state machine: it is automatically
        /// stopped and replaced the next time the movement tier actually changes (idle/slow/fast),
        /// which is when SetMoving would otherwise touch AnimManager. If the tier never changes
        /// while this is playing (e.g. triggered mid-stride without ever stopping), it is not
        /// self-healing - this is meant for the stop-act-resume pattern, not a mid-movement
        /// interrupt. Returns true if an entity is active and AnimManager accepted the animation
        /// code; false if no entity is active, animationCode is null/empty, or the code wasn't
        /// recognized (AnimManager.StartAnimation no-ops rather than throwing, so an unrecognized
        /// code is safe regardless of the spawned entity type).
        /// </summary>
        public bool PlayOneShotAnimation(string animationCode)
        {
            if (entity == null || string.IsNullOrEmpty(animationCode)) return false;

            if (activeAnim != null)
            {
                entity.AnimManager.StopAnimation(activeAnim);
                activeAnim = null;
            }

            if (entity.AnimManager.StartAnimation(animationCode))
            {
                activeAnim = animationCode;
                return true;
            }

            return false;
        }

        /// <summary>
        /// The entity's real, ground-truth position - logicalPos, not entity.Pos, which becomes a
        /// smoothed/lagging value once PushPosition starts feeding it into
        /// EntityBehaviorInterpolatePosition. Returns a defensive copy (mutating it has no effect
        /// on this handle), or null if no entity is currently spawned.
        /// </summary>
        public Vec3d GetPosition()
        {
            return logicalPos?.Clone();
        }

        /// <summary>
        /// Straight-line distance from the entity's real (logicalPos) position to (x, y, z) - the
        /// generic building block for a caller's own range-based decisions (attack range, flee
        /// range, whatever threshold that entity type cares about), independent of any pathfinding
        /// callback or fixed destination. Returns double.PositiveInfinity if no entity is spawned.
        /// </summary>
        public double DistanceTo(double x, double y, double z)
        {
            if (logicalPos == null) return double.PositiveInfinity;

            double dx = logicalPos.X - x;
            double dy = logicalPos.Y - y;
            double dz = logicalPos.Z - z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
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
            if (entity == null || tier == currentTier) return;
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
