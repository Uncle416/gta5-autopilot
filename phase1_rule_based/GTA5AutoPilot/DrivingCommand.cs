namespace GTA5AutoPilot
{
    /// <summary>
    /// Output struct from DecisionEngine to VehicleController.
    /// All values in normalized range.
    /// </summary>
    public struct DrivingCommand
    {
        /// <summary>Steering input: -1.0 (full left) to 1.0 (full right)</summary>
        public float Steer;

        /// <summary>Throttle input: 0.0 (none) to 1.0 (full)</summary>
        public float Throttle;

        /// <summary>Brake input: 0.0 (none) to 1.0 (full)</summary>
        public float Brake;

        /// <summary>Whether to engage handbrake</summary>
        public bool Handbrake;

        /// <summary>Whether to drive in reverse</summary>
        public bool Reverse;

        /// <summary>Target speed in m/s (informational, for debug overlay)</summary>
        public float TargetSpeed;

        public static DrivingCommand Cruise(float throttle, float steer, float targetSpeed) =>
            new DrivingCommand
            {
                Steer = steer,
                Throttle = throttle,
                Brake = 0f,
                TargetSpeed = targetSpeed
            };

        public static DrivingCommand Stop() =>
            new DrivingCommand
            {
                Steer = 0f,
                Throttle = 0f,
                Brake = 1f,
                TargetSpeed = 0f
            };

        public static DrivingCommand EmergencyBrake(float steer = 0f) =>
            new DrivingCommand
            {
                Steer = steer,
                Throttle = 0f,
                Brake = 1f,
                Handbrake = true,
                TargetSpeed = 0f
            };

        public static DrivingCommand ReverseGear(float steer) =>
            new DrivingCommand
            {
                Steer = steer,
                Throttle = 0.3f,
                Brake = 0f,
                Reverse = true,
                TargetSpeed = -3f
            };

        public override string ToString() =>
            $"Steer:{Steer:F2} Throttle:{Throttle:F2} Brake:{Brake:F2} " +
            $"Target:{TargetSpeed:F1}m/s Rev:{Reverse} HB:{Handbrake}";
    }
}
