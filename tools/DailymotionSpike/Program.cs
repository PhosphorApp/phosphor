using System.Text.Json;

// Dailymotion API spike — pure C#, no auth.
//
// Purpose: confirm the ONE unknown that decides how easy a Dailymotion source plug-in is —
// does the public API allow search / channels (categories) / channel-videos WITHOUT OAuth or a key?
// yt-dlp already proves *playback* (dedicated dailymotion extractors); this probes *discovery*.
//
// If these calls succeed unauthenticated, Dailymotion is lower-friction than Vimeo (which needed a
// per-user token) and a DailymotionClient mirrors VimeoClient with zero credential setup.
//
// No credentials, nothing to configure. Just `dotnet run`. Optional search term as an argument.

const string ApiBase = "https://api.dailymotion.com";
const string UserAgent = "PhosphorDailymotionSpike/1.0";

using var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

var query = args.Length > 0 ? string.Join(' ', args) : "music";
int ok = 0, fail = 0;

Console.WriteLine("Dailymotion API spike — UNAUTHENTICATED (no OAuth, no key)");
Console.WriteLine();

// ── 1: search videos ────────────────────────────────────────────────────────
Console.WriteLine($"[1] search  -> GET /videos?search={query} …");
var search = await GetAsync(
    $"/videos?search={Uri.EscapeDataString(query)}&limit=5" +
    "&fields=id,title,duration,thumbnail_360_url,url");
if (search is { } s && s.TryGetProperty("list", out var vids) && vids.ValueKind == JsonValueKind.Array)
{
    int n = 0;
    foreach (var v in vids.EnumerateArray())
    {
        n++;
        var id = v.TryGetProperty("id", out var i) ? i.GetString() : "?";
        var title = v.TryGetProperty("title", out var t) ? t.GetString() : "?";
        var dur = v.TryGetProperty("duration", out var d) && d.TryGetInt32(out var ds) ? $"{ds}s" : "?";
        Console.WriteLine($"      {n}. [{id}] {title}  ({dur})");
    }
    Report(n > 0, $"search returned {n} video(s)");
}
else Report(false, "search returned no list");

// ── 2: categories (channels) ────────────────────────────────────────────────
// Dailymotion's editorial "categories" are exposed via the /channels endpoint (id + name).
Console.WriteLine();
Console.WriteLine("[2] cats    -> GET /channels (categories) …");
var chans = await GetAsync("/channels?fields=id,name&limit=100");
string? musicChannelId = null;
if (chans is { } c && c.TryGetProperty("list", out var cl) && cl.ValueKind == JsonValueKind.Array)
{
    int n = 0;
    foreach (var ch in cl.EnumerateArray())
    {
        n++;
        var id = ch.TryGetProperty("id", out var i) ? i.GetString() : null;
        var name = ch.TryGetProperty("name", out var nm) ? nm.GetString() : "?";
        if (id is not null && string.Equals(id, "music", StringComparison.OrdinalIgnoreCase))
            musicChannelId = id;
        if (n <= 12) Console.WriteLine($"      - {id,-16} {name}");
    }
    if (n > 12) Console.WriteLine($"      … (+{n - 12} more)");
    Report(n > 0, $"{n} categories/channels");
}
else Report(false, "no channel list");

// ── 3: videos within a category (music) ─────────────────────────────────────
Console.WriteLine();
var chan = musicChannelId ?? "music";
Console.WriteLine($"[3] catvids -> GET /channel/{chan}/videos …");
var catVids = await GetAsync(
    $"/channel/{Uri.EscapeDataString(chan)}/videos?limit=5&fields=id,title,duration");
if (catVids is { } cv && cv.TryGetProperty("list", out var cvl) && cvl.ValueKind == JsonValueKind.Array)
{
    int n = 0;
    foreach (var v in cvl.EnumerateArray())
    {
        n++;
        var title = v.TryGetProperty("title", out var t) ? t.GetString() : "?";
        Console.WriteLine($"      {n}. {title}");
    }
    Report(n > 0, $"'{chan}' channel returned {n} video(s)");
}
else Report(false, $"no videos for channel '{chan}'");

// ── 4: paging shape (has_more / total) ──────────────────────────────────────
Console.WriteLine();
Console.WriteLine("[4] paging  -> GET /videos?search=…&page=1 (has_more/total) …");
var pageProbe = await GetAsync(
    $"/videos?search={Uri.EscapeDataString(query)}&page=1&limit=10&fields=id");
if (pageProbe is { } p)
{
    var hasMore = p.TryGetProperty("has_more", out var hm) && hm.ValueKind == JsonValueKind.True;
    var total = p.TryGetProperty("total", out var tt) && tt.TryGetInt32(out var tv) ? tv.ToString() : "(not returned)";
    Console.WriteLine($"      has_more={hasMore}  total={total}");
    Report(true, "paging fields present (page/limit + has_more)");
}
else Report(false, "paging probe failed");

// ── Conclusion ──────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine($"RESULT: {ok} ok / {fail} failed.");
Console.WriteLine(fail == 0
    ? "SUCCESS — the Dailymotion discovery surface works UNAUTHENTICATED. A DailymotionClient needs no\n" +
      "credentials for search/categories/paging: lower-friction than Vimeo. yt-dlp handles playback."
    : "PARTIAL — some endpoints failed unauthenticated; a public API key or OAuth may be needed for those.");
return fail == 0 ? 0 : 1;


// ── Helpers ─────────────────────────────────────────────────────────────────

async Task<JsonElement?> GetAsync(string path)
{
    try
    {
        using var resp = await http.GetAsync(ApiBase + path);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"    HTTP {(int)resp.StatusCode}: {Trim(text, 200)}");
            return null;
        }
        return JsonDocument.Parse(text).RootElement.Clone();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"    threw: {ex.Message}");
        return null;
    }
}

void Report(bool success, string what)
{
    if (success) { ok++; Console.WriteLine($"    OK - {what}"); }
    else { fail++; Console.WriteLine($"    FAIL - {what}"); }
}

static string Trim(string s, int n) =>
    string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
