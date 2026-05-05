using GTA;
using GTA.Math;
using GTA.Native;

namespace GTA5AutoPilot.NativeWrappers
{
    /// <summary>
    /// Utility methods for traffic light detection using GTA V natives.
    /// </summary>
    public static class TrafficLightUtils
    {
        /// <summary>
        /// Disable all game-managed traffic lights so the mod has full control.
        /// </summary>
        public static void DisableAllTrafficLights()
        {
            // Not directly supported in this SHVDN version; traffic lights
            // are detected via nearby prop scanning instead.
        }

        public static void SetTrafficLightState(Vector3 position, int state)
        {
            // Not directly supported in this SHVDN version.
        }

        /// <summary>
        /// Check if a specific model hash is a traffic light.
        /// </summary>
        public static bool IsTrafficLightModel(int modelHash)
        {
            // Comprehensive list of GTA V traffic light models
            int[] models = {
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

            foreach (int hash in models)
                if (hash == modelHash) return true;

            return false;
        }

        /// <summary>
        /// Check if a bone exists on the given entity and is active (scale > 0).
        /// </summary>
        public static bool IsBoneActive(Entity entity, string boneName)
        {
            int boneIndex = Function.Call<int>(Hash.GET_ENTITY_BONE_INDEX_BY_NAME, entity, boneName);
            if (boneIndex == -1)
                return false;

            Vector3 bonePos = Function.Call<Vector3>(Hash.GET_WORLD_POSITION_OF_ENTITY_BONE, entity, boneIndex);
            Vector3 entityPos = entity.Position;

            // If bone position differs significantly from entity origin, it's active
            return Vector3.Distance(bonePos, entityPos) > 0.02f;
        }
    }
}
