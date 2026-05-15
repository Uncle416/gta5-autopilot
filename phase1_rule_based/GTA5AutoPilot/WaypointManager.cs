using System.Collections.Generic;
using GTA;
using GTA.Math;

namespace GTA5AutoPilot
{
    public class Waypoint
    {
        public string Name;
        public Vector3 Position;

        public Waypoint(string name, Vector3 pos)
        {
            Name = name;
            Position = pos;
        }
    }

    public enum NavigateState
    {
        Idle,      // No navigation task
        Driving,   // Driving to current waypoint
        Arrived,   // Reached current waypoint
        Waiting,   // Waiting for user confirmation to continue
    }

    public class WaypointManager
    {
        private readonly List<Waypoint> _waypoints = new List<Waypoint>();
        private int _currentIndex = -1;

        public NavigateState State { get; private set; } = NavigateState.Idle;
        public int CurrentIndex => _currentIndex;
        public int TotalCount => _waypoints.Count;
        public string CurrentWaypointName =>
            (_currentIndex >= 0 && _currentIndex < _waypoints.Count)
                ? _waypoints[_currentIndex].Name : "";
        public bool IsLastWaypoint => _currentIndex >= _waypoints.Count - 1;

        public void LoadWaypoints(List<Waypoint> waypoints)
        {
            _waypoints.Clear();
            _waypoints.AddRange(waypoints);
            _currentIndex = -1;
            State = NavigateState.Idle;
        }

        public bool StartNavigation()
        {
            if (_waypoints.Count == 0) return false;
            _currentIndex = 0;
            State = NavigateState.Driving;
            return true;
        }

        public Vector3 GetCurrentDestination()
        {
            if (_currentIndex < 0 || _currentIndex >= _waypoints.Count)
                return Vector3.Zero;
            return _waypoints[_currentIndex].Position;
        }

        /// <summary>Called when vehicle reaches current waypoint.</summary>
        /// <returns>true if this is the final destination</returns>
        public bool OnArrival()
        {
            State = NavigateState.Arrived;
            if (IsLastWaypoint)
            {
                State = NavigateState.Idle;
                _currentIndex = -1;
                return true; // final destination
            }
            State = NavigateState.Waiting;
            return false; // more waypoints ahead
        }

        public bool ContinueToNext()
        {
            if (_currentIndex < _waypoints.Count - 1)
            {
                _currentIndex++;
                State = NavigateState.Driving;
                return true;
            }
            State = NavigateState.Idle;
            return false;
        }

        public void StopNavigation()
        {
            _currentIndex = -1;
            State = NavigateState.Idle;
        }

        public string GetStatusString(Vehicle vehicle)
        {
            if (State != NavigateState.Driving && State != NavigateState.Waiting)
                return "";

            float dist = Vector3.Distance(vehicle.Position, GetCurrentDestination());
            return $"-> {CurrentWaypointName} ({_currentIndex + 1}/{TotalCount}) | {dist:F0}m";
        }

        public string GetNextWaypointName()
        {
            int next = _currentIndex + 1;
            if (next < _waypoints.Count)
                return _waypoints[next].Name;
            return "";
        }

        public string GetCurrentPrompt()
        {
            if (State != NavigateState.Waiting) return "";
            if (IsLastWaypoint)
                return $"Arrived at final destination: {CurrentWaypointName}.";
            return $"Arrived at {CurrentWaypointName}. Continue to {GetNextWaypointName()}? (Num7=Yes / Num8=No)";
        }
    }
}
