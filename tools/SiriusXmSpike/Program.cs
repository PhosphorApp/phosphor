using System.Net;
using System.Text;
using System.Text.Json;

// SiriusXM auth + lineup spike (Phase 0, siriusxm branch).
//
// Purpose: prove — in pure C#, no browser/Python/yt-dlp — that we can (1) log in with
// subscriber credentials and (2) enumerate the account's channel lineup. Playback (the
// AES-HLS proxy) is intentionally out of scope here.
//
// Flow reverse-engineered from AngellusMortis/sxm-client (client.py):
//   1. POST modify/authentication   (deviceInfo + standardAuth) -> SXMAUTHNEW cookie
//   2. POST resume?OAtrial=false    (deviceInfo)                -> AWSALB + JSESSIONID cookies
//   3. POST get?type=2 (v4)         (channel-list module)       -> channel JSON
// All POST bodies wrap as {"moduleList":{"modules":[{"moduleRequest": <data>}]}} and
// responses unwrap via ["ModuleListResponse"].
//
// Credentials: never hard-coded. Read from env vars SXM_USER / SXM_PASS, or a gitignored
// tools/SiriusXmSpike/sxm.local.json  ({ "username": "...", "password": "...", "region": "US" }).

const string RestV2 = "https://player.siriusxm.com/rest/v2/experience/modules/{0}";
const string RestV4 = "https://player.siriusxm.com/rest/v4/experience/modules/{0}";
const string AppVersion = "5.36.514";
const string DeviceModel = "EverestWebClient";
const string UserAgent =
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:89.0) Gecko/20100101 Firefox/89.0";

var (username, password, region) = LoadCredentials();
if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
{
    Console.Error.WriteLine(
        "No credentials. Set SXM_USER + SXM_PASS env vars, or create tools/SiriusXmSpike/sxm.local.json.");
    return 2;
}
Console.WriteLine($"SiriusXM spike — user '{Mask(username)}', region {region}");

var cookies = new CookieContainer();
using var handler = new HttpClientHandler { CookieContainer = cookies, UseCookies = true };
using var http = new HttpClient(handler);
http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

// ── Step 1: login ──────────────────────────────────────────────────────────
var loginBody = Device();
loginBody["standardAuth"] = new Dictionary<string, object>
{
    ["username"] = username,
    ["password"] = password,
};
var login = await PostAsync("modify/authentication", loginBody, RestV2);
bool loggedIn = HasCookie("SXMAUTHNEW");
Console.WriteLine($"[1] login   -> status={StatusOf(login)}  SXMAUTHNEW cookie={loggedIn}");
if (!loggedIn)
{
    Console.Error.WriteLine("Login failed (no SXMAUTHNEW cookie). Dumping response for diagnosis:");
    Console.Error.WriteLine(Preview(login));
    return 1;
}

// ── Step 2: authenticate / resume session ──────────────────────────────────
var resume = await PostAsync("resume?OAtrial=false", Device(), RestV2);
bool authed = HasCookie("AWSALB") && HasCookie("JSESSIONID");
Console.WriteLine($"[2] resume  -> status={StatusOf(resume)}  session(AWSALB+JSESSIONID)={authed}");
if (!authed)
{
    Console.Error.WriteLine("Session not authenticated. Response:");
    Console.Error.WriteLine(Preview(resume));
    return 1;
}

// ── Step 3: channel lineup ─────────────────────────────────────────────────
var channelReq = new Dictionary<string, object>
{
    ["consumeRequests"] = new List<object>(),
    ["resultTemplate"] = "responsive",
    ["alerts"] = new List<object>(),
    ["profileInfos"] = new List<object>(),
};
var channelsJson = await PostAsync("get?type=2", channelReq, RestV4, channelList: true);
var channels = ParseChannels(channelsJson);
Console.WriteLine($"[3] lineup  -> {channels.Count} channel(s)");
Console.WriteLine();

