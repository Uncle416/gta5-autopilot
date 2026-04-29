using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;

namespace GTA5AutoPilot.Modules
{
    /// <summary>
    /// Detects nearby vehicles and pedestrians around the ego vehicle.
    /// Returns a list of EntityInfo with position/speed/distance data.
    /// </summary>
    public class EntityDetector
    {
        public List<EntityInfo> ScanSurroundings(Vehicle egoVehicle)
        {
            var entities = new List<EntityInfo>();
            if (egoVehicle == null || !egoVehicle.Exists())
                return entities;

            Vector3 egoPos = egoVehicle.Position;
            Vector3 egoForward = egoVehicle.ForwardVector;
            float egoSpeed = egoVehicle.Speed;
            float egoHeading = egoVehicle.Heading;

            // Scan nearby vehicles (far radius)
            Vehicle[] nearVehicles = World.GetNearbyVehicles(egoPos, Configuration.DetectionRadiusFar);
            foreach (var veh in nearVehicles)
            {
                if (veh == null || !veh.Exists() || veh == egoVehicle)
                    continue;

                Vector3 vehPos = veh.Position;
                float dist = Vector3.Distance(egoPos, vehPos);

                var info = CreateEntityInfo(veh, vehPos, dist, egoPos, egoForward, egoSpeed, egoHeading);
                info.IsVehicle = true;
                entities.Add(info);
            }

            // Scan nearby pedestrians (smaller radius)
            Ped[] nearPeds = World.GetNearbyPeds(egoPos, Configuration.PedestrianDetectionRadius);
            foreach (var ped in nearPeds)
            {
                if (ped == null || !ped.Exists() || ped == Game.Player.Character)
                    continue;
                if (ped.IsInVehicle())
                    continue; // Already covered by vehicle scan

                Vector3 pedPos = ped.Position;
                float dist = Vector3.Distance(egoPos, pedPos);

                var info = CreateEntityInfo(ped, pedPos, dist, egoPos, egoForward, egoSpeed, egoHeading);
                info.IsPedestrian = true;
                entities.Add(info);
            }

            // Sort by distance (closest first)
            entities.Sort((a, b) => a.Distance.CompareTo(b.Distance));

            return entities;
        }

        private EntityInfo CreateEntityInfo(Entity entity, Vector3 entityPos, float dist,
            Vector3 egoPos, Vector3 egoForward, float egoSpeed, float egoHeading)
        {
            Vector3 entityVel = entity.Velocity;
            float entitySpeed = entity.Speed;

            // Check if entity is in forward cone
            Vector3 toEntity = (entityPos - egoPos);
            toEntity.Normalize();
            float dotForward = Vector3.Dot(egoForward, toEntity);
            bool inForwardCone = dotForward > Math.Cos(Configuration.ForwardConeAngle * Math.PI / 180f);

            // Check if oncoming (opposite direction)
            bool isOncoming = false;
            if (entitySpeed > 2f && egoSpeed > 2f)
            {
                float entityHeading = entity.Heading;
                float headingDiff = Math.Abs(entityHeading - egoHeading);
                if (headingDiff > 180f) headingDiff = 360f - headingDiff;
                if (headingDiff > 90f && headingDiff < 270f)
                    isOncoming = true;
            }

            // Compute Time-To-Collision
            float ttc = float.MaxValue;
            if (inForwardCone || dist < 10f)
            {
                float closingSpeed = egoSpeed + (isOncoming ? entitySpeed : -entitySpeed);
                closingSpeed = Math.Max(0.1f, closingSpeed);
                ttc = dist / closingSpeed;
            }

            return new EntityInfo
            {
                Entity = entity,
                Position = entityPos,
                Velocity = entityVel,
                Speed = entitySpeed,
                Distance = dist,
                Heading = entity.Heading,
                IsVehicle = false,
                IsPedestrian = false,
                TimeToCollision = ttc,
                IsInForwardCone = inForwardCone,
                IsOncoming = isOncoming
            };
        }
    }
}
