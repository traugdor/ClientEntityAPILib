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
