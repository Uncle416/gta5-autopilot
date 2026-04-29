using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using Newtonsoft.Json;
using GTA;

namespace GTA5AutoPilot.Telemetry
{
    /// <summary>
    /// Receives visual perception data from the Python pipeline.
    /// Listens on a local TCP port for JSON-encoded VisionPerceptionResult packets.
    /// </summary>
    public class VisionDataReceiver
    {
        private TcpListener _listener;
        private VisionData _latestData;
        private readonly object _dataLock = new object();

        private const int Port = 21557;

        public VisionDataReceiver()
        {
            StartListening();
        }

        private void StartListening()
        {
            try
            {
                _listener = new TcpListener(System.Net.IPAddress.Loopback, Port);
                _listener.Start();
                _listener.BeginAcceptTcpClient(OnClientConnected, null);
            }
            catch (Exception)
            {
                // Port may be in use — vision perception will use game API fallback
            }
        }

        private void OnClientConnected(IAsyncResult ar)
        {
            try
            {
                var client = _listener.EndAcceptTcpClient(ar);
                // Handle this client and start accepting next
                _listener.BeginAcceptTcpClient(OnClientConnected, null);
                ReadFromClient(client);
            }
            catch { }
        }

        private void ReadFromClient(TcpClient client)
        {
            try
            {
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream))
                {
                    string json = reader.ReadToEnd();
                    if (!string.IsNullOrEmpty(json))
                    {
                        var data = JsonConvert.DeserializeObject<VisionData>(json);
                        if (data != null)
                        {
                            lock (_dataLock)
                            {
                                _latestData = data;
                            }
                        }
                    }
                }
            }
            catch { }
            finally
            {
                client?.Close();
            }
        }

        /// <summary>
        /// Get the latest visual perception data (thread-safe).
        /// Returns null if no data received yet.
        /// </summary>
        public VisionData GetLatestData()
        {
            lock (_dataLock)
            {
                return _latestData;
            }
        }

        public void Stop()
        {
            try { _listener?.Stop(); } catch { }
        }
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
