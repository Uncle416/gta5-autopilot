using System.Collections.Generic;
using GTA;
using GTA.Math;

namespace GTA5AutoPilot.Modules
{
    /// <summary>
    /// Evaluates collision risk using Time-To-Collision (TTC) metrics
    /// and proximity checks for nearby entities.
    /// </summary>
    public class CollisionPredictor
    {
        /// <summary>
        /// Returns the highest risk level among all nearby entities.
        /// </summary>
        public CollisionRiskLevel EvaluateRisk(Vehicle egoVehicle, List<EntityInfo> entities)
        {
            if (egoVehicle == null || !egoVehicle.Exists())
                return CollisionRiskLevel.None;

            var highestRisk = CollisionRiskLevel.None;
            Vector3 egoPos = egoVehicle.Position;
            Vector3 egoForward = egoVehicle.ForwardVector;

            foreach (var entity in entities)
            {
                var risk = EvaluateSingleEntity(egoVehicle, entity, egoPos, egoForward);
                if (risk > highestRisk)
                    highestRisk = risk;

                if (highestRisk == CollisionRiskLevel.Imminent)
                    break; // Can't get worse
            }

            return highestRisk;
        }

        private CollisionRiskLevel EvaluateSingleEntity(Vehicle ego, EntityInfo entity,
            Vector3 egoPos, Vector3 egoForward)
        {
            // Immediate proximity check: very close = imminent danger
            if (entity.Distance < Configuration.MinBrakingDistance && entity.IsInForwardCone)
                return CollisionRiskLevel.Imminent;

            // Pedestrians: conservative
            if (entity.IsPedestrian && entity.Distance < 5f && entity.IsInForwardCone)
                return CollisionRiskLevel.Imminent;

            if (entity.IsPedestrian && entity.Distance < 10f && entity.IsInForwardCone)
                return CollisionRiskLevel.High;

            // TTC-based assessment
            float ttc = entity.TimeToCollision;

            if (ttc < Configuration.TTCEmergencyThreshold && entity.IsInForwardCone)
                return CollisionRiskLevel.Imminent;

            if (ttc < Configuration.TTCWarningThreshold && entity.IsInForwardCone)
                return CollisionRiskLevel.High;

            if (ttc < Configuration.TTCWarningThreshold * 2f && entity.IsInForwardCone)
                return CollisionRiskLevel.Medium;

            // Oncoming traffic in wrong lane: medium risk
            if (entity.IsOncoming && entity.Distance < 20f && entity.IsInForwardCone)
                return CollisionRiskLevel.Medium;

            // Nearby entity in forward cone but far away
            if (entity.IsInForwardCone && entity.Distance < 15f)
                return CollisionRiskLevel.Low;

            return CollisionRiskLevel.None;
        }

        /// <summary>
        /// Check if a lane change to the right would be safe.
        /// </summary>
        public bool IsLaneChangeSafeRight(Vehicle ego, List<EntityInfo> entities, Vector3 egoRight)
        {
            return IsLaneChangeSafe(ego, entities, egoRight);
        }

        /// <summary>
        /// Check if a lane change to the left would be safe.
        /// </summary>
        public bool IsLaneChangeSafeLeft(Vehicle ego, List<EntityInfo> entities, Vector3 egoLeft)
        {
            return IsLaneChangeSafe(ego, entities, egoLeft);
        }

        private bool IsLaneChangeSafe(Vehicle ego, List<EntityInfo> entities, Vector3 direction)
        {
            if (ego.Speed < 1f) return true; // Safe at very low speeds

            Vector3 egoPos = ego.Position;
            foreach (var entity in entities)
            {
                if (!entity.IsVehicle) continue;

                Vector3 toEntity = entity.Position - egoPos;
                toEntity.Normalize();
                float dot = Vector3.Dot(direction, toEntity);

                // Entity is in the direction we want to change to
                if (dot > 0.7f && entity.Distance < 10f)
                    return false;
            }
            return true;
        }
    }
}
