using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;

namespace GTA5AutoPilot.Modules
{
    /// <summary>
    /// Detects traffic light state by scanning for traffic light prop objects
    /// near the vehicle's path and analyzing their bone positions.
    ///
    /// This is the most fragile module. GTA V has no reliable native API
    /// for reading traffic light state. Fallback: treat intersections as
    /// stop signs when detection fails.
    /// </summary>
    public class TrafficLightDetector
    {
        // Common GTA V traffic light prop model hashes
        private static readonly int[] TrafficLightModels =
        {
            Game.GenerateHash("prop_traffic_01a"),
            Game.GenerateHash("prop_traffic_01b"),
            Game.GenerateHash("prop_traffic_01d"),
            Game.GenerateHash("prop_traffic_02a"),
            Game.GenerateHash("prop_traffic_02b"),
            Game.GenerateHash("prop_traffic_03a"),
            Game.GenerateHash("prop_trafficlights_01"),
            Game.GenerateHash("prop_trafficlights_02"),
            Game.GenerateHash("prop_trafficlights_03"),
            Game.GenerateHash("prop_trafficlights_04"),
            Game.GenerateHash("prop_trafficlights_05"),
        };

        // Bone names that indicate light state
        private static readonly string[] RedBoneNames = { "bulb_red", "light_red", "red" };
        private static readonly string[] GreenBoneNames = { "bulb_green", "light_green", "green" };
        private static readonly string[] AmberBoneNames = { "bulb_amber", "light_amber", "amber", "bulb_yellow", "yellow" };

        private TrafficLightState _lastDetectedState = TrafficLightState.None;
        private float _stateChangeTime;
        private int _consecutiveDetectionFailures;

        public TrafficLightState GetCurrentState(Vehicle vehicle, PathInfo pathInfo)
        {
            if (vehicle == null || !vehicle.Exists())
                return TrafficLightState.None;

            Vector3 vehiclePos = vehicle.Position;
            Vector3 vehicleForward = vehicle.ForwardVector;

            // Only check when near an intersection or moving slowly
            bool shouldCheck = pathInfo.IsIntersectionAhead ||
                               pathInfo.IsAtIntersection ||
                               vehicle.Speed < 5f;

            if (!shouldCheck)
                return _lastDetectedState;

            // Scan for traffic light props ahead of the vehicle
            Prop[] nearbyProps = World.GetNearbyProps(
                vehiclePos + vehicleForward * Configuration.TrafficLightDetectionRange * 0.5f,
                Configuration.TrafficLightScanRadius);

            Prop closestLight = null;
            float closestDist = float.MaxValue;

            foreach (var prop in nearbyProps)
            {
                if (prop == null || !prop.Exists())
                    continue;

                // Check if this prop is a traffic light model
                if (!IsTrafficLightModel(prop.Model.Hash))
                    continue;

                float dist = Vector3.Distance(vehiclePos, prop.Position);

                // Only consider lights ahead of vehicle
                Vector3 toProp = prop.Position - vehiclePos;
                toProp.Normalize();
                float dot = Vector3.Dot(vehicleForward, toProp);

                if (dot < 0.5f || dist > Configuration.TrafficLightDetectionRange)
                    continue;

                if (dist < closestDist)
                {
                    closestDist = dist;
                    closestLight = prop;
                }
            }

            if (closestLight == null)
            {
                _consecutiveDetectionFailures++;
                // Keep last state for a short time (debounce)
                if (_consecutiveDetectionFailures < 30) // ~0.5 seconds at 60 FPS
                    return _lastDetectedState;
                _lastDetectedState = TrafficLightState.None;
                return TrafficLightState.None;
            }

            _consecutiveDetectionFailures = 0;
            TrafficLightState detected = AnalyzeLightBones(closestLight);
            _lastDetectedState = detected;
            return detected;
        }

        private bool IsTrafficLightModel(int modelHash)
        {
            return NativeWrappers.TrafficLightUtils.IsTrafficLightModel(modelHash);
        }

