using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;

namespace GTA5AutoPilot.Modules
{
    /// <summary>
    /// Finite State Machine that synthesizes all sensor inputs and produces
    /// driving commands. The brain of the autopilot system.
    ///
    /// States:
    ///   CRUISING → STOPPING_AT_LIGHT → WAITING_AT_LIGHT → CRUISING
    ///   CRUISING → EVADING → CRUISING
    ///   CRUISING → TURNING → CRUISING
    ///   CRUISING → EMERGENCY_STOP
    ///   Any → STUCK
    /// </summary>
    public class DecisionEngine
    {
        public DecisionState CurrentState { get; private set; } = DecisionState.Cruising;

        private float _stuckTimer;
        private Vector3 _stuckPosition;
        private float _waitStartTime;

        // PID for speed control
        private float _speedIntegral;

        public DrivingCommand Evaluate(SensorData data)
        {
            CheckStuck(data);

            switch (CurrentState)
            {
                case DecisionState.Cruising:
                    return HandleCruising(data);
                case DecisionState.StoppingAtLight:
                    return HandleStoppingAtLight(data);
                case DecisionState.WaitingAtLight:
                    return HandleWaitingAtLight(data);
                case DecisionState.Turning:
                    return HandleTurning(data);
                case DecisionState.Evading:
                    return HandleEvading(data);
                case DecisionState.Stuck:
                    return HandleStuck(data);
                case DecisionState.EmergencyStop:
                    return HandleEmergencyStop(data);
                default:
                    return DrivingCommand.Stop();
            }
        }

        // ==================== CRUISING ====================

        private DrivingCommand HandleCruising(SensorData data)
        {
            // --- Priority Check: Emergency ---
            if (data.CollisionRisk == CollisionRiskLevel.Imminent)
            {
                TransitionTo(DecisionState.EmergencyStop);
                return DrivingCommand.EmergencyBrake();
            }

            if (data.CollisionRisk == CollisionRiskLevel.High)
            {
                // Try evasive action
                if (TryEvade(data))
                {
                    TransitionTo(DecisionState.Evading);
                    return HandleEvading(data);
                }
                // Can't evade: emergency brake
                return DrivingCommand.EmergencyBrake();
            }

            // --- Intersection / Traffic Light ---
            if (data.TrafficLightState == TrafficLightState.Red ||
                data.TrafficLightState == TrafficLightState.Yellow)
            {
                TransitionTo(DecisionState.StoppingAtLight);
                return HandleStoppingAtLight(data);
            }

            // --- Turning ---
            if (data.IntersectionInfo.IsAtIntersection && data.IntersectionInfo.TurnRequired)
            {
                if (data.IntersectionInfo.ShouldYield && HasCrossTraffic(data))
                    return DrivingCommand.Stop();

                TransitionTo(DecisionState.Turning);
                return HandleTurning(data);
            }

            // --- Normal Cruise ---
            return ComputeCruiseCommand(data);
        }

        // ==================== TRAFFIC LIGHT ====================

        private DrivingCommand HandleStoppingAtLight(SensorData data)
        {
            // Light turned green? Resume cruising
            if (data.TrafficLightState == TrafficLightState.Green ||
                data.TrafficLightState == TrafficLightState.None)
            {
                TransitionTo(DecisionState.Cruising);
                return ComputeCruiseCommand(data);
            }

            // Still red/yellow: brake to stop at appropriate distance
            float distToStop = Configuration.TrafficLightStopDistance;
            float currentSpeed = data.Vehicle.Speed;

            if (currentSpeed < 0.5f)
            {
                TransitionTo(DecisionState.WaitingAtLight);
                _waitStartTime = (float)Game.GameTime / 1000f;
                return DrivingCommand.Stop();
            }

            // Compute deceleration
            float stopDistance = (currentSpeed * currentSpeed) / (2f * 4f); // decel ~4 m/s²
            if (stopDistance > distToStop * 2f)
            {
                // Still far, continue slowing gradually
                return new DrivingCommand
                {
                    Steer = ComputeSteer(data),
                    Throttle = 0.3f,
                    Brake = 0.2f,
                    TargetSpeed = currentSpeed * 0.85f
                };
            }
            else if (stopDistance > distToStop)
            {
                // Moderate braking
                return new DrivingCommand
                {
                    Steer = ComputeSteer(data),
                    Throttle = 0f,
                    Brake = 0.5f,
                    TargetSpeed = 0f
                };
            }
            else
            {
                // Close to stop line
                return new DrivingCommand
                {
                    Steer = ComputeSteer(data),
                    Throttle = 0f,
                    Brake = 0.8f,
                    TargetSpeed = 0f
                };
            }
        }

