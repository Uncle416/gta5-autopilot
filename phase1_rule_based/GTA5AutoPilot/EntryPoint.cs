using System;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA5AutoPilot.Modules;
using GTA5AutoPilot.Debug;

namespace GTA5AutoPilot
{
    public class EntryPoint : Script
    {
        public static EntryPoint Instance { get; private set; }
        public bool AutoPilotEnabled => _autoPilotEnabled;
        public CollisionPredictor CollisionPredictor => _collisionPredictor;

        private VehicleController _vehicleCtrl;
        private PathNavigator _pathNav;
        private CollisionPredictor _collisionPredictor;
        private EntityDetector _entityDetector;
        private DebugOverlay _debugOverlay;
        private bool _autoPilotEnabled;
        private Vector3 _destination;
        private bool _destSet;
        private PerceptionMode _perceptionMode = PerceptionMode.GameAPI;
        private int _debugFrameCounter;
        private int _tickSinceTaskIssue;

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

            GTA.UI.Screen.ShowSubtitle("GTA5AutoPilot loaded (AI)", 3000);
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

            if (_destSet)
            {
                // Re-issue AI drive task every ~2 seconds to keep it active
                _tickSinceTaskIssue++;
                if (_tickSinceTaskIssue > 120)
                {
                    Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                        driver, vehicle,
                        _destination.X, _destination.Y, _destination.Z,
                        20f,    // speed m/s
                        4 | 16 | 32 | 128 | 2048,  // avoid vehicles(4) + avoid objects(16) + avoid peds(32) + allow wrong way(128) + allow stop(2048)
                        10f);   // stop range (meters)
                    _tickSinceTaskIssue = 0;
                }

                // Check arrival
                float distToDest = Vector3.Distance(vehicle.Position, _destination);
                if (distToDest < 15f)
                {
                    Function.Call(Hash.CLEAR_PED_TASKS, driver);
                    _autoPilotEnabled = false;
                    _vehicleCtrl.Release();
                    _destSet = false;
                    GTA.UI.Screen.ShowSubtitle("Destination reached!", 4000);
                }
            }

            // Debug
            _debugFrameCounter++;
            if (_debugFrameCounter % 30 == 0)
            {
                float dist = _destSet ? Vector3.Distance(vehicle.Position, _destination) : 0f;
                GTA.UI.Screen.ShowSubtitle(
                    $"AI Drive | Dist={dist:F0}m | Spd={vehicle.Speed * 3.6f:F0}km/h | Ent={entities.Count}", 1000);
            }

            _debugOverlay.Render(
                new SensorData
                {
                    Vehicle = vehicle, PathInfo = pathInfo,
                    NearbyEntities = entities, LaneOffset = 0f,
                    TargetSpeed = 20f, SourceMode = _perceptionMode
                }, new DrivingCommand { TargetSpeed = 20f }, DecisionState.Cruising);
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.NumPad0) ToggleAutoPilot();
            else if (e.KeyCode == Keys.NumPad1) ToggleDebugOverlay();
            else if (e.KeyCode == Keys.NumPad2) SetDestination();
            else if (e.KeyCode == Keys.Decimal && _autoPilotEnabled) ToggleAutoPilot();
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
            }
            else
            {
                _vehicleCtrl.Release();
                Function.Call(Hash.CLEAR_PED_TASKS, Game.Player.Character);
            }
            GTA.UI.Screen.ShowSubtitle(_autoPilotEnabled ? "AutoPilot ON (AI)" : "AutoPilot OFF", 2000);
        }

        public void ToggleDebugOverlay()
        {
            _debugOverlay.Enabled = !_debugOverlay.Enabled;
            GTA.UI.Screen.ShowSubtitle(_debugOverlay.Enabled ? "Debug ON" : "Debug OFF", 2000);
        }

        public void SetDestination()
        {
            try
            {
                int id = Function.Call<int>(Hash.GET_FIRST_BLIP_INFO_ID, 8);
                if (Function.Call<bool>(Hash.DOES_BLIP_EXIST, id))
                {
                    var wp = Function.Call<Vector3>(Hash.GET_BLIP_INFO_ID_COORD, id);
                    if (wp != Vector3.Zero)
                    {
                        _destination = wp;
                        _destSet = true;
                        _pathNav.SetDestination(wp);
                        _tickSinceTaskIssue = 999;
                        GTA.UI.Screen.ShowSubtitle($"Dest: {wp.X:F0},{wp.Y:F0}", 4000);
                        return;
                    }
                }
            }
            catch { }
            GTA.UI.Screen.ShowSubtitle("No waypoint", 2000);
        }

        public void ToggleRecording() { }
        public void CyclePerceptionMode() { }
    }
}
