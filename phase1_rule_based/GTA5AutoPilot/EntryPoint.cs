using System;
using System.Windows.Forms;
using GTA;
using GTA5AutoPilot.Modules;
using GTA5AutoPilot.Debug;
using GTA5AutoPilot.Telemetry;

namespace GTA5AutoPilot
{
    /// <summary>
    /// ScriptHookVDotNet entry point. This class is instantiated by SHVDN
    /// when the game loads scripts from the /scripts/ folder.
    /// </summary>
    public class EntryPoint : Script
    {
        public static EntryPoint Instance { get; private set; }

        // Core modules
        private VehicleController _vehicleController;
        private PathNavigator _pathNavigator;
        private EntityDetector _entityDetector;
        private CollisionPredictor _collisionPredictor;
        private LaneKeeper _laneKeeper;
        private SpeedGovernor _speedGovernor;
        private TrafficLightDetector _trafficLightDetector;
        private IntersectionHandler _intersectionHandler;
        private DecisionEngine _decisionEngine;
        private TelemetryExporter _telemetryExporter;

        // Debug
        private DebugOverlay _debugOverlay;
        private DebugCommands _debugCommands;

        // State
        private bool _autoPilotEnabled;
        private bool _recordingEnabled;

        public bool AutoPilotEnabled => _autoPilotEnabled;
        public bool RecordingEnabled => _recordingEnabled;

        public EntryPoint()
        {
            Instance = this;

            Tick += OnTick;
            KeyDown += OnKeyDown;
            KeyUp += OnKeyUp;
            Interval = 0; // Run every frame

            InitializeModules();
        }

        private void InitializeModules()
        {
            _vehicleController = new VehicleController();
            _pathNavigator = new PathNavigator();
            _entityDetector = new EntityDetector();
            _collisionPredictor = new CollisionPredictor();
            _laneKeeper = new LaneKeeper();
            _speedGovernor = new SpeedGovernor();
            _trafficLightDetector = new TrafficLightDetector();
            _intersectionHandler = new IntersectionHandler();
            _decisionEngine = new DecisionEngine();

            _debugOverlay = new DebugOverlay();
            _debugCommands = new DebugCommands(this);
            _telemetryExporter = new TelemetryExporter();
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (!_autoPilotEnabled)
                return;

            var player = Game.Player;
            var vehicle = player.Character.CurrentVehicle;

            if (vehicle == null || !vehicle.Exists() || vehicle.IsDead)
                return;

            if (player.Character.IsDead)
                return;

            // --- Perception Phase ---
            var pathInfo = _pathNavigator.GetNextWaypoint(vehicle);
            var entities = _entityDetector.ScanSurroundings(vehicle);
            var trafficLight = _trafficLightDetector.GetCurrentState(vehicle, pathInfo);
            var collisionRisk = _collisionPredictor.EvaluateRisk(vehicle, entities);
            var laneOffset = _laneKeeper.GetSteerCorrection(vehicle, pathInfo);
            var targetSpeed = _speedGovernor.GetTargetSpeed(vehicle, pathInfo, entities);
            var intersectionInfo = _intersectionHandler.EvaluateIntersection(vehicle, pathInfo);

            // --- Decision Phase ---
            var sensorData = new SensorData
            {
                Vehicle = vehicle,
                PathInfo = pathInfo,
                NearbyEntities = entities,
                TrafficLightState = trafficLight,
                CollisionRisk = collisionRisk,
                LaneOffset = laneOffset,
                TargetSpeed = targetSpeed,
                IntersectionInfo = intersectionInfo
            };

            var command = _decisionEngine.Evaluate(sensorData);

            // --- Execution Phase ---
            _vehicleController.Execute(vehicle, command);

            // --- Telemetry ---
            if (_recordingEnabled)
            {
                _telemetryExporter.Send(sensorData, command);
            }

            // --- Debug ---
            _debugOverlay.Render(sensorData, command, _decisionEngine.CurrentState);
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            _debugCommands.HandleKeyDown(e);
        }

        private void OnKeyUp(object sender, KeyEventArgs e)
        {
            _debugCommands.HandleKeyUp(e);
        }

        // --- Public API for DebugCommands ---

        public void ToggleAutoPilot()
        {
            _autoPilotEnabled = !_autoPilotEnabled;

            if (_autoPilotEnabled)
            {
                var player = Game.Player;
                var vehicle = player.Character.CurrentVehicle;
                if (vehicle != null && vehicle.Exists())
                {
                    _pathNavigator.Reset(vehicle);
                }
            }
            else
            {
                _vehicleController.Release();
            }

            UI.Notify(_autoPilotEnabled ? "~g~AutoPilot ON" : "~r~AutoPilot OFF");
        }

        public void ToggleRecording()
        {
            _recordingEnabled = !_recordingEnabled;

            if (_recordingEnabled)
            {
                _telemetryExporter.StartRecording();
            }
            else
            {
                _telemetryExporter.StopRecording();
            }

            UI.Notify(_recordingEnabled ? "~y~Recording ON" : "~y~Recording OFF");
        }

        public void SetDestination()
        {
            var waypoint = World.WaypointPosition;
            if (waypoint != Vector3.Zero)
            {
                _pathNavigator.SetDestination(waypoint);
                UI.Notify("~b~Destination set to map waypoint");
            }
        }

        public void ToggleDebugOverlay()
        {
            _debugOverlay.Enabled = !_debugOverlay.Enabled;
            UI.Notify(_debugOverlay.Enabled ? "~b~Debug overlay ON" : "~b~Debug overlay OFF");
        }
    }
}
