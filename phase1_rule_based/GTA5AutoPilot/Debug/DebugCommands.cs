using System.Windows.Forms;

namespace GTA5AutoPilot.Debug
{
    /// <summary>
    /// Keyboard shortcut handler for controlling the autopilot mod.
    ///
    /// Controls:
    ///   Numpad0      - Toggle autopilot on/off
    ///   Numpad1      - Toggle debug overlay
    ///   Numpad2      - Set destination from map waypoint (fallback)
    ///   Numpad7      - Yes: continue to next waypoint
    ///   Numpad8      - No: stop navigation
    ///   NumpadDecimal - Emergency stop
    /// </summary>
    public class DebugCommands
    {
        private readonly EntryPoint _entryPoint;

        public DebugCommands(EntryPoint entryPoint)
        {
            _entryPoint = entryPoint;
        }

        public void HandleKeyDown(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.NumPad0:
                    _entryPoint.ToggleAutoPilot();
                    e.Handled = true;
                    break;

                case Keys.NumPad1:
                    _entryPoint.ToggleDebugOverlay();
                    e.Handled = true;
                    break;

                case Keys.NumPad2:
                    _entryPoint.SetDestinationFromMap();
                    e.Handled = true;
                    break;

                case Keys.NumPad7:
                    _entryPoint.ContinueNavigation();
                    e.Handled = true;
                    break;

                case Keys.NumPad8:
                    _entryPoint.StopAutopilot();
                    e.Handled = true;
                    break;

                case Keys.Decimal:
                    if (_entryPoint.AutoPilotEnabled)
                        _entryPoint.StopAutopilot();
                    e.Handled = true;
                    break;
            }
        }

        public void HandleKeyUp(KeyEventArgs e)
        {
            // Reserved for future key-up events
        }
    }
}
