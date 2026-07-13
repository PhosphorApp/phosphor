using Phosphor.Plugin.Abstractions;
using PluginChapterMarker = Phosphor.Plugin.Abstractions.ChapterMarker;

namespace Phosphor.Plugins.Plex;

/// <summary>
/// Adapts the host's Plex types (<see cref="VideoItem"/>, <see cref="PlexLibrary"/>,
/// <see cref="PlexHub"/>, <see cref="PlexPlaylist"/>) to the plug-in abstraction types.
/// Pure, behavior-preserving translation — the Plex REST logic stays in
/// <see cref="PlexService"/>; this only shapes data for the plug-in contract.
/// </summary>
internal static class PlexMappings
{
    // ── Items ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps a Plex <see cref="VideoItem"/> to a <see cref="SourceItem"/>. Container items
    /// (artist/album) become <see cref="SourceItem.IsContainer"/> so the host drills in via
    /// <see cref="IBrowsable"/>; playable items carry their pre-built stream through
    /// <see cref="SourceItem.SourceState"/>.
    /// </summary>
    public static SourceItem ToSourceItem(VideoItem v, string instanceId)
    {
        bool isContainer = v.PlexItemType is PlexItemType.Artist or PlexItemType.Album;
        return new SourceItem
        {
            SourceInstanceId = instanceId,
            ItemId = v.VideoId,
            Title = v.Title,
            Subtitle = string.IsNullOrEmpty(v.Author) ? null : v.Author,
            ThumbnailUrl = v.ThumbnailUrl,
            IsAudioOnly = v.IsAudioOnly,
            IsContainer = isContainer,
            Duration = v.Duration,
            Chapters = v.Chapters?.Select(ToPluginChapter).ToList(),
            // Keep the whole source VideoItem so resolve/metadata need no re-fetch, and
            // container drill-down can read PlexItemType/PlexRatingKey.
            SourceState = v,
        };
    }

    /// <summary>Recovers the source <see cref="VideoItem"/> from a <see cref="SourceItem"/>.</summary>
    public static VideoItem? VideoItemOf(SourceItem item) => item.SourceState as VideoItem;

    // ── Categories ─────────────────────────────────────────────────────────────

    /// <summary>Maps a configured library mapping to a root <see cref="SourceCategory"/>.</summary>
    public static SourceCategory ToRootCategory(PlexLibraryMapping lib, string instanceId) => new()
    {
        SourceInstanceId = instanceId,
        CategoryId = $"library:{lib.Key}",
        Title = lib.Title,
        HasSubCategories = true,
        SourceState = new PlexNode(PlexNodeKind.Library, lib.Key, lib.Type),
    };

    /// <summary>Maps a container <see cref="VideoItem"/> (artist/album/hub/playlist) to a browse node.</summary>
    public static SourceCategory ToCategory(VideoItem v, string instanceId, PlexNode node) => new()
    {
        SourceInstanceId = instanceId,
        CategoryId = v.VideoId,
        Title = v.Title,
        ThumbnailUrl = v.ThumbnailUrl,
        HasSubCategories = node.Kind is not PlexNodeKind.Album, // albums expand straight to tracks
        SourceState = node,
    };

    /// <summary>Maps a Plex hub to a browse node.</summary>
    public static SourceCategory ToCategory(PlexHub hub, string instanceId) => new()
    {
        SourceInstanceId = instanceId,
        CategoryId = $"hub:{hub.HubKey}",
        Title = hub.Title,
        HasSubCategories = false,
        SourceState = new PlexNode(PlexNodeKind.Hub, hub.HubKey, hub.Type),
    };

    /// <summary>Maps a Plex playlist to a browse node.</summary>
    public static SourceCategory ToCategory(PlexPlaylist pl, string instanceId) => new()
    {
        SourceInstanceId = instanceId,
        CategoryId = $"playlist:{pl.RatingKey}",
        Title = pl.Title,
        ThumbnailUrl = pl.Thumb,
        HasSubCategories = false,
        SourceState = new PlexNode(PlexNodeKind.Playlist, pl.RatingKey),
    };

    // ── Playback / metadata ────────────────────────────────────────────────────

    /// <summary>
    /// Maps a resolved Plex <see cref="VideoItem"/> to a <see cref="ResolvedStream"/>. Plex
    /// items carry a ready-to-play HTTP <see cref="VideoItem.StreamUrl"/>, so there is no
    /// separate resolution step — audio tracks are audio-only, everything else muxed.
    /// </summary>
    public static ResolvedStream? ToResolvedStream(VideoItem v)
    {
        if (string.IsNullOrEmpty(v.StreamUrl)) return null;
        var layout = v.IsAudioOnly ? StreamLayout.AudioOnly : StreamLayout.Muxed;
        return new ResolvedStream(StreamTransport.Http, layout, v.StreamUrl, null, null);
    }

    /// <summary>Builds <see cref="SourceMetadata"/> from a Plex item's known duration + chapters.</summary>
    public static SourceMetadata ToSourceMetadata(VideoItem v) => new(
        v.Duration,
        null,
        v.Chapters?.Select(ToPluginChapter).ToList() ?? [],
        v.UploadDate);

    private static PluginChapterMarker ToPluginChapter(Phosphor.ChapterMarker c) =>
        new(c.Title, c.StartTime, c.EndTime);

    // ── Reverse mapping (plug-in → host VideoItem) ─────────────────────────────

    /// <summary>
    /// Recovers a host <see cref="VideoItem"/> from a browse <see cref="SourceItem"/>. Plex
    /// items carry their original <see cref="VideoItem"/> in <see cref="SourceItem.SourceState"/>
    /// (set by <see cref="ToSourceItem"/>), so this is a direct unwrap with a defensive fallback.
    /// </summary>
    public static VideoItem ToVideoItem(SourceItem item) =>
        item.SourceState as VideoItem ?? new VideoItem
        {
            Title = item.Title,
            Author = item.Subtitle ?? "",
            ThumbnailUrl = item.ThumbnailUrl ?? "",
            VideoId = item.ItemId,
            Duration = item.Duration,
            IsAudioOnly = item.IsAudioOnly,
        };

    /// <summary>
    /// Rebuilds a container <see cref="VideoItem"/> (artist/album) from a browse
    /// <see cref="SourceCategory"/> whose <see cref="SourceCategory.SourceState"/> is a
    /// <see cref="PlexNode"/>. Used when a drill-down yields sub-categories (e.g. an artist's
    /// albums) that the UI still represents as <see cref="VideoItem"/>s.
    /// </summary>
    public static VideoItem ToContainerVideoItem(SourceCategory category)
    {
        var node = category.SourceState as PlexNode;
        var itemType = node?.Kind switch
        {
            PlexNodeKind.Artist => PlexItemType.Artist,
            PlexNodeKind.Album => PlexItemType.Album,
            _ => PlexItemType.None,
        };
        return new VideoItem
        {
            Title = category.Title,
            ThumbnailUrl = category.ThumbnailUrl ?? "",
            VideoId = category.CategoryId,
            PlexItemType = itemType,
            PlexRatingKey = node?.Key,
        };
    }
}