        private TrafficLightState AnalyzeLightBones(Prop lightProp)
        {
            // Method A: Check bone positions
            // Traffic lights in GTA V have bones for each bulb.
            // The "lit" bulb bone is typically offset/active based on state.

            bool redLit = false;
            bool greenLit = false;
            bool amberLit = false;

            foreach (string boneName in RedBoneNames)
            {
                int boneIndex = Function.Call<int>(Hash.GET_ENTITY_BONE_INDEX_BY_NAME, lightProp,boneName);
                if (boneIndex != -1)
                {
                    Vector3 bonePos = Function.Call<GTA.Math.Vector3>(Hash.GET_WORLD_POSITION_OF_ENTITY_BONE, lightProp,boneIndex);
                    // Check if bone is "active" (lit) by comparing to parent position
                    Vector3 propPos = lightProp.Position;
                    float boneOffset = Vector3.Distance(bonePos, propPos);
                    if (boneOffset > 0.05f)
                        redLit = true;
                    break;
                }
            }

            foreach (string boneName in GreenBoneNames)
            {
                int boneIndex = Function.Call<int>(Hash.GET_ENTITY_BONE_INDEX_BY_NAME, lightProp,boneName);
                if (boneIndex != -1)
                {
                    Vector3 bonePos = Function.Call<GTA.Math.Vector3>(Hash.GET_WORLD_POSITION_OF_ENTITY_BONE, lightProp,boneIndex);
                    Vector3 propPos = lightProp.Position;
                    if (Vector3.Distance(bonePos, propPos) > 0.05f)
                        greenLit = true;
                    break;
                }
            }

            foreach (string boneName in AmberBoneNames)
            {
                int boneIndex = Function.Call<int>(Hash.GET_ENTITY_BONE_INDEX_BY_NAME, lightProp,boneName);
                if (boneIndex != -1)
                {
                    Vector3 bonePos = Function.Call<GTA.Math.Vector3>(Hash.GET_WORLD_POSITION_OF_ENTITY_BONE, lightProp,boneIndex);
                    Vector3 propPos = lightProp.Position;
                    if (Vector3.Distance(bonePos, propPos) > 0.05f)
                        amberLit = true;
                    break;
                }
            }

            // Method B (fallback): Check entity alpha / render state
            // Some traffic light models toggle visibility of bulb components
            if (!redLit && !greenLit && !amberLit)
            {
                // Try checking if the prop is "active" (some lights use different sub-models)
                // This is heuristic and needs per-intersection calibration
                TrafficLightState fallback = DetectByColorSampling(lightProp);

                // Don't report "None" at intersections — conservatively assume red
                if (fallback == TrafficLightState.None)
                    return TrafficLightState.Red;

                return fallback;
            }

            if (redLit) return TrafficLightState.Red;
            if (amberLit) return TrafficLightState.Yellow;
            if (greenLit) return TrafficLightState.Green;

            // Default conservative: treat as red when uncertain
            return TrafficLightState.Red;
        }

        /// <summary>
        /// Fallback detection: try to detect light state by checking
        /// which sub-component of the traffic light prop is visible/active.
        /// Returns None when uncertain (caller falls back to treating as stop sign).
        /// </summary>
        private TrafficLightState DetectByColorSampling(Prop lightProp)
        {
            // Check all bulb colors and use bone offset as a heuristic
            bool redExists = false, greenExists = false;
            float redDist = 0f, greenDist = 0f;

            foreach (string name in RedBoneNames)
            {
                int idx = Function.Call<int>(Hash.GET_ENTITY_BONE_INDEX_BY_NAME, lightProp,name);
                if (idx != -1)
                {
                    redExists = true;
                    redDist = Vector3.Distance(Function.Call<GTA.Math.Vector3>(Hash.GET_WORLD_POSITION_OF_ENTITY_BONE, lightProp,idx), lightProp.Position);
                    break;
                }
            }

            foreach (string name in GreenBoneNames)
            {
                int idx = Function.Call<int>(Hash.GET_ENTITY_BONE_INDEX_BY_NAME, lightProp,name);
                if (idx != -1)
                {
                    greenExists = true;
                    greenDist = Vector3.Distance(Function.Call<GTA.Math.Vector3>(Hash.GET_WORLD_POSITION_OF_ENTITY_BONE, lightProp,idx), lightProp.Position);
                    break;
                }
            }

            // Both colors exist: compare distances to guess which is active
            if (redExists && greenExists)
            {
                if (redDist > greenDist * 1.1f) return TrafficLightState.Red;
                if (greenDist > redDist * 1.1f) return TrafficLightState.Green;
                return TrafficLightState.Red; // Default conservative
            }

            return TrafficLightState.None;
        }
    }
}
