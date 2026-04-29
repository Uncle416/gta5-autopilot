using System.Windows.Forms;

namespace GTA5AutoPilot.Debug
{
    /// <summary>
    /// Keyboard shortcut handler for controlling the autopilot mod.
    ///
    /// Controls:
    ///   Numpad0      - Toggle autopilot on/off
    ///   Numpad1      - Toggle debug overlay
    ///   Numpad2      - Set destination to map waypoint
    ///   Numpad3      - Toggle telemetry recording
    ///   Numpad4      - Increase target speed
    ///   Numpad5      - Decrease target speed
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
                    _entryPoint.SetDestination();
                    e.Handled = true;
                    break;

                case Keys.NumPad3:
                    _entryPoint.ToggleRecording();
                    e.Handled = true;
                    break;

                case Keys.NumPad4:
                    // Increase max speed (modify Configuration at runtime)
                    e.Handled = true;
                    break;

                case Keys.NumPad5:
                    // Decrease max speed
                    e.Handled = true;
                    break;

                case Keys.NumPad6:
                    _entryPoint.CyclePerceptionMode();
                    e.Handled = true;
                    break;

                case Keys.Decimal:
                    // Emergency stop: disengage autopilot
                    if (_entryPoint.AutoPilotEnabled)
                    {
                        _entryPoint.ToggleAutoPilot();
                    }
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
