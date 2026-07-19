using System.Text.Json;

// iHeartRadio PODCAST / on-demand spike — pure C#, no auth.
//
// Context: the shipped Phosphor.Plugins.IHeartRadio source only does LIVE radio — continuous,
// ad-supported streams with no track boundaries. This probes iHeart's ON-DEMAND podcast surface to
// confirm it is the clean finite/seekable, ad-light counterpart worth building next.
//
// Three unknowns, all against the key-less api.iheart.com (no OAuth, no key):
//   (1) DISCOVERY — /api/v3/podcast/categories → categories; /api/v3/podcast/categories/{id} lists
//                   its podcasts inline. (/api/v3/search/all?...&podcast=true also returns podcasts
//                   under results.podcasts for a keyword.)
//   (2) EPISODES  — /api/v3/podcast/podcasts/{id}/episodes → episode list with real durations
//                   (finite, seekable — unlike the live streams).
//   (3) PLAYBACK  — /api/v3/podcast/episodes/{id} → a direct 'mediaUrl' MP3 we can hand to LibVLC.
//
// No credentials, nothing to configure. Just `dotnet run`. Optional search term as an argument.

const string ApiBase = "https://api.iheart.com";
const string UserAgent = "PhosphorIHeartPodcastSpike/1.0";

using var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
http.DefaultRequestHeaders.Accept.ParseAdd("application/json");

var query = args.Length > 0 ? string.Join(' ', args) : "comedy";
int ok = 0, fail = 0;

Console.WriteLine("iHeartRadio PODCAST spike — UNAUTHENTICATED (no OAuth, no key)");
Console.WriteLine($"query = \"{query}\"");
Console.WriteLine();

// ── 1: categories (discovery tree root) ──────────────────────────────────────
Console.WriteLine("[1] cats    -> GET /api/v3/podcast/categories …");
int? firstCategoryId = null;
var cats = await GetAsync("/api/v3/podcast/categories");
if (cats is { } c && c.TryGetProperty("categories", out var carr) && carr.ValueKind == JsonValueKind.Array)
{
    int n = 0;
    foreach (var cat in carr.EnumerateArray())
    {
        n++;
        var id = cat.TryGetProperty("id", out var i) && i.TryGetInt32(out var iv) ? iv : 0;
        var name = cat.TryGetProperty("name", out var nm) ? nm.GetString() : "?";
        firstCategoryId ??= id > 0 ? id : null;
        if (n <= 10) Console.WriteLine($"      - {id,-5} {name}");
    }
    if (n > 10) Console.WriteLine($"      … (+{n - 10} more)");
    Report(n > 0, $"{n} podcast categories");
}
else Report(false, "no categories list");

// ── 2: podcasts within a category ────────────────────────────────────────────
Console.WriteLine();
var catId = firstCategoryId ?? 132; // 132 = "Podcast Top 100"
Console.WriteLine($"[2] pods    -> GET /api/v3/podcast/categories/{catId} (podcasts) …");
string? firstPodcastId = null;
var catDetail = await GetAsync($"/api/v3/podcast/categories/{catId}");
if (catDetail is { } cd && cd.TryGetProperty("podcasts", out var parr) && parr.ValueKind == JsonValueKind.Array)
{
    int n = 0;
    foreach (var p in parr.EnumerateArray())
    {
        n++;
        var id = p.TryGetProperty("id", out var i) ? i.GetRawText() : "?";
        var title = p.TryGetProperty("title", out var t) ? t.GetString() : "?";
        if (firstPodcastId is null && p.TryGetProperty("id", out var pid)) firstPodcastId = pid.GetRawText();
        if (n <= 5) Console.WriteLine($"      - [{id}] {title}");
    }
    Report(n > 0, $"category '{catId}' listed {n} podcast(s)");
}
else Report(false, "no podcasts in category");

// ── 3: keyword search (podcasts for a term) ──────────────────────────────────
Console.WriteLine();
Console.WriteLine($"[3] search  -> GET /api/v3/search/all?keywords={query}&podcast=true (podcasts) …");
var search = await GetAsync(
    $"/api/v3/search/all?keywords={Uri.EscapeDataString(query)}&maxRows=5&podcast=true");