        private DrivingCommand HandleWaitingAtLight(SensorData data)
        {
            float waitTime = (float)Game.GameTime / 1000f - _waitStartTime;

            if (data.TrafficLightState == TrafficLightState.Green ||
                data.TrafficLightState == TrafficLightState.None)
            {
                TransitionTo(DecisionState.Cruising);
                return ComputeCruiseCommand(data);
            }

            // If waiting too long (light might be broken/undetected), proceed with caution
            if (waitTime > 15f && data.CollisionRisk <= CollisionRiskLevel.Low)
            {
                TransitionTo(DecisionState.Cruising);
                return ComputeCruiseCommand(data);
            }

            return DrivingCommand.Stop();
        }

        // ==================== TURNING ====================

        private DrivingCommand HandleTurning(SensorData data)
        {
            float turnSteer = 0f;
            if (data.IntersectionInfo.TurnDirection == TurnDirection.Left)
                turnSteer = -0.7f;
            else if (data.IntersectionInfo.TurnDirection == TurnDirection.Right)
                turnSteer = 0.7f;

            // Slow down significantly for turns
            return new DrivingCommand
            {
                Steer = turnSteer,
                Throttle = 0.3f,
                Brake = 0f,
                TargetSpeed = Configuration.IntersectionSpeedLimit * 0.5f
            };
        }

        // ==================== EVASION ====================

        private DrivingCommand HandleEvading(SensorData data)
        {
            if (data.CollisionRisk <= CollisionRiskLevel.Low)
            {
                TransitionTo(DecisionState.Cruising);
                return ComputeCruiseCommand(data);
            }

            // Try to steer away from threat while braking
            var threat = FindClosestThreat(data.NearbyEntities, data.Vehicle);
            float evadeSteer = 0f;

            if (threat != null)
            {
                Vector3 toThreat = threat.Position - data.Vehicle.Position;
                Vector3 right = data.Vehicle.RightVector;
                float dotRight = Vector3.Dot(toThreat, right);

                // Steer away from threat
                evadeSteer = dotRight > 0 ? -0.6f : 0.6f;
            }

            return new DrivingCommand
            {
                Steer = evadeSteer,
                Throttle = 0f,
                Brake = 0.8f,
                TargetSpeed = 0f
            };
        }

        // ==================== STUCK ====================

        private DrivingCommand HandleStuck(SensorData data)
        {
            // Try reversing to get unstuck
            return DrivingCommand.ReverseGear(data.LaneOffset > 0 ? -0.3f : 0.3f);
        }

        // ==================== EMERGENCY ====================

        private DrivingCommand HandleEmergencyStop(SensorData data)
        {
            if (data.CollisionRisk <= CollisionRiskLevel.Medium && data.Vehicle.Speed < 1f)
            {
                TransitionTo(DecisionState.Cruising);
                return ComputeCruiseCommand(data);
            }

            return DrivingCommand.EmergencyBrake();
        }

        // ==================== HELPERS ====================

        private DrivingCommand ComputeCruiseCommand(SensorData data)
        {
            float steer = ComputeSteer(data);
            float speedError = data.TargetSpeed - data.Vehicle.Speed;

            // PI controller for speed
            float dt = 0.016f; // ~60 FPS
            _speedIntegral += speedError * dt;
            _speedIntegral = System.Math.Max(-3f, System.Math.Min(3f, _speedIntegral));

            float throttle = 0.4f * speedError + 0.1f * _speedIntegral;
            throttle = System.Math.Max(0f, System.Math.Min(1f, throttle));

            // If speed is too high, apply brake instead
            float brake = 0f;
            if (speedError < -5f)
            {
                throttle = 0f;
                brake = System.Math.Min(0.5f, System.Math.Abs(speedError) / 20f);
            }

            return new DrivingCommand
            {
                Steer = steer,
                Throttle = throttle,
                Brake = brake,
                TargetSpeed = data.TargetSpeed
            };
        }

