using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace GTA5AutoPilot.Networking
{
    /// <summary>One parsed line received from a client. Exactly one of Json / Error is set.</summary>
    public class ClientRequest
    {
        public int ClientId;
        public JObject Json;
        public string Error;
    }

    /// <summary>
    /// Line-delimited JSON over TCP (see docs/PROTOCOL.md).
    ///
    /// Threading rule: this class never touches the game. Network threads only
    /// parse lines and put them in a queue; the script thread drains the queue
    /// with TryDequeue() inside OnTick and answers with Send() / Broadcast(),
    /// which only enqueue text for the sender thread.
    /// </summary>
    public class TcpCommandServer
    {
        private const int MaxClients = 8;
        private const int MaxLineChars = 64 * 1024;
        private const int MaxPendingOutbound = 1000;
        private const int MaxPendingInbound = 500;

        private class ClientConn
        {
            public int Id;
            public TcpClient Tcp;
            public NetworkStream Stream;
            public readonly ConcurrentQueue<string> Outbound = new ConcurrentQueue<string>();
            public volatile bool Closed;
        }

        private readonly ConcurrentQueue<ClientRequest> _inbound = new ConcurrentQueue<ClientRequest>();
        private readonly ConcurrentDictionary<int, ClientConn> _clients = new ConcurrentDictionary<int, ClientConn>();
        private readonly AutoResetEvent _sendSignal = new AutoResetEvent(false);
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        private TcpListener _listener;
        private Thread _listenThread;
        private Thread _sendThread;
        private volatile bool _running;
        private volatile string _lastError;
        private readonly int _port;
        private int _nextClientId;

        public TcpCommandServer(int port = 21556)
        {
            _port = port;
        }

        /// <summary>Number of currently connected clients.</summary>
        public int ClientCount => _clients.Count;

        /// <summary>Last server-level error (e.g. port already in use), or null. Read from the script thread.</summary>
        public string LastError => _lastError;

        public void Start()
        {
            if (_running) return;
            _running = true;
            _listenThread = new Thread(ListenLoop) { IsBackground = true, Name = "AutoPilotTcpListen" };
            _sendThread = new Thread(SendLoop) { IsBackground = true, Name = "AutoPilotTcpSend" };
            _listenThread.Start();
            _sendThread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            foreach (var kv in _clients) CloseClient(kv.Value);
            _sendSignal.Set();
            try { _listenThread?.Join(500); } catch { }
            try { _sendThread?.Join(500); } catch { }
        }

        /// <summary>Script thread: take the next pending request, if any.</summary>
        public bool TryDequeue(out ClientRequest request)
        {
            return _inbound.TryDequeue(out request);
        }

        /// <summary>Queue one line for one client. Safe from any thread; never blocks.</summary>
        public void Send(int clientId, string line)
        {
            if (_clients.TryGetValue(clientId, out var conn))
                Enqueue(conn, line);
        }

        /// <summary>Queue one line for every connected client. Safe from any thread; never blocks.</summary>
        public void Broadcast(string line)
        {
            foreach (var kv in _clients) Enqueue(kv.Value, line);
        }

        private void Enqueue(ClientConn conn, string line)
        {
            if (conn.Closed) return;
            if (conn.Outbound.Count > MaxPendingOutbound)
            {
                // Client is connected but not reading. Drop it instead of growing memory.
                CloseClient(conn);
                return;
            }
            conn.Outbound.Enqueue(line);
            _sendSignal.Set();
        }

        private void ListenLoop()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, _port);
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Start();
                _lastError = null;

                while (_running)
                {
                    TcpClient tcp;
                    try
                    {
                        if (!_listener.Pending())
                        {
                            Thread.Sleep(100);
                            continue;
                        }
                        tcp = _listener.AcceptTcpClient();
                    }
                    catch
                    {
                        if (!_running) break;
                        continue;
                    }

                    if (_clients.Count >= MaxClients)
                    {
                        try { tcp.Close(); } catch { }
                        continue;
                    }

                    try
                    {
                        tcp.NoDelay = true;
                        tcp.SendTimeout = 2000;
                        var conn = new ClientConn
                        {
                            Id = Interlocked.Increment(ref _nextClientId),
                            Tcp = tcp,
                            Stream = tcp.GetStream(),
                        };
                        _clients[conn.Id] = conn;
                        var reader = new Thread(() => ReadLoop(conn))
                        {
                            IsBackground = true,
                            Name = "AutoPilotTcpRead" + conn.Id,
                        };
                        reader.Start();
                    }
                    catch
                    {
                        try { tcp.Close(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                // Do NOT call game functions here: this is not the script thread.
                if (_running) _lastError = ex.Message;
            }
            finally
            {
                try { _listener?.Stop(); } catch { }
            }
        }

        private void ReadLoop(ClientConn conn)
        {
            try
            {
                // leaveOpen-style: the stream is closed by CloseClient, not by the reader.
                var reader = new StreamReader(conn.Stream, Utf8NoBom, false, 4096);
                while (_running && !conn.Closed)
                {
                    string line = reader.ReadLine();
                    if (line == null) break; // client closed the connection
                    line = line.Trim();
                    if (line.Length == 0) continue;

                    var request = new ClientRequest { ClientId = conn.Id };
                    if (line.Length > MaxLineChars)
                    {
                        request.Error = "line too long";
                    }
                    else
                    {
                        try
                        {
                            request.Json = JObject.Parse(line);
                        }
                        catch (Exception ex)
                        {
                            request.Error = ex.Message;
                        }
                    }

                    if (_inbound.Count < MaxPendingInbound)
                        _inbound.Enqueue(request);
                }
            }
            catch
            {
                // connection reset / closed
            }
            finally
            {
                CloseClient(conn);
            }
        }

        private void SendLoop()
        {
            while (_running)
            {
                _sendSignal.WaitOne(250);
                foreach (var kv in _clients)
                {
                    var conn = kv.Value;
                    if (conn.Closed) continue;
                    try
                    {
                        while (conn.Outbound.TryDequeue(out string line))
                        {
                            byte[] bytes = Utf8NoBom.GetBytes(line + "\n");
                            conn.Stream.Write(bytes, 0, bytes.Length);
                        }
                    }
                    catch
                    {
                        CloseClient(conn);
                    }
                }
            }
        }

        private void CloseClient(ClientConn conn)
        {
            conn.Closed = true;
            _clients.TryRemove(conn.Id, out _);
            try { conn.Stream?.Close(); } catch { }
            try { conn.Tcp?.Close(); } catch { }
        }
    }
}
