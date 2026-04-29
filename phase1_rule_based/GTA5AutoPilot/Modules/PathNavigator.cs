using System;
using GTA;
using GTA.Math;
using GTA5AutoPilot.NativeWrappers;

namespace GTA5AutoPilot.Modules
{
    /// <summary>
    /// Road node-based navigation using GTA V's vehicle node graph.
    /// Uses PathfindingUtils for all native calls.
    /// </summary>
    public class PathNavigator
    {
        private Vector3 _destination;
        private Vector3 _currentWaypoint;
        private float _currentHeading;
        private int _consecutiveFailures;

        // Cached values to reduce native calls
        private Vector3 _lastRoadQueryPos;
        private int _cachedLaneCount = 2;
        private int _cachedRoadType;

        public bool HasDestination { get; private set; }

        public void SetDestination(Vector3 destination)
        {
            _destination = destination;
            HasDestination = true;
        }

        public void Reset(Vehicle vehicle)
        {
            _consecutiveFailures = 0;
            _currentWaypoint = vehicle.Position;
            _lastRoadQueryPos = Vector3.Zero;
        }

        public PathInfo GetNextWaypoint(Vehicle vehicle)
        {
            var info = new PathInfo();
            var pos = vehicle.Position;

            try
            {
                // Get closest road node with heading
                var (nodePos, nodeHeading) = PathfindingUtils.GetClosestNodeWithHeading(pos, 1);

                if (nodePos == Vector3.Zero)
                {
                    _consecutiveFailures++;
                    nodePos = pos + vehicle.ForwardVector * Configuration.WaypointLookAheadDistance;
                    nodeHeading = vehicle.Heading;
                }
                else
                {
                    _consecutiveFailures = 0;
                }

                // Navigation direction toward destination
                if (HasDestination && _destination != Vector3.Zero)
                {
                    Vector3 navDir = PathfindingUtils.GetDirectionToCoord(pos, _destination);
                    if (navDir != Vector3.Zero)
                        nodePos = pos + navDir * Configuration.WaypointLookAheadDistance;

                    info.DistanceToDestination = Vector3.Distance(pos, _destination);
                }

                _currentWaypoint = nodePos;
                _currentHeading = nodeHeading;

                // Cache road properties (only re-query when moved > 5m)
                if (Vector3.Distance(pos, _lastRoadQueryPos) > 5f)
                {
                    var (_, flags) = PathfindingUtils.GetNodeProperties(nodePos);
                    _cachedRoadType = (flags & 2) != 0 ? 1 : 0;
                    if ((flags & 4) != 0) _cachedRoadType = 2;
                    if ((flags & 8) != 0) _cachedRoadType = 3;
                    _cachedLaneCount = Math.Max(1, PathfindingUtils.GetRoadLaneCount(pos).forwardBackward);
                    _lastRoadQueryPos = pos;
                }

                info.IsAtIntersection = PathfindingUtils.IsIntersectionNode(nodePos);
                info.RoadType = _cachedRoadType;
                info.LaneCount = _cachedLaneCount;

                // Look ahead for intersection
                Vector3 aheadPos = pos + vehicle.ForwardVector * Configuration.IntersectionDetectionDistance;
                info.IsIntersectionAhead = PathfindingUtils.IsIntersectionNode(aheadPos);

                if (info.IsIntersectionAhead)
                {
                    var intersectionPos = PathfindingUtils.GetClosestNodeWithHeading(aheadPos, 1).Item1;
                    info.DistanceToIntersection = Vector3.Distance(pos, intersectionPos);
                }

                info.Waypoint = nodePos;
                info.RoadHeading = nodeHeading;
                info.HasDestination = HasDestination;
            }
            catch (Exception)
            {
                info.Waypoint = pos + vehicle.ForwardVector * Configuration.WaypointLookAheadDistance;
                info.RoadHeading = vehicle.Heading;
                info.LaneCount = 2;
            }

            return info;
        }
    }
}
