using SharpDX.DirectInput;

namespace VpinJukebox;

/// <summary>
/// Polls all connected DirectInput joystick/gamepad devices for button presses.
/// Raises <see cref="ButtonPressed"/> on the calling thread's dispatcher when a
/// new button press is detected (edge-triggered, not level-triggered).
/// </summary>
public sealed class DirectInputPoller : IDisposable
{
    private readonly DirectInput _directInput;
    private readonly List<(Joystick Stick, Guid DeviceGuid, bool[] PreviousButtons)> _devices = [];
    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private bool _disposed;

    /// <summary>
    /// Fired when a joystick button transitions from released to pressed.
    /// </summary>
    public event Action<Guid, int>? ButtonPressed;

    public DirectInputPoller(int pollIntervalMs = 16)
    {
        _directInput = new();
        _timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(pollIntervalMs)
        };
        _timer.Tick += Poll;
    }

    /// <summary>
    /// Enumerate and acquire all connected joystick/gamepad devices, then start polling.
    /// Device enumeration runs on a background thread to avoid blocking the UI.
    /// </summary>
    public void Start()
    {
        Stop();
        Task.Run(() =>
        {
            EnumerateDevices();
            DebugLog.Log("DirectInput", $"Enumeration complete: {_devices.Count} device(s) found");
            if (_devices.Count > 0)
                _timer.Dispatcher.BeginInvoke(() => _timer.Start());
        });
    }

    public void Stop()
    {
        _timer.Stop();
        ReleaseDevices();
    }

    public bool HasDevices => _devices.Count > 0;

    /// <summary>
    /// Returns a friendly display name for a device GUID, or a short GUID string if not found.
    /// </summary>
    public string GetDeviceName(Guid deviceGuid)
    {
        try
        {
            foreach (var dev in _directInput.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly))
            {
                if (dev.InstanceGuid == deviceGuid)
                    return dev.InstanceName;
            }
        }
        catch { }
        return deviceGuid.ToString()[..8];
    }

    private void EnumerateDevices()
    {
        try
        {
            var devices = _directInput.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly);
            foreach (var deviceInstance in devices)
            {
                try
                {
                    var joystick = new Joystick(_directInput, deviceInstance.InstanceGuid);
                    joystick.Properties.BufferSize = 128;
                    joystick.Acquire();

                    var state = joystick.GetCurrentState();
                    var buttons = state.Buttons;
                    var prev = new bool[buttons.Length];
                    Array.Copy(buttons, prev, buttons.Length);

                    _devices.Add((joystick, deviceInstance.InstanceGuid, prev));
                    System.Diagnostics.Debug.WriteLine(
                        $"DirectInput: Acquired '{deviceInstance.InstanceName}' ({deviceInstance.InstanceGuid})");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"DirectInput: Failed to acquire '{deviceInstance.InstanceName}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DirectInput: Enumeration failed: {ex.Message}");
        }
    }

    private void Poll(object? sender, EventArgs e)
    {
        for (int i = _devices.Count - 1; i >= 0; i--)
        {
            var (stick, guid, prev) = _devices[i];
            try
            {
                stick.Poll();
                var state = stick.GetCurrentState();
                var buttons = state.Buttons;

                for (int b = 0; b < buttons.Length && b < prev.Length; b++)
                {
                    if (buttons[b] && !prev[b])
                    {
                        ButtonPressed?.Invoke(guid, b);
                    }
                    prev[b] = buttons[b];
                }
            }
            catch (SharpDX.SharpDXException)
            {
                // Device disconnected
                try { stick.Unacquire(); } catch { }
                try { stick.Dispose(); } catch { }
                _devices.RemoveAt(i);
                System.Diagnostics.Debug.WriteLine($"DirectInput: Device {guid} lost");
            }
        }

        if (_devices.Count == 0)
            _timer.Stop();
    }

    private void ReleaseDevices()
    {
        foreach (var (stick, _, _) in _devices)
        {
            try { stick.Unacquire(); } catch { }
            try { stick.Dispose(); } catch { }
        }
        _devices.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _directInput.Dispose();
    }
}