// ── Optional Step 4: playback proof for one channel (`--play <channelId>`) ──
var playIdx = Array.FindIndex(args, a => a is "--play" or "-p");
if (playIdx >= 0 && playIdx + 1 < args.Length)
{
    var wanted = args[playIdx + 1];
    var ch = channels.FirstOrDefault(c =>
        string.Equals(c.ChannelId, wanted, StringComparison.OrdinalIgnoreCase) ||
        c.Number == wanted);
    if (ch.ChannelId is null or "")
    {
        Console.Error.WriteLine($"Channel '{wanted}' not found in lineup.");
        return 1;
    }
    return await ProvePlaybackAsync(ch);
}

// ── Optional Step 5: dump the category taxonomy (`--categories`) ──
if (args.Any(a => a is "--categories" or "-c"))
{
    DumpCategories(channelsJson);
    return 0;
}

foreach (var c in channels.OrderBy(c => c.SortNumber))
    Console.WriteLine($"  {c.Number,4}  {c.ChannelId,-28}  {c.Name}");

Console.WriteLine();
Console.WriteLine(channels.Count > 0
    ? "RESULT: SUCCESS — auth + lineup enumeration works in pure C#."
    : "RESULT: auth worked but lineup was empty — inspect the raw response above.");
return channels.Count > 0 ? 0 : 1;


// ── Helpers ─────────────────────────────────────────────────────────────────

// Static, publicly-known HLS segment AES-128 key (same constant the sxm-client proxy serves).
byte[] HlsAesKey() => Convert.FromBase64String("0Nsco7MAgxowGvkUT8aYag==");

