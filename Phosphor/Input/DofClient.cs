using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading.Channels;

namespace Phosphor;

/// <summary>
/// Client that manages the DofBridge.exe process and sends DOF trigger commands via named pipe.
/// Uses a single-reader channel to guarantee FIFO command ordering.
/// </summary>
public class DofClient : IDisposable, IAsyncDisposable
{
    private const string PipeName = "PhosphorDof";

    private Process? _bridgeProcess;
    private NamedPipeClientStream? _pipe;
    private BinaryWriter? _writer;
    private bool _disposed;
    private readonly Dictionary<(char Type, int Number), int> _activeTriggers = new();

    // Serializes access to _writer/_pipe and _activeTriggers between the consumer
    // thread, the reconnect task, and Dispose.
    private readonly object _writeLock = new();

    // Launch configuration, retained so the reconnect loop can relaunch the bridge.
    private string _romName = "vpinjukebox";
    private bool _simulatorMode;

    // Reconnect state. _started arms auto-reconnect only after a successful initial
    // connection; _reconnecting (0/1 via Interlocked) enforces single-flight recovery.
    private volatile bool _started;
    private int _reconnecting;

    private volatile ConnectionState _state = ConnectionState.Disconnected;

    /// <summary>Current connection state of the DOF bridge.</summary>
    public ConnectionState State => _state;

    /// <summary>Raised whenever the connection state changes (e.g. for a status indicator).</summary>
    public event Action<ConnectionState>? StatusChanged;

    private void SetState(ConnectionState state)
    {
        if (_state == state) return;
        _state = state;
        try { StatusChanged?.Invoke(state); }
        catch (Exception ex) { DebugLog.Log($"[DOF] StatusChanged handler error: {ex.Message}"); }
    }

    // FIFO command queue — unbounded, single consumer
    private readonly Channel<(char Type, int Number, int Value)> _commandChannel =
        Channel.CreateUnbounded<(char, int, int)>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    private Task? _consumerTask;
    private CancellationTokenSource? _consumerCts;

    public bool IsConnected => _pipe?.IsConnected == true;

