namespace VpinJukebox;

/// <summary>
/// Global runtime flag to suppress mouse cursor auto-hide.
/// Any window/UI element that should prevent cursor hiding increments the suppress count
/// while open, and decrements it on close.
/// </summary>
public static class MouseHideState
{
    private static int _suppressCount;

    /// <summary>
    /// True when no UI elements are suppressing cursor hide.
    /// The cursor idle timer should only hide the cursor when this is true.
    /// </summary>
    public static bool EnableMouseHide => _suppressCount <= 0;

    /// <summary>
    /// Call when opening a window/dialog that should prevent cursor hiding.
    /// </summary>
    public static void Suppress() => Interlocked.Increment(ref _suppressCount);

    /// <summary>
    /// Call when closing a window/dialog to allow cursor hiding again.
    /// </summary>
    public static void Unsuppress() => Interlocked.Decrement(ref _suppressCount);
}
