using System;
using GTA;
using GTA.Math;

namespace GTA5AutoPilot.Modules
{
    /// <summary>
    /// Low-level vehicle control. Applies smooth steering, throttle, and brake
    /// using low-pass filtering to avoid jerky movements.
    /// </summary>
    public class VehicleController
    {
        private float _currentSteer;
        private float _currentThrottle;
        private float _currentBrake;
        private bool _wasBraking;
        private Ped _driver;
        private bool _isReversing;

        public void Execute(Vehicle vehicle, DrivingCommand command)
        {
            if (vehicle == null || !vehicle.Exists())
                return;

            EnsureDriver(vehicle);

            // Smooth control transitions
            float steerSmooth = Configuration.SteerSmoothing;
            float throttleSmooth = Configuration.ThrottleSmoothing;
            float brakeSmooth = Configuration.BrakeSmoothing;

            // Faster response for emergency braking
            if (command.Brake > 0.8f || command.Handbrake)
            {
                brakeSmooth = 0.6f;
                throttleSmooth = 0.6f;
            }

            _currentSteer = Lerp(_currentSteer, command.Steer, steerSmooth);
            _currentThrottle = Lerp(_currentThrottle, command.Throttle, throttleSmooth);
            _currentBrake = Lerp(_currentBrake, command.Brake, brakeSmooth);

            // Apply steering
            vehicle.SteeringAngle = _currentSteer * 45f; // Convert normalized to degrees

            // Handle reverse
            if (command.Reverse && vehicle.Speed < 1f)
            {
                if (!_isReversing)
                {
                    _isReversing = true;
                }
                vehicle.Speed = -command.Throttle * 5f; // Reverse at low speed
                return;
            }
            else if (command.Reverse)
            {
                // Still decelerating, brake instead
                vehicle.Speed = 0f;
            }
            else
            {
                _isReversing = false;
            }

            // Braking: use native speed set to zero for strong braking
            if (command.Brake > 0.1f)
            {
                float brakeSpeed = vehicle.Speed * (1f - command.Brake);
                // For emergency braking, cut speed more aggressively
                if (command.Handbrake || command.Brake > 0.8f)
                {
                    brakeSpeed = Math.Max(0f, vehicle.Speed - command.Brake * 15f * Time.DeltaTime);
                }
                vehicle.Speed = Math.Max(0f, brakeSpeed);
                _wasBraking = true;
            }
            else if (_wasBraking)
            {
                // Transition from braking to accelerating
                vehicle.Speed = command.TargetSpeed * command.Throttle;
                _wasBraking = false;
            }
            else
            {
                // Normal cruise: set forward speed
                float targetSpd = command.TargetSpeed * command.Throttle;
                vehicle.Speed = targetSpd;
            }
        }

        /// <summary>
        /// Release control when autopilot is disengaged.
        /// </summary>
        public void Release()
        {
            _currentSteer = 0f;
            _currentThrottle = 0f;
            _currentBrake = 0f;
            _wasBraking = false;
            _isReversing = false;
            _driver = null;
        }

        private void EnsureDriver(Vehicle vehicle)
        {
            if (_driver != null && _driver.Exists() && _driver.IsInVehicle(vehicle))
                return;

            var player = Game.Player;
            if (player.Character.IsInVehicle(vehicle))
            {
                _driver = player.Character;
            }
        }

        private static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * Math.Min(t, 1f);
        }
    }
}
