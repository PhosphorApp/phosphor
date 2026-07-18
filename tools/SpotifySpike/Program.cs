using System.Net;
using System.Text.Json;

// Spotify discovery spike — pure C#, no browser/Python at runtime.
//
// Purpose: prove the *answerable* half of Spotify integration — that a SpotAPI-style discovery
// layer works from .NET: (1) turn a browser `sp_dc` cookie into a Spotify **web access token**,
// then (2) exercise `/v1/search` and `/v1/me/playlists` against the public web API.
//
// OUT OF SCOPE — on purpose: audio. Spotify web-player audio is per-track-key encrypted over the
// proprietary AP protocol (Widevine/EME); yt-dlp does NOT support it and there is no static-key
// trick like SiriusXM. Real Premium audio needs `librespot` (a separate, high-effort bridge).
// This spike therefore ends by printing that conclusion rather than pretending to resolve a stream.
//
// Why a cookie, not user/pass: SpotAPI shows headless user/pass login is CAPTCHA-gated (needs a
// CAPTCHA solver). The realistic, dependency-free auth for a spike is importing the `sp_dc` cookie
// from a logged-in browser (open.spotify.com -> DevTools -> Application -> Cookies -> sp_dc).
//
// Credentials: never hard-coded. Read from env var SPOTIFY_SP_DC, or a gitignored
// tools/SpotifySpike/spotify.local.json  ({ "sp_dc": "..." }).

// The plain get_access_token endpoint is now CDN-blocked (403 "URL Blocked / Error 54113")
// unless the request carries a TOTP one-time code + server-time. This is the newer host and the
// parameter set the web player sends. See BuildTokenUrlAsync / GenerateTotp below.
const string TokenHost = "https://open.spotify.com/api/token";
const string ServerTimeUrl = "https://open.spotify.com/api/server-time";
const string ApiBase = "https://api.spotify.com/v1";
const string UserAgent =
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
    "Chrome/125.0.0.0 Safari/537.36";

// The web player's TOTP shared secret (a rotating constant reverse-engineered from the bundle).
// If Spotify rotates it, the token step 403s again — that's the whole point of documenting the
// brittleness. Represented as the byte sequence the player maps to ASCII digits before HMAC.
const int TotpVersion = 5;
byte[] TotpSecretBytes() => new byte[]
{
    // "5507145693475469617262596236172047424544" transform used by the web player.
    53, 53, 48, 55, 49, 52, 53, 54, 57, 51,
    52, 55, 53, 52, 54, 57, 54, 49, 55, 50,
    54, 50, 53, 57, 54, 50, 51, 54, 49, 55,
    50, 48, 52, 55, 52, 50, 52, 53, 52, 52,
};

var spDc = LoadSpDc();
if (string.IsNullOrWhiteSpace(spDc))
{
    Console.Error.WriteLine(
        "No sp_dc cookie. Set SPOTIFY_SP_DC env var, or create tools/SpotifySpike/spotify.local.json " +
        "with { \"sp_dc\": \"<cookie from open.spotify.com>\" }.");
    return 2;
}
Console.WriteLine($"Spotify discovery spike — sp_dc '{Mask(spDc)}'");

var cookies = new CookieContainer();
cookies.Add(new Cookie("sp_dc", spDc, "/", ".spotify.com"));
using var handler = new HttpClientHandler { CookieContainer = cookies, UseCookies = true };
using var http = new HttpClient(handler);
http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

// ── Step 1: cookie -> web access token ──────────────────────────────────────
Console.WriteLine();
Console.WriteLine("[1] token   -> exchanging sp_dc for a web access token…");
var (token, isAnonymous, tokenRaw) = await GetAccessTokenAsync();
if (token is null)
{
    Console.Error.WriteLine("    ✗ Could not obtain an access token. Raw response:");
    Console.Error.WriteLine("    " + Trim(tokenRaw, 400));
    Console.Error.WriteLine();
    Console.Error.WriteLine("    NOTE: this now sends TOTP + Spotify server-time (the plain endpoint is");
    Console.Error.WriteLine("    CDN-blocked -> 403 'URL Blocked / Error 54113'). A remaining 403/400 means");
    Console.Error.WriteLine("    Spotify rotated the TOTP secret or shape — exactly the brittleness SpotAPI");
    Console.Error.WriteLine("    hides behind its session/solver code, and why this integration is 🟠 Hard.");
    return 1;
}
Console.WriteLine($"    ✓ token acquired (anonymous={isAnonymous}). This proves cookie->token in pure C#.");
if (isAnonymous)
    Console.WriteLine("    ⚠ token is ANONYMOUS — sp_dc was not accepted; /me endpoints will 401. " +
                      "Re-copy a fresh sp_dc from a logged-in browser.");
