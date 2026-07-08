using System.Net.Http;

namespace Phosphor.Search;

/// <summary>
/// Creates the configured <see cref="ISearchEngine"/> implementation. This is the single
/// switch point between YoutubeExplode and (later) yt-dlp for the discovery path.
/// </summary>
public static class SearchEngineFactory
{
    public static ISearchEngine Create(SearchEngineKind kind, HttpClient? http = null) => kind switch
    {
        // YtDlp search is added in a later phase; fall back to YoutubeExplode until then.
        _ => new YoutubeExplodeSearchEngine(http),
    };
}
