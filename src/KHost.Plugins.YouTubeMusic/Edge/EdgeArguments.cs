namespace KHost.Plugins.YouTubeMusic.Edge;

/// <summary>Command lines for the plugin's own Edge instance. Returned as a list for
/// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>, which does the quoting.</summary>
/// <remarks>Every switch but the URL applies only when it starts the browser process. A launch that
/// lands on an instance already running on this profile only opens a window there.</remarks>
public static class EdgeArguments
{
    /// <summary>The id Edge gives the YouTube Music web app; derived from its manifest URL, so the
    /// same on every machine.</summary>
    public const string YouTubeMusicAppId = "cinhimbnkkaeohfgghhklpknlkffjgod";

    public const string YouTubeMusicHome = "https://music.youtube.com/";

    /// <summary>Opens the installed app, at <paramref name="startUrl"/> when given.</summary>
    /// <remarks>The URL goes through the switch a jump-list shortcut uses, which keeps the window
    /// the installed app: <c>--app=&lt;url&gt;</c> would open an anonymous app window that
    /// registers its media session as <c>MSEdge</c>, which is never matched.</remarks>
    public static IReadOnlyList<string> ForApp(string profileDirectory, string? startUrl)
    {
        List<string> arguments = [.. Common(profileDirectory), $"--app-id={YouTubeMusicAppId}"];

        if (!string.IsNullOrWhiteSpace(startUrl))
            arguments.Add($"--app-launch-url-for-shortcuts-menu-item={startUrl}");

        return arguments;
    }

    /// <summary>An ordinary window on the profile, where the host signs in and installs the app.</summary>
    public static IReadOnlyList<string> ForSetup(string profileDirectory)
        => [.. Common(profileDirectory), YouTubeMusicHome];

    private static IEnumerable<string> Common(string profileDirectory) =>
    [
        $"--user-data-dir={profileDirectory}",
        "--profile-directory=Default",
        "--no-first-run",
        "--no-default-browser-check",
        "--disable-sync",
        // A crash or a killed kiosk otherwise greets the room with "Restore pages?".
        "--hide-crash-restore-bubble",
        // An app window under the karaoke screen counts as hidden; Edge then throttles the page
        // and the music stutters or stops.
        "--disable-features=CalculateNativeWinOcclusion",
        "--disable-backgrounding-occluded-windows",
        // Nobody clicks inside the app window at a venue, so a launch must be allowed to sound.
        "--autoplay-policy=no-user-gesture-required",
    ];
}
