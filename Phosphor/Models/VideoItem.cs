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
    /// Used by ThumbnailCacheConverter to refresh bindings after async download.
    /// </summary>
    public void NotifyPropertyChanged(string propertyName) =>
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// When set, the player uses this URL directly instead of resolving via YouTube.
    /// Used for Plex and other direct-stream sources.
    /// </summary>
    public string? StreamUrl { get; set; }

    public bool IsPlex => VideoId.StartsWith("plex:");

    /// <summary>
    /// True for real YouTube items. YouTube video IDs are plain identifiers with no
    /// "scheme:" prefix, so anything containing a colon (e.g. "plex:", "ditti:startup")
    /// is a non-YouTube source.
    /// </summary>
    public bool IsYouTube => !string.IsNullOrEmpty(VideoId) && !VideoId.Contains(':');

    /// <summary>
    /// True for Plex video items (non-music) which typically have portrait/poster thumbnails.
    /// </summary>
    public bool IsPlexVideo => IsPlex && PlexItemType == PlexItemType.None && !IsAudioOnly;

    /// <summary>
    /// For Plex music drill-down: indicates whether this item is an artist, album, or track.
    /// </summary>
    public PlexItemType PlexItemType { get; set; }

    /// <summary>
    /// Plex rating key used for drill-down navigation (e.g. artist → albums, album → tracks).
    /// </summary>
    public string? PlexRatingKey { get; set; }

    /// <summary>
    /// For Plex hub items: the hub key used to fetch hub contents.
    /// </summary>
    public string? PlexHubKey { get; set; }

    /// <summary>
    /// For Plex hub items: the hub type (artist, album, etc.).
    /// </summary>
    public string? PlexHubType { get; set; }

    /// <summary>
    /// When true, this item contains only audio (e.g. Plex music track) and should not attempt video rendering.
    /// </summary>
    public bool IsAudioOnly { get; set; }

    /// <summary>
    /// For Plex items, indicates what audio stream selection was made (native stereo, transcode, or other).
    /// </summary>
    public PlexAudioStream PlexAudioStream { get; set; }

    /// <summary>
    /// Chapter markers for Plex video items, if available.
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
