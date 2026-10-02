namespace KHost.Plugins.YouTubeMusic.Edge;

/// <summary>Gives the foreground back to whatever had it when Edge was asked for the app window
/// behind the karaoke screen.</summary>
/// <remarks>Edge cannot be asked not to activate: the window is made by the browser process, often
/// one already running, which never sees the launch's show-window flags, and Chromium has no
/// switch for it. So the window is let come up and the foreground is handed straight back.</remarks>
public static class ForegroundReturn
{
    /// <summary>A cold Edge start with a sign-in check takes several seconds to draw its window.</summary>
    public static readonly TimeSpan Watch = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    /// <param name="before">The foreground window when the launch was asked for; 0 for none.</param>
    /// <param name="isEdgeWindow">Whether a window belongs to Edge.</param>
    /// <param name="bringToFront">Puts a window in front; false when it could not.</param>
    /// <returns>Whether the foreground was handed back. Only the first change is acted on: a move to
    /// anything but Edge is the host's own, and is left alone.</returns>
    public static async Task<bool> HandBackAsync(
        nint before,
        Func<nint> foreground,
        Func<nint, bool> isEdgeWindow,
        Func<nint, bool> bringToFront,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken = default)
    {
        if (before == 0)
            return false;

        for (var waited = TimeSpan.Zero; waited < Watch; waited += Poll)
        {
            await delay(Poll, cancellationToken);

            var now = foreground();

            // None is the moment between one window losing it and the next taking it.
            if (now == before || now == 0)
                continue;

            return isEdgeWindow(now) && bringToFront(before);
        }

        return false;
    }
}
