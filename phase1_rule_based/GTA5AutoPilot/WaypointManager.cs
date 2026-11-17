using System.Collections.Generic;
using GTA;
using GTA.Math;

namespace GTA5AutoPilot
{
    public class Waypoint
    {
        public string Id;      // gazetteer id from the agent; may be null for map-picked points
        public string Name;
        public Vector3 Position;

        public Waypoint(string name, Vector3 pos)
        {
            Name = name;
            Position = pos;
        }

        public Waypoint(string id, string name, Vector3 pos)
        {
            Id = id;
            Name = name;
            Position = pos;
        }
    }

    public enum NavigateState
    {
        Idle,      // No navigation task
        Driving,   // Driving to current waypoint
        Arrived,   // Reached current waypoint (unused, kept for compatibility)
        Waiting,   // Stopped at an intermediate waypoint, waiting for "continue"
        Stuck,     // Gave up driving to the current waypoint; waiting for retry / skip / stop
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

        /// <summary>The waypoint currently being driven to (or the next one while Waiting), or null.</summary>
        public Waypoint Current =>
            (_currentIndex >= 0 && _currentIndex < _waypoints.Count)
                ? _waypoints[_currentIndex] : null;

        /// <summary>Horizontal distance. Stored Z values are not reliable enough for arrival checks.</summary>
        public static float Distance2D(Vector3 a, Vector3 b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            return (float)System.Math.Sqrt(dx * dx + dy * dy);
        }

        public void LoadWaypoints(List<Waypoint> waypoints)
        {
            _waypoints.Clear();
            _waypoints.AddRange(waypoints);
            _currentIndex = -1;
            State = NavigateState.Idle;
        }

        /// <summary>Add stops to the end of the queue without touching the current leg.</summary>
        public void AppendWaypoints(List<Waypoint> waypoints)
        {
            _waypoints.AddRange(waypoints);
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
            if (_currentIndex < 0 || _currentIndex >= _waypoints.Count) _currentIndex = 0;
            _waypoints.RemoveAt(_currentIndex);
            _currentIndex = 0;
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

        /// <summary>Driving -> Stuck. The waypoint stays in the queue.</summary>
        public bool MarkStuck()
        {
            if (State != NavigateState.Driving) return false;
            State = NavigateState.Stuck;
            return true;
        }

        /// <summary>Stuck -> Driving, same waypoint.</summary>
        public bool Retry()
        {
            if (State != NavigateState.Stuck || Current == null) return false;
            State = NavigateState.Driving;
            return true;
        }

        /// <summary>
        /// Drop the current target without counting it as reached.
        /// If navigation was active and stops remain, the next one becomes the driving target.
        /// </summary>
        /// <returns>The skipped waypoint, or null if the queue was empty.</returns>
        public Waypoint SkipCurrent()
        {
            if (_waypoints.Count == 0) return null;
            bool active = State != NavigateState.Idle;
            int idx = (_currentIndex >= 0 && _currentIndex < _waypoints.Count) ? _currentIndex : 0;
            Waypoint skipped = _waypoints[idx];
            _waypoints.RemoveAt(idx);

            if (_waypoints.Count == 0)
            {
                _currentIndex = -1;
                State = NavigateState.Idle;
            }
            else if (active)
            {
                _currentIndex = 0;
                State = NavigateState.Driving;
            }
            else
            {
                _currentIndex = -1;
            }
            return skipped;
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
                if (Distance2D(wp.Position, pos) < threshold)
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
            if (State != NavigateState.Driving && State != NavigateState.Waiting && State != NavigateState.Stuck)
                return "";

            float dist = Distance2D(vehicle.Position, GetCurrentDestination());
            string prefix = State == NavigateState.Stuck ? "STUCK " : "";
            return $"{prefix}-> {CurrentWaypointName} | {dist:F0}m | {TotalCount} left";
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
            return $"Arrived. Continue to {_waypoints[0].Name}? (Num1=Yes / Num2=No)";
        }
    }
}
