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
