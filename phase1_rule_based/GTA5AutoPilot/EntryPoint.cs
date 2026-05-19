using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
        private LlamaNavigator _llamaNav;

        private bool _autoPilotEnabled;
        private int _tickSinceTaskIssue;
        private int _arrivalDisplayTimer;
        private int _statusCounter;

        // Keyboard
        private bool _keyboardActive;
        private bool _showQueue;

        // LLM
        private Task<(List<Waypoint>, string)> _pendingNluTask;
        private bool _nluProcessing;
        private string _lastNluResult;
        private int _nluShowTimer;

        // Progress-based task reissue (not timer-based — avoids resetting AI state)
        private float _lastProgressDist = float.MaxValue;
        private int _progressCheckTicks;

        // Stuck detection
        private float _lastDestDist = float.MaxValue;
        private int _destStuckTicks;

        // Arrival braking
        private bool _arrivalStopping;
        private string _arrivedName;
        private bool _arrivedWasStuck;

        // (GPS handled by AI driving task natively)

        public EntryPoint()
        {
            Instance = this;
            Tick += OnTick;
            KeyDown += OnKeyDown;
            KeyUp += OnKeyUp;
            Interval = 0;

            _vehicleCtrl = new VehicleController();
            _pathNav = new PathNavigator();
            _collisionPredictor = new CollisionPredictor();
            _entityDetector = new EntityDetector();
            _debugOverlay = new DebugOverlay();
            _waypointManager = new WaypointManager();

            _tcpServer = new TcpCommandServer(Configuration.CommandServerPort);
            _tcpServer.OnWaypointsReceived += OnWaypointsReceived;
            _tcpServer.OnCommandReceived += OnTcpCommand;
            _tcpServer.Start();

            _llamaNav = new LlamaNavigator(Configuration.LlamaModelPath);

            GTA.UI.Screen.ShowSubtitle(
                $"GTA5AutoPilot ready | LLM:{(_llamaNav.IsReady ? "on" : "off")}", 3000);
        }

        private void OnWaypointsReceived(List<Waypoint> waypoints)
        {
            if (waypoints == null || waypoints.Count == 0) return;
            _waypointManager.LoadWaypoints(waypoints);
            GTA.UI.Screen.ShowSubtitle($"Loaded {waypoints.Count} waypoints via TCP", 4000);
        }

        private void OnTcpCommand(string action)
        {
            if (action == "continue") ContinueNavigation();
            else if (action == "stop") StopAutopilot();
        }

        private void OnTick(object sender, EventArgs e)
        {
            // ---- Keyboard poll ----
            if (_keyboardActive)
            {
                int kbStatus = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);
                if (kbStatus == 2)
                {
                    string text = Function.Call<string>(Hash.GET_ONSCREEN_KEYBOARD_RESULT);
                    _keyboardActive = false;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        _nluProcessing = true;
                        _nluShowTimer = 0;
                        _pendingNluTask = _llamaNav.ParseNavigationAsync(text);
                    }
                }
                else if (kbStatus == 3) _keyboardActive = false;
            }

            // ---- NLU completion ----
            if (_nluProcessing && _pendingNluTask != null && _pendingNluTask.IsCompleted)
            {
                var (waypoints, response) = _pendingNluTask.Result;
                _nluProcessing = false;
                _lastNluResult = response;
                _nluShowTimer = 600; // show result for ~10 seconds
                if (waypoints.Count > 0)
                {
                    _waypointManager.LoadWaypoints(waypoints);
                    _waypointManager.StartNavigation();
                    SetGpsToCurrent();
                }
            }

            // Timer for NLU display
            if (_nluShowTimer > 0) _nluShowTimer--;

            if (!_autoPilotEnabled) return;

            var driver = Game.Player.Character;
            var vehicle = driver.CurrentVehicle;
            if (vehicle == null || !vehicle.Exists() || vehicle.IsDead) return;
            if (driver.IsDead) return;

            // ---- Arrival braking: keep braking until fully stopped ----
            if (_arrivalStopping)
            {
                if (vehicle.Speed > 0.5f)
                {
                    // Loop brake: re-apply temp action while still moving
                    Function.Call(Hash.TASK_VEHICLE_TEMP_ACTION, driver, vehicle, 1, 500);
                }
                else
                {
                    // Finally stopped — process arrival
                    Function.Call(Hash.SET_VEHICLE_FORWARD_SPEED, vehicle, 0f);
                    _arrivalStopping = false;
                    _vehicleCtrl.Release();
                    _destStuckTicks = 0;
                    _lastDestDist = float.MaxValue;
                    _lastProgressDist = float.MaxValue;
                    _progressCheckTicks = 0;

                    bool isFinal = _waypointManager.OnArrival();
                    string reason = _arrivedWasStuck ? " (skipped)" : "";
                    if (isFinal)
                    {
                        _autoPilotEnabled = false;
                        GTA.UI.Screen.ShowSubtitle($"Arrived: {_arrivedName}{reason}", 5000);
                    }
                    else
                    {
                        GTA.UI.Screen.ShowSubtitle(_waypointManager.GetCurrentPrompt() + reason, 5000);
                        _arrivalDisplayTimer = 300;
                    }
                }
                return; // skip driving logic while braking
            }

            // ---- Driving ----
            if (_waypointManager.State == NavigateState.Driving)
            {
                Vector3 dest = _waypointManager.GetCurrentDestination();
                if (dest != Vector3.Zero)
                {
                    _tickSinceTaskIssue++;
                    _progressCheckTicks++;

                    float distToDest = Vector3.Distance(vehicle.Position, dest);

                    // Issue task once, reissue only if genuinely stuck.
                    // NPCs drive well because their task isn't constantly reset.
                    bool shouldReissue = false;
                    if (_lastProgressDist == float.MaxValue)
                    {
                        // First issue after navigation start
                        shouldReissue = true;
                        _lastProgressDist = distToDest;
                    }
                    else if (_progressCheckTicks >= 180) // check every ~6s
                    {
                        float progress = _lastProgressDist - distToDest; // positive = closer
                        bool makingProgress = progress > 3f;
                        bool waitingAtLight = vehicle.Speed < 1f; // AI chose to stop (red light / traffic)
                        bool safetyTimeout = _tickSinceTaskIssue > 900; // 30s max

                        if (!makingProgress && !waitingAtLight)
                            shouldReissue = true;
                        else if (safetyTimeout)
                            shouldReissue = true;

                        _lastProgressDist = distToDest;
                        _progressCheckTicks = 0;
                    }

                    if (shouldReissue)
                    {
                        Function.Call(Hash.SET_DRIVER_ABILITY, driver, 1.0f);
                        Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, driver, 0.15f);
                        Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                            driver, vehicle,
                            dest.X, dest.Y, dest.Z,
                            Configuration.AiDrivingSpeed,
                            Configuration.AiDrivingFlags,
                            Configuration.DestinationArrivalRadius);
                        _tickSinceTaskIssue = 0;
                    }

                    // Stuck detection near destination
                    if (distToDest < 40f && System.Math.Abs(distToDest - _lastDestDist) < 0.5f)
                        _destStuckTicks++;
                    else { _destStuckTicks = 0; _lastDestDist = distToDest; }

                    bool reached = distToDest < Configuration.DestinationArrivalRadius;
                    bool stuck = _destStuckTicks > Configuration.DestinationStuckTimeout;

                    if (reached || stuck)
                    {
                        _arrivedName = _waypointManager.CurrentWaypointName;
                        _arrivedWasStuck = stuck;
                        _arrivalStopping = true;
                        Function.Call(Hash.CLEAR_PED_TASKS, driver);
                    }
                }
            }

            // Minimap GPS: AI task doesn't show route; set waypoint at key transitions.
            // (Handled in ToggleAutoPilot / ContinueNavigation / NLU load)

            // Waiting at intermediate — show prompt until user responds
            if (_waypointManager.State == NavigateState.Waiting)
            {
                _arrivalDisplayTimer--;
                if (_arrivalDisplayTimer > 0)
                {
                    if (_arrivalDisplayTimer % 60 == 0 || _arrivalDisplayTimer == 300)
                        GTA.UI.Screen.ShowSubtitle(_waypointManager.GetCurrentPrompt(), 3000);
                }
            }

            // ---- Status (suppressed while arrival prompt is active) ----
            _statusCounter++;
            if (_statusCounter % 30 == 0)
            {
                if (_showQueue)
                {
                    GTA.UI.Screen.ShowSubtitle(_waypointManager.GetQueueString(), 800);
                }
                else if (_nluProcessing)
                {
                    GTA.UI.Screen.ShowSubtitle("Thinking...", 800);
                }
                else if (_nluShowTimer > 0)
                {
                    GTA.UI.Screen.ShowSubtitle(_lastNluResult ?? "", 800);
                }
                else if (_arrivalDisplayTimer > 0)
                {
                    // prompt handles display — skip normal status
                }
                else
                {
                    string status = _waypointManager.GetStatusString(vehicle);
                    if (string.IsNullOrEmpty(status))
                        status = _waypointManager.State == NavigateState.Waiting ? "WAITING" : "Idle";
                    GTA.UI.Screen.ShowSubtitle(
                        $"AI | {status} | {vehicle.Speed * 3.6f:F0}km/h", 1000);
                }
            }

        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.NumPad0) ToggleAutoPilot();
            else if (e.KeyCode == Keys.NumPad1) ToggleDebugOverlay();
            else if (e.KeyCode == Keys.NumPad2) SetDestinationFromMap();
            else if (e.KeyCode == Keys.NumPad3) _showQueue = true;
            else if (e.KeyCode == Keys.NumPad7) ContinueNavigation();
            else if (e.KeyCode == Keys.NumPad8) StopAutopilot();
            else if (e.KeyCode == Keys.NumPad9) ShowNluKeyboard();
            else if (e.KeyCode == Keys.Decimal && _autoPilotEnabled) StopAutopilot();
            e.Handled = true;
        }

        private void OnKeyUp(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.NumPad3) _showQueue = false;
        }

        private void ShowNluKeyboard()
        {
            if (_keyboardActive) return;
            if (!_llamaNav.IsReady)
            {
                GTA.UI.Screen.ShowSubtitle("LLM model not found at scripts/models/", 5000);
                return;
            }
            _keyboardActive = true;
            Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD,
                0, "NAVIGATION", "", "", "", "", "", "", "",
                "e.g. 去机场然后回家", 200);
        }

        public void ToggleDebugOverlay()
        {
            _debugOverlay.Enabled = !_debugOverlay.Enabled;
            GTA.UI.Screen.ShowSubtitle(_debugOverlay.Enabled ? "Debug ON" : "Debug OFF", 2000);
        }

        public void ToggleAutoPilot()
        {
            _autoPilotEnabled = !_autoPilotEnabled;
            if (_autoPilotEnabled)
            {
                var v = Game.Player.Character.CurrentVehicle;
                if (v != null && v.Exists()) _pathNav.Reset(v);
                _tickSinceTaskIssue = 0;
                _progressCheckTicks = 0;
                _lastProgressDist = float.MaxValue;
                _arrivalStopping = false;
                if (_waypointManager.TotalCount > 0 && _waypointManager.State != NavigateState.Driving)
                {
                    _waypointManager.StartNavigation();
                    SetGpsToCurrent();
                }
            }
            else
            {
                _vehicleCtrl.Release();
                Function.Call(Hash.CLEAR_PED_TASKS, Game.Player.Character);
            }
            GTA.UI.Screen.ShowSubtitle(_autoPilotEnabled ? "AutoPilot ON" : "AutoPilot OFF", 2000);
        }

        private void SetGpsToCurrent()
        {
            Vector3 dest = _waypointManager.GetCurrentDestination();
            if (dest != Vector3.Zero)
                Function.Call(Hash.SET_NEW_WAYPOINT, dest.X, dest.Y);
        }

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
                        if (_waypointManager.ContainsNear(wp, 5f))
                        {
                            GTA.UI.Screen.ShowSubtitle("Already in queue", 2000);
                            return;
                        }
                        _waypointManager.AddWaypoint(new Waypoint($"WP{_waypointManager.TotalCount + 1}", wp));
                        // GPS line handled by SetGpsToCurrent() on autopilot start
                        GTA.UI.Screen.ShowSubtitle(
                            $"WP {_waypointManager.TotalCount} added | {_waypointManager.TotalCount} total", 3000);
                        return;
                    }
                }
            }
            catch { }
            GTA.UI.Screen.ShowSubtitle("No map waypoint found", 2000);
        }

        public void ContinueNavigation()
        {
            if (_waypointManager.State != NavigateState.Waiting)
            {
                if (_waypointManager.TotalCount > 0 && _waypointManager.State == NavigateState.Idle)
                {
                    _waypointManager.StartNavigation();
                    if (!_autoPilotEnabled) ToggleAutoPilot();
                }
                return;
            }
            if (_waypointManager.ContinueToNext())
            {
                _tickSinceTaskIssue = 0;
                _progressCheckTicks = 0;
                _lastProgressDist = float.MaxValue;
                _arrivalStopping = false;
                _arrivalDisplayTimer = 0; // reset so status shows again
                SetGpsToCurrent();
                GTA.UI.Screen.ShowSubtitle(
                    $"Next: {_waypointManager.CurrentWaypointName} | {_waypointManager.TotalCount} left", 3000);
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
            _arrivalStopping = false;
            _vehicleCtrl.Release();
            Function.Call(Hash.CLEAR_PED_TASKS, Game.Player.Character);
            GTA.UI.Screen.ShowSubtitle("Stopped — queue cleared", 2000);
        }
    }
}
