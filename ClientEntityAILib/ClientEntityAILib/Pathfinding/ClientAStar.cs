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

        // NOTE: unlike vanilla's own ToWaypoints, which skips path[0], this includes every step -
        // path[0] is already the first real step away from start (RetracePath never puts the start
        // node itself into the array), and the caller here is this mod's own waypoint-following
        // mover rather than vanilla's WaypointsTraverser, which has no equivalent assumption to skip
        // the first entry for.
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
