using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GTA5AutoPilot.Networking
{
    public class TcpCommandServer
    {
        public event Action<List<Waypoint>> OnWaypointsReceived;
        public event Action<string> OnCommandReceived; // "continue" / "stop"

        private TcpListener _listener;
        private Thread _listenThread;
        private volatile bool _running;
        private readonly int _port;

        public TcpCommandServer(int port = 21556)
        {
            _port = port;
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _listenThread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "TcpCommandServer"
            };
            _listenThread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            _listenThread?.Join(1000);
        }

        private void ListenLoop()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, _port);
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Start();

                while (_running)
                {
                    TcpClient client = null;
                    try
                    {
                        if (_listener.Pending())
                            client = _listener.AcceptTcpClient();
                        else
                        {
                            Thread.Sleep(100);
                            continue;
                        }
                    }
                    catch
                    {
                        if (!_running) break;
                        continue;
                    }

                    ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
                }
            }
            catch (Exception ex)
            {
                if (_running)
                    GTA.UI.Screen.ShowSubtitle($"TCP server error: {ex.Message}", 3000);
            }
            finally
            {
                try { _listener?.Stop(); } catch { }
            }
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                using (client)
                {
                    client.ReceiveTimeout = 5000;
                    using (var stream = client.GetStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string json = reader.ReadLine();
                        if (string.IsNullOrEmpty(json)) return;
                        ProcessMessage(json);
                    }
                }
            }
            catch { /* client disconnect */ }
        }

        private void ProcessMessage(string json)
        {
            try
            {
                var obj = JObject.Parse(json);
                string type = obj["type"]?.ToString();

                switch (type)
                {
                    case "set_waypoints":
                        var wps = new List<Waypoint>();
                        foreach (var item in obj["waypoints"])
                        {
                            string name = item["name"]?.ToString() ?? "Unknown";
                            float x = item["x"]?.Value<float>() ?? 0f;
                            float y = item["y"]?.Value<float>() ?? 0f;
                            float z = item["z"]?.Value<float>() ?? 0f;
                            wps.Add(new Waypoint(name, new GTA.Math.Vector3(x, y, z)));
                        }
                        OnWaypointsReceived?.Invoke(wps);
                        break;

                    case "command":
                        string action = obj["action"]?.ToString();
                        OnCommandReceived?.Invoke(action);
                        break;
                }
            }
            catch (JsonException) { /* ignore malformed json */ }
        }
    }
}