if (search is { } s && s.TryGetProperty("results", out var results))
{
    // Podcasts appear under results.podcasts (id + title) when &podcast=true is set. Prefer a
    // search hit for the episode probe.
    if (results.TryGetProperty("podcasts", out var pods) && pods.ValueKind == JsonValueKind.Array
        && pods.GetArrayLength() > 0)
    {
        int n = 0;
        foreach (var p in pods.EnumerateArray())
        {
            if (++n > 5) break;
            var id = p.TryGetProperty("id", out var i) ? i.GetRawText() : "?";
            var title = p.TryGetProperty("title", out var t) ? t.GetString() : "?";
            if (firstPodcastId is null && p.TryGetProperty("id", out var pid)) firstPodcastId = pid.GetRawText();
            Console.WriteLine($"      - [{id}] {title}");
        }
        Report(true, $"search returned {n} podcast(s)");
    }
    else Report(true, "search ok (no podcast hits for this term — using category podcast)");
}
else Report(false, "search returned no results node");

// ── 4: episodes of a podcast (finite, with durations) ────────────────────────
Console.WriteLine();
string? firstEpisodeId = null;
if (firstPodcastId is not null)
{
    Console.WriteLine($"[4] eps     -> GET /api/v3/podcast/podcasts/{firstPodcastId}/episodes …");
    var eps = await GetAsync($"/api/v3/podcast/podcasts/{firstPodcastId}/episodes?limit=3");
    if (eps is { } e && e.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
    {
        int n = 0;
        foreach (var ep in data.EnumerateArray())
        {
            n++;
            var id = ep.TryGetProperty("id", out var i) ? i.GetRawText() : "?";
            var title = ep.TryGetProperty("title", out var t) ? t.GetString() : "?";
            var dur = ep.TryGetProperty("duration", out var d) && d.TryGetInt32(out var ds) ? $"{ds}s" : "?";
            if (firstEpisodeId is null && id != "?") firstEpisodeId = id;
            Console.WriteLine($"      {n}. [{id}] {title}  ({dur})");
        }
        Report(n > 0, $"podcast '{firstPodcastId}' listed {n} episode(s) with durations (finite!)");
    }
    else Report(false, "no episode data");
}
else Console.WriteLine("[4] eps     -> SKIPPED (no podcast id from discovery)");

// ── 5: episode -> direct mediaUrl MP3 (the playback crux) ────────────────────
Console.WriteLine();
if (firstEpisodeId is not null)
{
    Console.WriteLine($"[5] media   -> GET /api/v3/podcast/episodes/{firstEpisodeId} (mediaUrl) …");
    var epDetail = await GetAsync($"/api/v3/podcast/episodes/{firstEpisodeId}");
    string? mediaUrl = null;
    if (epDetail is { } ed && ed.TryGetProperty("episode", out var episode)
        && episode.TryGetProperty("mediaUrl", out var mu) && mu.ValueKind == JsonValueKind.String)
        mediaUrl = mu.GetString();

    if (!string.IsNullOrWhiteSpace(mediaUrl))
    {
        Console.WriteLine($"      mediaUrl = {Trim(mediaUrl!, 120)}");
        // Confirm it's a real, fetchable audio file (finite Content-Length, audio/* type).
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, mediaUrl);
            using var resp = await http.SendAsync(head);
            var type = resp.Content.Headers.ContentType?.ToString() ?? "?";
            var len = resp.Content.Headers.ContentLength;
            Console.WriteLine($"      HEAD {(int)resp.StatusCode}  type={type}  length={(len is { } l ? $"{l / 1024}KB" : "?")}");
            Report(resp.IsSuccessStatusCode && type.StartsWith("audio", StringComparison.OrdinalIgnoreCase),
                "episode mediaUrl is a real, seekable audio file");
        }
        catch (Exception ex) { Report(false, $"media HEAD failed: {ex.Message}"); }
    }
    else Report(false, "episode had no mediaUrl");
}
else Console.WriteLine("[5] media   -> SKIPPED (no episode id)");

// ── Conclusion ───────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine($"RESULT: {ok} ok / {fail} failed.");
Console.WriteLine(fail == 0
    ? "SUCCESS — iHeart's PODCAST surface is key-less and finite/seekable. Discovery\n" +
      "(categories → podcasts, plus keyword search) + episode lists (real durations) + a direct\n" +
      "mediaUrl MP3 per episode. This is the clean jukebox fit the live streams aren't: no\n" +
      "IsLiveStream, seekable, and far less ad-laden. A natural second capability for the iHeart\n" +
      "plug-in (browse-tree: Podcasts → category → podcast → episodes; resolve → mediaUrl)."
    : "PARTIAL — some endpoints failed unauthenticated; see notes above for which half held.");
return fail == 0 ? 0 : 1;


// ── Helpers ──────────────────────────────────────────────────────────────────

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
