using System;
using GTA;
using GTA.Math;
using GTA.Native;

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
        private int _stuckCounter;

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

            // Apply steering via native (confirmed working in SHVDN Enhanced v3)
            Function.Call(Hash.SET_VEHICLE_STEER_BIAS, vehicle, _currentSteer);

            // Handle reverse
            if (command.Reverse)
            {
                _isReversing = true;
                float revSpeed = -command.Throttle * 5f;
                Function.Call(Hash.SET_VEHICLE_FORWARD_SPEED, vehicle, revSpeed);
                return;
            }
            _isReversing = false;

            // Compute target forward speed (always use native for reliability)
            float targetForwardSpeed;
            if (command.Brake > 0.1f)
            {
                // Braking: reduce speed toward zero
                float currentSpeed = vehicle.Speed;
                float brakeForce = command.Brake;
                if (command.Handbrake || command.Brake > 0.8f)
                    brakeForce = 1f;

                float decelAmount = brakeForce * 15f * 0.016f;
                float newSpeed = System.Math.Max(0f, currentSpeed - decelAmount);

                // If braking to stop, use sharper deceleration
                if (command.TargetSpeed <= 0f)
                    newSpeed = System.Math.Max(0f, currentSpeed * (1f - brakeForce));

                targetForwardSpeed = newSpeed;
                _wasBraking = true;
            }
            else if (_wasBraking && vehicle.Speed < 0.5f)
            {
                // Transition from stop: use accelerate temp action to kick-start
                if (_driver != null && _driver.Exists())
                {
                    Function.Call(Hash.TASK_VEHICLE_TEMP_ACTION, _driver, vehicle, 27, 200); // 27 = accelerate
                }
                targetForwardSpeed = command.TargetSpeed * _currentThrottle;
                _wasBraking = false;
                _stuckCounter = 0;
            }
            else
            {
                _wasBraking = false;
                targetForwardSpeed = command.TargetSpeed * _currentThrottle;
            }

            // Stuck detection: if trying to move but speed stays near zero
            if (targetForwardSpeed > 1f && vehicle.Speed < 0.3f && !command.Reverse)
            {
                _stuckCounter++;
                if (_stuckCounter > 60) // ~1 second at 60fps
                {
                    // Kick-start with temp action
                    if (_driver != null && _driver.Exists())
                    {
                        Function.Call(Hash.TASK_VEHICLE_TEMP_ACTION, _driver, vehicle, 27, 400); // accelerate
                    }
                    _stuckCounter = 0;
                }
            }
            else
            {
                _stuckCounter = 0;
            }

            Function.Call(Hash.SET_VEHICLE_FORWARD_SPEED, vehicle, targetForwardSpeed);
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
            return a + (b - a) * System.Math.Min(t, 1f);
        }
    }
}
