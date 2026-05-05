using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.UI;

namespace GTA5AutoPilot.Debug
{
    /// <summary>
    /// Renders debug information on screen using GTA V text primitives.
    /// Shows sensor data, FSM state, and control commands.
    /// </summary>
    public class DebugOverlay
    {
        public bool Enabled { get; set; } = true;

        public void Render(SensorData data, DrivingCommand command, DecisionState state)
        {
            if (!Enabled)
                return;

            DrawTextBlock(data, command, state);
            DrawEntityBoxes(data);
            DrawWaypointMarker(data);
            DrawTrafficLightInfo(data);
        }

        private void DrawTextBlock(SensorData data, DrivingCommand command, DecisionState state)
        {
            // Text block disabled — ShowSubtitle is not suitable for persistent multi-line text.
            // Use the per-frame debug subtitle in EntryPoint instead.
        }

        private void DrawEntityBoxes(SensorData data)
        {
            foreach (var entity in data.NearbyEntities)
            {
                if (!entity.IsVehicle && !entity.IsPedestrian)
                    continue;

                var color = entity.IsOncoming ? System.Drawing.Color.Red :
                           entity.IsInForwardCone ? System.Drawing.Color.Yellow :
                           System.Drawing.Color.Gray;

                // Draw bounding box marker
                World.DrawMarker(MarkerType.HorizontalCircleFat,
                    entity.Position + Vector3.WorldUp * 1.5f,
                    Vector3.Zero, Vector3.Zero,
                    new Vector3(1f, 1f, 1f),
                    color);

                // Draw distance label
                if (entity.Distance < 30f)
                {
                    // In practice, use a 3D text drawing method here
                }
            }
        }

        private void DrawWaypointMarker(SensorData data)
        {
            if (data.PathInfo.Waypoint == Vector3.Zero)
                return;

            World.DrawMarker(MarkerType.VerticalCylinder,
                data.PathInfo.Waypoint,
                Vector3.Zero, Vector3.Zero,
                new Vector3(0.5f, 0.5f, 0.5f),
                System.Drawing.Color.Cyan);
        }

        private void DrawTrafficLightInfo(SensorData data)
        {
            if (data.TrafficLightState == TrafficLightState.None)
                return;

            var color = data.TrafficLightState == TrafficLightState.Red ? System.Drawing.Color.Red :
                       data.TrafficLightState == TrafficLightState.Yellow ? System.Drawing.Color.Yellow :
                       System.Drawing.Color.Green;

            // Draw indicator at vehicle position
            World.DrawMarker(MarkerType.HorizontalCircleFat,
                data.Vehicle.Position + Vector3.WorldUp * 3f,
                Vector3.Zero, Vector3.Zero,
                new Vector3(0.5f, 0.5f, 0.5f),
                color);
        }

        private static string GetStateColor(DecisionState state) => state switch
        {
            DecisionState.Cruising => "~g~",
            DecisionState.StoppingAtLight => "~y~",
            DecisionState.WaitingAtLight => "~y~",
            DecisionState.Turning => "~b~",
            DecisionState.Evading => "~r~",
            DecisionState.EmergencyStop => "~r~",
            DecisionState.Stuck => "~o~",
            _ => "~w~"
        };

        private static string GetLightColor(TrafficLightState state) => state switch
        {
            TrafficLightState.Red => "~r~",
            TrafficLightState.Yellow => "~y~",
            TrafficLightState.Green => "~g~",
            _ => "~w~"
        };

        private static string GetRiskColor(CollisionRiskLevel risk) => risk switch
        {
            CollisionRiskLevel.None => "~g~",
            CollisionRiskLevel.Low => "~y~",
            CollisionRiskLevel.Medium => "~o~",
            CollisionRiskLevel.High => "~r~",
            CollisionRiskLevel.Imminent => "~r~",
            _ => "~w~"
        };

        private static string RoadTypeName(int type) => type switch
        {
            1 => "Highway",
            2 => "Alley",
            3 => "Gravel",
            _ => "Urban"
        };
    }
}
