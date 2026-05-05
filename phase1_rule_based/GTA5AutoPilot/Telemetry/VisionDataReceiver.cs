using System;
using System.Collections.Generic;
using GTA;

namespace GTA5AutoPilot.Telemetry
{
    /// <summary>
    /// Stub receiver for Phase 1 testing. Will be replaced with TCP+JSON
    /// receiver when Phase 2 vision pipeline is running.
    /// </summary>
    public class VisionDataReceiver
    {
        public VisionDataReceiver() { }

        public VisionData GetLatestData()
        {
            return null; // Always triggers game API fallback in Phase 1
        }

        public void Stop() { }
    }

    /// <summary>
    /// Data structure matching Python VisionPerceptionBridge.to_sensor_data_dict() output.
    /// </summary>
    public class VisionData
    {
        public List<VisionEntity> Entities { get; set; } = new List<VisionEntity>();
        public int EntityCount { get; set; }
        public int TrafficLight { get; set; }     // 0=none, 1=green, 2=yellow, 3=red
        public float TrafficLightConfidence { get; set; }
        public float LaneOffset { get; set; }
        public bool LaneDetected { get; set; }
        public float LaneCurvature { get; set; }
        public long TimestampMs { get; set; }
    }

    public class VisionEntity
    {
        public float Distance { get; set; }
        public float Angle { get; set; }
        public bool IsVehicle { get; set; }
        public bool IsPedestrian { get; set; }
        public bool IsInForwardCone { get; set; }
        public bool IsOncoming { get; set; }
        public List<float> Bbox { get; set; }
        public float Confidence { get; set; }
    }

    /// <summary>
    /// Extension method to convert VisionData to EntityInfo list.
    /// </summary>
    public static class VisionDataExtensions
    {
        public static List<EntityInfo> ToEntityInfoList(this VisionData visionData)
        {
            var list = new List<EntityInfo>();
            if (visionData?.Entities == null) return list;

            foreach (var ve in visionData.Entities)
            {
                list.Add(new EntityInfo
                {
                    Distance = ve.Distance,
                    Speed = 0f, // Visual can't easily estimate speed
                    IsVehicle = ve.IsVehicle,
                    IsPedestrian = ve.IsPedestrian,
                    IsInForwardCone = ve.IsInForwardCone,
                    IsOncoming = ve.IsOncoming,
                    TimeToCollision = ve.Distance > 0.1f ? ve.Distance / 10f : float.MaxValue,
                });
            }

            return list;
        }
    }
}
