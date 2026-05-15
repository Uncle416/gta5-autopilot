using System;
using System.Collections.Generic;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA5AutoPilot.Modules;
using GTA5AutoPilot.Debug;
using GTA5AutoPilot.Networking;

namespace GTA5AutoPilot
{
    public class EntryPoint : Script
    {
        public static EntryPoint Instance { get; private set; }
        public bool AutoPilotEnabled => _autoPilotEnabled;
        public CollisionPredictor CollisionPredictor => _collisionPredictor;
        public WaypointManager WaypointManager => _waypointManager;

        private VehicleController _vehicleCtrl;
        private PathNavigator _pathNav;
        private CollisionPredictor _collisionPredictor;
        private EntityDetector _entityDetector;
        private DebugOverlay _debugOverlay;
        private WaypointManager _waypointManager;
        private TcpCommandServer _tcpServer;

        private bool _autoPilotEnabled;
        private int _debugFrameCounter;
        private int _tickSinceTaskIssue;
        private int _arrivalDisplayTimer;

        public EntryPoint()
        {
            Instance = this;
            Tick += OnTick;
            KeyDown += OnKeyDown;
            Interval = 0;

            _vehicleCtrl = new VehicleController();
            _pathNav = new PathNavigator();
            _collisionPredictor = new CollisionPredictor();
            _entityDetector = new EntityDetector();
            _debugOverlay = new DebugOverlay();
            _waypointManager = new WaypointManager();

            // Start TCP command server + wire events
            _tcpServer = new TcpCommandServer(Configuration.CommandServerPort);
            _tcpServer.OnWaypointsReceived += OnWaypointsReceived;
            _tcpServer.OnCommandReceived += OnTcpCommand;
            _tcpServer.Start();

            GTA.UI.Screen.ShowSubtitle("GTA5AutoPilot loaded (NL Nav)", 3000);
        }

        private void OnWaypointsReceived(List<Waypoint> waypoints)
        {
            if (waypoints == null || waypoints.Count == 0)
            {
                GTA.UI.Screen.ShowSubtitle("No waypoints received", 3000);
                return;
            }

            _waypointManager.LoadWaypoints(waypoints);

            // Build confirmation message
            string msg = $"Loaded {waypoints.Count} waypoints:";
            for (int i = 0; i < waypoints.Count; i++)
                msg += $"\n  {i + 1}. {waypoints[i].Name}";

            GTA.UI.Screen.ShowSubtitle(msg, 5000);
        }

        private void OnTcpCommand(string action)
        {
            switch (action)
            {
                case "continue":
                    ContinueNavigation();
                    break;
                case "stop":
                    StopAutopilot();
                    break;
            }
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (!_autoPilotEnabled) return;

            var driver = Game.Player.Character;
            var vehicle = driver.CurrentVehicle;
            if (vehicle == null || !vehicle.Exists() || vehicle.IsDead) return;
            if (driver.IsDead) return;

            var entities = _entityDetector.ScanSurroundings(vehicle);
            var pathInfo = _pathNav.GetNextWaypoint(vehicle);

            // ---- Driving state ----
            if (_waypointManager.State == NavigateState.Driving)
            {
                Vector3 dest = _waypointManager.GetCurrentDestination();
                if (dest != Vector3.Zero)
                {
                    // Re-issue AI drive task periodically
                    _tickSinceTaskIssue++;
                    if (_tickSinceTaskIssue > Configuration.AiTaskReissueInterval)
                    {
                        Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                            driver, vehicle,
                            dest.X, dest.Y, dest.Z,
                            Configuration.AiDrivingSpeed,
                            Configuration.AiDrivingFlags,
                            10f);
                        _tickSinceTaskIssue = 0;
                    }

                    // Check arrival
                    float distToDest = Vector3.Distance(vehicle.Position, dest);
                    if (distToDest < Configuration.DestinationArrivalRadius)
                    {
                        Function.Call(Hash.CLEAR_PED_TASKS, driver);
                        _vehicleCtrl.Release();

                        bool isFinal = _waypointManager.OnArrival();

                        if (isFinal)
                        {
                            _autoPilotEnabled = false;
                            GTA.UI.Screen.ShowSubtitle(
                                $"Arrived at final destination: {_waypointManager.CurrentWaypointName}", 5000);
                        }
                        else
                        {
                            // Show prompt for next waypoint
                            string prompt = _waypointManager.GetCurrentPrompt();
                            GTA.UI.Screen.ShowSubtitle(prompt, 5000);
                            _arrivalDisplayTimer = 300; // ~5s reminder
                        }
                    }
                }
            }

            // Waiting state — periodically re-show prompt
            if (_waypointManager.State == NavigateState.Waiting)
            {
                _arrivalDisplayTimer--;
                if (_arrivalDisplayTimer > 0 && _arrivalDisplayTimer % 150 == 0)
                {
                    GTA.UI.Screen.ShowSubtitle(_waypointManager.GetCurrentPrompt(), 3000);
                }
            }

