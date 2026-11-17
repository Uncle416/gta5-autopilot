using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA5AutoPilot.Modules;
using GTA5AutoPilot.Networking;
using Newtonsoft.Json.Linq;

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

        private WaypointManager _waypointManager;
        private TcpCommandServer _tcpServer;
        private LlamaNavigator _llamaNav;

        private bool _autoPilotEnabled;
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

        // Script clock in milliseconds. Advances only while OnTick runs (so not while the
        // game is paused or the map is open) and is independent of frame rate.
        private readonly System.Diagnostics.Stopwatch _wall = System.Diagnostics.Stopwatch.StartNew();
        private long _lastWallMs;
        private long _clockMs;

        // Drive task / progress watchdog
        private bool _needsIssue;          // issue the drive task on the next driving tick
        private long _lastIssueMs;
        private long _windowStartMs;
        private float _windowStartDist;
        private Vector3 _windowStartPos;
        private long _stationaryMs;        // consecutive time without real movement
        private int _legRetries;

        // Arrival braking
        private bool _arrivalStopping;
        private long _arrivalStopStartMs;
        private Waypoint _arrivedWaypoint;
        private bool _arrivedNear;
        private float _arrivedDist;

        // Mission bookkeeping (protocol v2)
        private string _missionId;
        private int _localMissionCounter;
        private int _skippedCount;
        private string _blockedReason;
        private long _eventSeq;
        private long _stateSeq;
        private long _lastHeartbeatMs;
        private bool _tcpErrorShown;

        // Auto-detect map blip after closing map
        private Vector3 _lastKnownBlipPos;
        private bool _hasOwnGps;
        private Vector3 _ownGpsPos;

        // Runtime tunable (Num4/Num5)
        private float _driverAggression = 0.15f;
        private float _drivingSpeed = 25f;
        private int _suppressStatusTicks;

        private const string LogPath = "scripts/GTA5AutoPilot.log";

        public EntryPoint()
        {
            Instance = this;
            Tick += OnTick;
            KeyDown += OnKeyDown;
            KeyUp += OnKeyUp;
            Aborted += OnAborted;
            Interval = 0;

            _vehicleCtrl = new VehicleController();
            _pathNav = new PathNavigator();
            _collisionPredictor = new CollisionPredictor();
            _entityDetector = new EntityDetector();

            _waypointManager = new WaypointManager();

            // The server never calls into the game; requests are drained in OnTick.
            _tcpServer = new TcpCommandServer(Configuration.CommandServerPort);
            _tcpServer.Start();

            _llamaNav = new LlamaNavigator(Configuration.LlamaModelPath);

            GTA.UI.Screen.ShowSubtitle(
                $"GTA5AutoPilot ready | LLM:{(_llamaNav.IsReady ? "on" : "off")}", 3000);
        }

        private void OnAborted(object sender, EventArgs e)
        {
            // Free the port when SHVDN reloads scripts.
            try { _tcpServer?.Stop(); } catch { }
        }

        // =====================================================================
        //  Tick
        // =====================================================================

        private void OnTick(object sender, EventArgs e)
        {
            AdvanceClock();

            // ---- TCP: execute queued requests on the script thread ----
            DrainTcpRequests();
            if (!_tcpErrorShown && _tcpServer.LastError != null)
            {
                _tcpErrorShown = true;
                GTA.UI.Screen.ShowSubtitle("TCP server error: " + _tcpServer.LastError, 5000);
            }

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

            // ---- NLU completion (embedded LlamaSharp path) ----
            if (_nluProcessing && _pendingNluTask != null && _pendingNluTask.IsCompleted)
            {
                var finished = _pendingNluTask;
                _pendingNluTask = null;
                _nluProcessing = false;

                List<Waypoint> waypoints = null;
                string response;
                try
                {
                    var result = finished.Result;
                    waypoints = result.Item1;
                    response = result.Item2;
                }
                catch (Exception ex)
                {
                    response = "LLM error: " + ex.Message;
                }

                _lastNluResult = response;
                _nluShowTimer = 600; // show result for ~10 seconds
                _suppressStatusTicks = 120;
                if (waypoints != null && waypoints.Count > 0)
                {
                    if (MissionActive)
                    {
                        EndDrivingTask();
                        Emit("cancelled", new JObject { ["reason"] = "replaced" });
                    }
                    _missionId = NewLocalMissionId();
                    _skippedCount = 0;
                    _waypointManager.LoadWaypoints(waypoints);
                    Emit("route_loaded", new JObject { ["mode"] = "replace", ["queue"] = QueueJson() });
                    _waypointManager.StartNavigation();
                    BeginLeg(false);
                }
            }

            // Timer for NLU display
            if (_nluShowTimer > 0) _nluShowTimer--;

            // Auto-detect map blip after closing map (OnTick suspended while map open)
            if (_statusCounter % 10 == 0)
            {
                Vector3 blip = ReadMapBlipPos();
                if (blip != Vector3.Zero && WaypointManager.Distance2D(blip, _lastKnownBlipPos) > 3f)
                {
                    _lastKnownBlipPos = blip;
                    // Ignore the GPS marker this script placed itself (its Z differs from the stored stop).
                    bool isOwn = _hasOwnGps && WaypointManager.Distance2D(blip, _ownGpsPos) < 10f;
                    if (!isOwn && !_waypointManager.ContainsNear(blip, 5f))
                    {
                        _waypointManager.AddWaypoint(new Waypoint($"WP{_waypointManager.TotalCount + 1}", blip));
                        SetGpsToCurrent();
                        Emit("route_loaded", new JObject { ["mode"] = "map", ["queue"] = QueueJson() });
                        GTA.UI.Screen.ShowSubtitle(
                            $"WP {_waypointManager.TotalCount} added | {_waypointManager.TotalCount} total", 3000);
                    }
                }
                if (blip == Vector3.Zero)
                    _lastKnownBlipPos = Vector3.Zero;
            }

            // ---- State heartbeat for TCP clients ----
            if (_tcpServer.ClientCount > 0 && _clockMs - _lastHeartbeatMs >= Configuration.StateHeartbeatMs)
            {
                _lastHeartbeatMs = _clockMs;
                _tcpServer.Broadcast(Serialize(BuildState()));
            }

            if (!_autoPilotEnabled)
            {
                _statusCounter++;
                return;
            }

            var driver = Game.Player.Character;
            Vehicle vehicle = (driver != null && driver.Exists()) ? driver.CurrentVehicle : null;

            // ---- Preconditions: report instead of silently idling ----
            bool needsVehicle = _waypointManager.State == NavigateState.Driving || _arrivalStopping;
            string blocked = null;
            if (needsVehicle)
            {
                if (driver == null || !driver.Exists() || driver.IsDead) blocked = "player_dead";
                else if (vehicle == null || !vehicle.Exists()) blocked = "no_vehicle";
                else if (vehicle.IsDead) blocked = "vehicle_destroyed";
            }
            if (blocked != _blockedReason)
            {
                _blockedReason = blocked;
                if (blocked != null)
                {
                    Emit("blocked", new JObject { ["reason"] = blocked });
                    GTA.UI.Screen.ShowSubtitle("AutoPilot paused: " + blocked, 4000);
                    _suppressStatusTicks = 120;
                }
                else
                {
                    Emit("unblocked");
                    _needsIssue = true;   // the old native task is gone; issue a fresh one
                    _stationaryMs = 0;
                }
            }
            if (blocked != null) { _statusCounter++; return; }

            if (driver == null || !driver.Exists() || driver.IsDead) return;
            if (vehicle == null || !vehicle.Exists() || vehicle.IsDead) return;

            // ---- Arrival braking: keep braking until fully stopped ----
            if (_arrivalStopping)
            {
                bool timedOut = _clockMs - _arrivalStopStartMs > Configuration.ArrivalBrakeTimeoutMs;
                if (vehicle.Speed > 0.5f && !timedOut)
                {
                    // Loop brake: re-apply temp action while still moving
                    Function.Call(Hash.TASK_VEHICLE_TEMP_ACTION, driver, vehicle, 1, 500);
                }
                else
                {
                    // Stopped (or gave up waiting) — process arrival
                    Function.Call(Hash.SET_VEHICLE_FORWARD_SPEED, vehicle, 0f);
                    _arrivalStopping = false;
                    _vehicleCtrl.Release();
                    _stationaryMs = 0;
                    FinishArrival();
                }
                return; // skip driving logic while braking
            }

            // ---- Driving ----
            if (_waypointManager.State == NavigateState.Driving)
            {
                Waypoint target = _waypointManager.Current;
                if (target != null)
                {
                    Vector3 dest = target.Position;
                    Vector3 pos = vehicle.Position;
                    float distToDest = WaypointManager.Distance2D(pos, dest);

                    // Issue task once, reissue only if genuinely not progressing.
                    // NPCs drive well because their task isn't constantly reset.
                    bool shouldIssue = false;
                    if (_needsIssue)
                    {
                        _needsIssue = false;
                        shouldIssue = true;
                        StartProgressWindow(pos, distToDest);
                    }
                    else if (_clockMs - _windowStartMs >= Configuration.ProgressCheckMs)
                    {
                        long windowMs = _clockMs - _windowStartMs;
                        float progress = _windowStartDist - distToDest;           // positive = closer
                        float moved = WaypointManager.Distance2D(pos, _windowStartPos);
                        bool makingProgress = progress > 3f;
                        bool standingStill = vehicle.Speed < 1f;                  // red light / traffic, or wedged
                        bool safetyTimeout = _clockMs - _lastIssueMs > Configuration.TaskSafetyReissueMs;

                        if (!makingProgress && !standingStill) shouldIssue = true;
                        else if (safetyTimeout) shouldIssue = true;   // unchanged from the verified baseline

                        if (moved < Configuration.StationaryMoveThreshold) _stationaryMs += windowMs;
                        else _stationaryMs = 0;

                        StartProgressWindow(pos, distToDest);
                    }

                    if (shouldIssue) IssueDriveTask(driver, vehicle, dest);

                    bool reached = distToDest < Configuration.DestinationArrivalRadius;
                    bool nearAndParked = distToDest < Configuration.NearDestinationRadius
                                         && _stationaryMs >= Configuration.NearStationaryArriveMs;

                    if (reached || nearAndParked)
                    {
                        _arrivedWaypoint = target;
                        _arrivedNear = !reached;
                        _arrivedDist = distToDest;
                        _arrivalStopping = true;
                        _arrivalStopStartMs = _clockMs;
                        Function.Call(Hash.CLEAR_PED_TASKS, driver);
                    }
                    else if (_stationaryMs >= Configuration.StuckStationaryMs)
                    {
                        EnterStuck(driver, target, distToDest);
                    }
                }
            }

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

            // ---- Status (suppressed while special subtitle is active) ----
            _statusCounter++;
            if (_suppressStatusTicks > 0) _suppressStatusTicks--;
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
                    GTA.UI.Screen.ShowSubtitle(_lastNluResult ?? "", 2000);
                }
                else if (_arrivalDisplayTimer > 0 || _suppressStatusTicks > 0)
                {
                    // suppress normal status
                }
                else if (_waypointManager.State == NavigateState.Stuck)
                {
                    GTA.UI.Screen.ShowSubtitle(
                        $"STUCK -> {_waypointManager.CurrentWaypointName} | Num1 retry / Num2 stop", 1000);
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

        private void AdvanceClock()
        {
            long now = _wall.ElapsedMilliseconds;
            long dt = now - _lastWallMs;
            _lastWallMs = now;
            if (dt < 0) dt = 0;
            if (dt > 250) dt = 250; // game was paused / map open / long hitch: do not count it
            _clockMs += dt;
        }

        private void StartProgressWindow(Vector3 pos, float distToDest)
        {
            _windowStartMs = _clockMs;
            _windowStartPos = pos;
            _windowStartDist = distToDest;
        }

        private void IssueDriveTask(Ped driver, Vehicle vehicle, Vector3 dest)
        {
            Function.Call(Hash.SET_DRIVER_ABILITY, driver, 1.0f);
            Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, driver, _driverAggression);
            Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                driver, vehicle,
                dest.X, dest.Y, dest.Z,
                _drivingSpeed,
                Configuration.AiDrivingFlags,
                Configuration.DestinationArrivalRadius);
            _lastIssueMs = _clockMs;
        }

        /// <summary>The car is stopped at (or near) the stop: update the queue and tell everyone.</summary>
        private void FinishArrival()
        {
            Waypoint arrived = _arrivedWaypoint;
            _arrivedWaypoint = null;
            bool isFinal = _waypointManager.OnArrival();
            Waypoint next = isFinal ? null : _waypointManager.Current;

            var data = new JObject
            {
                ["stop"] = StopJson(arrived),
                ["near"] = _arrivedNear,
                ["dist_m"] = Round1(_arrivedDist),
                ["final"] = isFinal,
                ["next"] = next != null ? StopJson(next) : null,
            };
            Emit("arrived", data);

            string name = arrived != null ? arrived.Name : "";
            string note = _arrivedNear ? $" (near, {_arrivedDist:F0}m)" : "";
            if (isFinal)
            {
                _autoPilotEnabled = false;
                Emit("mission_completed", new JObject { ["skipped"] = _skippedCount });
                GTA.UI.Screen.ShowSubtitle($"Arrived: {name}{note}", 5000);
            }
            else
            {
                GTA.UI.Screen.ShowSubtitle(_waypointManager.GetCurrentPrompt() + note, 5000);
                _arrivalDisplayTimer = 300;
            }
        }

        private void EnterStuck(Ped driver, Waypoint target, float distToDest)
        {
            if (!_waypointManager.MarkStuck()) return;
            Function.Call(Hash.CLEAR_PED_TASKS, driver);
            _vehicleCtrl.Release();
            Emit("stuck", new JObject
            {
                ["stop"] = StopJson(target),
                ["dist_m"] = Round1(distToDest),
                ["stationary_ms"] = _stationaryMs,
                ["retries"] = _legRetries,
            });
            _stationaryMs = 0;
            GTA.UI.Screen.ShowSubtitle($"Stuck on the way to {target.Name}. Num1 retry / Num2 stop", 5000);
            _suppressStatusTicks = 150;
        }

        // =====================================================================
        //  Navigation actions (shared by hotkeys and TCP). Script thread only.
        // =====================================================================

        private bool MissionActive =>
            _waypointManager.State != NavigateState.Idle || _arrivalStopping;

        private string NewLocalMissionId()
        {
            _localMissionCounter++;
            return "local-" + _localMissionCounter;
        }

        /// <summary>Why the player cannot be driven right now, or null if fine.</summary>
        private string DriveBlocker()
        {
            var ped = Game.Player.Character;
            if (ped == null || !ped.Exists() || ped.IsDead) return "player_dead";
            if (!ped.IsInVehicle()) return "no_vehicle";
            return null;
        }

        /// <summary>Mark the start of a leg towards the current waypoint.</summary>
        private void BeginLeg(bool isRetry)
        {
            if (!isRetry) _legRetries = 0;
            _needsIssue = true;
            _stationaryMs = 0;
            _arrivalStopping = false;
            _arrivalDisplayTimer = 0; // reset so status shows again
            SetGpsToCurrent();
            Emit("leg_started", new JObject
            {
                ["stop"] = StopJson(_waypointManager.Current),
                ["retry"] = _legRetries,
                ["remaining"] = _waypointManager.TotalCount,
            });
        }

        /// <summary>Stop the native driving task if this script owns one.</summary>
        private void EndDrivingTask()
        {
            bool owning = _autoPilotEnabled &&
                          (_waypointManager.State == NavigateState.Driving || _arrivalStopping);
            _arrivalStopping = false;
            _vehicleCtrl.Release();
            if (!owning) return;
            var ped = Game.Player.Character;
            if (ped != null && ped.Exists())
                Function.Call(Hash.CLEAR_PED_TASKS, ped);
        }

        /// <summary>Start driving the loaded queue from its first stop.</summary>
        private bool StartMission(out string error)
        {
            error = null;
            if (_waypointManager.TotalCount == 0) { error = "empty_queue"; return false; }
            error = DriveBlocker();
            if (error != null) return false;

            if (_missionId == null) _missionId = NewLocalMissionId();
            var v = Game.Player.Character.CurrentVehicle;
            if (v != null && v.Exists()) _pathNav.Reset(v);
            _autoPilotEnabled = true;
            _blockedReason = null;
            _waypointManager.StartNavigation();
            BeginLeg(false);
            return true;
        }

        public void ToggleAutoPilot()
        {
            if (!_autoPilotEnabled)
            {
                var state = _waypointManager.State;
                if (_waypointManager.TotalCount > 0 && state != NavigateState.Driving)
                {
                    // Idle / Waiting / Stuck with a queue: (re)start from the first remaining stop.
                    if (!StartMission(out string error))
                    {
                        GTA.UI.Screen.ShowSubtitle("Cannot start: " + error, 3000);
                        _suppressStatusTicks = 90;
                        return;
                    }
                }
                else
                {
                    _autoPilotEnabled = true;
                    if (state == NavigateState.Driving)
                    {
                        // Resume a paused leg.
                        var v = Game.Player.Character.CurrentVehicle;
                        if (v != null && v.Exists()) _pathNav.Reset(v);
                        _needsIssue = true;
                        _stationaryMs = 0;
                        _arrivalStopping = false;
                        SetGpsToCurrent();
                        Emit("resumed");
                    }
                }
            }
            else
            {
                bool wasActive = MissionActive;
                EndDrivingTask();
                _autoPilotEnabled = false;
                _blockedReason = null;
                if (wasActive) Emit("paused");
            }
            GTA.UI.Screen.ShowSubtitle(_autoPilotEnabled ? "AutoPilot ON" : "AutoPilot OFF", 2000);
        }

        /// <summary>Num1: continue after a stop, retry when stuck, or start an idle queue.</summary>
        public void ContinueNavigation()
        {
            var state = _waypointManager.State;
            if (state == NavigateState.Stuck)
            {
                RetryLeg(out _);
                return;
            }
            if (state == NavigateState.Idle)
            {
                if (_waypointManager.TotalCount > 0 && !StartMission(out string error))
                    GTA.UI.Screen.ShowSubtitle("Cannot start: " + error, 3000);
                return;
            }
            if (state != NavigateState.Waiting) return;

            if (_waypointManager.ContinueToNext())
            {
                _autoPilotEnabled = true;
                BeginLeg(false);
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

        private bool RetryLeg(out string error)
        {
            error = null;
            if (_waypointManager.State != NavigateState.Stuck) { error = "invalid_state"; return false; }
            error = DriveBlocker();
            if (error != null) return false;
            if (!_waypointManager.Retry()) { error = "invalid_state"; return false; }
            _legRetries++;
            _autoPilotEnabled = true;
            BeginLeg(true);
            return true;
        }

        /// <summary>Give up on the current target and move on. Never reported as an arrival.</summary>
        private bool SkipCurrentStop(out string error)
        {
            error = null;
            if (_waypointManager.TotalCount == 0) { error = "empty_queue"; return false; }

            bool wasActive = MissionActive;
            EndDrivingTask();
            Waypoint skipped = _waypointManager.SkipCurrent();
            _skippedCount++;
            Emit("skipped", new JObject
            {
                ["stop"] = StopJson(skipped),
                ["remaining"] = _waypointManager.TotalCount,
            });

            if (_waypointManager.State == NavigateState.Driving)
            {
                BeginLeg(false);
            }
            else if (wasActive && _waypointManager.TotalCount == 0)
            {
                _autoPilotEnabled = false;
                Emit("mission_completed", new JObject { ["skipped"] = _skippedCount });
                GTA.UI.Screen.ShowSubtitle("Navigation ended (last stop skipped)", 3000);
            }
            return true;
        }

        /// <summary>Num2 / decimal key. Does nothing when no mission is running (a merely loaded queue is kept).</summary>
        public void StopAutopilot()
        {
            CancelPendingNlu();
            if (!_autoPilotEnabled && !MissionActive) return;
            StopAll("hotkey");
        }

        /// <summary>Stop the car's task and clear the queue. Idempotent.</summary>
        private void StopAll(string reason)
        {
            CancelPendingNlu();
            bool hadSomething = MissionActive || _waypointManager.TotalCount > 0;
            EndDrivingTask();
            _waypointManager.StopNavigation();
            _autoPilotEnabled = false;
            _blockedReason = null;
            _needsIssue = false;
            _stationaryMs = 0;
            if (hadSomething) Emit("cancelled", new JObject { ["reason"] = reason });
            GTA.UI.Screen.ShowSubtitle("Stopped — queue cleared", 2000);
        }

        /// <summary>A parse that finishes after a stop must not restart navigation.</summary>
        private void CancelPendingNlu()
        {
            _pendingNluTask = null;
            _nluProcessing = false;
        }

        // =====================================================================
        //  TCP protocol v2 (docs/PROTOCOL.md). Script thread only.
        // =====================================================================

        private void DrainTcpRequests()
        {
            for (int i = 0; i < 20; i++)
            {
                if (!_tcpServer.TryDequeue(out ClientRequest request)) break;
                JToken id = null;
                try
                {
                    if (request.Error != null || request.Json == null)
                    {
                        Reply(request.ClientId, null, "bad_json", request.Error ?? "not a JSON object");
                        continue;
                    }
                    id = request.Json["id"];
                    HandleRequest(request.ClientId, id, request.Json);
                }
                catch (Exception ex)
                {
                    Reply(request.ClientId, id, "internal_error", ex.Message);
                }
            }
        }

        private void HandleRequest(int clientId, JToken id, JObject obj)
        {
            string type = Str(obj["type"]);
            string reqMission = Str(obj["mission_id"]);
            string error;

            switch (type)
            {
                case "hello":
                case "get_state":
                    ReplyOk(clientId, id, new JObject { ["state"] = BuildState() });
                    return;

                case "set_route":
                    HandleSetRoute(clientId, id, obj, false);
                    return;

                case "set_waypoints": // legacy v1
                    HandleSetRoute(clientId, id, obj, true);
                    return;

                case "command": // legacy v1
                {
                    string action = Str(obj["action"]);
                    if (action == "continue") { ContinueNavigation(); ReplyOk(clientId, id, null); }
                    else if (action == "stop") { StopAll("stop"); ReplyOk(clientId, id, null); }
                    else Reply(clientId, id, "unknown_type", "unknown action");
                    return;
                }
            }

            // Control commands: refuse ones addressed to a mission that is no longer current.
            if (type == "start" || type == "continue" || type == "retry" || type == "skip")
            {
                if (reqMission != null && reqMission != _missionId)
                {
                    Reply(clientId, id, "stale_mission", "current mission is " + (_missionId ?? "none"));
                    return;
                }
            }

            switch (type)
            {
                case "start":
                    if (_waypointManager.State == NavigateState.Idle)
                    {
                        if (StartMission(out error)) ReplyOk(clientId, id, null);
                        else Reply(clientId, id, error, "cannot start");
                    }
                    else if (!_autoPilotEnabled)
                    {
                        // Paused by hotkey: resume.
                        error = DriveBlocker();
                        if (error != null) { Reply(clientId, id, error, "cannot resume"); return; }
                        ToggleAutoPilot();
                        ReplyOk(clientId, id, null);
                    }
                    else Reply(clientId, id, "invalid_state", "already running");
                    return;

                case "continue":
                    if (_waypointManager.State != NavigateState.Waiting)
                    {
                        Reply(clientId, id, "invalid_state", "not waiting at a stop");
                        return;
                    }
                    error = DriveBlocker();
                    if (error != null) { Reply(clientId, id, error, "cannot continue"); return; }
                    ContinueNavigation();
                    ReplyOk(clientId, id, null);
                    return;

                case "retry":
                    if (RetryLeg(out error)) ReplyOk(clientId, id, null);
                    else Reply(clientId, id, error, "cannot retry");
                    return;

                case "skip":
                    if (SkipCurrentStop(out error)) ReplyOk(clientId, id, null);
                    else Reply(clientId, id, error, "cannot skip");
                    return;

                case "stop":
                    StopAll("stop");
                    ReplyOk(clientId, id, null);
                    return;

                default:
                    Reply(clientId, id, "unknown_type", "unknown type: " + (type ?? "(missing)"));
                    return;
            }
        }

        private void HandleSetRoute(int clientId, JToken id, JObject obj, bool legacy)
        {
            string mode = legacy ? "replace" : (Str(obj["mode"]) ?? "replace");
            if (mode != "replace" && mode != "append")
            {
                Reply(clientId, id, "invalid_route", "mode must be replace or append");
                return;
            }

            var array = obj[legacy ? "waypoints" : "stops"] as JArray;
            if (array == null || array.Count == 0 || array.Count > Configuration.MaxRouteStops)
            {
                Reply(clientId, id, "invalid_route", "stops must contain 1.." + Configuration.MaxRouteStops + " items");
                return;
            }

            // Validate everything before touching the queue.
            var stops = new List<Waypoint>();
            for (int i = 0; i < array.Count; i++)
            {
                string problem = ParseStop(array[i], out Waypoint wp);
                if (problem != null)
                {
                    Reply(clientId, id, "invalid_stop", "stops[" + i + "]: " + problem);
                    return;
                }
                stops.Add(wp);
            }

            string reqMission = Str(obj["mission_id"]);
            JToken autoToken = obj["autostart"];
            bool autostart = !legacy && autoToken != null && autoToken.Type == JTokenType.Boolean && autoToken.Value<bool>();

            if (mode == "append")
            {
                if (reqMission != null && _missionId != null && reqMission != _missionId)
                {
                    Reply(clientId, id, "stale_mission", "current mission is " + _missionId);
                    return;
                }
                if (_waypointManager.TotalCount + stops.Count > Configuration.MaxRouteStops)
                {
                    Reply(clientId, id, "invalid_route", "queue would exceed " + Configuration.MaxRouteStops + " stops");
                    return;
                }
                bool willStart = autostart && _waypointManager.State == NavigateState.Idle;
                if (willStart)
                {
                    string blocker = DriveBlocker();
                    if (blocker != null) { Reply(clientId, id, blocker, "cannot autostart; queue unchanged"); return; }
                }

                if (_missionId == null) _missionId = reqMission ?? NewLocalMissionId();
                _waypointManager.AppendWaypoints(stops);
                Emit("route_loaded", new JObject { ["mode"] = "append", ["queue"] = QueueJson() });
                if (willStart) StartMission(out _);
                ReplyOk(clientId, id, null);
                return;
            }

            // replace
            if (autostart)
            {
                string blocker = DriveBlocker();
                if (blocker != null) { Reply(clientId, id, blocker, "cannot autostart; queue unchanged"); return; }
            }

            CancelPendingNlu();
            if (MissionActive)
            {
                EndDrivingTask();
                Emit("cancelled", new JObject { ["reason"] = "replaced" });
            }
            _autoPilotEnabled = false;
            _blockedReason = null;
            _missionId = reqMission ?? NewLocalMissionId();
            _skippedCount = 0;
            _waypointManager.LoadWaypoints(stops);
            Emit("route_loaded", new JObject { ["mode"] = "replace", ["queue"] = QueueJson() });
            GTA.UI.Screen.ShowSubtitle($"Loaded {stops.Count} waypoints via TCP", 4000);
            _suppressStatusTicks = 120;

            if (autostart) StartMission(out _);
            else SetGpsToCurrent();
            ReplyOk(clientId, id, null);
        }

        /// <summary>Returns null and a waypoint on success, or a description of what is wrong.</summary>
        private static string ParseStop(JToken token, out Waypoint wp)
        {
            wp = null;
            var item = token as JObject;
            if (item == null) return "not an object";

            if (!TryNumber(item["x"], out float x)) return "x missing or not a number";
            if (!TryNumber(item["y"], out float y)) return "y missing or not a number";
            if (!TryNumber(item["z"], out float z)) return "z missing or not a number";
            if (x < -4500f || x > 5000f) return "x out of range";
            if (y < -4500f || y > 8500f) return "y out of range";
            if (z < -200f || z > 1500f) return "z out of range";

            string name = Str(item["name"]);
            if (string.IsNullOrWhiteSpace(name)) name = "Unknown";
            if (name.Length > 80) name = name.Substring(0, 80);
            wp = new Waypoint(Str(item["id"]), name, new Vector3(x, y, z));
            return null;
        }

        private static bool TryNumber(JToken token, out float value)
        {
            value = 0f;
            if (token == null) return false;
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float) return false;
            value = token.Value<float>();
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>String value of a scalar token; null for missing, null, objects and arrays.</summary>
        private static string Str(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (!(token is JValue)) return null;
            return token.ToString();
        }

        private void ReplyOk(int clientId, JToken id, JObject extra)
        {
            var ack = extra ?? new JObject();
            ack["type"] = "ack";
            ack["id"] = id != null ? id.DeepClone() : null;
            ack["ok"] = true;
            ack["mission_id"] = _missionId;
            ack["nav"] = NavName(_waypointManager.State);
            _tcpServer.Send(clientId, Serialize(ack));
        }

        private void Reply(int clientId, JToken id, string error, string message)
        {
            var ack = new JObject
            {
                ["type"] = "ack",
                ["id"] = id != null ? id.DeepClone() : null,
                ["ok"] = false,
                ["error"] = error,
                ["message"] = message,
                ["mission_id"] = _missionId,
                ["nav"] = NavName(_waypointManager.State),
            };
            _tcpServer.Send(clientId, Serialize(ack));
        }

        private void Emit(string name, JObject data = null)
        {
            var ev = data ?? new JObject();
            ev["type"] = "event";
            ev["event"] = name;
            ev["seq"] = ++_eventSeq;
            ev["clock_ms"] = _clockMs;
            ev["mission_id"] = _missionId;
            string line = Serialize(ev);
            _tcpServer.Broadcast(line);
            try
            {
                File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + line + "\n");
            }
            catch { /* logging is best-effort */ }
        }

        private JObject BuildState()
        {
            var state = new JObject
            {
                ["type"] = "state",
                ["seq"] = ++_stateSeq,
                ["clock_ms"] = _clockMs,
                ["nav"] = NavName(_waypointManager.State),
                ["autopilot"] = _autoPilotEnabled,
                ["braking"] = _arrivalStopping,
                ["mission_id"] = _missionId,
                ["blocked"] = _blockedReason,
                ["in_vehicle"] = false,
                ["pos"] = null,
                ["speed_mps"] = 0,
                ["current"] = null,
                ["queue"] = QueueJson(),
                ["style"] = _driverAggression,
            };

            try
            {
                var ped = Game.Player.Character;
                if (ped != null && ped.Exists())
                {
                    Vehicle v = ped.CurrentVehicle;
                    bool inVehicle = v != null && v.Exists();
                    Vector3 p = inVehicle ? v.Position : ped.Position;
                    state["in_vehicle"] = inVehicle;
                    state["pos"] = new JObject { ["x"] = Round1(p.X), ["y"] = Round1(p.Y), ["z"] = Round1(p.Z) };
                    if (inVehicle) state["speed_mps"] = Round1(v.Speed);

                    Waypoint cur = _waypointManager.Current;
                    if (cur != null)
                    {
                        JObject c = StopJson(cur);
                        c["dist_m"] = Round1(WaypointManager.Distance2D(p, cur.Position));
                        state["current"] = c;
                    }
                }
            }
            catch { /* state is best-effort */ }
            return state;
        }

        private JArray QueueJson()
        {
            var arr = new JArray();
            foreach (var wp in _waypointManager.Waypoints) arr.Add(StopJson(wp));
            return arr;
        }

        private static JObject StopJson(Waypoint wp)
        {
            if (wp == null) return new JObject { ["id"] = null, ["name"] = "" };
            return new JObject { ["id"] = wp.Id, ["name"] = wp.Name };
        }

        private static string NavName(NavigateState state)
        {
            switch (state)
            {
                case NavigateState.Driving: return "driving";
                case NavigateState.Waiting: return "waiting";
                case NavigateState.Stuck: return "stuck";
                default: return "idle";
            }
        }

        private static double Round1(float value)
        {
            return System.Math.Round(value, 1);
        }

        private static string Serialize(JObject obj)
        {
            return obj.ToString(Newtonsoft.Json.Formatting.None);
        }

        // =====================================================================
        //  Input
        // =====================================================================

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.NumPad0) ToggleAutoPilot();
            else if (e.KeyCode == Keys.NumPad1) ContinueNavigation();
            else if (e.KeyCode == Keys.NumPad2) StopAutopilot();
            else if (e.KeyCode == Keys.NumPad3) _showQueue = true;
            else if (e.KeyCode == Keys.NumPad4) AdjustAggression(-0.05f);
            else if (e.KeyCode == Keys.NumPad5) AdjustAggression(+0.05f);
            else if (e.KeyCode == Keys.NumPad6) ShowNluKeyboard();
            else if (e.KeyCode == Keys.Decimal && _autoPilotEnabled) StopAutopilot();
            e.Handled = true;
        }

        private void OnKeyUp(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.NumPad3) _showQueue = false;
        }

        private static readonly float[] AggressionPresets = { 0.0f, 0.1f, 0.15f, 0.2f };
        private static readonly string[] AggressionNames = { "Sloth", "Base", "Aggressive", "Mad Max" };
        private int _aggressionIndex = 2; // start at 0.15

        private void AdjustAggression(float delta)
        {
            _aggressionIndex = (_aggressionIndex + (delta > 0 ? 1 : 3)) % 4;
            _driverAggression = AggressionPresets[_aggressionIndex];
            var driver = Game.Player.Character;
            if (driver != null && driver.Exists() && driver.IsInVehicle())
                Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, driver, _driverAggression);
            GTA.UI.Screen.ShowSubtitle(
                $"Driving Style: {AggressionNames[_aggressionIndex]} ({_driverAggression:F2})", 2000);
            _suppressStatusTicks = 90; // ~1.5s
        }

        private void ShowNluKeyboard()
        {
            if (_keyboardActive) return;
            if (!_llamaNav.IsReady)
            {
                GTA.UI.Screen.ShowSubtitle("LLM model not found at scripts/models/", 5000);
                _suppressStatusTicks = 120;
                return;
            }
            _keyboardActive = true;
            Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD,
                0, "NAVIGATION", "", "", "", "", "", "", "",
                "e.g. 去机场然后回家", 200);
        }

        private void SetGpsToCurrent()
        {
            Vector3 dest = _waypointManager.GetCurrentDestination();
            // Fallback: first queued waypoint when not yet driving
            if (dest == Vector3.Zero && _waypointManager.TotalCount > 0)
                dest = _waypointManager.Waypoints[0].Position;
            if (dest != Vector3.Zero)
            {
                Function.Call(Hash.SET_NEW_WAYPOINT, dest.X, dest.Y);
                _hasOwnGps = true;
                _ownGpsPos = dest;
            }
        }

        private Vector3 ReadMapBlipPos()
        {
            try
            {
                int id = Function.Call<int>(Hash.GET_FIRST_BLIP_INFO_ID, 8);
                if (Function.Call<bool>(Hash.DOES_BLIP_EXIST, id))
                    return Function.Call<Vector3>(Hash.GET_BLIP_INFO_ID_COORD, id);
            }
            catch { }
            return Vector3.Zero;
        }
    }
}
