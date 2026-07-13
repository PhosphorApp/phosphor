using System.Runtime.CompilerServices;
using System.Text.Json;
using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.Plex;

/// <summary>
/// In-box Plex source. Wraps the existing <see cref="PlexService"/> REST client and presents
/// its search + drill-down + playback surface through the plug-in contract. Implements
/// <see cref="IBrowsable"/> (the hierarchical shape that stress-tests
/// <see cref="SourceCategory"/>/<see cref="BrowseResult"/>) and <see cref="IConfigurable"/>
/// (the "browse libraries" setup action). Multiple instances (two Plex servers) are supported
/// via the provider.
/// </summary>
/// <remarks>
/// In-box, so it uses <see cref="PlexService"/>, <see cref="VideoItem"/>, and the Plex enums
/// directly. Pure data producer: no UI, no thread assumptions.
/// </remarks>
public sealed class PlexSource : IPhosphorSource, ITextSearchCapable, IBrowsable, IPagedBrowsable, IPlayableResolver, IConfigurable
{
    private readonly PlexService _plex = new();
    private IPluginHost? _host;

    private string _serverUrl = "";
    private string _token = "";
    private bool _stereoAudio;
    private List<PlexLibraryMapping> _libraries = [];

    public PlexSource(string instanceId, IReadOnlyDictionary<string, string?> settings)
    {
        InstanceId = instanceId;
        ApplySettingsInternal(settings);
    }

    public string InstanceId { get; }
    public string TypeId => PlexSourceProvider.PlexTypeId;
    public string DisplayName { get; set; } = "Plex";

    public bool IsConfigured => _plex.IsConfigured;
    public bool IsEnabled { get; set; } = true;

    public Task InitializeAsync(IPluginHost host, CancellationToken ct = default)
    {
        _host = host;
        return Task.CompletedTask;
    }

    public void ApplySettings(IReadOnlyDictionary<string, string?> values) => ApplySettingsInternal(values);

    private void ApplySettingsInternal(IReadOnlyDictionary<string, string?> values)
    {
        _serverUrl = Get(values, PlexSourceProvider.KeyServerUrl) ?? "";
        _token = Get(values, PlexSourceProvider.KeyToken) ?? "";
        _stereoAudio = bool.TryParse(Get(values, PlexSourceProvider.KeyStereoAudio), out var s) && s;
        _libraries = ParseLibraries(Get(values, PlexSourceProvider.KeyLibraries));

        _plex.Configure(_serverUrl, _token, _stereoAudio);
        _host?.Log($"PlexSource: server={_serverUrl} stereo={_stereoAudio} libraries={_libraries.Count}");
    }

    // ── ITextSearchCapable ─────────────────────────────────────────────────────

    public async IAsyncEnumerable<SourceItem> SearchAsync(
        string query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var results = await _plex.SearchAsync(query);
        foreach (var v in results)
        {
            ct.ThrowIfCancellationRequested();
            yield return PlexMappings.ToSourceItem(v, InstanceId);
        }
    }

    // ── IBrowsable ─────────────────────────────────────────────────────────────

