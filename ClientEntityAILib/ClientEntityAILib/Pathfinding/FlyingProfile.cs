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
