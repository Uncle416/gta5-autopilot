using System;
using System.Collections.Generic;
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

        // Core modules (public for cross-module access)
        public VehicleController VehicleController { get; private set; }
        public PathNavigator PathNavigator { get; private set; }
        public EntityDetector EntityDetector { get; private set; }
        public CollisionPredictor CollisionPredictor { get; private set; }
        public LaneKeeper LaneKeeper { get; private set; }
        public SpeedGovernor SpeedGovernor { get; private set; }
        public TrafficLightDetector TrafficLightDetector { get; private set; }
        public IntersectionHandler IntersectionHandler { get; private set; }
        public DecisionEngine DecisionEngine { get; private set; }
        public TelemetryExporter TelemetryExporter { get; private set; }

        // Debug
        private DebugOverlay _debugOverlay;
        private DebugCommands _debugCommands;

        // State
        private bool _autoPilotEnabled;
        private bool _recordingEnabled;
        private PerceptionMode _perceptionMode = PerceptionMode.GameAPI;

        // Phase 2: visual perception receiver
        private VisionDataReceiver _visionReceiver;

        public bool AutoPilotEnabled => _autoPilotEnabled;
        public bool RecordingEnabled => _recordingEnabled;
        public PerceptionMode CurrentPerceptionMode => _perceptionMode;

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
            VehicleController = new VehicleController();
            PathNavigator = new PathNavigator();
            EntityDetector = new EntityDetector();
            CollisionPredictor = new CollisionPredictor();
            LaneKeeper = new LaneKeeper();
            SpeedGovernor = new SpeedGovernor();
            TrafficLightDetector = new TrafficLightDetector();
            IntersectionHandler = new IntersectionHandler();
            DecisionEngine = new DecisionEngine();

            _debugOverlay = new DebugOverlay();
            _debugCommands = new DebugCommands(this);
            TelemetryExporter = new TelemetryExporter();
            _visionReceiver = new VisionDataReceiver();
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
            // Path navigation always uses game API (visual can't do GPS)
            var pathInfo = PathNavigator.GetNextWaypoint(vehicle);

            List<EntityInfo> entities;
            TrafficLightState trafficLight;
            float laneOffset;

            if (_perceptionMode == PerceptionMode.Vision || _perceptionMode == PerceptionMode.Hybrid)
            {
                // Use visual perception from Python pipeline
                var visionData = _visionReceiver.GetLatestData();
                if (visionData != null)
                {
                    entities = visionData.ToEntityInfoList();
                    trafficLight = (TrafficLightState)visionData.TrafficLight;
                    laneOffset = visionData.LaneOffset;
                }
                else
                {
                    // Fallback to game API if no vision data available
                    entities = EntityDetector.ScanSurroundings(vehicle);
                    trafficLight = TrafficLightDetector.GetCurrentState(vehicle, pathInfo);
                    laneOffset = LaneKeeper.GetSteerCorrection(vehicle, pathInfo);
                }

                // Game API navigation is always used (path, collision, speed, intersection)
            }
            else
            {
                entities = EntityDetector.ScanSurroundings(vehicle);
                trafficLight = TrafficLightDetector.GetCurrentState(vehicle, pathInfo);
                laneOffset = LaneKeeper.GetSteerCorrection(vehicle, pathInfo);
            }

            var collisionRisk = CollisionPredictor.EvaluateRisk(vehicle, entities);
            var targetSpeed = SpeedGovernor.GetTargetSpeed(vehicle, pathInfo, entities);
            var intersectionInfo = IntersectionHandler.EvaluateIntersection(vehicle, pathInfo);

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
                IntersectionInfo = intersectionInfo,
                SourceMode = _perceptionMode
            };

            var command = DecisionEngine.Evaluate(sensorData);

            // --- Execution Phase ---
            VehicleController.Execute(vehicle, command);

            // --- Telemetry ---
            if (_recordingEnabled)
            {
                TelemetryExporter.Send(sensorData, command);
            }

            // --- Debug ---
            _debugOverlay.Render(sensorData, command, DecisionEngine.CurrentState);
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
                    PathNavigator.Reset(vehicle);
                }
            }
            else
            {
                VehicleController.Release();
            }

            UI.Notify(_autoPilotEnabled ? "~g~AutoPilot ON" : "~r~AutoPilot OFF");
        }

        public void ToggleRecording()
        {
            _recordingEnabled = !_recordingEnabled;

            if (_recordingEnabled)
            {
                TelemetryExporter.StartRecording();
            }
            else
            {
                TelemetryExporter.StopRecording();
            }

            UI.Notify(_recordingEnabled ? "~y~Recording ON" : "~y~Recording OFF");
        }

        public void SetDestination()
        {
            var waypoint = World.WaypointPosition;
            if (waypoint != Vector3.Zero)
            {
                PathNavigator.SetDestination(waypoint);
                UI.Notify("~b~Destination set to map waypoint");
            }
        }

        public void ToggleDebugOverlay()
        {
            _debugOverlay.Enabled = !_debugOverlay.Enabled;
            UI.Notify(_debugOverlay.Enabled ? "~b~Debug overlay ON" : "~b~Debug overlay OFF");
        }

        public void CyclePerceptionMode()
        {
            switch (_perceptionMode)
            {
                case PerceptionMode.GameAPI:
                    _perceptionMode = PerceptionMode.Vision;
                    UI.Notify("~b~Perception: VISION");
                    break;
                case PerceptionMode.Vision:
                    _perceptionMode = PerceptionMode.Hybrid;
                    UI.Notify("~b~Perception: HYBRID");
                    break;
                case PerceptionMode.Hybrid:
                    _perceptionMode = PerceptionMode.GameAPI;
                    UI.Notify("~b~Perception: GAME API");
                    break;
            }
        }
    }
}
