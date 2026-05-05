using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using GTA;
using GTA.Math;
using GTA.UI;

namespace GTA5AutoPilot.Telemetry
{
    /// <summary>
    /// Exports sensor data and control commands over TCP using a simple
    /// binary protocol. In production, this should use Protobuf serialization
    /// via Google.Protobuf. Here we use a compact binary format for low latency.
    ///
    /// The Python side (frame_server.py) deserializes this and pairs it with
    /// captured frames via the frame_id.
    /// </summary>
    public class TelemetryExporter
    {
        private TcpClient _client;
        private NetworkStream _stream;
        private Thread _sendThread;
        private volatile bool _isRecording;
        private ulong _frameId;

        // Buffered outgoing data
        private readonly Queue<byte[]> _sendQueue = new Queue<byte[]>();
        private readonly object _queueLock = new object();

        private readonly string _host;
        private readonly int _port;

        public bool IsConnected => _client?.Connected ?? false;

        public TelemetryExporter(string host = null, int port = 0)
        {
            _host = host ?? Configuration.TelemetryHost;
            _port = port > 0 ? port : Configuration.TelemetryPort;
        }

        public void StartRecording()
        {
            if (_isRecording) return;

            _isRecording = true;
            _frameId = 0;

            try
            {
                _client = new TcpClient();
                _client.ConnectAsync(_host, _port).Wait(2000);
            }
            catch (Exception)
            {
                // Connection failed — recording will work without remote endpoint
                // Data can still be logged locally in the future
                Notification.Show("Could not connect to Python recorder. Telemetry will not be saved.");
            }
        }

        public void StopRecording()
        {
            _isRecording = false;

            try
            {
                _client?.Close();
                _client?.Dispose();
            }
            catch { }
            finally
            {
                _client = null;
                _stream = null;
            }
        }

        public void Send(SensorData data, DrivingCommand command)
        {
            if (!_isRecording) return;

            _frameId++;

            // Build binary telemetry packet
            var packet = SerializeFrame(data, command);

            // Try to send over TCP if connected
            if (_client?.Connected == true)
            {
                try
                {
                    _stream = _client.GetStream();
                    _stream.Write(packet, 0, packet.Length);
                    _stream.Flush();
                }
                catch
                {
                    // Connection lost, stop attempting
                }
            }
        }

        private byte[] SerializeFrame(SensorData data, DrivingCommand command)
        {
            // Simple binary format (can be replaced with Protobuf):
            // [u64 frame_id][f32 speed][f32 steer][f32 throttle][f32 brake]
            // [f32 pos_x][f32 pos_y][f32 pos_z][f32 heading][f32 target_speed]
            // [u32 entity_count][entity_data...][u16 state]

            using (var ms = new System.IO.MemoryStream())
            using (var bw = new System.IO.BinaryWriter(ms))
            {
                // Frame header
                bw.Write(_frameId);
                bw.Write((ulong)(Game.GameTime));

                // Vehicle state
                var v = data.Vehicle;
                var pos = v.Position;
                bw.Write(v.Speed);
                bw.Write(v.SteeringAngle);
                bw.Write(v.Heading);
                bw.Write(pos.X);
                bw.Write(pos.Y);
                bw.Write(pos.Z);

                // Control command (ground truth for training)
                bw.Write(command.Steer);
                bw.Write(command.Throttle);
                bw.Write(command.Brake);
                bw.Write(command.TargetSpeed);
                bw.Write(command.Handbrake);
                bw.Write(command.Reverse);

                // Path info
                bw.Write(data.PathInfo.RoadHeading);
                bw.Write(data.PathInfo.RoadType);
                bw.Write(data.PathInfo.LaneCount);
                bw.Write(data.PathInfo.IsIntersectionAhead);
                bw.Write(data.PathInfo.DistanceToIntersection);

                // Traffic light
                bw.Write((byte)data.TrafficLightState);

                // Collision risk
                bw.Write((byte)data.CollisionRisk);

                // Entity count + state
                int maxEntities = Math.Min(data.NearbyEntities.Count, 20);
                bw.Write((ushort)maxEntities);
                for (int i = 0; i < maxEntities; i++)
                {
                    var e = data.NearbyEntities[i];
                    bw.Write(e.Distance);
                    bw.Write(e.Speed);
                    bw.Write(e.IsVehicle);
                    bw.Write(e.IsPedestrian);
                    bw.Write(e.IsInForwardCone);
                    bw.Write(e.IsOncoming);
                    bw.Write(e.TimeToCollision);
                    bw.Write(e.Position.X);
                    bw.Write(e.Position.Y);
                    bw.Write(e.Position.Z);
                }

                // Decision state
                bw.Write((ushort)0); // state enum will be filled by caller

                return ms.ToArray();
            }
        }
    }
}
