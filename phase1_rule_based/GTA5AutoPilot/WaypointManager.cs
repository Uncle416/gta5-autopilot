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
        public IReadOnlyList<Waypoint> Waypoints => _waypoints;
        public string CurrentWaypointName =>
            (_currentIndex >= 0 && _currentIndex < _waypoints.Count)
                ? _waypoints[_currentIndex].Name : "";
        public bool IsLastWaypoint => _waypoints.Count <= 1;

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
            if (IsLastWaypoint)
            {
                _waypoints.Clear();
                _currentIndex = -1;
                State = NavigateState.Idle;
                return true;
            }
            // Remove completed waypoint, next becomes index 0
            _waypoints.RemoveAt(_currentIndex);
            // _currentIndex stays valid; if not last, point to next (now at same index)
            State = NavigateState.Waiting;
            return false;
        }

        public bool ContinueToNext()
        {
            if (_waypoints.Count == 0)
            {
                State = NavigateState.Idle;
                _currentIndex = -1;
                return false;
            }
            _currentIndex = 0;
            State = NavigateState.Driving;
            return true;
        }

        public string GetQueueString()
        {
            if (_waypoints.Count == 0) return "Queue empty";
            var sb = new System.Text.StringBuilder();
            sb.Append($"{_waypoints.Count} WP(s): ");
            int maxShow = 4;
            for (int i = 0; i < _waypoints.Count && i < maxShow; i++)
            {
                var wp = _waypoints[i];
                string mark = (State == NavigateState.Driving && i == 0) ? "*" : "";
                if (i > 0) sb.Append(" | ");
                sb.Append($"{i + 1}.{wp.Name}{mark}({wp.Position.X:F0},{wp.Position.Y:F0})");
            }
            if (_waypoints.Count > maxShow)
                sb.Append($" ... +{_waypoints.Count - maxShow}");
            return sb.ToString();
        }

        public bool ContainsNear(Vector3 pos, float threshold)
        {
            foreach (var wp in _waypoints)
                if (Vector3.Distance(wp.Position, pos) < threshold)
                    return true;
            return false;
        }

        public void AddWaypoint(Waypoint wp)
        {
            _waypoints.Add(wp);
        }

        public void StopNavigation()
        {
            _waypoints.Clear();
            _currentIndex = -1;
            State = NavigateState.Idle;
        }

        public string GetStatusString(Vehicle vehicle)
        {
            if (State != NavigateState.Driving && State != NavigateState.Waiting)
                return "";

            float dist = Vector3.Distance(vehicle.Position, GetCurrentDestination());
            return $"-> {CurrentWaypointName} | {dist:F0}m | {TotalCount} left";
        }

        public string GetNextWaypointName()
        {
            if (_waypoints.Count > 0)
                return _waypoints[0].Name;
            return "";
        }

        public string GetCurrentPrompt()
        {
            if (State != NavigateState.Waiting) return "";
            if (_waypoints.Count == 0)
                return "Navigation complete.";
            return $"Arrived. Continue to {_waypoints[0].Name}? (Num7=Yes / Num8=No)";
        }
    }
}