    /// <summary>Connection lifecycle states for the DOF bridge.</summary>
    public enum ConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Faulted
    }

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
        _romName = romName;
        _simulatorMode = simulatorMode;

        SetState(ConnectionState.Connecting);
        var connected = await LaunchAndConnectAsync();
        if (connected)
        {
            _started = true;
            SetState(ConnectionState.Connected);
        }
        else
        {
            SetState(ConnectionState.Faulted);
        }
        return connected;
    }

    /// <summary>
    /// Launches the bridge process and connects the pipe. Shared by the initial
    /// <see cref="StartAsync"/> and the reconnect loop. Does not alter <see cref="State"/>.
    /// </summary>
    private async Task<bool> LaunchAndConnectAsync()
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

            var arguments = $"-rom {_romName}" + (_simulatorMode ? " -simulator" : "");
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
            _bridgeProcess.Exited += OnBridgeProcessExited;
            _bridgeProcess.Start();
            _bridgeProcess.BeginOutputReadLine();
            _bridgeProcess.BeginErrorReadLine();

            // Check if the bridge process exited immediately (e.g. DOF config not found)
            if (_bridgeProcess.WaitForExit(500))
            {
                DebugLog.Log($"[DOF] Bridge process exited immediately with code {_bridgeProcess.ExitCode}.");
                CleanupConnection();
                return false;
            }

            var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            await pipe.ConnectAsync(5000);
            lock (_writeLock)
            {
                _pipe = pipe;
                _writer = new BinaryWriter(pipe);
            }

            // Start the single consumer that drains commands in FIFO order (only once).
            if (_consumerTask == null)
            {
                _consumerCts = new CancellationTokenSource();
                _consumerTask = Task.Run(() => ProcessCommandQueueAsync(_consumerCts.Token));
            }

            DebugLog.Log("[DOF] Connected to DofBridge.");
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Log($"[DOF] Failed to start bridge: {ex.Message}");
            CleanupConnection();
            return false;
        }
    }

    /// <summary>
    /// Fires when the bridge process exits unexpectedly. Triggers the reconnect loop
    /// if the client was started and is not being disposed.
    /// </summary>
    private void OnBridgeProcessExited(object? sender, EventArgs e)
    {
        DebugLog.Log($"[DOF] Bridge process exited with code {(sender as Process)?.ExitCode}.");
        if (_disposed || !_started) return;
        _ = ReconnectAsync();
    }

    /// <summary>
    /// Single-flight reconnect loop. Tears down the dead connection, relaunches the
    /// bridge with backoff, and on success replays "off" (value 0) for every trigger
    /// DOF still believes is active so we resume from a known-good state.
    /// </summary>
    private async Task ReconnectAsync()
    {
        // Ensure only one reconnect runs at a time.
        if (Interlocked.CompareExchange(ref _reconnecting, 1, 0) != 0) return;

        try
        {
            SetState(ConnectionState.Connecting);
            DebugLog.Log("[DOF] Connection lost — attempting to reconnect.");

            // Tear down the dead process/pipe but keep the consumer and _activeTriggers.
            CleanupConnection();

            var delays = new[] { 500, 1000, 2000, 5000 };
            const int maxAttempts = 10;

            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (_disposed) return;

                var delay = delays[Math.Min(attempt, delays.Length - 1)];
                try { await Task.Delay(delay); }
                catch { return; }

                if (_disposed) return;

                DebugLog.Log($"[DOF] Reconnect attempt {attempt + 1}/{maxAttempts}...");
                if (await LaunchAndConnectAsync())
                {
                    ResetToKnownGoodState();
                    SetState(ConnectionState.Connected);
                    DebugLog.Log("[DOF] Reconnected to DofBridge.");
                    return;
                }
            }

            DebugLog.Log("[DOF] Reconnect gave up after max attempts.");
            SetState(ConnectionState.Faulted);
        }
        finally
        {
            Interlocked.Exchange(ref _reconnecting, 0);
        }
    }

    /// <summary>
    /// Sends value 0 for every trigger DOF still believes is active, bringing the
    /// freshly reconnected bridge to a known-good state without re-firing effects.
    /// Mirrors the auto-off performed on shutdown.
    /// </summary>
    private void ResetToKnownGoodState()
    {
        lock (_writeLock)
        {
            if (_writer == null || _pipe?.IsConnected != true) return;

            try
            {
                foreach (var ((type, number), _) in _activeTriggers)
                {
                    DebugLog.Log($"[DOF] Reconnect auto-off {type}{number}=0");
                    _writer.Write(type);
                    _writer.Write(number);
                    _writer.Write(0);
                }
                _writer.Flush();
                _activeTriggers.Clear();
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[DOF] Reconnect reset failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Enqueues a DOF trigger command. Returns immediately; order is guaranteed by the consumer.
    /// </summary>
    public void Trigger(char tableElementType, int number, int value)
    {
        if (_disposed) return;

        if (!_commandChannel.Writer.TryWrite((tableElementType, number, value)))
        {
            DebugLog.Log($"[DOF] Failed to enqueue trigger {tableElementType}{number}={value}");
        }
    }

    /// <summary>
    /// Enqueues a pulse trigger: value 1 followed by value 0.
    /// The consumer processes them in order with a 50ms delay between.
    /// </summary>
    public void TriggerPulse(char tableElementType, int number)
    {
        Trigger(tableElementType, number, 1);
        Trigger(tableElementType, number, -1); // sentinel: -1 means "delay then send 0"
    }

    /// <summary>
    /// Single consumer loop — guarantees strict FIFO ordering of all commands.
    /// </summary>
    private async Task ProcessCommandQueueAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (type, number, value) in _commandChannel.Reader.ReadAllAsync(ct))
            {
                // Drop commands while disconnected; the reconnect loop restores a
                // known-good state (all-off) once the bridge is back.
                if (_state != ConnectionState.Connected)
                    continue;

                try
                {
                    if (value == -1)
                    {
                        // Pulse sentinel: delay then send 0
                        await Task.Delay(50, ct);
                        WriteTrigger(type, number, 0);
                    }
                    else
                    {
                        WriteTrigger(type, number, value);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"[DOF] Trigger failed: {ex.Message}");
                    // A write failure means the pipe/bridge is gone. Kick off recovery;
                    // if the process-exit event already started it, this is a no-op.
                    if (!_disposed && _started)
                    {
                        SetState(ConnectionState.Connecting);
                        _ = ReconnectAsync();
                    }
                }
            }
        }
        catch (OperationCanceledException) { }

        DebugLog.Log("[DOF] Command queue consumer exited.");
    }

    private void WriteTrigger(char type, int number, int value)
    {
        DebugLog.Log($"[DOF] Trigger {type}{number}={value}");
        lock (_writeLock)
        {
            if (_writer == null || _pipe?.IsConnected != true)
                throw new IOException("DOF pipe is not connected.");

            _writer.Write(type);
            _writer.Write(number);
            _writer.Write(value);
            _writer.Flush();

            var key = (type, number);
            if (value != 0)
                _activeTriggers[key] = value;
            else
                _activeTriggers.Remove(key);
        }
    }

    /// <summary>
    /// Sends the shutdown command and cleans up.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _started = false;

        DebugLog.Log("[DOF] Shutting down DofBridge.");

        // Stop the consumer
        _consumerCts?.Cancel();
        _commandChannel.Writer.TryComplete();
        _consumerTask?.Wait(2000);

        SendShutdown();
        Cleanup();
    }

    /// <summary>
    /// Sends the shutdown command and cleans up asynchronously.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _started = false;

        DebugLog.Log("[DOF] Shutting down DofBridge.");

        _consumerCts?.Cancel();
        _commandChannel.Writer.TryComplete();
        if (_consumerTask != null)
        {
            try { await _consumerTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { }
        }

        SendShutdown();
        Cleanup();
    }

    /// <summary>
    /// Turns off all active triggers and sends the shutdown command to the bridge.
    /// </summary>
    private void SendShutdown()
    {
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
    }

    /// <summary>
    /// Tears down the current bridge process and pipe without touching the command
    /// consumer or <see cref="_activeTriggers"/>. Used by the reconnect loop.
    /// </summary>
    private void CleanupConnection()
    {
        lock (_writeLock)
        {
            _writer?.Dispose();
            _writer = null;

            _pipe?.Dispose();
            _pipe = null;
        }

        if (_bridgeProcess != null)
        {
            // Detach so tearing the process down doesn't re-enter the reconnect loop.
            _bridgeProcess.Exited -= OnBridgeProcessExited;

            if (!_bridgeProcess.HasExited)
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
                    DebugLog.Log($"[DOF] Connection cleanup error: {ex.Message}");
                }
            }

            _bridgeProcess.Dispose();
            _bridgeProcess = null;
        }
    }

    private void Cleanup()
    {
        _consumerCts?.Dispose();
        _consumerCts = null;

        CleanupConnection();

        DebugLog.Log("[DOF] Cleanup complete.");
        SetState(ConnectionState.Disconnected);
    }
}