http.DefaultRequestHeaders.Authorization =
    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

// ── Step 2: identity (who are we?) ─────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("[2] me      -> GET /me …");
var me = await GetJsonAsync($"{ApiBase}/me");
if (me is { } meDoc && meDoc.TryGetProperty("id", out var idEl))
{
    var display = meDoc.TryGetProperty("display_name", out var dn) ? dn.GetString() : null;
    var product = meDoc.TryGetProperty("product", out var pr) ? pr.GetString() : null;
    Console.WriteLine($"    ✓ {display ?? "(no name)"}  id={idEl.GetString()}  product={product ?? "?"}");
    if (!string.Equals(product, "premium", StringComparison.OrdinalIgnoreCase))
        Console.WriteLine("    ⚠ product is not 'premium' — librespot audio (Path B) requires Premium.");
}
else
{
    Console.WriteLine("    ✗ /me returned no id (token likely anonymous — see the warning above).");
}

// ── Step 3: search (public discovery) ───────────────────────────────────────
var query = args.Length > 0 ? string.Join(' ', args) : "weezer";
Console.WriteLine();
Console.WriteLine($"[3] search  -> GET /search?q={query} (type=track)…");
var search = await GetJsonAsync(
    $"{ApiBase}/search?q={Uri.EscapeDataString(query)}&type=track&limit=10");
