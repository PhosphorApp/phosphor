using System.Collections.Concurrent;
using System.Net.Http;
using System.Runtime.CompilerServices;
using LibVLCSharp.Shared;
using VlcLogLevel = LibVLCSharp.Shared.LogLevel;

namespace Phosphor.Playback;

/// <summary>
/// Forwards LibVLC's own log and MediaPlayer events into <see cref="DebugLog"/> (all lines tagged
/// <c>[VLC]</c>). Warnings/errors are always forwarded; with <see cref="Verbose"/> on, selected modules
/// and clock/sync messages are forwarded too. The log callback runs on a native thread, so it only
/// filters, throttles and enqueues to the non-blocking DebugLog — never touches the UI thread.
/// </summary>
public static class VlcDiagnostics
{
    private const long ThrottleWindowMs = 2000;
    private const long TimeSampleMs = 30_000;
    private const int MaxThrottleKeys = 512;

    private static readonly string[] RelevantModules =
    {
        "mmdevice", "directsound", "waveout", "adaptive", "hls", "avcodec", "aac", "adts", "audio output", "aout",
    };

    private static readonly string[] InterestingText =
    {
        "too late", "too early", "out of range", "discontinuity", "buffer too late", "late picture",
        "clock", "drift", "resampl", "underrun", "dropped", "lost", "gap",
    };

    private static readonly string[] AudioOutputs = { "default", "mmdevice", "directsound", "waveout" };

    // Strong static reference so the delegate registered with native code is never collected.
    private static readonly EventHandler<LogEventArgs> LogHandler = OnLog;
    private static readonly ConditionalWeakTable<LibVLC, object> AttachedInstances = new();
    private static readonly ConditionalWeakTable<MediaPlayer, object> AttachedPlayers = new();
    private static readonly ConcurrentDictionary<string, ThrottleState> Throttle = new();
    private static readonly object AttachLock = new();
    private static string _lastCachingLine = "";

    private sealed class ThrottleState
    {
        public long WindowStart;
        public int Suppressed;
    }

    /// <summary>True when verbose LibVLC diagnostics are enabled.</summary>
    public static bool Verbose { get; private set; }

    /// <summary>Selected audio output module: default, mmdevice, directsound or waveout.</summary>
    public static string AudioOutput { get; private set; } = "default";

    public static void Configure(bool verbose, string? audioOutput)
    {
        Verbose = verbose;
        var aout = (audioOutput ?? "default").Trim().ToLowerInvariant();
        AudioOutput = Array.IndexOf(AudioOutputs, aout) >= 0 ? aout : "default";
    }

    /// <summary>Instance arguments for the app's shared LibVLC (honors the audio-output + verbose settings).</summary>
    public static string[] BuildSharedArgs()
    {
        var args = new List<string> { "--no-video-title-show", "--network-caching=3000", "--http-reconnect" };
        if (AudioOutput != "default")
            args.Add($"--aout={AudioOutput}");
        if (Verbose)
            args.Add("--verbose=2");
        return args.ToArray();
    }

    /// <summary>Registers the log callback once per instance and logs the effective setup.</summary>
    public static void Attach(LibVLC vlc, string name, IEnumerable<string> options)
    {
        lock (AttachLock)
        {
            if (AttachedInstances.TryGetValue(vlc, out _))
                return;
            AttachedInstances.Add(vlc, new object());
            vlc.Log += LogHandler;
        }

        string version;
        try { version = vlc.Version; } catch { version = "?"; }
        DebugLog.Log(LogLevel.Info, "VLC", $"[setup] {name}: LibVLC {version}; options=[{string.Join(" ", options)}]; " +
            $"audioOutputSetting={AudioOutput}; verbose={Verbose}");
    }

    /// <summary>Unregisters the log callback (call before disposing an instance).</summary>
    public static void Detach(LibVLC? vlc)
    {
        if (vlc == null)
            return;
        lock (AttachLock)
        {
            if (!AttachedInstances.Remove(vlc))
                return;
            try { vlc.Log -= LogHandler; } catch { }
        }
    }

    /// <summary>Logs the network/live caching values actually applied to media (only when they change).</summary>
    public static void LogCaching(int network, int live, int file, bool reconnect)
    {
        var line = $"[setup] network-caching={network} live-caching={live} file-caching={file} http-reconnect={reconnect}";
        if (line == _lastCachingLine)
            return;
        _lastCachingLine = line;
        DebugLog.Log(LogLevel.Info, "VLC", line);
    }

    private static void OnLog(object? sender, LogEventArgs e)
    {
        try
        {
            var message = e.Message;
            if (string.IsNullOrEmpty(message))
                return;
            var module = string.IsNullOrEmpty(e.Module) ? "?" : e.Module;
            bool important = e.Level is VlcLogLevel.Warning or VlcLogLevel.Error;
            bool interesting = !important && ContainsAny(message, InterestingText);

            if (!important)
            {
                if (!Verbose)
                    return;
                if (!interesting && !ContainsAny(module, RelevantModules))
                    return;
            }

            // Interesting/important lines are keyed exactly (numbers matter); chatty lines ignore digits.
            var key = important || interesting
                ? $"{module}|{e.Level}|{message}"
                : $"{module}|{e.Level}|{StripDigits(message)}";
            if (Throttle.Count > MaxThrottleKeys)
                Throttle.Clear();
            var state = Throttle.GetOrAdd(key, _ => new ThrottleState());
            int suppressed;
            lock (state)
            {
                var now = Environment.TickCount64;
                if (state.WindowStart != 0 && now - state.WindowStart < ThrottleWindowMs)
                {
                    state.Suppressed++;
                    return;
                }
                suppressed = state.Suppressed;
                state.Suppressed = 0;
                state.WindowStart = now;
            }

            var level = e.Level switch
            {
                VlcLogLevel.Error => LogLevel.Error,
                VlcLogLevel.Warning => LogLevel.Warning,
                _ => LogLevel.Info,
            };
            var repeat = suppressed > 0 ? $" (+{suppressed} similar suppressed)" : "";
            DebugLog.Log(level, "VLC", $"[{module}] [{e.Level}] {message}{repeat}");

            if (e.Level == VlcLogLevel.Error)
                TryProbeFailedMrl(message);
        }
        catch { /* never throw into native code */ }
    }

