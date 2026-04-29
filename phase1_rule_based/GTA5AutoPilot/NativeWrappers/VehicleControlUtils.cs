using GTA;

namespace GTA5AutoPilot.NativeWrappers
{
    /// <summary>
    /// Utility methods wrapping GTA V vehicle control natives.
    /// </summary>
    public static class VehicleControlUtils
    {
        // Temporary action IDs for TASK_VEHICLE_TEMP_ACTION
        public const int TempActionHandbrake = 1;
        public const int TempActionHandbrakeStraight = 3;
        public const int TempActionHandbrakeTurnLeft = 6;
        public const int TempActionBrake = 18;
        public const int TempActionBrakeStrong = 23;
        public const int TempActionBrakeMax = 24;
        public const int TempActionAccelerate = 27;
        public const int TempActionAccelerateStrong = 28;
        public const int TempActionReverse = 28;
        public const int TempActionForward = 9;

        /// <summary>
        /// Set vehicle forward speed in m/s. Use negative for reverse.
        /// </summary>
        public static void SetForwardSpeed(Vehicle vehicle, float speed)
        {
            Function.Call(Hash.SET_VEHICLE_FORWARD_SPEED, vehicle, speed);
        }

        /// <summary>
        /// Apply a persistent steering bias. Range: -1.0 (full left) to 1.0 (full right).
        /// </summary>
        public static void SetSteerBias(Vehicle vehicle, float bias)
        {
            Function.Call(Hash.SET_VEHICLE_STEER_BIAS, vehicle, bias);
        }

        /// <summary>
        /// Apply a temporary driving action to the driver ped.
        /// Useful for emergency maneuvers.
        /// </summary>
        /// <param name="driver">The ped controlling the vehicle</param>
        /// <param name="vehicle">The vehicle</param>
        /// <param name="action">Action ID (use TempAction* constants)</param>
        /// <param name="durationMs">Duration in milliseconds</param>
        public static void ApplyTempAction(Ped driver, Vehicle vehicle, int action, int durationMs)
        {
            Function.Call(Hash.TASK_VEHICLE_TEMP_ACTION, driver, vehicle, action, durationMs);
        }

        /// <summary>
        /// Perform an emergency brake (max brake temp action).
        /// </summary>
        public static void EmergencyBrake(Ped driver, Vehicle vehicle)
        {
            ApplyTempAction(driver, vehicle, TempActionBrakeMax, 100);
        }

        /// <summary>
        /// Perform hard acceleration.
        /// </summary>
        public static void AccelerateHard(Ped driver, Vehicle vehicle)
        {
            ApplyTempAction(driver, vehicle, TempActionAccelerateStrong, 100);
        }

        /// <summary>
        /// Check if the vehicle has a driver (ped in the driver seat).
        /// </summary>
        public static bool HasDriver(Vehicle vehicle)
        {
            return Function.Call<bool>(Hash.IS_VEHICLE_SEAT_FREE, vehicle, (int)VehicleSeat.Driver, false) == false;
        }

        /// <summary>
        /// Get the driver ped of the vehicle, or null if no driver.
        /// </summary>
        public static Ped GetDriver(Vehicle vehicle)
        {
            if (!HasDriver(vehicle))
                return null;

            return new Ped(Function.Call<int>(Hash.GET_PED_IN_VEHICLE_SEAT,
                vehicle, (int)VehicleSeat.Driver));
        }

        /// <summary>
        /// Check if a vehicle is stopped (speed near zero).
        /// </summary>
        public static bool IsStopped(Vehicle vehicle)
        {
            return vehicle.Speed < 0.1f;
        }

        /// <summary>
        /// Get the speed of a vehicle in km/h.
        /// </summary>
        public static float GetSpeedKmh(Vehicle vehicle)
        {
            return vehicle.Speed * 3.6f;
        }

        /// <summary>
        /// Toggle vehicle brake lights (visual only, doesn't affect physics).
        /// </summary>
        public static void SetBrakeLights(Vehicle vehicle, bool on)
        {
            Function.Call(Hash.SET_VEHICLE_BRAKE_LIGHTS, vehicle, on);
        }
    }
}
