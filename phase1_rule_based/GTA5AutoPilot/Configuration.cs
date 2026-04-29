namespace GTA5AutoPilot
{
    /// <summary>
    /// Central configuration for all tunable parameters.
    /// Modify these values to adjust driving behavior.
    /// </summary>
    public static class Configuration
    {
        // --- Vehicle Control ---
        public const float MaxSteerAngle = 1.0f;        // Max normalized steering
        public const float SteerSmoothing = 0.15f;       // Low-pass filter factor (lower = smoother)
        public const float ThrottleSmoothing = 0.2f;
        public const float BrakeSmoothing = 0.3f;

        // --- Path Following ---
        public const float WaypointLookAheadDistance = 30f; // How far ahead to look for next waypoint
        public const float WaypointReachedThreshold = 5f;    // Distance to consider waypoint reached
        public const int MaxPathNodes = 50;                   // Max nodes to request from game

        // --- Lane Keeping ---
        public const float LaneKeepPGain = 0.8f;     // PID proportional gain
        public const float LaneKeepDGain = 0.3f;     // PID derivative gain
        public const float MaxLaneSteerCorrection = 0.4f;

        // --- Speed Control ---
        public const float UrbanSpeedLimit = 13.4f;    // 30 mph in m/s
        public const float HighwaySpeedLimit = 31.3f;  // 70 mph in m/s
        public const float IntersectionSpeedLimit = 6.7f; // 15 mph
        public const float MinFollowDistance = 8f;      // Minimum distance to vehicle ahead
        public const float SafeFollowTimeGap = 2.0f;    // Seconds of following distance

        // --- Entity Detection ---
        public const float DetectionRadiusNear = 25f;   // Near zone radius
        public const float DetectionRadiusFar = 80f;    // Far zone radius
        public const float ForwardConeAngle = 60f;      // Half-angle of forward detection cone
        public const float PedestrianDetectionRadius = 20f;

        // --- Collision Prediction ---
        public const float TTCWarningThreshold = 3.0f;   // Time-to-collision warning (seconds)
        public const float TTCEmergencyThreshold = 1.5f;  // Time-to-collision emergency (seconds)
        public const float MinBrakingDistance = 5f;       // Always brake within this distance

        // --- Traffic Light ---
        public const float TrafficLightDetectionRange = 60f;  // Max distance to detect lights
        public const float TrafficLightStopDistance = 8f;     // Distance to stop before light
        public const float TrafficLightScanRadius = 50f;      // Radius around vehicle to search for light props

        // --- Intersection ---
        public const float IntersectionDetectionDistance = 25f; // Look ahead for intersections
        public const float IntersectionSlowDistance = 30f;      // Start slowing this far from intersection
        public const float TurnCompleteAngle = 30f;              // Consider turn complete within this angle

        // --- Decision Engine ---
        public const float StuckTimeThreshold = 5f;      // Seconds before car is considered stuck
        public const float StuckSpeedThreshold = 0.5f;   // m/s below which car is considered stationary

        // --- Telemetry ---
        public const string TelemetryHost = "127.0.0.1";
        public const int TelemetryPort = 21555;
        public const int TelemetrySendIntervalMs = 50;   // Send telemetry every 50ms (20 Hz)
    }
}
