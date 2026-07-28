namespace Phosphor.Playback;

/// <summary>
/// The per-player command channel between a view-model's now-playing surface and a
/// <see cref="JukeboxPlayer"/>/host window. It carries the playback COMMAND events (play / stop /
/// pause / resume / seek / volume) that the window's playback engine subscribes to today.
///
/// Phase 0 goal: give the VM a single <c>Player1</c> instance and have it re-expose the existing
/// event names as pass-throughs, so all current XAML bindings and the Backglass's
/// <c>AttachViewModel</c> subscriptions keep working byte-for-byte. Richer now-playing UI state
/// (CurrentlyPlaying, Queue, chapters) stays on the VM for this increment and migrates in a later
/// step; a second player only needs its own command channel to exist.
/// </summary>
public sealed class PlayerContext
{
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