            // ---- Debug HUD ----
            _debugFrameCounter++;
            if (_debugFrameCounter % 30 == 0)
            {
                string status = _waypointManager.GetStatusString(vehicle);
                if (string.IsNullOrEmpty(status))
                    status = _waypointManager.State == NavigateState.Waiting
                        ? "WAITING" : "Idle";
                GTA.UI.Screen.ShowSubtitle(
                    $"AI | {status} | {vehicle.Speed * 3.6f:F0}km/h | Ent={entities.Count}", 1000);
            }

            _debugOverlay.Render(
                new SensorData
                {
                    Vehicle = vehicle, PathInfo = pathInfo,
                    NearbyEntities = entities, LaneOffset = 0f,
                    TargetSpeed = Configuration.AiDrivingSpeed
                }, new DrivingCommand { TargetSpeed = Configuration.AiDrivingSpeed }, DecisionState.Cruising);
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.NumPad0) ToggleAutoPilot();
            else if (e.KeyCode == Keys.NumPad1) ToggleDebugOverlay();
            else if (e.KeyCode == Keys.NumPad2) SetDestinationFromMap();
            else if (e.KeyCode == Keys.NumPad7) ContinueNavigation();
            else if (e.KeyCode == Keys.NumPad8) StopAutopilot();
            else if (e.KeyCode == Keys.Decimal && _autoPilotEnabled) StopAutopilot();
            e.Handled = true;
        }

        public void ToggleAutoPilot()
        {
            _autoPilotEnabled = !_autoPilotEnabled;
            if (_autoPilotEnabled)
            {
                var v = Game.Player.Character.CurrentVehicle;
                if (v != null && v.Exists()) _pathNav.Reset(v);
                _tickSinceTaskIssue = 999; // Issue task on next tick

                // If waypoints loaded but not driving, start
                if (_waypointManager.TotalCount > 0 &&
                    _waypointManager.State != NavigateState.Driving)
                    _waypointManager.StartNavigation();
            }
            else
            {
                _vehicleCtrl.Release();
                Function.Call(Hash.CLEAR_PED_TASKS, Game.Player.Character);
            }
            GTA.UI.Screen.ShowSubtitle(_autoPilotEnabled ? "AutoPilot ON (NL Nav)" : "AutoPilot OFF", 2000);
        }

        public void ToggleDebugOverlay()
        {
            _debugOverlay.Enabled = !_debugOverlay.Enabled;
            GTA.UI.Screen.ShowSubtitle(_debugOverlay.Enabled ? "Debug ON" : "Debug OFF", 2000);
        }

        /// <summary>Fallback: set single destination from map waypoint.</summary>
        public void SetDestinationFromMap()
        {
            try
            {
                int id = Function.Call<int>(Hash.GET_FIRST_BLIP_INFO_ID, 8);
                if (Function.Call<bool>(Hash.DOES_BLIP_EXIST, id))
                {
                    var wp = Function.Call<Vector3>(Hash.GET_BLIP_INFO_ID_COORD, id);
                    if (wp != Vector3.Zero)
                    {
                        var wps = new List<Waypoint> { new Waypoint("Map Waypoint", wp) };
                        _waypointManager.LoadWaypoints(wps);
                        if (!_autoPilotEnabled) ToggleAutoPilot();
                        else _waypointManager.StartNavigation();
                        GTA.UI.Screen.ShowSubtitle($"Dest: {wp.X:F0},{wp.Y:F0}", 4000);
                        return;
                    }
                }
            }
            catch { }
            GTA.UI.Screen.ShowSubtitle("No waypoint on map", 2000);
        }

        public void ContinueNavigation()
        {
            if (_waypointManager.State != NavigateState.Waiting)
            {
                // If waypoints loaded but not started, start now
                if (_waypointManager.TotalCount > 0 &&
                    _waypointManager.State == NavigateState.Idle)
                {
                    _waypointManager.StartNavigation();
                    if (!_autoPilotEnabled) ToggleAutoPilot();
                }
                return;
            }

            if (_waypointManager.ContinueToNext())
            {
                _tickSinceTaskIssue = 999;
                GTA.UI.Screen.ShowSubtitle(
                    $"Continuing to {_waypointManager.CurrentWaypointName} ({_waypointManager.CurrentIndex + 1}/{_waypointManager.TotalCount})", 3000);
            }
            else
            {
                _autoPilotEnabled = false;
                Function.Call(Hash.CLEAR_PED_TASKS, Game.Player.Character);
                GTA.UI.Screen.ShowSubtitle("Navigation complete", 3000);
            }
        }

        public void StopAutopilot()
        {
            if (!_autoPilotEnabled && _waypointManager.State != NavigateState.Waiting) return;

            _waypointManager.StopNavigation();
            _autoPilotEnabled = false;
            _vehicleCtrl.Release();
            Function.Call(Hash.CLEAR_PED_TASKS, Game.Player.Character);
            GTA.UI.Screen.ShowSubtitle("AutoPilot OFF — Stopped", 2000);
        }

        protected override void Dispose(bool disposing)
        {
            _tcpServer?.Stop();
            base.Dispose(disposing);
        }
    }
}
