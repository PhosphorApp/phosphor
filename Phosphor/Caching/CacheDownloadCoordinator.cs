namespace Phosphor;

/// <summary>
/// Process-wide guard that prevents two cache paths (the on-disk <see cref="VideoCache"/> and the
/// next-track <see cref="PrefetchCache"/>) from downloading the SAME video id at the same time.
/// </summary>
/// <remarks>
/// Both caches shell out to the same YouTube engine. Even though the engine serializes downloads on
/// its own gate, letting both paths queue a fetch for the same id doubles the request volume against
/// YouTube for that id in a short window — extra pressure that feeds the 403 throttle. This coordinator
/// makes the second caller for an id a no-op until the first completes. It is intentionally a simple
/// in-progress set (not a lock): callers that lose the race skip rather than wait.
/// </remarks>
internal static class CacheDownloadCoordinator
{
    private static readonly object _lock = new();
    private static readonly HashSet<string> _inProgress = new(StringComparer.Ordinal);

    /// <summary>
    /// Attempts to claim <paramref name="videoId"/> for downloading. Returns true if the caller now
    /// owns the download (and must call <see cref="End"/> when done); false if another cache path is
    /// already downloading this id, in which case the caller should skip.
    /// </summary>
    public static bool TryBegin(string videoId)
    {
        lock (_lock)
        {
            return _inProgress.Add(videoId);
        }
    }

    /// <summary>Releases a claim taken by <see cref="TryBegin"/>. Safe to call for an unknown id.</summary>
    public static void End(string videoId)
    {
        lock (_lock)
        {
            _inProgress.Remove(videoId);
        }
    }
}
