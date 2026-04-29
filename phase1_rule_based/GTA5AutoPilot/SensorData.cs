using System.Collections.Generic;
using GTA;
using GTA.Math;

namespace GTA5AutoPilot
{
    /// <summary>
    /// Data bundle passed to DecisionEngine each tick.
    /// Contains all perception outputs for the current frame.
    /// </summary>
    public class SensorData
    {
        public Vehicle Vehicle;
        public PathInfo PathInfo;
        public List<EntityInfo> NearbyEntities;
        public TrafficLightState TrafficLightState;
        public CollisionRiskLevel CollisionRisk;
        public float LaneOffset;
        public float TargetSpeed;
        public IntersectionInfo IntersectionInfo;
        public PerceptionMode SourceMode = PerceptionMode.GameAPI;
    }

    public enum CollisionRiskLevel
    {
        None,
        Low,
        Medium,
        High,
        Imminent
    }

    public enum PerceptionMode
    {
        GameAPI,    // Use GTA V internal APIs (Phase 1 default)
        Vision,     // Use Python visual perception pipeline (Phase 2)
        Hybrid      // Vision for entities + traffic lights, GameAPI for navigation
    }

    public enum TrafficLightState
    {
        None,       // No traffic light detected
        Green,
        Yellow,
        Red
    }

    public enum DecisionState
    {
        Cruising,
        StoppingAtLight,
        WaitingAtLight,
        Turning,
        Evading,
        Stuck,
        EmergencyStop
    }

    public class PathInfo
    {
        /// <summary>Current target waypoint position</summary>
        public Vector3 Waypoint;

        /// <summary>Road heading at waypoint (radians)</summary>
        public float RoadHeading;

        /// <summary>True if approaching an intersection</summary>
        public bool IsIntersectionAhead;

        /// <summary>Distance to next intersection (0 if none)</summary>
        public float DistanceToIntersection;

        /// <summary>True if waypoint is an intersection node</summary>
        public bool IsAtIntersection;

        /// <summary>Road type: 0=urban, 1=highway, 2=alley, 3=gravel</summary>
        public int RoadType;

        /// <summary>Number of lanes in current direction</summary>
        public int LaneCount;

        /// <summary>True if we have a valid destination set</summary>
        public bool HasDestination;

        /// <summary>Distance remaining to destination</summary>
        public float DistanceToDestination;
    }

    public class EntityInfo
    {
        public Entity Entity;
        public Vector3 Position;
        public Vector3 Velocity;
        public float Speed;
        public float Distance;
        public float Heading;
        public bool IsVehicle;
        public bool IsPedestrian;
        public float TimeToCollision; // TTC in seconds, float.MaxValue if not on collision course
        public bool IsInForwardCone;
        public bool IsOncoming;
    }

    public class IntersectionInfo
    {
        public bool IsApproaching;
        public bool IsAtIntersection;
        public float DistanceToIntersection;
        public bool ShouldYield;          // Yield to cross traffic
        public bool HasTrafficLight;
        public TrafficLightState LightState;
        public bool TurnRequired;         // Need to turn at this intersection
        public TurnDirection TurnDirection; // Left, Right, or Straight
    }

    public enum TurnDirection
    {
        Straight,
        Left,
        Right
    }
}
