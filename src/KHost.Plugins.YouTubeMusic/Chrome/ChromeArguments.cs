namespace KHost.Plugins.YouTubeMusic.Chrome;

/// <summary>Command lines for the plugin's own Chrome instance on macOS, for
/// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>.</summary>
/// <remarks>Every switch but the URL applies only when it starts the browser process. A launch that
/// lands on an instance already running on this profile only opens a window there.</remarks>
public static class ChromeArguments
{
    /// <summary>The id Chrome gives the installed YouTube Music app; the same one Edge gives it, and
    /// the same on every machine.</summary>
    public const string YouTubeMusicAppId = "cinhimbnkkaeohfgghhklpknlkffjgod";

    public const string YouTubeMusicHome = "https://music.youtube.com/";

    /// <summary>The installed app when <paramref name="appInstalled"/>, else an anonymous app window.</summary>
    /// <remarks>Only the installed app gets a "YouTube Music" Dock icon of its own (Chrome's app
    /// shim) that raises the window. An anonymous <c>--app=</c> window sits under a second Google
    /// Chrome icon, and clicking that opens a new browser window instead.</remarks>
    public static IReadOnlyList<string> ForApp(string profileDirectory, string? startUrl, bool appInstalled)
    {
        var url = IsYouTubeMusicUrl(startUrl) ? startUrl : null;

        if (!appInstalled)
            return [.. Common(profileDirectory), $"--app={url ?? YouTubeMusicHome}"];

        List<string> arguments = [.. Common(profileDirectory), $"--app-id={YouTubeMusicAppId}"];

        // The switch a shortcut-menu item uses keeps the window the installed app; --app=<url> would not.
        if (url is not null)
            arguments.Add($"--app-launch-url-for-shortcuts-menu-item={url}");

        return arguments;
    }

    /// <summary>An ordinary window on the profile, where the host signs in and installs the app.
    /// Chrome on macOS has no switch that installs it.</summary>
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
