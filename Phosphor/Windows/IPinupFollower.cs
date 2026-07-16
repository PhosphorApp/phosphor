namespace Phosphor;

/// <summary>
/// A screen (playfield, backglass, future topper) that can display synchronized Pinup
/// Popper clips driven by the <see cref="PinupSyncCoordinator"/>. The coordinator owns
/// the shuffled game list, the shared clip index, and the single dwell timer; each
/// follower is a "dumb" renderer that simply plays whatever game the coordinator selects.
///
/// Followers map the coordinator's canonical playfield glob to their own screen's media
/// folder (e.g. <c>…\Playfield\Game.*</c> → <c>…\BackGlass\Game.*</c>) and resolve the
/// actual file extension-agnostically, so a screen may use a different container (mp4 vs
/// mkv) than the playfield. If no matching file exists for a screen, it shows black.
/// </summary>
public interface IPinupFollower
{
    /// <summary>
    /// The media sub-folder name for this screen (e.g. "Playfield", "BackGlass", "Topper").
    /// Used to re-point the coordinator's canonical playfield glob to this screen's folder.
    /// </summary>
    string PinupScreenFolder { get; }

    /// <summary>
    /// Sets the media sub-folder this follower re-points the coordinator's canonical
    /// playfield glob to (e.g. "BackGlass", "Topper", "Menu"). Driven by the configurable
    /// window→folder map on the General tab. Should be applied before the coordinator starts.
    /// </summary>
    void SetPinupFolder(string folder);

    /// <summary>
    /// Plays the given game on this screen, looping seamlessly until the coordinator
    /// advances. <paramref name="canonicalPlayfieldGlob"/> is the playfield-relative glob
    /// (…\Playfield\&lt;base&gt;.*); the follower re-points it to <see cref="PinupScreenFolder"/>
    /// and resolves the actual file. Shows black if the screen has no matching file.
    /// Safe to call from any thread (the follower marshals to its own dispatcher).
    /// </summary>
    void PlayPinupGame(string canonicalPlayfieldGlob);

    /// <summary>
    /// Stops synchronized Pinup playback on this screen (e.g. when the coordinator stops
    /// because no screen uses Pinup mode any more).
    /// </summary>
    void StopPinup();
}
