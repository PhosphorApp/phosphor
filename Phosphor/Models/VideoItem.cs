using CommunityToolkit.Mvvm.ComponentModel;

namespace Phosphor;

public class VideoItem : ObservableObject
{
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string ThumbnailUrl { get; set; } = "";
    public string VideoId { get; set; } = "";

    /// <summary>
    /// Raises PropertyChanged for the specified property name.
    /// </summary>
    public void NotifyPropertyChanged(string propertyName) =>
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// Returns a shallow copy of this item. Used when persisting a variant of an item (e.g. the queue
    /// saver strips ephemeral live-stream URLs on a copy so the live original stays playable in-session).
    /// </summary>
    public VideoItem ShallowCopy() => (VideoItem)MemberwiseClone();

    /// <summary>
    /// When set, the player uses this URL directly instead of resolving via YouTube.
    /// Used for Plex and other direct-stream sources.
    /// </summary>
    public string? StreamUrl { get; set; }

    /// <summary>
    /// Optional separate audio-slave URL to attach when <see cref="StreamUrl"/> is a video-only
    /// stream (yt-dlp <c>SeparateVideoAudio</c>, e.g. Vimeo/Dailymotion). When set, the player adds
    /// it as an audio slave input; null for muxed/direct streams that carry their own audio.
    /// </summary>
    public string? AudioStreamUrl { get; set; }

    /// <summary>
    /// The plug-in source instance that produced this playable item, when known. Lets the host
    /// route metadata/gapless/caching requests back to the owning source instead of guessing from
    /// the id shape (the Plex/YouTube fallback). Null for legacy items or the built-in engine path.
    /// </summary>
    public string? SourceInstanceId { get; set; }

    /// <summary>
    /// True for real YouTube items. YouTube video IDs are plain identifiers with no
    /// "scheme:" prefix, so anything containing a colon (e.g. "plex:", "ditti:startup")
    /// is a non-YouTube source.
    /// </summary>
    public bool IsYouTube => !string.IsNullOrEmpty(VideoId) && !VideoId.Contains(':');

    /// <summary>
    /// When true, this item contains only audio (e.g. a music track) and should not attempt video rendering.
    /// </summary>
    public bool IsAudioOnly { get; set; }

    /// <summary>
    /// True when this item plays as audio by default but a <em>video</em> version likely exists on the
    /// host's video source (e.g. an iHeart "video podcast" episode whose video lives on YouTube). The
    /// UI shows an optional "watch video" (TV) button that opportunistically resolves the video via
    /// <see cref="VideoSearchQuery"/> and falls back to this item's audio when no match is found.
    /// </summary>
    public bool HasVideoAlternative { get; set; }

    /// <summary>
    /// Best-effort query the host runs against its video source (YouTube) to find the video version
    /// when <see cref="HasVideoAlternative"/> is set (e.g. <c>"Show Name" Episode Title</c>). Null when
    /// the source couldn't supply one.
    /// </summary>
    public string? VideoSearchQuery { get; set; }

    /// <summary>
    /// When true, this item is a continuous live stream with no fixed duration or seekable timeline
    /// (e.g. a SiriusXM radio channel). The host suppresses the progress bar/seek, shows elapsed time
    /// as "M:SS / *", and does not auto-advance the playlist when it "ends".
    /// </summary>
    public bool IsLiveStream { get; set; }

    /// <summary>
    /// True when the host should decorate this item's thumbnail with a small red "live" corner dot to
    /// highlight a <em>currently-broadcasting</em> feed among finite items (e.g. a Twitch channel's
    /// live stream shown atop its VODs). A pure display hint, distinct from <see cref="IsLiveStream"/>:
    /// sources whose items are all live (e.g. SiriusXM) leave this false so they don't badge every row.
    /// </summary>
    public bool ShowLiveBadge { get; set; }

    private bool _showUnavailableBadge;
    /// <summary>
    /// True when the host should decorate this item's thumbnail with a small ⊘ "unavailable" corner
    /// badge because a previous play attempt failed. A soft, <em>retryable</em> hint (distinct from
    /// <see cref="IsPlayable"/> = false, which removes the action buttons): the row stays playable so
    /// the user can retry, and the badge clears on the next successful play. Observable so a row can
    /// flip live when a play attempt fails or succeeds.
    /// </summary>
    public bool ShowUnavailableBadge
    {
        get => _showUnavailableBadge;
        set => SetProperty(ref _showUnavailableBadge, value);
    }

