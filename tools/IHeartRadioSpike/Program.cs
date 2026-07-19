using System.Text.Json;

// iHeartRadio API spike — pure C#, no auth.
//
// Purpose: confirm the TWO unknowns that decide how feasible an iHeartRadio source plug-in is.
// yt-dlp only has *partial* iHeart support, so unlike Vimeo/Dailymotion we cannot lean on it for
// resolution — we need to see if the public REST API hands back raw stream URLs directly.
//
//   (1) DISCOVERY — does GET /api/v*/catalog/searchAll return live stations + podcasts + tracks
//                   WITHOUT an API key / OAuth?
//   (2) PLAYBACK  — does GET /api/v2/content/liveStations/{id} expose a raw HLS/PLS/Shoutcast
//                   stream URL we can hand to LibVLC? And do podcast episodes expose direct audio?
//
// Endpoints per https://github.com/api-evangelist/iheart-radio (host: api.iheart.com).
// The catalog endpoints are documented as accessible without API-key auth.
//
// No credentials, nothing to configure. Just `dotnet run`. Optional search term as an argument.

const string ApiBase = "https://api.iheart.com";
const string UserAgent = "PhosphorIHeartRadioSpike/1.0";

using var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
http.DefaultRequestHeaders.Accept.ParseAdd("application/json");

var query = args.Length > 0 ? string.Join(' ', args) : "rock";
int ok = 0, fail = 0;

Console.WriteLine("iHeartRadio API spike — UNAUTHENTICATED (no OAuth, no key)");
Console.WriteLine($"query = \"{query}\"");
Console.WriteLine();

// ── 1: catalog search (stations + podcasts + tracks) ─────────────────────────
Console.WriteLine($"[1] search  -> GET /api/v1/catalog/searchAll?keywords={query} …");
string? liveStationId = null;
string? podcastId = null;
var search = await GetAsync(
    $"/api/v1/catalog/searchAll?keywords={Uri.EscapeDataString(query)}" +
    "&maxRows=5&bundle=false&startIndex=0");
if (search is { } s)
{
    // Results are grouped at the TOP level by type: stations[], tracks[], artists[],
    // talkShows[] (podcasts), etc. Ids live on each element (station has 'id'; talkShow 'id').
    liveStationId = FirstId(s, "stations", "id");
    podcastId = FirstId(s, "talkShows", "id");
    DumpGroup(s, "stations", "name");
    DumpGroup(s, "talkShows", "title", "name");
    DumpGroup(s, "tracks", "title", "name");
    DumpGroup(s, "artists", "name");
    Report(liveStationId is not null || podcastId is not null,
        $"search returned stations={liveStationId ?? "-"} talkShows(podcasts)={podcastId ?? "-"}");
}
else Report(false, "search request failed");

// ── 2: live station -> stream URLs (the crux for playback) ───────────────────
Console.WriteLine();
liveStationId ??= "1469"; // fallback: a well-known iHeart live station id if search shape differs
Console.WriteLine($"[2] stream  -> GET /api/v2/content/liveStations/{liveStationId} …");
var station = await GetAsync($"/api/v2/content/liveStations/{Uri.EscapeDataString(liveStationId)}");
if (station is { } st)
{
    // Streams typically under hits[0].streams { hls_stream, shoutcast_stream, pls_stream, secure_* }
    JsonElement node = st;
    if (st.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array
        && hits.GetArrayLength() > 0)
        node = hits[0];

    if (node.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Object)
    {
        int urls = 0;
        foreach (var stream in streams.EnumerateObject())
        {
            if (stream.Value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(stream.Value.GetString()))
            {
                urls++;
                Console.WriteLine($"      {stream.Name,-22} {stream.Value.GetString()}");
            }
        }
        Report(urls > 0, $"live station exposed {urls} raw stream URL(s)");
    }
    else Report(false, "no 'streams' object on live station");
}
else Report(false, "live station request failed");

// ── 3: podcast episodes -> direct audio URLs ─────────────────────────────────
Console.WriteLine();
if (podcastId is not null)
{
    Console.WriteLine($"[3] podeps  -> GET /api/v3/podcast/podcasts/{podcastId}/episodes …");
    var eps = await GetAsync(
        $"/api/v3/podcast/podcasts/{Uri.EscapeDataString(podcastId)}/episodes?limit=3");
    if (eps is { } e && e.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
    {
        int n = 0, withAudio = 0;
        foreach (var ep in data.EnumerateArray())
        {
            n++;
            var title = ep.TryGetProperty("title", out var t) ? t.GetString() : "?";
            var mediaUrl = ep.TryGetProperty("mediaUrl", out var m) ? m.GetString()
                : ep.TryGetProperty("streamUrl", out var su) ? su.GetString() : null;
            if (!string.IsNullOrWhiteSpace(mediaUrl)) withAudio++;
            Console.WriteLine($"      {n}. {title}");
            if (mediaUrl is not null) Console.WriteLine($"         audio: {mediaUrl}");
        }
        Report(withAudio > 0, $"{withAudio}/{n} episode(s) exposed a direct audio URL");
    }
    else Report(false, "podcast episodes shape unexpected / empty");
}
else
{
    Console.WriteLine("[3] podeps  -> SKIPPED (search returned no podcast id)");
}

// ── Conclusion ───────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine($"RESULT: {ok} ok / {fail} failed.");
Console.WriteLine(fail == 0
    ? "SUCCESS — iHeart's discovery + raw stream URLs are reachable UNAUTHENTICATED. A native\n" +
      "IHeartClient (pure HttpClient, no yt-dlp) can browse/search and resolve live-station HLS\n" +
      "(IsLiveStream) plus on-demand podcast audio — mirroring the SiriusXM/Dailymotion shape."
    : "PARTIAL — some endpoints failed unauthenticated. Live stations may need the IsLiveStream host\n" +
      "path; podcasts are the cleaner finite/seekable fit. See notes above for which half held.");
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

// Pull the first id from results.<group>[0].<idField>, tolerating array-or-object shapes.
static string? FirstId(JsonElement results, string group, string idField)
{
    if (!results.TryGetProperty(group, out var g)) return null;
    JsonElement arr = g;
    if (g.ValueKind == JsonValueKind.Object && g.TryGetProperty("hits", out var h)) arr = h;
    if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) return null;
    var first = arr[0];
    if (first.TryGetProperty(idField, out var id))
        return id.ValueKind == JsonValueKind.Number ? id.GetRawText() : id.GetString();
    return null;
}

void DumpGroup(JsonElement results, string group, params string[] nameFields)
{
    if (!results.TryGetProperty(group, out var g)) return;
    JsonElement arr = g;
    if (g.ValueKind == JsonValueKind.Object && g.TryGetProperty("hits", out var h)) arr = h;
    if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) return;
    Console.WriteLine($"      {group}:");
    int n = 0;
    foreach (var item in arr.EnumerateArray())
    {
        if (++n > 5) break;
        string? name = null;
        foreach (var f in nameFields)
            if (item.TryGetProperty(f, out var nv) && nv.ValueKind == JsonValueKind.String)
            { name = nv.GetString(); break; }
        Console.WriteLine($"        - {name ?? "?"}");
    }
}

void Report(bool success, string what)
{
    if (success) { ok++; Console.WriteLine($"    OK - {what}"); }
    else { fail++; Console.WriteLine($"    FAIL - {what}"); }
}

static string Trim(string s, int n) =>
    string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