    private static readonly HttpClient ProbeClient = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static long _lastProbeTick;

    /// <summary>
    /// LibVLC discards the HTTP error body, so when it cannot open an http(s) MRL, re-request the URL
    /// in the background and log the status, key headers and start of the body (e.g. why Plex returned 400).
    /// </summary>
    private static void TryProbeFailedMrl(string message)
    {
        const string marker = "unable to open the MRL '";
        var start = message.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return;
        start += marker.Length;
        var end = message.IndexOf("'. ", start, StringComparison.Ordinal);
        if (end < 0)
            end = message.LastIndexOf('\'');
        if (end <= start)
            return;
        var url = message.Substring(start, end - start);
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return;

        var now = Environment.TickCount64;
        if (now - Interlocked.Exchange(ref _lastProbeTick, now) < 1000)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var resp = await ProbeClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                var headers = string.Join("; ", resp.Headers.Concat(resp.Content.Headers)
                    .Select(h => $"{h.Key}={string.Join(",", h.Value)}")
                    .Where(h => h.StartsWith("X-Plex", StringComparison.OrdinalIgnoreCase)
                             || h.StartsWith("Content-Type", StringComparison.OrdinalIgnoreCase)
                             || h.StartsWith("Server", StringComparison.OrdinalIgnoreCase)));
                string body;
                try
                {
                    using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    var buf = new byte[2048];
                    int n = await stream.ReadAsync(buf.AsMemory(0, buf.Length)).ConfigureAwait(false);
                    body = System.Text.Encoding.UTF8.GetString(buf, 0, n).Replace('\r', ' ').Replace('\n', ' ');
                }
                catch (Exception bex) { body = $"(body unreadable: {bex.Message})"; }

                var lvl = resp.IsSuccessStatusCode ? LogLevel.Info : LogLevel.Warning;
                DebugLog.Log(lvl, "VLC", $"[probe] {(int)resp.StatusCode} {resp.ReasonPhrase}; headers=[{headers}]; body=[{body}]; url={url}");
            }
            catch (Exception ex)
            {
                DebugLog.Log(LogLevel.Warning, "VLC", $"[probe] request failed: {ex.GetType().Name}: {ex.Message}; url={url}");
            }
        });
    }

    private static bool ContainsAny(string text, string[] needles)
    {
        foreach (var n in needles)
            if (text.Contains(n, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string StripDigits(string s) => string.Create(s.Length, s, static (span, src) =>
    {
        for (int i = 0; i < src.Length; i++)
            span[i] = char.IsDigit(src[i]) ? '#' : src[i];
    });

    /// <summary>Logs MediaPlayer events (once per player) with timestamps from DebugLog.</summary>
    public static void AttachPlayer(MediaPlayer mp, string name)
    {
        lock (AttachLock)
        {
            if (AttachedPlayers.TryGetValue(mp, out _))
                return;
            AttachedPlayers.Add(mp, new object());
        }

        string Tag(string text) => $"[{name}] {text}";
        int lastPct = -1;
        long lastTimeLog = 0;

        mp.Opening += (_, _) => DebugLog.Log(LogLevel.Info, "VLC", Tag("event Opening"));
        mp.Playing += (_, _) =>
        {
            string device;
            try { device = mp.OutputDevice ?? "(default)"; } catch { device = "?"; }
            DebugLog.Log(LogLevel.Info, "VLC", Tag($"event Playing; aoutSetting={AudioOutput}; device={device}"));
        };
        mp.Paused += (_, _) => DebugLog.Log(LogLevel.Info, "VLC", Tag("event Paused"));
        mp.Stopped += (_, _) =>
        {
            lastPct = -1;
            DebugLog.Log(LogLevel.Info, "VLC", Tag("event Stopped"));
        };
        mp.EndReached += (_, _) => DebugLog.Log(LogLevel.Info, "VLC", Tag("event EndReached"));
        mp.EncounteredError += (_, _) => DebugLog.Log(LogLevel.Error, "VLC", Tag("event EncounteredError"));
        mp.LengthChanged += (_, e) => DebugLog.Log(LogLevel.Info, "VLC", Tag($"event LengthChanged {e.Length} ms"));
        mp.Buffering += (_, e) =>
        {
            int pct = (int)e.Cache;
            if (pct == lastPct)
                return;
            lastPct = pct;
            DebugLog.Log(LogLevel.Info, "VLC", Tag($"event Buffering {pct}%"));
        };
        mp.TimeChanged += (_, e) =>
        {
            var now = Environment.TickCount64;
            if (now - lastTimeLog < TimeSampleMs)
                return;
            lastTimeLog = now;
            DebugLog.Log(LogLevel.Info, "VLC", Tag($"event TimeChanged {TimeSpan.FromMilliseconds(e.Time):hh\\:mm\\:ss}"));
        };
        mp.AudioDevice += (_, e) => DebugLog.Log(LogLevel.Info, "VLC", Tag($"event AudioDevice {e.AudioDevice}"));
    }
}