// Dumps the category taxonomy: each channel's categories.categories[] name/key/order, aggregated
// so we can seed the Music/Talk/Sports super-group map from real account data.
void DumpCategories(JsonElement? response)
{
    if (response is not { } r) { Console.Error.WriteLine("No lineup to inspect."); return; }
    // name -> (key, order, channel names)
    var cats = new SortedDictionary<string, (string? key, int order, List<string> chans)>(StringComparer.OrdinalIgnoreCase);
    int channelCount = 0;
    try
    {
        var channels = r.GetProperty("moduleList").GetProperty("modules")[0]
            .GetProperty("moduleResponse").GetProperty("contentData")
            .GetProperty("channelListing").GetProperty("channels");
        foreach (var ch in channels.EnumerateArray())
        {
            channelCount++;
            var chName = ch.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (!ch.TryGetProperty("categories", out var categories) ||
                !categories.TryGetProperty("categories", out var arr))
                continue;
            foreach (var cat in arr.EnumerateArray())
            {
                var name = cat.TryGetProperty("name", out var cn) ? cn.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var key = cat.TryGetProperty("key", out var ck) ? ck.GetString() : null;
                var order = cat.TryGetProperty("order", out var co) && co.TryGetInt32(out var ov) ? ov : 0;
                if (!cats.TryGetValue(name, out var entry))
                    entry = (key, order, new List<string>());
                entry.chans.Add(chName);
                cats[name] = entry;
            }
        }
    }
    catch (Exception ex) { Console.Error.WriteLine($"Category parse failed: {ex.Message}"); return; }

    Console.WriteLine($"[categories] {cats.Count} distinct categories across {channelCount} channels");
    Console.WriteLine();
    Console.WriteLine($"{"CATEGORY",-28} {"KEY",-22} {"#",4}  SAMPLE CHANNELS");
    Console.WriteLine(new string('─', 100));
    foreach (var kv in cats.OrderByDescending(k => k.Value.chans.Count))
    {
        var sample = string.Join(", ", kv.Value.chans.Take(4));
        if (kv.Value.chans.Count > 4) sample += ", …";
        Console.WriteLine($"{Trunc(kv.Key, 27),-28} {Trunc(kv.Value.key ?? "", 21),-22} {kv.Value.chans.Count,4}  {sample}");
    }
    Console.WriteLine();
    Console.WriteLine("Copy the CATEGORY column to seed the Music/Talk/Sports super-group JSON.");

    static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}

// Proves a channel's live stream is reachable + decryptable end to end:
//  1. fetch the HLS root substitutions (get/configuration)
//  2. resolve the channel's master playlist URL (tune/now-playing-live)
//  3. fetch the master + variant playlists (with token params)
//  4. run a tiny local HLS proxy that rewrites playlists and serves the static AES key +
//     decrypted-in-transit segments, so any player (ffplay/VLC) can play http://127.0.0.1:<port>/master.m3u8
async Task<int> ProvePlaybackAsync(Channel ch)
{
    Console.WriteLine($"[4] play    -> resolving '{ch.ChannelId}' ({ch.Name})…");

    // (a) HLS roots from configuration.
    var cfgParams = new Dictionary<string, string>
    {
        ["result-template"] = "html5",
        ["app-region"] = region,
        ["cacheBuster"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
    };
    var cfg = await GetAsync("get/configuration", cfgParams, RestV2);
    var (primaryRoot, secondaryRoot) = ExtractHlsRoots(cfg);
    Console.WriteLine($"    HLS roots: primary={primaryRoot ?? "(none)"} secondary={secondaryRoot ?? "(none)"}");
    if (primaryRoot == null)
    {
        Console.Error.WriteLine("    Could not extract Live_Primary_HLS from configuration.");
        return 1;
    }

    // (b) now-playing → master playlist URL (templated with %Live_Primary_HLS%).
    var now = DateTimeOffset.UtcNow;
    var npParams = new Dictionary<string, string>
    {
        ["assetGUID"] = ch.Guid,
        ["ccRequestType"] = "AUDIO_VIDEO",
        ["channelId"] = ch.ChannelId,
        ["hls_output_mode"] = "custom",
        ["marker_mode"] = "all_separate_cue_points",
        ["result-template"] = "web",
        ["time"] = now.ToUnixTimeMilliseconds().ToString(),
        ["timestamp"] = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff") + "Z",
    };
    var np = await GetAsync("tune/now-playing-live", npParams, RestV2);
    var masterTemplate = ExtractMasterPlaylistUrl(np);
    if (masterTemplate == null)
    {
        Console.Error.WriteLine("    Could not find an HLS URL in now-playing-live. Raw:");
        Console.Error.WriteLine("    " + Preview(np));
        return 1;
    }
    var masterUrl = masterTemplate
        .Replace("%Live_Primary_HLS%", primaryRoot)
        .Replace("%Live_Secondary_HLS%", secondaryRoot ?? primaryRoot);
    Console.WriteLine($"    master: {masterUrl}");

    // (c) token params required on every akamai request.
    var token = SxmToken();
    var gup = GupId();
    var tokenParams = $"token={Uri.EscapeDataString(token ?? "")}&consumer=k2&gupId={Uri.EscapeDataString(gup ?? "")}";

    // Fetch the master playlist to confirm it's reachable + pick a variant.
    var masterText = await GetRawAsync(masterUrl + (masterUrl.Contains('?') ? "&" : "?") + tokenParams);
    if (masterText == null)
    {
        Console.Error.WriteLine("    Master playlist fetch failed (auth/token issue).");
        return 1;
    }
    Console.WriteLine("    ✓ master playlist fetched:");
    foreach (var l in masterText.Split('\n').Take(8)) Console.WriteLine("      " + l.Trim());

    var baseUri = new Uri(masterUrl);
    var variantRels = masterText.Split('\n')
        .Select(l => l.Trim())
        .Where(l => l.Length > 0 && !l.StartsWith('#'))
        .ToList();
    if (variantRels.Count == 0)
    {
        Console.Error.WriteLine("    No variant streams in master playlist.");
        return 1;
    }
    var variantUrl = new Uri(baseUri, variantRels[0]).ToString();
    var variantText = await GetRawAsync(variantUrl + (variantUrl.Contains('?') ? "&" : "?") + tokenParams);
    Console.WriteLine(variantText != null
        ? "    ✓ variant playlist fetched (segments listed)."
        : "    ✗ variant playlist fetch failed.");
    if (variantText == null) return 1;

    // Confirm the segment key line + that a segment actually downloads and decrypts.
    var keyLine = variantText.Split('\n').FirstOrDefault(l => l.Contains("#EXT-X-KEY", StringComparison.Ordinal));
    Console.WriteLine($"    key line: {keyLine?.Trim() ?? "(none)"}");
    var firstSeg = variantText.Split('\n').Select(l => l.Trim())
        .FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
    if (firstSeg != null)
    {
        var segUrl = new Uri(new Uri(variantUrl), firstSeg).ToString();
        var seg = await GetBytesAsync(segUrl + (segUrl.Contains('?') ? "&" : "?") + tokenParams);
        if (seg != null && seg.Length > 0)
        {
            var dec = TryDecryptSegment(seg, HlsAesKey(), keyLine);
            Console.WriteLine($"    ✓ first segment: {seg.Length} bytes encrypted → {dec?.Length ?? 0} bytes decrypted");
        }
        else
        {
            Console.Error.WriteLine("    ✗ segment download failed.");
            return 1;
        }
    }

    // (d) run the local proxy so a real player can consume it.
    var port = 8912;
    using var proxy = new SxmProxy(http, baseUri, variantUrl, tokenParams, HlsAesKey(), keyLine, port);
    proxy.Start();
    var localUrl = $"http://127.0.0.1:{port}/master.m3u8";
    Console.WriteLine();
    Console.WriteLine("RESULT: STREAM RESOLVED ✓  Local proxy running.");
    Console.WriteLine($"    Play it with:  ffplay -nodisp \"{localUrl}\"");
    Console.WriteLine($"            or:    vlc \"{localUrl}\"");
    Console.WriteLine("    Press Enter to stop the proxy…");
    Console.ReadLine();
    return 0;
}


// ── Helpers ─────────────────────────────────────────────────────────────────

Dictionary<string, object> Device() => new()
{
    ["resultTemplate"] = "web",
    ["deviceInfo"] = new Dictionary<string, object>
    {
        ["osVersion"] = "Windows",
        ["platform"] = "Web",
        ["sxmAppVersion"] = AppVersion,
        ["browser"] = "Firefox",
        ["browserVersion"] = "89.0",
        ["appRegion"] = region,
        ["deviceModel"] = DeviceModel,
        ["clientDeviceId"] = "null",
        ["player"] = "html5",
        ["clientDeviceType"] = "web",
    },
};

async Task<JsonElement?> PostAsync(
    string path, Dictionary<string, object> moduleRequest, string urlFormat, bool channelList = false)
{
    var module = new Dictionary<string, object> { ["moduleRequest"] = moduleRequest };
    if (channelList)
    {
        module["moduleArea"] = "Discovery";
        module["moduleType"] = "ChannelListing";
        module["moduleRequest"] = new Dictionary<string, object> { ["resultTemplate"] = "responsive" };
    }
    var envelope = new Dictionary<string, object>
    {
        ["moduleList"] = new Dictionary<string, object>
        {
            ["modules"] = new List<object> { module },
        },
    };

    var url = string.Format(urlFormat, path);
    var json = JsonSerializer.Serialize(envelope);
    using var content = new StringContent(json, Encoding.UTF8, "application/json");
    try
    {
        using var resp = await http.PostAsync(url, content);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            Console.Error.WriteLine($"    HTTP {(int)resp.StatusCode} for {path}: {Trim(text, 300)}");
        if (string.IsNullOrWhiteSpace(text)) return null;
        var root = JsonDocument.Parse(text).RootElement;
        return root.TryGetProperty("ModuleListResponse", out var mlr) ? mlr.Clone() : root.Clone();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"    request '{path}' threw: {ex.Message}");
        return null;
    }
}

// GET against a REST module path (used for get/configuration and tune/now-playing-live).
async Task<JsonElement?> GetAsync(string path, Dictionary<string, string> queryParams, string urlFormat)
{
    var qs = string.Join("&", queryParams.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    var url = string.Format(urlFormat, path) + "?" + qs;
    try
    {
        using var resp = await http.GetAsync(url);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            Console.Error.WriteLine($"    HTTP {(int)resp.StatusCode} for {path}: {Trim(text, 300)}");
        if (string.IsNullOrWhiteSpace(text)) return null;
        var root = JsonDocument.Parse(text).RootElement;
        return root.TryGetProperty("ModuleListResponse", out var mlr) ? mlr.Clone() : root.Clone();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"    GET '{path}' threw: {ex.Message}");
        return null;
    }
}

async Task<string?> GetRawAsync(string url)
{
    try
    {
        using var resp = await http.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"    HTTP {(int)resp.StatusCode} for {Trim(url, 120)}");
            return null;
        }
        return await resp.Content.ReadAsStringAsync();
    }
    catch (Exception ex) { Console.Error.WriteLine($"    raw GET threw: {ex.Message}"); return null; }
}

