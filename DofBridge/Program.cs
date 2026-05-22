using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace DofBridge
{
    /// <summary>
    /// A lightweight .NET Framework 4.8 bridge process that hosts DirectOutput Framework
    /// and receives trigger commands from the main Phosphor app via named pipes.
    /// 
    /// Protocol (binary, little-endian):
    ///   [char tableElementType] [int32 number] [int32 value]
    ///   
    /// Special commands:
    ///   tableElementType = '\0' → shutdown
    /// </summary>
    internal class Program
    {
        private const string PipeName = "PhosphorDof";

        private const string DefaultRomName = "vpinjukebox";

        private static DirectOutput.Pinball _pinball;

        private static StreamWriter _logWriter;
        private static bool _loggingEnabled;

        private static void Log(string message)
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
            Console.WriteLine(line);
            try
            {
                if (_logWriter != null)
                {
                    _logWriter.WriteLine(line);
                    _logWriter.Flush();
                }
            }
            catch { }
        }

        private static void InitLog()
        {
            _loggingEnabled = LoadLoggingSetting();
            if (!_loggingEnabled)
                return;

            try
            {
                var logPath = Path.Combine(
                    Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                    "DofBridge.log");
                _logWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };
                Log($"[DofBridge] Log file opened: {logPath}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DofBridge] Could not open log file: {ex.Message}");
            }
        }

        private static bool LoadLoggingSetting()
        {
            try
            {
                var settingsPath = Path.Combine(
                    Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                    "DofBridgeSettings.json");

                if (!File.Exists(settingsPath))
                    return false;

                var json = File.ReadAllText(settingsPath);

                // Simple parse — look for "EnableLogging" : true/false
                var key = "\"EnableLogging\"";
                var idx = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                    return false;

                var rest = json.Substring(idx + key.Length);
                return rest.IndexOf("true", StringComparison.OrdinalIgnoreCase) >= 0
                    && (rest.IndexOf("true", StringComparison.OrdinalIgnoreCase)
                        < rest.IndexOf("}", StringComparison.Ordinal));
            }
            catch
            {
                return false;
            }
        }

        private static string LoadDofConfigPathSetting()
        {
            try
            {
                var settingsPath = Path.Combine(
                    Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                    "DofBridgeSettings.json");

                if (!File.Exists(settingsPath))
                    return null;

                var json = File.ReadAllText(settingsPath);

                var key = "\"DofConfigPath\"";
                var idx = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                    return null;

                var rest = json.Substring(idx + key.Length);
                var colon = rest.IndexOf(':');
                if (colon < 0)
                    return null;

                rest = rest.Substring(colon + 1);
                var quote1 = rest.IndexOf('"');
                if (quote1 < 0)
                    return null;

                var quote2 = rest.IndexOf('"', quote1 + 1);
                if (quote2 < 0)
                    return null;

                var value = rest.Substring(quote1 + 1, quote2 - quote1 - 1).Trim();
                return string.IsNullOrEmpty(value) ? null : value;
            }
            catch
            {
                return null;
            }
        }

        private static bool _simulatorMode;

        static int Main(string[] args)
        {
            var argList = new System.Collections.Generic.List<string>(args);
            _simulatorMode = argList.Exists(a => a.Equals("-simulator", StringComparison.OrdinalIgnoreCase));
            var romName = DefaultRomName;
            for (int i = 0; i < argList.Count - 1; i++)
            {
                if (argList[i].Equals("-rom", StringComparison.OrdinalIgnoreCase))
                {
                    romName = argList[i + 1];
                    break;
                }
            }

            InitLog();
            Log("[DofBridge] Starting DOF bridge process...");
            Log($"[DofBridge] ROM name: {romName}");
            Log($"[DofBridge] PID: {System.Diagnostics.Process.GetCurrentProcess().Id}");

            if (_simulatorMode)
            {
                Log("[DofBridge] DOF simulator initialized successfully.");
            }
            else
            {
                // Locate DOF configuration
                var dofPath = LoadDofConfigPathSetting();
                if (dofPath != null && !File.Exists(dofPath))
                {
                    Log($"[DofBridge] Configured DofConfigPath not found: {dofPath}");
                    dofPath = null;
                }
                if (dofPath == null)
                    dofPath = FindDofConfigPath();
                if (dofPath == null)
                {
                    Log("[DofBridge] Could not locate DOF GlobalConfig. Exiting.");
                    return 1;
                }

                Log($"[DofBridge] DOF config: {dofPath}");

                // Initialize DOF
                try
                {
                    _pinball = new DirectOutput.Pinball();
                    _pinball.Setup(dofPath, "", romName);
                    _pinball.Init();
                    Log("[DofBridge] DOF initialized successfully.");
                }
                catch (Exception ex)
                {
                    Log($"[DofBridge] DOF init failed: {ex}");
                    return 2;
                }
            }

            try
            {
                RunPipeServer();
            }
            catch (Exception ex)
            {
                Log($"[DofBridge] Unhandled exception in pipe server: {ex}");
            }
            finally
            {
                Log("[DofBridge] Shutting down DOF...");
                if (!_simulatorMode)
                {
                    try { _pinball?.Finish(); } catch (Exception ex) { Log($"[DofBridge] DOF Finish error: {ex.Message}"); }
                }
                try { _logWriter?.Dispose(); } catch { }
            }

            return 0;
        }

        private static void RunPipeServer()
        {
            while (true)
            {
                Log("[DofBridge] Waiting for connection...");
                using (var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1))
                {
                    server.WaitForConnection();
                    Log("[DofBridge] Client connected.");

                    using (var reader = new BinaryReader(server))
                    {
                        try
                        {
                            while (server.IsConnected)
                            {
                                var type = reader.ReadChar();

                                // Shutdown command
                                if (type == '\0')
                                {
                                    Log("[DofBridge] Shutdown command received.");
                                    return;
                                }

                                var number = reader.ReadInt32();
                                var value = reader.ReadInt32();

                                Log($"[DofBridge] Trigger: {type}{number} = {value}");
                                if (!_simulatorMode)
                                {
                                    try
                                    {
                                        _pinball.ReceiveData(type, number, value);
                                    }
                                    catch (Exception ex)
                                    {
                                        Log($"[DofBridge] ReceiveData FAILED for {type}{number}={value}: {ex}");
                                    }
                                }
                            }
                        }
                        catch (EndOfStreamException)
                        {
                            Log("[DofBridge] Client disconnected (EndOfStream).");
                            return;
                        }
                        catch (IOException ex)
                        {
                            Log($"[DofBridge] Pipe broken: {ex.Message}");
                            return;
                        }
                    }
                }
            }
        }

        private static string FindDofConfigPath()
        {
            // Check common DOF install locations
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DirectOutput", "config", "GlobalConfig_B2SServer.xml"),
                @"C:\DirectOutput\config\GlobalConfig_B2SServer.xml",
                @"C:\Visual Pinball\DirectOutputConfig\GlobalConfig_B2SServer.xml",
            };

            foreach (var path in candidates)
            {
                if (File.Exists(path))
                    return path;
            }

            // Also check DOF_CONFIG environment variable
            var envPath = Environment.GetEnvironmentVariable("DOF_CONFIG");
            if (!string.IsNullOrEmpty(envPath))
            {
                var envConfig = Path.Combine(envPath, "GlobalConfig_B2SServer.xml");
                if (File.Exists(envConfig))
                    return envConfig;
            }

            return null;
        }
    }
}
