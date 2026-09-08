using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

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

        private Entity entity;
        private Vec3d logicalPos;
        private Vec3d moveTarget;
        private bool hasMoveTarget;
        private bool isMoving;
        private string activeAnim;
        private float pushAccum;
        private long tickListenerId = -1;

        public ClientControlledEntity(ICoreClientAPI capi)
        {
            this.capi = capi;
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

            tickListenerId = capi.Event.RegisterGameTickListener(OnGameTick, 30);

            return true;
        }

        /// <summary>
        /// Moves the entity toward (x, z), following the real terrain surface vertically. If y is
        /// given, the target is a full 3D point and the entity paths to it without moving through
        /// terrain (walls, floors, ceilings) rather than just following the ground under a straight
        /// line. Returns true if pathing to the destination is possible (or, for the 2D overload,
        /// simply that an entity is active to move); false if no entity is active, or - only when y
        /// is given - no path to the destination could be found.
        /// </summary>
        public bool MoveTo(double x, double z, double? y = null)
        {
            if (entity == null) return false;

            // 3D obstacle-aware pathfinding for the y-given overload is not implemented (see
            // README "Open research items" #1); this always drives the straight-line, terrain-
            // following mover, same as the 2D overload.
            moveTarget = new Vec3d(x, y ?? logicalPos.Y, z);
            hasMoveTarget = true;
            return true;
        }

        public void Despawn()
        {
            if (entity == null) return;

            if (tickListenerId != -1)
            {
                capi.Event.UnregisterGameTickListener(tickListenerId);
                tickListenerId = -1;
            }

            ClientMain game = (ClientMain)capi.World;
            EntityDespawnData despawnData = new EntityDespawnData { Reason = EnumDespawnReason.Removed };
            game.eventManager.TriggerEntityDespawn(entity, despawnData);
            game.RemoveEntityRenderer(entity);
            entity.OnEntityDespawn(despawnData);
            ((IClientWorldAccessor)capi.World).LoadedEntities.Remove(entity.EntityId);

            entity = null;
            hasMoveTarget = false;
            isMoving = false;
            activeAnim = null;
        }

        public void Dispose()
        {
            Despawn();
        }

        private void OnGameTick(float dt)
        {
            if (entity == null) return;

            bool moving = false;
            float yaw = entity.Pos.Yaw;

            if (hasMoveTarget)
            {
                // Horizontal delta only - mixing in a non-zero Y here would turn dist into a 3D
                // distance inflated by however far logicalPos.Y has drifted from the target's Y,
                // corrupting both the arrival check and the normalized step direction.
                Vec3d toTarget = new Vec3d(moveTarget.X - logicalPos.X, 0, moveTarget.Z - logicalPos.Z);
                double dist = toTarget.Length();

                if (dist > ArriveDistance)
                {
                    moving = true;

                    double step = Math.Min(DefaultSpeed * dt, dist);
                    logicalPos.X += toTarget.X / dist * step;
                    logicalPos.Z += toTarget.Z / dist * step;
                    logicalPos.Y = FindGroundY(capi, logicalPos.X, logicalPos.Y, logicalPos.Z);

                    yaw = (float)Math.Atan2(toTarget.X, toTarget.Z);

                    if (entity is EntityAgent agent)
                    {
                        agent.Controls.WalkVector.Set(toTarget.X / dist * DefaultSpeed, 0, toTarget.Z / dist * DefaultSpeed);
                    }
                }
                else
                {
                    hasMoveTarget = false;
                }
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
