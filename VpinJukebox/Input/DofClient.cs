using System.Diagnostics;
using System.IO;
using System.IO.Pipes;

namespace VpinJukebox;

/// <summary>
/// Client that manages the DofBridge.exe process and sends DOF trigger commands via named pipe.
/// </summary>
public class DofClient : IDisposable, IAsyncDisposable
{
    private const string PipeName = "VpinJukeboxDof";

    private Process? _bridgeProcess;
    private NamedPipeClientStream? _pipe;
    private BinaryWriter? _writer;
    private bool _disposed;
    private readonly object _writeLock = new();
    private readonly Dictionary<(char Type, int Number), int> _activeTriggers = new();

    public bool IsConnected => _pipe?.IsConnected == true;

    /// <summary>
    /// Returns the path to the correct DofBridge executable for the current OS architecture.
    /// Looks in the architecture-specific subfolder (x64 or x86) under the application directory.
    /// </summary>
    private static string ResolveBridgePath()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var subfolder = Environment.Is64BitOperatingSystem ? "x64" : "x86";
        return Path.Combine(baseDir, subfolder, "DofBridge.exe");
    }

    /// <summary>
    /// Returns true if the DofBridge executable exists for the current OS architecture.
    /// </summary>
    public static bool IsBridgeAvailable() => File.Exists(ResolveBridgePath());

    /// <summary>
    /// Starts the DofBridge.exe process and connects the named pipe asynchronously.
    /// </summary>
    public async Task<bool> StartAsync(string romName = "vpinjukebox", bool simulatorMode = false)
    {
        try
        {
            var bridgePath = ResolveBridgePath();
            if (!File.Exists(bridgePath))
            {
                DebugLog.Log($"[DOF] Bridge executable not found: {bridgePath}");
                return false;
            }

            DebugLog.Log($"[DOF] Using bridge: {bridgePath} (64-bit OS: {Environment.Is64BitOperatingSystem})");

            var arguments = $"-rom {romName}" + (simulatorMode ? " -simulator" : "");
            DebugLog.Log($"[DOF] Arguments: {arguments}");

            _bridgeProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = bridgePath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                },
                EnableRaisingEvents = true
            };
            _bridgeProcess.OutputDataReceived += (s, e) =>
            {
                if (e.Data != null) DebugLog.Log($"[DOF-Bridge] {e.Data}");
            };
            _bridgeProcess.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null) DebugLog.Log($"[DOF-Bridge-ERR] {e.Data}");
            };
            _bridgeProcess.Exited += (s, e) =>
            {
                DebugLog.Log($"[DOF] Bridge process exited with code {(s as Process)?.ExitCode}");
            };
            _bridgeProcess.Start();
            _bridgeProcess.BeginOutputReadLine();
            _bridgeProcess.BeginErrorReadLine();

            // Check if the bridge process exited immediately (e.g. DOF config not found)
            if (_bridgeProcess.WaitForExit(500))
            {
                DebugLog.Log($"[DOF] Bridge process exited immediately with code {_bridgeProcess.ExitCode}.");
                Cleanup();
                return false;
            }

            var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            await pipe.ConnectAsync(5000);
            _pipe = pipe;
            _writer = new BinaryWriter(pipe);

            DebugLog.Log("[DOF] Connected to DofBridge.");
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Log($"[DOF] Failed to start bridge: {ex.Message}");
            Cleanup();
            return false;
        }
    }

    /// <summary>
    /// Sends a DOF trigger command to the bridge process asynchronously.
    /// </summary>
    public Task TriggerAsync(char tableElementType, int number, int value)
    {
        if (_disposed || _writer == null || _pipe?.IsConnected != true)
            return Task.CompletedTask;

        return Task.Run(() =>
        {
            lock (_writeLock)
            {
                try
                {
                    DebugLog.Log($"[DOF] Trigger {tableElementType}{number}={value}");
                    _writer.Write(tableElementType);
                    _writer.Write(number);
                    _writer.Write(value);
                    _writer.Flush();

                    var key = (tableElementType, number);
                    if (value != 0)
                        _activeTriggers[key] = value;
                    else
                        _activeTriggers.Remove(key);
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"[DOF] Trigger failed: {ex.Message}");
                }
            }
        });
    }

    /// <summary>
    /// Sends a pulse trigger: value 1 followed by value 0 after a 50ms delay.
    /// </summary>
    public async Task TriggerPulseAsync(char tableElementType, int number)
    {
        await TriggerAsync(tableElementType, number, 1);
        await Task.Delay(50);
        await TriggerAsync(tableElementType, number, 0);
    }

    /// <summary>
    /// Sends the shutdown command and cleans up.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DebugLog.Log("[DOF] Shutting down DofBridge.");
        lock (_writeLock)
        {
            try
            {
                if (_writer != null && _pipe?.IsConnected == true)
                {
                    // Turn off all active triggers before shutdown
                    foreach (var ((type, number), _) in _activeTriggers)
                    {
                        DebugLog.Log($"[DOF] Auto-off {type}{number}=0");
                        _writer.Write(type);
                        _writer.Write(number);
                        _writer.Write(0);
                    }
                    _writer.Flush();
                    _activeTriggers.Clear();

                    _writer.Write('\0'); // shutdown command
                    _writer.Flush();
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[DOF] Shutdown send failed: {ex.Message}");
            }
        }

        Cleanup();
    }

    /// <summary>
    /// Sends the shutdown command and cleans up asynchronously.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await Task.Run(() =>
        {
            DebugLog.Log("[DOF] Shutting down DofBridge.");
            lock (_writeLock)
            {
                try
                {
                    if (_writer != null && _pipe?.IsConnected == true)
                    {
                        foreach (var ((type, number), _) in _activeTriggers)
                        {
                            DebugLog.Log($"[DOF] Auto-off {type}{number}=0");
                            _writer.Write(type);
                            _writer.Write(number);
                            _writer.Write(0);
                        }
                        _writer.Flush();
                        _activeTriggers.Clear();

                        _writer.Write('\0');
                        _writer.Flush();
                    }
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"[DOF] Shutdown send failed: {ex.Message}");
                }
            }

            Cleanup();
        });
    }

    private void Cleanup()
    {
        _writer?.Dispose();
        _writer = null;

        _pipe?.Dispose();
        _pipe = null;

        if (_bridgeProcess is { HasExited: false })
        {
            try
            {
                _bridgeProcess.WaitForExit(3000);
                if (!_bridgeProcess.HasExited)
                {
                    DebugLog.Log("[DOF] Bridge process did not exit in time, killing.");
                    _bridgeProcess.Kill();
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[DOF] Cleanup error: {ex.Message}");
            }
        }
        DebugLog.Log("[DOF] Cleanup complete.");
        _bridgeProcess?.Dispose();
        _bridgeProcess = null;
    }
}