async Task<byte[]?> GetBytesAsync(string url)
{
    try
    {
        using var resp = await http.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsByteArrayAsync();
    }
    catch { return null; }
}

// The SXMAKTOKEN cookie carries "...=<token>,..." — extract the token value.
string? SxmToken()
{
    var c = cookies.GetAllCookies().FirstOrDefault(x =>
        string.Equals(x.Name, "SXMAKTOKEN", StringComparison.OrdinalIgnoreCase));
    if (c == null) return null;
    var v = c.Value;
    var eq = v.IndexOf('=');
    if (eq < 0) return v;
    var after = v[(eq + 1)..];
    var comma = after.IndexOf(',');
    return comma < 0 ? after : after[..comma];
}

// The SXMDATA cookie is URL-encoded JSON containing gupId.
string? GupId()
{
    var c = cookies.GetAllCookies().FirstOrDefault(x =>
        string.Equals(x.Name, "SXMDATA", StringComparison.OrdinalIgnoreCase));
    if (c == null) return null;
    try
    {
        using var doc = JsonDocument.Parse(Uri.UnescapeDataString(c.Value));
        return doc.RootElement.TryGetProperty("gupId", out var g) ? g.ToString() : null;
    }
    catch { return null; }
}

(string? primary, string? secondary) ExtractHlsRoots(JsonElement? cfg)
{
    if (cfg is not { } r) return (null, null);
    try
    {
        var components = r.GetProperty("moduleList").GetProperty("modules")[0]
            .GetProperty("moduleResponse").GetProperty("configuration").GetProperty("components");
        // Walk components → settings → relativeUrls, collecting named url entries.
        string? primary = null, secondary = null;
        foreach (var comp in components.EnumerateArray())
        {
            if (!comp.TryGetProperty("settings", out var settings)) continue;
            foreach (var s in settings.EnumerateArray())
            {
                if (!s.TryGetProperty("relativeUrls", out var rels)) continue;
                foreach (var u in rels.EnumerateArray())
                {
                    var name = Str(u, "name");
                    var url = Str(u, "url");
                    if (url == null) continue;
                    if (name == "Live_Primary_HLS") primary = url;
                    else if (name == "Live_Secondary_HLS") secondary = url;
                }
            }
        }
        return (primary, secondary);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"    HLS-root extract failed: {ex.Message}");
        return (null, null);
    }
}

