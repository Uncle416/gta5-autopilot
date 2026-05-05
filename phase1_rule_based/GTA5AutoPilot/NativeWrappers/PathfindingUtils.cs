using GTA;
using GTA.Math;

namespace GTA5AutoPilot.NativeWrappers
{
    /// <summary>
    /// Stub pathfinding — returns vehicle-forward-based waypoints without
    /// calling native node functions that may differ in SHVDN Enhanced v3.
    /// </summary>
    public static class PathfindingUtils
    {
        public static (Vector3, float) GetClosestNodeWithHeading(Vector3 position, int nth = 1)
        {
            // Return zero — caller will use vehicle ForwardVector as fallback
            return (Vector3.Zero, 0f);
        }

        public static (int density, int flags) GetNodeProperties(Vector3 position)
        {
            return (0, 0);
        }

        public static bool IsIntersectionNode(Vector3 position)
        {
            return false;
        }

        public static bool IsHighwayNode(Vector3 position)
        {
            return false;
        }

        public static (int forwardBackward, int leftRight) GetRoadLaneCount(Vector3 position)
        {
            return (2, 1);
        }

        public static Vector3 GetDirectionToCoord(Vector3 from, Vector3 to)
        {
            // Simple direction toward destination
            var dir = to - from;
            dir.Normalize();
            return dir;
        }

        public static bool IsOnRoad(Vector3 position, Vehicle vehicle = null)
        {
            return true; // Assume always on road
        }
    }
}
