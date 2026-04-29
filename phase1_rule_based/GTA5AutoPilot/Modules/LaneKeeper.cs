using System;
using GTA;
using GTA.Math;

namespace GTA5AutoPilot.Modules
{
    /// <summary>
    /// PID-based lane keeping. Computes a steering correction to keep the
    /// vehicle centered in its lane using road node heading as reference.
    /// </summary>
    public class LaneKeeper
    {
        private float _previousError;
        private float _previousTime;

        public float GetSteerCorrection(Vehicle vehicle, PathInfo pathInfo)
        {
            if (vehicle == null || !vehicle.Exists())
                return 0f;

            float vehicleHeading = vehicle.Heading;
            float roadHeading = pathInfo.RoadHeading;

            // Compute heading error in degrees, normalize to [-180, 180]
            float error = roadHeading - vehicleHeading;
            while (error > 180f) error -= 360f;
            while (error < -180f) error += 360f;

            // Normalize to [-1, 1] for steering
            float normalizedError = Math.Max(-1f, Math.Min(1f, error / 180f));

            // PID computation
            float currentTime = (float)Time.CurrentTime;
            float dt = currentTime - _previousTime;
            if (dt <= 0f || dt > 0.1f) dt = 0.016f; // Assume ~60 FPS for first frame

            float derivative = (normalizedError - _previousError) / dt;

            float correction = Configuration.LaneKeepPGain * normalizedError
                             + Configuration.LaneKeepDGain * derivative;

            // Clamp
            correction = Math.Max(-Configuration.MaxLaneSteerCorrection,
                         Math.Min(Configuration.MaxLaneSteerCorrection, correction));

            _previousError = normalizedError;
            _previousTime = currentTime;

            return correction;
        }

        public void Reset()
        {
            _previousError = 0f;
            _previousTime = 0f;
        }
    }
}
