using System.Windows.Threading;

namespace Phosphor;

/// <summary>
/// Drives synchronized Pinup Popper playback across all screens (playfield, backglass,
/// future topper). Owned by the DMD window and runs on the DMD dispatcher thread.
///
/// The coordinator is the single source of truth: it holds the shuffled game list, the
/// shared current index, and one dwell <see cref="DispatcherTimer"/>. On each dwell tick it
/// advances the index and pushes the SAME game to every registered <see cref="IPinupFollower"/>
/// simultaneously, so all screens switch clips together. Each follower maps the canonical
/// playfield glob to its own screen folder and loops its clip until the next advance — clip
/// lengths may differ per screen (they loop independently), which is why the dwell is a
/// single shared duration rather than a per-clip min/max.
///
/// A follower is registered only while its screen is in Pinup mode; a solo screen is simply
/// a coordinator with one follower. When no screens use Pinup, the coordinator is stopped.
/// </summary>
public sealed class PinupSyncCoordinator
{
    private readonly Dispatcher _dispatcher;
    private readonly Random _rng = new();
    private readonly List<IPinupFollower> _followers = new();
    private DispatcherTimer? _timer;

    // The canonical playfield globs (…\Playfield\<base>.*) for the shuffled game list.
    private string[] _gameGlobs = [];
    private int _index;
    private bool _running;

    public PinupSyncCoordinator(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>True while a dwell timer is active and games are being driven.</summary>
    public bool IsRunning => _running;

    /// <summary>
    /// Replaces the set of followers (screens currently in Pinup mode). Any follower no
    /// longer present is told to stop. If the coordinator is running, the newly-registered
    /// followers immediately receive the current game so they catch up in sync.
    /// </summary>
    public void SetFollowers(IEnumerable<IPinupFollower> followers)
    {
        var next = followers.Where(f => f != null).Distinct().ToList();

        // Stop followers being removed.
        foreach (var old in _followers.Where(f => !next.Contains(f)))
            old.StopPinup();

        _followers.Clear();
        _followers.AddRange(next);

        // Catch up newly-added followers to the current game.
        if (_running && _gameGlobs.Length > 0)
        {
            var glob = _gameGlobs[_index % _gameGlobs.Length];
            foreach (var f in _followers)
                f.PlayPinupGame(glob);
        }
    }

    /// <summary>
    /// (Re)starts coordinated playback with a freshly shuffled copy of <paramref name="gameGlobs"/>
    /// (canonical playfield globs) and the shared <paramref name="dwellSeconds"/>. Immediately
    /// pushes the first game to all followers, then advances every dwell interval. Safe to call
    /// repeatedly (e.g. on settings-apply) — it resets the shuffle, index, and timer.
    /// </summary>
    public void Start(IReadOnlyList<string> gameGlobs, int dwellSeconds)
    {
        _dispatcher.BeginInvoke(() =>
        {
            _gameGlobs = Shuffle(gameGlobs
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .ToArray());
            _index = 0;

            StopTimer();

            if (_gameGlobs.Length == 0)
            {
                // Nothing to play — ensure followers show black.
                foreach (var f in _followers)
                    f.StopPinup();
                _running = false;
                return;
            }

            _running = true;
            PushCurrentGame();

            int dwell = Math.Max(5, dwellSeconds);
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(dwell) };
            _timer.Tick += OnDwellTick;
            _timer.Start();
        });
    }

    /// <summary>Stops coordinated playback and tells all followers to stop Pinup.</summary>
    public void Stop()
    {
        _dispatcher.BeginInvoke(() =>
        {
            StopTimer();
            _running = false;
            foreach (var f in _followers)
                f.StopPinup();
        });
    }

    private void OnDwellTick(object? sender, EventArgs e)
    {
        if (_gameGlobs.Length == 0)
            return;
        _index = (_index + 1) % _gameGlobs.Length;
        PushCurrentGame();
    }

    private void PushCurrentGame()
    {
        if (_gameGlobs.Length == 0)
            return;
        var glob = _gameGlobs[_index];
        foreach (var f in _followers)
            f.PlayPinupGame(glob);
    }

    private void StopTimer()
    {
        if (_timer != null)
        {
            _timer.Stop();
            _timer.Tick -= OnDwellTick;
            _timer = null;
        }
    }

    private string[] Shuffle(string[] items)
    {
        // Fisher–Yates.
        for (int i = items.Length - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
        return items;
    }
}