// Find the master playlist URL (templated with %Live_*_HLS%) in the now-playing-live response.
string? ExtractMasterPlaylistUrl(JsonElement? np)
{
    if (np is not { } r) return null;
    try
    {
        var live = r.GetProperty("moduleList").GetProperty("modules")[0]
            .GetProperty("moduleResponse").GetProperty("liveChannelData");
        // Prefer customAudioInfos/hlsAudioInfos entries carrying a "url" with the %Live_*% template.
        foreach (var key in new[] { "hlsAudioInfos", "customAudioInfos" })
        {
            if (!live.TryGetProperty(key, out var arr)) continue;
            foreach (var info in arr.EnumerateArray())
            {
                var url = Str(info, "url");
                if (url != null && url.Contains("%Live_"))
                    return url;
            }
        }
        return null;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"    master-URL extract failed: {ex.Message}");
        return null;
    }
}

// AES-128-CBC decrypt one segment (SXM's static key; IV from the EXT-X-KEY line or zero).
static byte[]? TryDecryptSegment(byte[] data, byte[] key, string? keyLine)
{
    try
    {
        byte[] iv = new byte[16];
        if (keyLine != null)
        {
            var m = System.Text.RegularExpressions.Regex.Match(keyLine, "IV=0x([0-9A-Fa-f]+)");
            if (m.Success)
            {
                var hex = m.Groups[1].Value;
                for (int i = 0; i < 16 && i * 2 + 1 < hex.Length; i++)
                    iv[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
        }
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key; aes.IV = iv;
        aes.Mode = System.Security.Cryptography.CipherMode.CBC;
        aes.Padding = System.Security.Cryptography.PaddingMode.None;
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(data, 0, data.Length - (data.Length % 16));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"    decrypt failed: {ex.Message}");
        return null;
    }
}

List<Channel> ParseChannels(JsonElement? response)
{
    var result = new List<Channel>();
    if (response is not { } r) return result;
    try
    {
        var channels = r
            .GetProperty("moduleList").GetProperty("modules")[0]
            .GetProperty("moduleResponse").GetProperty("contentData")
            .GetProperty("channelListing").GetProperty("channels");
        foreach (var ch in channels.EnumerateArray())
        {
            string id = Str(ch, "channelId") ?? Str(ch, "id") ?? "";
            string name = Str(ch, "name") ?? "";
            string number = Str(ch, "channelNumber") ?? "";
            string guid = Str(ch, "channelGuid") ?? Str(ch, "guid") ?? "";
            if (!string.IsNullOrEmpty(id))
                result.Add(new Channel(id, name, number, guid));
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"    channel parse failed: {ex.Message}");
        Console.Error.WriteLine("    raw (first 800 chars): " + Preview(response));
    }
    return result;
}

static string? Str(JsonElement e, string prop) =>
    e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

bool HasCookie(string name) =>
    cookies.GetAllCookies().Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

static string StatusOf(JsonElement? r) =>
    r is { } e && e.ValueKind == JsonValueKind.Object ? "ok" : "null";

static string Preview(JsonElement? r) => r is { } e ? Trim(e.GetRawText(), 800) : "(null)";
static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
static string Mask(string s) => s.Length <= 3 ? "***" : s[..2] + new string('*', s.Length - 2);

static (string user, string pass, string region) LoadCredentials()
{
    var user = Environment.GetEnvironmentVariable("SXM_USER");
    var pass = Environment.GetEnvironmentVariable("SXM_PASS");
    var region = Environment.GetEnvironmentVariable("SXM_REGION") ?? "US";

    var local = Path.Combine(AppContext.BaseDirectory, "sxm.local.json");
    // Also check the project dir when run via `dotnet run` (BaseDirectory is bin/…).
    var projLocal = Path.Combine(Directory.GetCurrentDirectory(), "sxm.local.json");
    var path = File.Exists(local) ? local : File.Exists(projLocal) ? projLocal : null;
    if (path != null && (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass)))
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            user ??= root.TryGetProperty("username", out var u) ? u.GetString() : null;
            pass ??= root.TryGetProperty("password", out var p) ? p.GetString() : null;
            if (root.TryGetProperty("region", out var rg) && rg.GetString() is { Length: > 0 } rv)
                region = rv;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to read {path}: {ex.Message}");
        }
    }
    return (user ?? "", pass ?? "", region);
}

