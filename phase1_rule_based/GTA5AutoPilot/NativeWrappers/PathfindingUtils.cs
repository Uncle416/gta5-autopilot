using GTA;
using GTA.Math;

namespace GTA5AutoPilot.NativeWrappers
{
    /// <summary>
    /// Utility methods wrapping GTA V pathfinding natives.
    /// </summary>
    public static class PathfindingUtils
    {
        /// <summary>
        /// Get the closest vehicle node with its road heading.
        /// </summary>
        /// <param name="position">Search origin</param>
        /// <param name="nth">0 = closest, 1 = next, etc.</param>
        /// <returns>(nodePosition, heading) or (Zero, 0) on failure</returns>
        public static (Vector3, float) GetClosestNodeWithHeading(Vector3 position, int nth = 1)
        {
            OutputArgument outPos = new OutputArgument();
            OutputArgument outHeading = new OutputArgument();

            Function.Call(Hash.GET_NTH_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                position.X, position.Y, position.Z,
                nth, outPos, outHeading,
                1, 3.0f, 0f);

            Vector3 nodePos = outPos.GetResult<Vector3>();
            float heading = outHeading.GetResult<float>();

            return (nodePos, heading);
        }

        /// <summary>
        /// Get road node properties: density and flags bitmask.
        /// Flags: bit0=intersection, bit1=highway, bit2=alley, bit3=gravel
        /// </summary>
        public static (int density, int flags) GetNodeProperties(Vector3 position)
        {
            OutputArgument outDensity = new OutputArgument();
            OutputArgument outFlags = new OutputArgument();

            Function.Call(Hash.GET_VEHICLE_NODE_PROPERTIES,
                position.X, position.Y, position.Z,
                outDensity, outFlags);

            return (outDensity.GetResult<int>(), outFlags.GetResult<int>());
        }

        /// <summary>
        /// Check if a position is at an intersection.
        /// </summary>
        public static bool IsIntersectionNode(Vector3 position)
        {
            var (_, flags) = GetNodeProperties(position);
            return (flags & 1) != 0;
        }

        /// <summary>
        /// Check if a position is on a highway.
        /// </summary>
        public static bool IsHighwayNode(Vector3 position)
        {
            var (_, flags) = GetNodeProperties(position);
            return (flags & 2) != 0;
        }

        /// <summary>
        /// Get road layout information at a position.
        /// Returns (lanesForwardBackward, lanesLeftRight).
        /// </summary>
        public static (int forwardBackward, int leftRight) GetRoadLaneCount(Vector3 position)
        {
            OutputArgument corner1 = new OutputArgument();
            OutputArgument corner2 = new OutputArgument();
            OutputArgument corner3 = new OutputArgument();
            OutputArgument corner4 = new OutputArgument();
            OutputArgument lanesFB = new OutputArgument();
            OutputArgument lanesLR = new OutputArgument();

            Function.Call(Hash.GET_CLOSEST_ROAD,
                position.X, position.Y, position.Z,
                1f, 1,
                corner1, corner2, corner3, corner4,
                lanesFB, lanesLR);

            return (lanesFB.GetResult<int>(), lanesLR.GetResult<int>());
        }

        /// <summary>
        /// Get navigation direction towards a target coordinate.
        /// Similar to GPS routing. Returns direction vector.
        /// </summary>
        public static Vector3 GetDirectionToCoord(Vector3 from, Vector3 to)
        {
            OutputArgument outDir = new OutputArgument();
            OutputArgument outP5 = new OutputArgument();
            OutputArgument outP6 = new OutputArgument();

            Function.Call(Hash.GENERATE_DIRECTIONS_TO_COORD,
                to.X, to.Y, to.Z,
                true, outDir, outP5, outP6);

            return outDir.GetResult<Vector3>();
        }

        /// <summary>
        /// Check if a given position lies on a road.
        /// </summary>
        public static bool IsOnRoad(Vector3 position, Vehicle vehicle = null)
        {
            return Function.Call<bool>(Hash.IS_POINT_ON_ROAD,
                position.X, position.Y, position.Z,
                vehicle ?? 0);
        }
    }
}
