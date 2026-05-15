using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading.Channels;

namespace VpinJukebox;

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

            // Start the single consumer that drains commands in FIFO order
            _consumerCts = new CancellationTokenSource();
            _consumerTask = Task.Run(() => ProcessCommandQueueAsync(_consumerCts.Token));

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
                if (_writer == null || _pipe?.IsConnected != true)
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
                }
            }
        }
        catch (OperationCanceledException) { }

        DebugLog.Log("[DOF] Command queue consumer exited.");
    }

    private void WriteTrigger(char type, int number, int value)
    {
        DebugLog.Log($"[DOF] Trigger {type}{number}={value}");
        _writer!.Write(type);
        _writer.Write(number);
        _writer.Write(value);
        _writer.Flush();

        var key = (type, number);
        if (value != 0)
            _activeTriggers[key] = value;
        else
            _activeTriggers.Remove(key);
    }

    /// <summary>
    /// Sends the shutdown command and cleans up.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DebugLog.Log("[DOF] Shutting down DofBridge.");

        // Stop the consumer
        _consumerCts?.Cancel();
        _commandChannel.Writer.TryComplete();
        _consumerTask?.Wait(2000);

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


        Cleanup();
    }

    /// <summary>
    /// Sends the shutdown command and cleans up asynchronously.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        DebugLog.Log("[DOF] Shutting down DofBridge.");

        _consumerCts?.Cancel();
        _commandChannel.Writer.TryComplete();
        if (_consumerTask != null)
        {
            try { await _consumerTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { }
        }

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

        Cleanup();
    }

    private void Cleanup()
    {
        _consumerCts?.Dispose();
        _consumerCts = null;

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