internal readonly record struct Channel(string ChannelId, string Name, string Number, string Guid)
{
    public int SortNumber => int.TryParse(Number, out var n) ? n : int.MaxValue;
}

/// <summary>
/// Minimal local HLS proxy: serves a rewritten master + variant playlist and the static AES key
/// so any HLS-capable player can play the SXM stream without knowing SXM's tokens. Segments are
/// fetched (with token params) and decrypted in transit, then served as plain AAC — the served
/// playlist therefore carries no EXT-X-KEY (already decrypted). Prototype only; single channel.
/// </summary>
internal sealed class SxmProxy : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _masterBase;
    private readonly string _variantUrl;
    private readonly string _tokenParams;
    private readonly byte[] _key;
    private readonly string? _keyLine;
    private readonly int _port;
    private readonly System.Net.HttpListener _listener = new();
    private CancellationTokenSource? _cts;

    public SxmProxy(HttpClient http, Uri masterBase, string variantUrl, string tokenParams,
        byte[] key, string? keyLine, int port)
    {
        _http = http; _masterBase = masterBase; _variantUrl = variantUrl;
        _tokenParams = tokenParams; _key = key; _keyLine = keyLine; _port = port;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public void Start()
    {
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            System.Net.HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(ctx), ct);
        }
    }

    private async Task HandleAsync(System.Net.HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        try
        {
            if (path.EndsWith("master.m3u8"))
            {
                // Single-variant master pointing at our own variant.m3u8.
                var body = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=256000\nvariant.m3u8\n";
                await WriteAsync(ctx, body, "application/vnd.apple.mpegurl");
            }
            else if (path.EndsWith("variant.m3u8"))
            {
                var raw = await FetchStringAsync(_variantUrl);
                var rewritten = RewriteVariant(raw ?? "");
                await WriteAsync(ctx, rewritten, "application/vnd.apple.mpegurl");
            }
            else if (path.EndsWith(".aac") || path.Contains("/seg/"))
            {
                // seg path is base64url of the absolute segment URL.
                var b64 = path[(path.LastIndexOf('/') + 1)..].Replace(".aac", "");
                var segUrl = System.Text.Encoding.UTF8.GetString(
                    Convert.FromBase64String(b64.Replace('-', '+').Replace('_', '/').PadRight((b64.Length + 3) / 4 * 4, '=')));
                var enc = await FetchBytesAsync(segUrl);
                var dec = enc == null ? null : TryDecrypt(enc);
                await WriteBytesAsync(ctx, dec ?? Array.Empty<byte>(), "audio/aac");
            }
            else ctx.Response.StatusCode = 404;
        }
        catch { try { ctx.Response.StatusCode = 500; } catch { } }
        finally { try { ctx.Response.OutputStream.Close(); } catch { } }
    }

    private string RewriteVariant(string text)
    {
        var sb = new StringBuilder();
        foreach (var lineRaw in text.Split('\n'))
        {
            var line = lineRaw.TrimEnd('\r');
            if (line.StartsWith("#EXT-X-KEY", StringComparison.Ordinal))
                continue; // segments served already-decrypted
            if (line.Length > 0 && !line.StartsWith('#'))
            {
                var abs = new Uri(new Uri(_variantUrl), line).ToString();
                var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(abs))
                    .Replace('+', '-').Replace('/', '_').TrimEnd('=');
                sb.Append("seg/").Append(b64).Append(".aac\n");
            }
            else sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    private byte[]? TryDecrypt(byte[] data)
    {
        byte[] iv = new byte[16];
        if (_keyLine != null)
        {
            var m = System.Text.RegularExpressions.Regex.Match(_keyLine, "IV=0x([0-9A-Fa-f]+)");
            if (m.Success)
            {
                var hex = m.Groups[1].Value;
                for (int i = 0; i < 16 && i * 2 + 1 < hex.Length; i++)
                    iv[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
        }
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = _key; aes.IV = iv;
        aes.Mode = System.Security.Cryptography.CipherMode.CBC;
        aes.Padding = System.Security.Cryptography.PaddingMode.None;
        using var d = aes.CreateDecryptor();
        return d.TransformFinalBlock(data, 0, data.Length - (data.Length % 16));
    }

    private async Task<string?> FetchStringAsync(string url)
    {
        var full = url + (url.Contains('?') ? "&" : "?") + _tokenParams;
        using var r = await _http.GetAsync(full);
        return r.IsSuccessStatusCode ? await r.Content.ReadAsStringAsync() : null;
    }

    private async Task<byte[]?> FetchBytesAsync(string url)
    {
        var full = url + (url.Contains('?') ? "&" : "?") + _tokenParams;
        using var r = await _http.GetAsync(full);
        return r.IsSuccessStatusCode ? await r.Content.ReadAsByteArrayAsync() : null;
    }

    private static async Task WriteAsync(System.Net.HttpListenerContext ctx, string body, string contentType)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
    }

    private static async Task WriteBytesAsync(System.Net.HttpListenerContext ctx, byte[] bytes, string contentType)
    {
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }
}
