namespace Phosphor.Playback;

/// <summary>
/// Owns the AutoDJ fill/orchestration that previously lived on <c>JukeboxViewModel</c>: the used-id
/// de-dup set, the shuffle RNG, the refill thresholds, and the genre/video fill bodies. It operates on
/// a given <see cref="PlayerContext"/> (and its <see cref="PlayerQueue"/>) passed as a parameter, so
/// either player's queue can be refilled independently.
///
/// All couplings back to the VM (status text, source search, UI marshalling, next-track playback,
/// active-genre resolution, history titles) are injected as delegates at construction, keeping the
/// engine free of VM/UI types while behavior stays byte-for-byte identical.
/// </summary>
public sealed class AutoDjService
{
    private const int RefillThreshold = 5;
    private const int BatchSize = 10;

    private readonly HashSet<string> _usedIds = new();
    private readonly Random _rng = new();

    private readonly Action<string> _setStatus;
    private readonly Func<string, IAsyncEnumerable<VideoItem>> _search;
    private readonly Func<Action, System.Threading.Tasks.Task> _runOnUi;
    private readonly Action<PlayerContext> _playNext;
    private readonly Func<Category?> _resolveActiveGenre;
    private readonly Func<string?> _mostRecentHistoryTitle;
    private readonly Action<string, Exception> _logException;

    public AutoDjService(
        Action<string> setStatus,
        Func<string, IAsyncEnumerable<VideoItem>> search,
        Func<Action, System.Threading.Tasks.Task> runOnUi,
        Action<PlayerContext> playNext,
        Func<Category?> resolveActiveGenre,
        Func<string?> mostRecentHistoryTitle,
        Action<string, Exception> logException)
    {
        _setStatus = setStatus;
        _search = search;
        _runOnUi = runOnUi;
        _playNext = playNext;
        _resolveActiveGenre = resolveActiveGenre;
        _mostRecentHistoryTitle = mostRecentHistoryTitle;
        _logException = logException;
    }

    /// <summary>Clears the used-id de-dup set (called when AutoDJ is switched off).</summary>
    public void ClearUsedIds() => _usedIds.Clear();

    /// <summary>Randomizes <paramref name="items"/> in place with the service RNG (Fisher-Yates), used by
    /// the queue Shuffle command so shuffle and AutoDJ share one deterministic RNG source.</summary>
    public void Shuffle(IList<VideoItem> items)
    {
        for (int i = items.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    /// <summary>Refills <paramref name="player"/>'s queue via AutoDJ when it is running low.</summary>
    public async System.Threading.Tasks.Task FillAsync(PlayerContext player)
    {
        var pq = player.Queue;
        if (pq.IsAutoDjFilling || !pq.AutoDjEnabled) return;

        // Only refill when fewer than 5 items remain ahead of the current position
        int remaining = pq.Queue.Count - Math.Max(pq.QueueIndex, 0);
        if (remaining >= RefillThreshold && pq.Queue.Count > 0) return;

        pq.IsAutoDjFilling = true;
        int targetSize = pq.Queue.Count + BatchSize;

        try
        {
            var genre = _resolveActiveGenre();
            if (genre != null)
                await FillFromGenre(player, genre, targetSize);
            else
                await FillFromVideo(player, targetSize);

            if (pq.AutoDjEnabled && pq.Queue.Count > 0)
                _setStatus($"AutoDJ active — {pq.Queue.Count} in queue");
        }
        finally
        {
            pq.IsAutoDjFilling = false;
        }
    }

    private async System.Threading.Tasks.Task FillFromGenre(PlayerContext player, Category genre, int targetSize)
    {
        var pq = player.Queue;
        _setStatus($"AutoDJ: browsing {genre.Name}...");

        try
        {
            // Load a larger pool from this genre to pick randomly from
            var results = new List<VideoItem>();
            var enumerator = _search(genre.SearchTerm).GetAsyncEnumerator();
            try
            {
                int fetched = 0;
                while (fetched < 50 && await enumerator.MoveNextAsync())
                {
                    results.Add(enumerator.Current);
                    fetched++;
                }
            }
            finally
            {
                try { await enumerator.DisposeAsync(); }
                catch { /* enumerator may be faulted */ }
            }

            // Shuffle and pick items not already queued/played
            var shuffled = results.OrderBy(_ => _rng.Next()).ToList();

            await _runOnUi(() =>
            {
                foreach (var item in shuffled)
                {
                    if (pq.Queue.Count >= targetSize) break;
                    var videoId = item.VideoId;
                    if (_usedIds.Contains(videoId)) continue;
                    if (pq.Queue.Any(q => q.VideoId == videoId)) continue;
                    if (player.CurrentlyPlaying?.VideoId == videoId) continue;

                    pq.Queue.Add(item);
                    _usedIds.Add(videoId);
                    _setStatus($"AutoDJ queued: {item.Title}");

                    if (player.CurrentlyPlaying == null)
                        _playNext(player);
                }
            });
        }
        catch (Exception ex)
        {
            _logException("AutoDJ genre", ex);
        }
    }

    private async System.Threading.Tasks.Task FillFromVideo(PlayerContext player, int targetSize)
    {
        var pq = player.Queue;
        // Use the title of the currently playing (or most recent) track to find similar content,
        // since the channel/author name often doesn't reflect the actual music.
        string? query = player.CurrentlyPlaying?.Title;

        if (string.IsNullOrWhiteSpace(query))
            query = _mostRecentHistoryTitle();

        if (string.IsNullOrWhiteSpace(query))
        {
            _setStatus("AutoDJ: no track info available");
            return;
        }

        _setStatus($"AutoDJ: finding similar to {query}...");

        try
        {
            // Fetch a page of results and randomize so we don't always pick the same top results
            var pool = new List<VideoItem>();
            var enumerator = _search(query).GetAsyncEnumerator();
            try
            {
                int fetched = 0;
                while (fetched < 50 && await enumerator.MoveNextAsync())
                {
                    pool.Add(enumerator.Current);
                    fetched++;
                }
            }
            finally
            {
                try { await enumerator.DisposeAsync(); }
                catch { /* enumerator may be faulted */ }
            }

            var shuffled = pool.OrderBy(_ => _rng.Next()).ToList();

            await _runOnUi(() =>
            {
                foreach (var item in shuffled)
                {
                    if (pq.Queue.Count >= targetSize) break;
                    var videoId = item.VideoId;
                    if (_usedIds.Contains(videoId)) continue;
                    if (pq.Queue.Any(q => q.VideoId == videoId)) continue;
                    if (player.CurrentlyPlaying?.VideoId == videoId) continue;

                    pq.Queue.Add(item);
                    _usedIds.Add(videoId);
                    _setStatus($"AutoDJ queued: {item.Title}");

                    if (player.CurrentlyPlaying == null)
                        _playNext(player);
                }
            });
        }
        catch (Exception ex)
        {
            _logException("AutoDJ video", ex);
        }
    }
}