    public async IAsyncEnumerable<SourceCategory> GetRootCategoriesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        foreach (var lib in _libraries)
            yield return PlexMappings.ToRootCategory(lib, InstanceId);
    }

    public async Task<BrowseResult> BrowseAsync(SourceCategory category, CancellationToken ct = default)
    {
        if (category.SourceState is not PlexNode node)
            return new BrowseResult();

        return node.Kind switch
        {
            PlexNodeKind.Library => await BrowseLibraryAsync(node, ct),
            PlexNodeKind.Artist => await BrowseChildrenAsync(node, PlexItemType.Album, PlexNodeKind.Album, ct),
            PlexNodeKind.Album => await BrowseTracksAsync(node, ct),
            PlexNodeKind.HubList => await BrowseHubListAsync(node, ct),
            PlexNodeKind.Hub => await BrowseHubAsync(node, ct),
            PlexNodeKind.PlaylistList => await BrowsePlaylistListAsync(node, ct),
            PlexNodeKind.Playlist => await BrowsePlaylistAsync(node, ct),
            _ => new BrowseResult(),
        };
    }

    private async Task<BrowseResult> BrowseLibraryAsync(PlexNode node, CancellationToken ct)
    {
        // A library expands to its top-level items (artists for music, videos otherwise),
        // plus "Hubs" and "Playlists" grouping nodes mirroring the ViewModel's tiles.
        var items = await _plex.GetLibraryVideosAsync(node.Key);
        var categories = new List<SourceCategory>
        {
            new()
            {
                SourceInstanceId = InstanceId,
                CategoryId = $"hublist:{node.Key}",
                Title = "Hubs",
                HasSubCategories = true,
                SourceState = new PlexNode(PlexNodeKind.HubList, node.Key, node.LibraryType),
            },
            new()
            {
                SourceInstanceId = InstanceId,
                CategoryId = $"playlistlist:{node.Key}",
                Title = "Playlists",
                HasSubCategories = true,
                SourceState = new PlexNode(PlexNodeKind.PlaylistList, node.Key, node.LibraryType),
            },
        };

        var leafItems = new List<SourceItem>();
        foreach (var v in items)
        {
            if (v.PlexItemType is PlexItemType.Artist or PlexItemType.Album)
            {
                var childKind = v.PlexItemType == PlexItemType.Artist ? PlexNodeKind.Artist : PlexNodeKind.Album;
                categories.Add(PlexMappings.ToCategory(v, InstanceId,
                    new PlexNode(childKind, v.PlexRatingKey ?? "", node.LibraryType)));
            }
            else
            {
                leafItems.Add(PlexMappings.ToSourceItem(v, InstanceId));
            }
        }

        return new BrowseResult { Categories = categories, Items = leafItems };
    }

    private async Task<BrowseResult> BrowseChildrenAsync(
        PlexNode node, PlexItemType childType, PlexNodeKind childKind, CancellationToken ct)
    {
        var children = await _plex.GetChildrenAsync(node.Key, childType, ct);
        var categories = children
            .Select(v => PlexMappings.ToCategory(v, InstanceId,
                new PlexNode(childKind, v.PlexRatingKey ?? "", node.LibraryType)))
            .ToList();
        return new BrowseResult { Categories = categories };
    }

    private async Task<BrowseResult> BrowseTracksAsync(PlexNode node, CancellationToken ct)
    {
        var tracks = await _plex.GetChildrenAsync(node.Key, PlexItemType.Track, ct);
        return new BrowseResult { Items = tracks.Select(v => PlexMappings.ToSourceItem(v, InstanceId)).ToList() };
    }

    private async Task<BrowseResult> BrowseHubListAsync(PlexNode node, CancellationToken ct)
    {
        var hubs = await _plex.GetLibraryHubsAsync(node.Key, ct);
        return new BrowseResult { Categories = hubs.Select(h => PlexMappings.ToCategory(h, InstanceId)).ToList() };
    }

    private async Task<BrowseResult> BrowseHubAsync(PlexNode node, CancellationToken ct)
    {
        var items = await _plex.GetHubItemsAsync(node.Key, node.LibraryType ?? "", ct);
        return new BrowseResult { Items = items.Select(v => PlexMappings.ToSourceItem(v, InstanceId)).ToList() };
    }

    private async Task<BrowseResult> BrowsePlaylistListAsync(PlexNode node, CancellationToken ct)
    {
        var playlistType = node.LibraryType == "artist" ? "audio" : "video";
        var playlists = await _plex.GetPlaylistsAsync(playlistType, ct);
        return new BrowseResult { Categories = playlists.Select(p => PlexMappings.ToCategory(p, InstanceId)).ToList() };
    }

    private async Task<BrowseResult> BrowsePlaylistAsync(PlexNode node, CancellationToken ct)
    {
        var items = await _plex.GetPlaylistItemsAsync(node.Key, ct);
        return new BrowseResult { Items = items.Select(v => PlexMappings.ToSourceItem(v, InstanceId)).ToList() };
    }

    // ── IPagedBrowsable ────────────────────────────────────────────────────────

    public async Task<BrowsePage> BrowsePageAsync(
        SourceCategory category, int offset, int count, CancellationToken ct = default)
    {
        if (category.SourceState is not PlexNode node)
            return new BrowsePage();

        // Route to the paginated Plex endpoint matching the node kind. Hubs, libraries, and
        // playlists all page by offset/count and report a total size.
        PlexPage page = node.Kind switch
        {
            PlexNodeKind.Hub => await _plex.GetHubItemsPageAsync(node.Key, node.LibraryType ?? "", offset, count, ct),
            PlexNodeKind.Library => await _plex.GetLibraryVideosPageAsync(node.Key, offset, count, node.LibraryType, ct),
            PlexNodeKind.Playlist => await _plex.GetPlaylistItemsPageAsync(node.Key, offset, count, ct),
            _ => new PlexPage(),
        };

        return new BrowsePage
        {
            Items = page.Items.Select(v => PlexMappings.ToSourceItem(v, InstanceId)).ToList(),
            TotalSize = page.TotalSize,
        };
    }

    // ── IPlayableResolver ──────────────────────────────────────────────────────

    public Task<ResolvedStream?> ResolveAsync(SourceItem item, PlaybackPreferences prefs, CancellationToken ct = default)
    {
        // Plex items already carry a ready-to-play StreamUrl (built at browse time).
        var v = PlexMappings.VideoItemOf(item);
        return Task.FromResult(v == null ? null : PlexMappings.ToResolvedStream(v));
    }

    public async Task<SourceMetadata?> GetMetadataAsync(SourceItem item, CancellationToken ct = default)
    {
        var v = PlexMappings.VideoItemOf(item);
        if (v == null) return null;

        // Fetch chapters on demand when the item didn't already carry them.
        if ((v.Chapters == null || v.Chapters.Count == 0) && !string.IsNullOrEmpty(v.PlexRatingKey))
        {
            var chapters = await _plex.GetChaptersAsync(v.PlexRatingKey);
            if (chapters != null) v.Chapters = chapters;
        }

        return PlexMappings.ToSourceMetadata(v);
    }

    // ── IConfigurable ──────────────────────────────────────────────────────────

    public IReadOnlyList<ConfigAction> GetConfigActions() =>
    [
        new(PlexSourceProvider.ActionBrowseLibraries, "Browse libraries…",
            "List the server's libraries and choose which become tiles."),
    ];

    public async Task<ConfigSelection> InvokeConfigActionAsync(string actionId, CancellationToken ct = default)
    {
        if (actionId != PlexSourceProvider.ActionBrowseLibraries)
            return new ConfigSelection([]);

        var enabled = _libraries.Select(l => l.Key).ToHashSet();
        var libs = await _plex.GetLibrariesAsync();
        var options = libs
            .Select(l => new ConfigOption(l.Key, $"{l.Title} ({l.Type})", enabled.Contains(l.Key)))
            .ToList();

        return new ConfigSelection(options, AllowMultiple: true, Title: "Plex libraries");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string? Get(IReadOnlyDictionary<string, string?> values, string key)
        => values.TryGetValue(key, out var v) ? v : null;

    private static List<PlexLibraryMapping> ParseLibraries(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<PlexLibraryMapping>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
