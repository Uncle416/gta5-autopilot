using System;
using GTA;
using GTA.Math;
using GTA.Native;

namespace GTA5AutoPilot.Modules
{
    /// <summary>
    /// Handles intersection detection, turn planning, and execution.
    /// Determines whether the vehicle needs to turn at an upcoming intersection
    /// and manages the turn maneuver.
    /// </summary>
    public class IntersectionHandler
    {
        private TurnDirection _pendingTurn = TurnDirection.Straight;
        private bool _isTurning;
        private float _turnStartHeading;
        private float _turnAccumulatedAngle;

        public IntersectionInfo EvaluateIntersection(Vehicle vehicle, PathInfo pathInfo)
        {
            var info = new IntersectionInfo
            {
                IsApproaching = pathInfo.IsIntersectionAhead,
                IsAtIntersection = pathInfo.IsAtIntersection,
                DistanceToIntersection = pathInfo.DistanceToIntersection,
                HasTrafficLight = false, // Will be set by TrafficLightDetector
                TurnDirection = TurnDirection.Straight
            };

            if (!pathInfo.IsIntersectionAhead && !pathInfo.IsAtIntersection)
            {
                _isTurning = false;
                _pendingTurn = TurnDirection.Straight;
                return info;
            }

            // Determine if we need to turn
            if (pathInfo.IsAtIntersection && pathInfo.HasDestination)
            {
                Vector3 vehiclePos = vehicle.Position;
                float headingRad = pathInfo.RoadHeading * (float)Math.PI / 180f;
                Vector3 roadForward = new Vector3(
                    (float)Math.Cos(headingRad),
                    (float)Math.Sin(headingRad),
                    0f);

                // Get the "next" node after the intersection to determine turn direction
                OutputArgument outPos = new OutputArgument();
                OutputArgument outHeading = new OutputArgument();

                Function.Call(Hash.GET_NTH_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                    vehiclePos.X + roadForward.X * 30f,
                    vehiclePos.Y + roadForward.Y * 30f,
                    vehiclePos.Z,
                    1, outPos, outHeading,
                    1, 0f, 0f);

                float nextHeading = outHeading.GetResult<float>();
                float headingDiff = nextHeading - pathInfo.RoadHeading;

                // Normalize to [-180, 180] degrees
                while (headingDiff > 180f) headingDiff -= 360f;
                while (headingDiff < -180f) headingDiff += 360f;

                const float straightThreshold = 15f;
                if (Math.Abs(headingDiff) < straightThreshold)
                    info.TurnDirection = TurnDirection.Straight;
                else if (headingDiff > 0)
                    info.TurnDirection = TurnDirection.Left;
                else
                    info.TurnDirection = TurnDirection.Right;

                info.TurnRequired = info.TurnDirection != TurnDirection.Straight;
            }

            // Yield to cross traffic at intersections without lights
            info.ShouldYield = pathInfo.IsAtIntersection && !info.HasTrafficLight;

            return info;
        }

        /// <summary>
        /// Get the steering target for executing a turn.
        /// Returns 0 if no turn is in progress.
        /// </summary>
        public float GetTurnSteering(Vehicle vehicle, IntersectionInfo intersectionInfo, PathInfo pathInfo)
        {
            if (!intersectionInfo.TurnRequired && !_isTurning)
                return 0f;

            if (!_isTurning)
            {
                // Start a new turn
                _isTurning = true;
                _pendingTurn = intersectionInfo.TurnDirection;
                _turnStartHeading = vehicle.Heading;
                _turnAccumulatedAngle = 0f;
            }

            float currentHeading = vehicle.Heading;
            float angleDelta = currentHeading - _turnStartHeading;

            // Normalize
            while (angleDelta > Math.PI) angleDelta -= 2f * (float)Math.PI;
            while (angleDelta < -Math.PI) angleDelta += 2f * (float)Math.PI;

            _turnAccumulatedAngle = angleDelta;

            // Turn complete when accumulated angle reaches ~90 degrees
            float targetAngle = _pendingTurn == TurnDirection.Left ? 90f : -90f;
            float remainingAngle = targetAngle - _turnAccumulatedAngle;

            // Check if turn is complete
            if (Math.Abs(remainingAngle) < Configuration.TurnCompleteAngle * Math.PI / 180f)
            {
                _isTurning = false;
                _pendingTurn = TurnDirection.Straight;
                return 0f;
            }

            // Full steering in the turn direction
            float steer = Math.Sign(remainingAngle);
            float turnProgress = Math.Abs(_turnAccumulatedAngle / targetAngle);

            // Reduce steering near the end of the turn for smooth completion
            if (turnProgress > 0.7f)
                steer *= (1f - turnProgress) / 0.3f;

            return steer;
        }

        public void Reset()
        {
            _pendingTurn = TurnDirection.Straight;
            _isTurning = false;
            _turnAccumulatedAngle = 0f;
        }
    }
}
