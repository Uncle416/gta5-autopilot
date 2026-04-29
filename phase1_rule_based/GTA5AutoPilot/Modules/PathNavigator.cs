using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;

namespace GTA5AutoPilot.Modules
{
    /// <summary>
    /// Road node-based navigation. Uses GTA V's vehicle node graph to find
    /// and follow waypoints along roads toward a destination.
    /// </summary>
    public class PathNavigator
    {
        private Vector3 _destination;
        private Vector3 _currentWaypoint;
        private float _currentHeading;
        private int _consecutiveFailures;

        public bool HasDestination { get; private set; }

        public void SetDestination(Vector3 destination)
        {
            _destination = destination;
            HasDestination = true;
        }

        public void Reset(Vehicle vehicle)
        {
            _consecutiveFailures = 0;
            // Initialize waypoint to current position
            _currentWaypoint = vehicle.Position;
        }

        public PathInfo GetNextWaypoint(Vehicle vehicle)
        {
            var info = new PathInfo();
            var pos = vehicle.Position;

            try
            {
                // Get the closest road node with heading
                OutputArgument outPos = new OutputArgument();
                OutputArgument outHeading = new OutputArgument();

                Function.Call(Hash.GET_NTH_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                    pos.X, pos.Y, pos.Z,
                    1, // 1st closest (0-based would be the one we're on, 1 is ahead)
                    outPos, outHeading,
                    1, // nodeType: 1 = any road
                    0f, 0f);

                Vector3 nodePos = outPos.GetResult<Vector3>();
                float nodeHeading = outHeading.GetResult<float>();

                if (nodePos == Vector3.Zero)
                {
                    _consecutiveFailures++;
                    // Fallback: use vehicle forward projection
                    nodePos = pos + vehicle.ForwardVector * Configuration.WaypointLookAheadDistance;
                    nodeHeading = vehicle.Heading;
                }
                else
                {
                    _consecutiveFailures = 0;
                }

                // If we have a destination, try to get navigation direction
                if (HasDestination && _destination != Vector3.Zero)
                {
                    OutputArgument outDir = new OutputArgument();
                    OutputArgument outP5 = new OutputArgument();
                    OutputArgument outP6 = new OutputArgument();

                    Function.Call(Hash.GENERATE_DIRECTIONS_TO_COORD,
                        _destination.X, _destination.Y, _destination.Z,
                        true, outDir, outP5, outP6);

                    Vector3 navDir = outDir.GetResult<Vector3>();
                    if (navDir != Vector3.Zero)
                    {
                        // Blend node heading with nav direction
                        nodePos = pos + navDir * Configuration.WaypointLookAheadDistance;
                    }

                    info.DistanceToDestination = Vector3.Distance(pos, _destination);
                }

                _currentWaypoint = nodePos;
                _currentHeading = nodeHeading;

                // Get node properties for intersection detection
                OutputArgument outDensity = new OutputArgument();
                OutputArgument outFlags = new OutputArgument();

                Function.Call(Hash.GET_VEHICLE_NODE_PROPERTIES,
                    nodePos.X, nodePos.Y, nodePos.Z,
                    outDensity, outFlags);

                int flags = outFlags.GetResult<int>();
                info.IsAtIntersection = (flags & 1) != 0;     // bit 0: intersection
                info.RoadType = (flags & 2) != 0 ? 1 : 0;     // bit 1: highway
                if ((flags & 4) != 0) info.RoadType = 2;      // bit 2: alley
                if ((flags & 8) != 0) info.RoadType = 3;      // bit 3: gravel

                // Look ahead for intersections
                Vector3 aheadPos = pos + vehicle.ForwardVector * Configuration.IntersectionDetectionDistance;
                OutputArgument aheadDensity = new OutputArgument();
                OutputArgument aheadFlags = new OutputArgument();

                Function.Call(Hash.GET_VEHICLE_NODE_PROPERTIES,
                    aheadPos.X, aheadPos.Y, aheadPos.Z,
                    aheadDensity, aheadFlags);

                int aheadFlagsVal = aheadFlags.GetResult<int>();
                info.IsIntersectionAhead = (aheadFlagsVal & 1) != 0;

                if (info.IsIntersectionAhead)
                {
                    Vector3 intersectionPos = GetNearestIntersectionNode(pos, vehicle.ForwardVector);
                    info.DistanceToIntersection = Vector3.Distance(pos, intersectionPos);
                }

                // Get lane count
                int lanesForward = GetLaneCount(pos);
                info.LaneCount = Math.Max(1, lanesForward);

                info.Waypoint = _currentWaypoint;
                info.RoadHeading = _currentHeading;
                info.HasDestination = HasDestination;
            }
            catch (Exception)
            {
                // Extreme fallback: project forward
                info.Waypoint = pos + vehicle.ForwardVector * Configuration.WaypointLookAheadDistance;
                info.RoadHeading = vehicle.Heading;
                info.LaneCount = 2;
            }

            return info;
        }

        private Vector3 GetNearestIntersectionNode(Vector3 position, Vector3 forward)
        {
            // Scan ahead for intersection nodes
            Vector3 scanPos = position;
            for (int i = 0; i < 10; i++)
            {
                scanPos += forward * 5f;
                OutputArgument outDensity = new OutputArgument();
                OutputArgument outFlags = new OutputArgument();

                Function.Call(Hash.GET_VEHICLE_NODE_PROPERTIES,
                    scanPos.X, scanPos.Y, scanPos.Z,
                    outDensity, outFlags);

                if ((outFlags.GetResult<int>() & 1) != 0)
                    return scanPos;
            }
            return position + forward * Configuration.IntersectionDetectionDistance;
        }

        private int GetLaneCount(Vector3 position)
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

            return lanesFB.GetResult<int>();
        }
    }
}
