namespace Phosphor.Playback;

using System.ComponentModel;
using System.Runtime.CompilerServices;

/// <summary>
/// The per-player state + command channel between a now-playing bar and a <see cref="JukeboxPlayer"/>/
/// host window. Phase 2: this is now the per-player STATE holder — each player (Backglass = Player 1,
/// Topper = Player 2) owns its own <see cref="CurrentlyPlaying"/>, scrubber position/duration, volume,
/// paused/transitioning flags, and audio-only setting, so two windows play independently and each bar
/// binds to its own context. It still carries the playback COMMAND events (play / stop / pause / resume /
/// seek / volume) the host engine subscribes to.
///
/// Shared *services* (cache, prefetch, stream resolution, source registry) stay on the VM and are reached
/// via <see cref="JukeboxPlayer.Model"/>. The VM re-exposes <c>Player1</c>'s state as pass-throughs so the
/// existing single now-playing bar keeps working unchanged.
/// </summary>
public sealed class PlayerContext : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    /// <summary>
    /// Resolves the short source annotation (e.g. "(from YouTube)") for the now-playing item. Injected
    /// by the VM because it needs the source registry; takes the item and whether it played from cache.
    /// </summary>
    public Func<VideoItem?, bool, string>? SourceTextResolver { get; set; }

    /// <summary>
    /// This player's own play queue (Phase 3). Set by the VM at construction. Player 1 (Backglass)
    /// persists to <c>queue.json</c>; Player 2 (Topper) to <c>queue_topper.json</c>.
    /// </summary>
    public PlayerQueue Queue { get; set; } = null!;

    // ── Now-playing state (per player) ──

    private VideoItem? _currentlyPlaying;
    public VideoItem? CurrentlyPlaying
    {
        get => _currentlyPlaying;
        set
        {
            var outgoing = _currentlyPlaying;
            if (SetProperty(ref _currentlyPlaying, value))
            {
                IsPaused = false;
                _lastChapterIndex = -1;
                _currentChapterName = "";
                _chapterTickPositions = [];
                _isCurrentFromCache = false;
                OnPropertyChanged(nameof(IsPlaying));
                OnPropertyChanged(nameof(CurrentChapterName));
                OnPropertyChanged(nameof(ChapterTickPositions));
                OnPropertyChanged(nameof(ShouldSnapToChapters));
                OnPropertyChanged(nameof(NowPlayingTitle));
                OnPropertyChanged(nameof(NowPlayingSourceText));
                OnPropertyChanged(nameof(IsLiveStream));
                OnPropertyChanged(nameof(PlaybackTimeText));
                CurrentlyPlayingChanged?.Invoke(outgoing, value);
            }
        }
    }

    /// <summary>Raised after <see cref="CurrentlyPlaying"/> changes with (outgoing, incoming) so the VM
    /// can run source-specific side effects (e.g. release a held tuner on the outgoing item).</summary>
    public event Action<VideoItem?, VideoItem?>? CurrentlyPlayingChanged;

    private double _playbackPosition;
    public double PlaybackPosition
    {
        get => _playbackPosition;
        set
        {
            if (SetProperty(ref _playbackPosition, value))
            {
                OnPropertyChanged(nameof(PlaybackTimeText));
                UpdateCurrentChapter();
            }
        }
    }

    private double _playbackDuration = 1;
    public double PlaybackDuration
    {
        get => _playbackDuration;
        set
        {
            if (SetProperty(ref _playbackDuration, value))
            {
                OnPropertyChanged(nameof(PlaybackTimeText));
                OnPropertyChanged(nameof(ShouldSnapToChapters));
                UpdateChapterTickPositions();
                UpdateCurrentChapter();
            }
        }
    }

    private int _volume = 100;
    public int Volume
    {
        get => _volume;
        set
        {
            if (SetProperty(ref _volume, Math.Clamp(value, 0, 100)))
                RaiseVolumeChanged(_volume);
        }
    }

    private bool _isPaused;
    public bool IsPaused
    {
        get => _isPaused;
        set => SetProperty(ref _isPaused, value);
    }

    private bool _playTransitioning;
    public bool PlayTransitioning
    {
        get => _playTransitioning;
        set => SetProperty(ref _playTransitioning, value);
    }

    private bool _audioOnly;
    /// <summary>When true, this player plays audio with no video surface (video stays on another screen).</summary>
    public bool AudioOnly
    {
        get => _audioOnly;
        set
        {
            if (SetProperty(ref _audioOnly, value))
                AudioOnlyChanged?.Invoke(value);
        }
    }

    /// <summary>Raised when <see cref="AudioOnly"/> changes so the host can apply it.</summary>
    public event Action<bool>? AudioOnlyChanged;

    private bool _isSeeking;
    public bool IsSeeking { get => _isSeeking; set => SetProperty(ref _isSeeking, value); }

    // ── Cache-source annotation ──

    private bool _isCurrentFromCache;
    public void SetCurrentFromCache(bool fromCache)
    {
        if (_isCurrentFromCache == fromCache) return;
        _isCurrentFromCache = fromCache;
        OnPropertyChanged(nameof(NowPlayingSourceText));
    }

    // ── Derived now-playing getters ──

    public bool IsPlaying => _currentlyPlaying != null;

    public bool IsLiveStream => _currentlyPlaying?.IsLiveStream == true;

    public string NowPlayingTitle
    {
        get
        {
            var title = _currentlyPlaying?.Title ?? "Nothing playing";
            if (!string.IsNullOrEmpty(_currentChapterName))
                title = $"{title} \u2014 {_currentChapterName}";
            var dateText = _currentlyPlaying?.UploadDateText;
            if (!string.IsNullOrEmpty(dateText))
                title = $"{title} ({dateText})";
            return title;
        }
    }

    public string NowPlayingSourceText =>
        _currentlyPlaying == null ? "" : (SourceTextResolver?.Invoke(_currentlyPlaying, _isCurrentFromCache) ?? "");

    public string PlaybackTimeText
    {
        get
        {
            if (_currentlyPlaying?.IsLiveStream == true)
            {
                var elapsed = TimeSpan.FromMilliseconds(Math.Max(0, _playbackPosition));
                var lfmt = elapsed.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss";
                return $"{elapsed.ToString(lfmt)} / *";
            }
            if (_playbackDuration <= 1) return "0:00 / 0:00";
            var pos = TimeSpan.FromMilliseconds(_playbackPosition);
            var dur = TimeSpan.FromMilliseconds(_playbackDuration);
            var fmt = dur.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss";
            return $"{pos.ToString(fmt)} / {dur.ToString(fmt)}";
        }
    }

    // ── Chapters ──

    private string _currentChapterName = "";
    public string CurrentChapterName
    {
        get => _currentChapterName;
        private set
        {
            if (SetProperty(ref _currentChapterName, value))
                OnPropertyChanged(nameof(NowPlayingTitle));
        }
    }

    /// <summary>Re-raises <see cref="NowPlayingTitle"/> after the current item's own fields (e.g. upload
    /// date) are enriched in place, so the now-playing bar refreshes without a full item swap.</summary>
    public void NotifyNowPlayingTitleChanged() => OnPropertyChanged(nameof(NowPlayingTitle));

    private List<double> _chapterTickPositions = [];
    public List<double> ChapterTickPositions
    {
        get => _chapterTickPositions;
        private set => SetProperty(ref _chapterTickPositions, value);
    }

    public bool ShouldSnapToChapters => (_currentlyPlaying?.Chapters?.Count ?? 0) >= 3 && _playbackDuration > 1;

    private int _lastChapterIndex = -1;

    /// <summary>Recomputes the chapter tick fractions from the current item + duration.</summary>
    public void UpdateChapterTickPositions()
    {
        var chapters = _currentlyPlaying?.Chapters;
        var duration = _playbackDuration;
        if (chapters == null || chapters.Count == 0 || duration <= 1)
        {
            ChapterTickPositions = [];
            return;
        }
        ChapterTickPositions = chapters
            .Select(c => c.StartTime.TotalMilliseconds / duration)
            .Where(p => p > 0 && p < 1)
            .ToList();
    }

    /// <summary>Called when chapters are restored from cache on the currently playing item.</summary>
    public void NotifyCachedChaptersRestored()
    {
        UpdateChapterTickPositions();
        OnPropertyChanged(nameof(ShouldSnapToChapters));
        UpdateCurrentChapter();
    }

    private void UpdateCurrentChapter()
    {
        var chapters = _currentlyPlaying?.Chapters;
        if (chapters == null || chapters.Count == 0 || _playbackDuration <= 1)
        {
            if (_lastChapterIndex != -1)
            {
                _lastChapterIndex = -1;
                CurrentChapterName = "";
            }
            return;
        }
        int idx = GetCurrentChapterIndex(chapters);
        if (idx != _lastChapterIndex)
        {
            _lastChapterIndex = idx;
            CurrentChapterName = chapters[idx].Title;
        }
    }

    public int GetCurrentChapterIndex(List<ChapterMarker> chapters)
    {
        var posMs = _playbackPosition;
        for (int i = chapters.Count - 1; i >= 0; i--)
        {
            if (posMs >= chapters[i].StartTime.TotalMilliseconds)
                return i;
        }
        return 0;
    }

    // ── Command events (host engine subscribes to these) ──

    /// <summary>Raised to ask the host engine to play the item with the given id.</summary>
    public event Action<string>? PlayRequested;

    /// <summary>Raised to ask the host engine to stop playback.</summary>
    public event Action? StopRequested;

    /// <summary>Raised to ask the host engine to pause playback.</summary>
    public event Action? PauseRequested;

    /// <summary>Raised to ask the host engine to resume playback.</summary>
    public event Action? ResumeRequested;

    /// <summary>Raised to ask the host engine to seek to the given position (ms).</summary>
    public event Action<long>? SeekRequested;

    /// <summary>Raised to ask the host engine to change volume (0–100).</summary>
    public event Action<int>? VolumeChanged;

    public void RaisePlayRequested(string videoId) => PlayRequested?.Invoke(videoId);
    public void RaiseStopRequested() => StopRequested?.Invoke();
    public void RaisePauseRequested() => PauseRequested?.Invoke();
    public void RaiseResumeRequested() => ResumeRequested?.Invoke();
    public void RaiseSeekRequested(long timeMs) => SeekRequested?.Invoke(timeMs);
    public void RaiseVolumeChanged(int volume) => VolumeChanged?.Invoke(volume);

    // ── Pass-through subscription helpers (used by the VM to re-expose its event surface) ──
    public void AddPlayRequested(Action<string> handler) => PlayRequested += handler;
    public void RemovePlayRequested(Action<string> handler) => PlayRequested -= handler;
    public void AddStopRequested(Action handler) => StopRequested += handler;
    public void RemoveStopRequested(Action handler) => StopRequested -= handler;
    public void AddPauseRequested(Action handler) => PauseRequested += handler;
    public void RemovePauseRequested(Action handler) => PauseRequested -= handler;
    public void AddResumeRequested(Action handler) => ResumeRequested += handler;
    public void RemoveResumeRequested(Action handler) => ResumeRequested -= handler;
    public void AddSeekRequested(Action<long> handler) => SeekRequested += handler;
    public void RemoveSeekRequested(Action<long> handler) => SeekRequested -= handler;
    public void AddVolumeChanged(Action<int> handler) => VolumeChanged += handler;
    public void RemoveVolumeChanged(Action<int> handler) => VolumeChanged -= handler;
}