    /// <summary>
    /// For live-stream leaves, the originating plug-in <c>SourceItem</c> (opaque to the host) kept so
    /// the stream can be resolved <em>lazily at play time</em> rather than eagerly during browse
    /// (which would fire one authenticated request per channel). Null for non-live items.
    /// </summary>
    public object? PendingLiveSourceItem { get; set; }

    /// <summary>
    /// For finite items from a source with <c>IDeferredStreamResolution</c> (e.g. Vimeo via yt-dlp),
    /// the originating plug-in <c>SourceItem</c> kept so the stream is resolved <em>lazily at play
    /// time</em> instead of eagerly per search/browse row (which would fire one yt-dlp probe each).
    /// Unlike <see cref="PendingLiveSourceItem"/> this carries no live semantics. Null otherwise.
    /// </summary>
    public object? PendingResolveSourceItem { get; set; }

    /// <summary>
    /// True when this row came from the host-level aggregated Favorites tile — it carries only display
    /// data (<see cref="SourceInstanceId"/> + <see cref="VideoId"/>), so the play path must first call
    /// the owning source's <c>IFavoritable.GetFavorite(VideoId)</c> to rebuild a resolvable item.
    /// </summary>
    public bool IsAggregatedFavorite { get; set; }

    /// <summary>
    /// True when this row is a non-interactive section header (e.g. a provider group label in the
    /// grouped Favorites view). The player/queue/favorite commands ignore header rows, and the list
    /// renders them as a plain label.
    /// </summary>
    public bool IsHeader { get; set; }
    /// <summary>The label text for a header row (see <see cref="IsHeader"/>).</summary>
    public string? HeaderText { get; set; }

    /// <summary>
    /// True when this row is a layout separator marker in the Custom-order Favorites view — a thin
    /// vertical divider tile. Non-interactive; ignored by play/queue/favorite commands.
    /// </summary>
    public bool IsSeparator { get; set; }

    /// <summary>
    /// True when this row is a layout line-break marker in the Custom-order Favorites view — a
    /// full-width zero-height row that forces subsequent tiles onto a new line. Non-interactive.
    /// </summary>
    public bool IsLineBreak { get; set; }

    /// <summary>
    /// Grouping key for the aggregated Favorites view (the provider label) when "Group by provider" is
    /// active — used by the ListBox's CollectionView grouping to render full-width provider headers.
    /// Null when ungrouped.
    /// </summary>
    public string? GroupKey { get; set; }

    /// <summary>
    /// True when this item's owning source supports favorites (implements <c>IFavoritable</c>), so the
    /// UI shows a star toggle on its row. Set by the host when building the item.
    /// </summary>
    public bool CanFavorite { get; set; }

    private bool _isFavorite;
    /// <summary>Whether this item is currently favorited. Observable so the star reflects toggles live.</summary>
    public bool IsFavorite
    {
        get => _isFavorite;
        set => SetProperty(ref _isFavorite, value);
    }

    private bool _isPlayable = true;
    /// <summary>
    /// Whether this item can actually be played. Defaults to <c>true</c>. A source may surface an item
    /// it knows it cannot resolve (e.g. a SoundCloud track previously seen to fail with DRM) with this
    /// set to <c>false</c>; the row then renders as unplayable (action buttons removed, a "no entry"
    /// indicator shown) instead of being hidden. Observable so a row can flip live when a play attempt
    /// fails definitively.
    /// </summary>
    public bool IsPlayable
    {
        get => _isPlayable;
        set => SetProperty(ref _isPlayable, value);
    }

