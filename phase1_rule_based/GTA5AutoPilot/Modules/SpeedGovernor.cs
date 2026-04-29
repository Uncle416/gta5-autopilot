using System.Collections.Generic;
using GTA;
using GTA.Math;

namespace GTA5AutoPilot.Modules
{
    /// <summary>
    /// Determines target speed based on road type, traffic conditions,
    /// and proximity to intersections.
    /// </summary>
    public class SpeedGovernor
    {
        private float _currentTargetSpeed;
        private float _adaptiveCruiseTarget;

        public float GetTargetSpeed(Vehicle egoVehicle, PathInfo pathInfo, List<EntityInfo> entities)
        {
            if (egoVehicle == null || !egoVehicle.Exists())
                return 0f;

            // 1. Base speed from road type
            float baseSpeed;
            switch (pathInfo.RoadType)
            {
                case 1: // Highway
                    baseSpeed = Configuration.HighwaySpeedLimit;
                    break;
                case 2: // Alley
                case 3: // Gravel
                    baseSpeed = Configuration.UrbanSpeedLimit * 0.5f;
                    break;
                default: // Urban
                    baseSpeed = Configuration.UrbanSpeedLimit;
                    break;
            }

            // 2. Slow down near intersections
            float intersectionFactor = 1f;
            if (pathInfo.IsIntersectionAhead && pathInfo.DistanceToIntersection < Configuration.IntersectionSlowDistance)
            {
                intersectionFactor = Math.Max(0.2f,
                    pathInfo.DistanceToIntersection / Configuration.IntersectionSlowDistance);
            }

            if (pathInfo.IsAtIntersection)
            {
                baseSpeed = Configuration.IntersectionSpeedLimit;
                intersectionFactor = 1f;
            }

            // 3. Adaptive cruise: match speed of vehicle ahead
            float adaptiveSpeed = baseSpeed;
            EntityInfo leadVehicle = FindLeadVehicle(egoVehicle, entities);
            if (leadVehicle != null)
            {
                float safeDistance = egoVehicle.Speed * Configuration.SafeFollowTimeGap
                                     + Configuration.MinFollowDistance;

                if (leadVehicle.Distance < safeDistance)
                {
                    // Need to slow down
                    float ratio = leadVehicle.Distance / safeDistance;
                    adaptiveSpeed = Math.Max(leadVehicle.Speed, egoVehicle.Speed * ratio);
                }
                else if (leadVehicle.Distance < safeDistance * 1.5f)
                {
                    // Match lead vehicle speed
                    adaptiveSpeed = leadVehicle.Speed;
                }
            }

            // 4. Smooth transition
            float targetSpeed = Math.Min(adaptiveSpeed, baseSpeed) * intersectionFactor;
            targetSpeed = Math.Max(2f, targetSpeed); // Don't go below 2 m/s unless stopping

            // Smooth changes
            _currentTargetSpeed = Lerp(_currentTargetSpeed, targetSpeed, 0.1f);

            return _currentTargetSpeed;
        }

        /// <summary>
        /// Find the closest vehicle directly ahead in the same lane.
        /// </summary>
        private EntityInfo FindLeadVehicle(Vehicle ego, List<EntityInfo> entities)
        {
            EntityInfo closest = null;
            float closestDist = float.MaxValue;
            Vector3 egoForward = ego.ForwardVector;
            Vector3 egoPos = ego.Position;

            foreach (var entity in entities)
            {
                if (!entity.IsVehicle || !entity.IsInForwardCone)
                    continue;

                // Check it's actually ahead (not beside)
                Vector3 toEntity = entity.Position - egoPos;
                toEntity.Normalize();
                float dot = Vector3.Dot(egoForward, toEntity);

                if (dot > 0.9f && entity.Distance < closestDist && entity.Distance < 50f)
                {
                    closestDist = entity.Distance;
                    closest = entity;
                }
            }

            return closest;
        }

        public void Reset()
        {
            _currentTargetSpeed = 0f;
            _adaptiveCruiseTarget = 0f;
        }

        private static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * t;
        }
    }
}
