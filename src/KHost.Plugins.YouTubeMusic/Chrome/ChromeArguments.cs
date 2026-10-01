namespace KHost.Plugins.YouTubeMusic.Chrome;

/// <summary>Command lines for the plugin's own Chrome instance on macOS, for
/// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>.</summary>
/// <remarks>Every switch but the URL applies only when it starts the browser process. A launch that
/// lands on an instance already running on this profile only opens a window there.</remarks>
public static class ChromeArguments
{
    public const string YouTubeMusicHome = "https://music.youtube.com/";

    /// <summary>A chromeless app window. Chrome on macOS has no switch that installs the web app,
    /// and none is needed: nothing here depends on the window being an installed app.</summary>
    public static IReadOnlyList<string> ForApp(string profileDirectory, string? startUrl)
        => [.. Common(profileDirectory), $"--app={(IsYouTubeMusicUrl(startUrl) ? startUrl : YouTubeMusicHome)}"];

    /// <summary>An ordinary window on the profile, where the host signs in.</summary>
    public static IReadOnlyList<string> ForSetup(string profileDirectory)
        => [.. Common(profileDirectory), YouTubeMusicHome];

    /// <summary>Only YouTube Music is ever opened, whatever a setting held.</summary>
    public static bool IsYouTubeMusicUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && string.Equals(uri.Host, "music.youtube.com", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Common(string profileDirectory) =>
    [
        $"--user-data-dir={profileDirectory}",
        "--profile-directory=Default",
        "--no-first-run",
        "--no-default-browser-check",
        "--disable-sync",
        // A crash or a killed kiosk otherwise greets the room with "Restore pages?".
        "--hide-crash-restore-bubble",
        // Nobody clicks inside the app window at a venue, so a launch must be allowed to sound.
        "--autoplay-policy=no-user-gesture-required",
        // A minimised or covered window kept playing without these when measured, because an audible
        // page is exempt; they cover the silent gap between tracks, where it is not.
        "--disable-backgrounding-occluded-windows",
        "--disable-renderer-backgrounding",
        "--disable-background-timer-throttling",
    ];
}
