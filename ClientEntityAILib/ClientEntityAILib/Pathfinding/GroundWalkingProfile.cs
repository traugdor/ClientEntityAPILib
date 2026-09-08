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
