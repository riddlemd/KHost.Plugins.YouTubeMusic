namespace KHost.Plugins.YouTubeMusic.Edge;

/// <summary>The Edge profile this plugin owns, kept apart from the host's own browsing.</summary>
public static class EdgeProfile
{
    /// <summary>Under Local AppData rather than the plugin folder: an update replaces the plugin
    /// folder whole, and the sign-in must survive it.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KHost", "youtube-music-profile");

    public static string Resolve(string? configured)
        => string.IsNullOrWhiteSpace(configured) ? DefaultDirectory : Path.GetFullPath(configured.Trim());

    /// <summary>Whether the app is installed in <paramref name="profileDirectory"/>. Edge keeps an
    /// installed app's icons under its id, and lists the id in the profile's Preferences.</summary>
    public static bool HasYouTubeMusicApp(string profileDirectory)
    {
        var profile = Path.Combine(profileDirectory, "Default");

        if (Directory.Exists(Path.Combine(profile, "Web Applications", "Manifest Resources", EdgeArguments.YouTubeMusicAppId)))
            return true;

        var preferences = Path.Combine(profile, "Preferences");

        try
        {
            return File.Exists(preferences)
                   && File.ReadAllText(preferences).Contains(EdgeArguments.YouTubeMusicAppId, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            // Edge rewrites Preferences while running; a locked read says nothing either way.
            return false;
        }
    }
}