    // ── Generic plug-in browse (source-agnostic drill-down) ──
    /// <summary>When true, this result is a browsable container: activating it drills in via the
    /// generic browse stack rather than playing. Carries <see cref="GenericSourceInstanceId"/> +
    /// <see cref="GenericSourceState"/> so the source resolves the node.</summary>
    public bool IsGenericContainer { get; set; }
    /// <summary>The plug-in source instance id for a generic browse container/leaf.</summary>
    public string? GenericSourceInstanceId { get; set; }
    /// <summary>The opaque <c>SourceCategory.SourceState</c> handed back to the source on drill-in.</summary>
    public object? GenericSourceState { get; set; }
    /// <summary>The opaque <c>SourceCategory.CategoryId</c> for a generic browse container.</summary>
    public string? GenericCategoryId { get; set; }
    /// <summary>Optional glyph/emoji shown on a generic browse container tile (e.g. a Plex library's
    /// music note, inherited by its Hubs/Playlists sub-tiles when they don't set their own).</summary>
    public string? ContainerIcon { get; set; }

    /// <summary>
    /// A short, human-readable audio-stream tag for the status bar (e.g. " (Stereo)", " (Surround)"),
    /// or "" when there's nothing noteworthy. Source-populated so the play path stays source-agnostic:
    /// a source (e.g. Plex) sets it to reflect the audio selection it made.
    /// </summary>
    public string AudioTag { get; set; } = "";

    /// <summary>
    /// Chapter markers for video items, if available.
    /// </summary>
    public List<ChapterMarker>? Chapters { get; set; }

    private TimeSpan? _duration;
    public TimeSpan? Duration
    {
        get => _duration;
        set
        {
            if (SetProperty(ref _duration, value))
                OnPropertyChanged(nameof(DurationText));
        }
    }

    /// <summary>
    /// Video upload date, lazily populated only when full metadata is fetched (on play /
    /// duration refresh) — never during search. Null until then, or if the source has none.
    /// </summary>
    private DateTimeOffset? _uploadDate;
    public DateTimeOffset? UploadDate
    {
        get => _uploadDate;
        set
        {
            if (SetProperty(ref _uploadDate, value))
            {
                OnPropertyChanged(nameof(UploadDateText));
                OnPropertyChanged(nameof(DetailTextDurationFirst));
                OnPropertyChanged(nameof(DetailTextDurationThenAuthor));
            }
        }
    }

    /// <summary>
    /// Upload date formatted with the user's locale short-date pattern (e.g. MM/dd/yyyy
    /// or dd/MM/yyyy). Formatted from the value's own offset (no local-time conversion) so
    /// a UTC-midnight date never shifts a day. Empty when no date is known.
    /// </summary>
    public string UploadDateText =>
        UploadDate is { } d ? d.ToString("d", System.Globalization.CultureInfo.CurrentCulture) : "";

    public string DurationText => Duration switch
    {
        { TotalHours: >= 1 } d => d.ToString(@"h\:mm\:ss"),
        { } d => d.ToString(@"m\:ss"),
        _ => ""
    };

    public string DetailText =>
        string.IsNullOrWhiteSpace(Author)
            ? DurationText
            : string.IsNullOrEmpty(DurationText)
                ? Author
                : $"{Author} \u00B7 {DurationText}";

    /// <summary>
    /// "duration · author" ordering for the compact two-row result layout, with the upload date
    /// appended when known (e.g. "42:15 · Some Author · 7/24/2026"). The date uses the local system's
    /// short-date pattern (MM/dd/yyyy vs dd/MM/yyyy). Falls back gracefully when any part is missing.
    /// </summary>
    public string DetailTextDurationThenAuthor
    {
        get
        {
            var baseText =
                string.IsNullOrEmpty(DurationText)
                    ? Author
                    : string.IsNullOrWhiteSpace(Author)
                        ? DurationText
                        : $"{DurationText} \u00B7 {Author}";

            if (UploadDateText.Length == 0)
                return baseText;

            return baseText.Length == 0 ? UploadDateText : $"{baseText} \u00B7 {UploadDateText}";
        }
    }

    public string DetailTextDurationFirst
    {
        get
        {
            // Base: "duration · author" (or whichever is present).
            var baseText =
                string.IsNullOrWhiteSpace(Author)
                    ? DurationText
                    : string.IsNullOrEmpty(DurationText)
                        ? Author
                        : $"{DurationText} \u00B7 {Author}";

            // Append the upload date after the author when both are available.
            if (!string.IsNullOrWhiteSpace(Author) && UploadDateText.Length > 0)
                return $"{baseText} \u00B7 {UploadDateText}";

            return baseText;
        }
    }
}