int trackCount = 0;
if (search is { } s && s.TryGetProperty("tracks", out var tracks) &&
    tracks.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
{
    foreach (var t in items.EnumerateArray())
    {
        trackCount++;
        var name = t.TryGetProperty("name", out var nm) ? nm.GetString() : "?";
        var artist = t.TryGetProperty("artists", out var ar) && ar.GetArrayLength() > 0 &&
                     ar[0].TryGetProperty("name", out var an) ? an.GetString() : "?";
        Console.WriteLine($"      {trackCount,2}. {artist} — {name}");
    }
}
Console.WriteLine(trackCount > 0
    ? $"    ✓ search works — {trackCount} track(s). This is the artist+title data a hybrid (Path A) match would use."
    : "    ✗ search returned no tracks.");

// ── Step 4: the user's playlists (private discovery) ────────────────────────
Console.WriteLine();
Console.WriteLine("[4] library -> GET /me/playlists …");
var playlists = await GetJsonAsync($"{ApiBase}/me/playlists?limit=20");
int plCount = 0;
if (playlists is { } pl && pl.TryGetProperty("items", out var plItems) &&
    plItems.ValueKind == JsonValueKind.Array)
{
    foreach (var p in plItems.EnumerateArray())
    {
        plCount++;
        var name = p.TryGetProperty("name", out var pn) ? pn.GetString() : "?";
        var total = p.TryGetProperty("tracks", out var tr) && tr.TryGetProperty("total", out var tt)
            ? tt.GetInt32() : 0;
        Console.WriteLine($"      {plCount,2}. {name}  ({total} tracks)");
    }
}
Console.WriteLine(plCount > 0
    ? $"    ✓ private library works — {plCount} playlist(s) enumerated."
    : "    ✗ no playlists (anonymous token, or an account with none).");

// ── Conclusion ──────────────────────────────────────────────────────────────
Console.WriteLine();
bool discoveryOk = trackCount > 0 || plCount > 0;
Console.WriteLine(discoveryOk
    ? "RESULT: DISCOVERY PROVEN ✓ — a SpotAPI-style browse/search client is fully doable in pure C#."
    : "RESULT: discovery NOT proven — token was likely anonymous; re-copy a fresh sp_dc and retry.");
Console.WriteLine();
Console.WriteLine("AUDIO (unchanged, still the blocker): this spike does NOT resolve a playable stream.");
Console.WriteLine("Spotify audio is per-track-key encrypted over the AP protocol; yt-dlp has no extractor");
Console.WriteLine("and there is no static-key trick like SiriusXM. Real Premium audio needs a librespot");
Console.WriteLine("bridge (Path B) — that is the next spike if we pursue Spotify. See SOURCE_PLUGIN_CANDIDATES.md.");
return discoveryOk ? 0 : 1;


// ── Helpers ─────────────────────────────────────────────────────────────────

async Task<(string? token, bool anonymous, string raw)> GetAccessTokenAsync()
{
    try
    {
        // 1. Ask Spotify for its server time (the TOTP is validated against Spotify's clock,
        //    not ours — this defeats naive local-time TOTP).
        long serverTimeSec;
        try
        {
            using var stResp = await http.GetAsync(ServerTimeUrl);
            var stText = await stResp.Content.ReadAsStringAsync();
            var stRoot = JsonDocument.Parse(stText).RootElement;
            serverTimeSec = stRoot.TryGetProperty("serverTime", out var st) && st.TryGetInt64(out var v)
                ? v
                : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        catch
        {
            serverTimeSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        // 2. Derive the TOTP from Spotify's server time + the web-player secret.
        var totp = GenerateTotp(TotpSecretBytes(), serverTimeSec);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var url =
            $"{TokenHost}?reason=transport&productType=web-player" +
            $"&totp={totp}&totpVer={TotpVersion}&ts={nowMs}";

        using var resp = await http.GetAsync(url);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            return (null, false, $"HTTP {(int)resp.StatusCode} (serverTime={serverTimeSec}, totp={totp}): {text}");
        var root = JsonDocument.Parse(text).RootElement;
        var token = root.TryGetProperty("accessToken", out var at) ? at.GetString() : null;
        var anon = root.TryGetProperty("isAnonymous", out var an) && an.ValueKind == JsonValueKind.True;
        return (token, anon, text);
    }
    catch (Exception ex)
    {
        return (null, false, ex.Message);
    }
}

// RFC 6238 TOTP (SHA-1, 6 digits, 30s step) over Spotify's server time. The web player uses this
// exact shape; the only "secret sauce" is the shared secret constant (TotpSecretBytes).
static string GenerateTotp(byte[] secret, long unixTimeSec)
{
    long counter = unixTimeSec / 30;
    var counterBytes = new byte[8];
    for (int i = 7; i >= 0; i--)
    {
        counterBytes[i] = (byte)(counter & 0xff);
        counter >>= 8;
    }
    using var hmac = new System.Security.Cryptography.HMACSHA1(secret);
    var hash = hmac.ComputeHash(counterBytes);
    int offset = hash[^1] & 0x0f;
    int binary =
        ((hash[offset] & 0x7f) << 24) |
        ((hash[offset + 1] & 0xff) << 16) |
        ((hash[offset + 2] & 0xff) << 8) |
        (hash[offset + 3] & 0xff);
    return (binary % 1_000_000).ToString("D6");
}

async Task<JsonElement?> GetJsonAsync(string url)
{
    try
    {
        using var resp = await http.GetAsync(url);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"    HTTP {(int)resp.StatusCode} for {Trim(url, 80)}: {Trim(text, 200)}");
            return null;
        }
        if (string.IsNullOrWhiteSpace(text)) return null;
        return JsonDocument.Parse(text).RootElement.Clone();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"    GET threw: {ex.Message}");
        return null;
    }
}

string? LoadSpDc()
{
    var env = Environment.GetEnvironmentVariable("SPOTIFY_SP_DC");
    if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

    var path = Path.Combine(AppContext.BaseDirectory, "spotify.local.json");
    // Also try the source dir (bin/Debug/net8.0 -> project root) for convenience.
    if (!File.Exists(path))
    {
        var alt = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "spotify.local.json");
        if (File.Exists(alt)) path = alt;
    }
    if (!File.Exists(path)) return null;
    try
    {
        var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.TryGetProperty("sp_dc", out var v) ? v.GetString() : null;
    }
    catch { return null; }
}

static string Mask(string s) =>
    s.Length <= 8 ? new string('*', s.Length) : s[..4] + new string('*', 6) + s[^4..];

static string Trim(string s, int n) =>
    string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