        private float ComputeSteer(SensorData data)
        {
            // Combine lane keeping with path following
            float steer = data.LaneOffset;

            // If we have a waypoint, also steer toward it
            if (data.PathInfo.Waypoint != Vector3.Zero)
            {
                Vector3 vehiclePos = data.Vehicle.Position;
                Vector3 toWaypoint = data.PathInfo.Waypoint - vehiclePos;
                toWaypoint.Normalize();

                Vector3 forward = data.Vehicle.ForwardVector;
                float cross = Vector3.Cross(forward, toWaypoint).Z;
                float waypointSteer = System.Math.Max(-0.5f, System.Math.Min(0.5f, cross * 1.5f));

                // Blend lane keeping and waypoint following
                steer = steer * 0.6f + waypointSteer * 0.4f;
            }

            return System.Math.Max(-1f, System.Math.Min(1f, steer));
        }

        private bool TryEvade(SensorData data)
        {
            // Only evade if there's room
            var threat = FindClosestThreat(data.NearbyEntities, data.Vehicle);
            if (threat == null) return false;

            // Check if lane change is possible
            // Simplified: check both sides for obstacles
            bool leftClear = EntryPoint.Instance?.CollisionPredictor?.IsLaneChangeSafeLeft(
                data.Vehicle, data.NearbyEntities, -data.Vehicle.RightVector) ?? false;
            bool rightClear = EntryPoint.Instance?.CollisionPredictor?.IsLaneChangeSafeRight(
                data.Vehicle, data.NearbyEntities, data.Vehicle.RightVector) ?? false;

            return leftClear || rightClear;
        }

        private EntityInfo FindClosestThreat(List<EntityInfo> entities, Vehicle ego)
        {
            EntityInfo closest = null;
            float closestTTC = float.MaxValue;

            foreach (var e in entities)
            {
                if (e.TimeToCollision < closestTTC && e.IsInForwardCone)
                {
                    closestTTC = e.TimeToCollision;
                    closest = e;
                }
            }

            return closest;
        }

        private bool HasCrossTraffic(SensorData data)
        {
            // Check if there are vehicles approaching from left or right
            foreach (var entity in data.NearbyEntities)
            {
                if (!entity.IsVehicle) continue;

                Vector3 toEntity = entity.Position - data.Vehicle.Position;
                Vector3 forward = data.Vehicle.ForwardVector;
                Vector3 right = data.Vehicle.RightVector;

                float dotForward = Vector3.Dot(toEntity, forward);
                float dotRight = Vector3.Dot(toEntity, right);

                // Entities coming from sides within 20m
                if (System.Math.Abs(dotRight) > System.Math.Abs(dotForward) && entity.Distance < 20f && Function.Call<float>(Hash.GET_ENTITY_SPEED, entity.Entity) > 2f)
                    return true;
            }

            return false;
        }

        private void CheckStuck(SensorData data)
        {
            if (CurrentState == DecisionState.Stuck)
                return;

            if (data.Vehicle.Speed < Configuration.StuckSpeedThreshold)
            {
                _stuckTimer += 0.016f; // ~60 FPS

                // Check if we've moved
                if (Vector3.Distance(data.Vehicle.Position, _stuckPosition) > 1f)
                {
                    _stuckTimer = 0f;
                    _stuckPosition = data.Vehicle.Position;
                }

                if (_stuckTimer > Configuration.StuckTimeThreshold)
                {
                    TransitionTo(DecisionState.Stuck);
                }
            }
            else
            {
                _stuckTimer = 0f;
                _stuckPosition = data.Vehicle.Position;
            }
        }

        private void TransitionTo(DecisionState newState)
        {
            if (CurrentState == newState)
                return;

            CurrentState = newState;

            // Reset state-specific variables
            if (newState == DecisionState.Cruising)
            {
                _speedIntegral = 0f;
            }
            else if (newState == DecisionState.Stuck)
            {
                _stuckTimer = 0f;
            }
        }
    }
}
