namespace KHost.Plugins.YouTubeMusic.Edge;

/// <summary>Reads an <c>msedge.exe</c> command line to tell which process belongs to the plugin's
/// profile. Every Edge process on the machine shares the one image name.</summary>
public static class EdgeCommandLine
{
    /// <summary>The browser process itself: our profile, and no <c>--type</c>.</summary>
    public static bool IsBrowserFor(string? commandLine, string profileDirectory)
        => commandLine is not null
           && !commandLine.Contains("--type=", StringComparison.OrdinalIgnoreCase)
           && UsesProfile(commandLine, profileDirectory);

    /// <summary>The audio service utility, which owns the profile's one Windows audio session. The
    /// sub-type alone names it: only a utility process carries one.</summary>
    public static bool IsAudioServiceFor(string? commandLine, string profileDirectory)
        => commandLine is not null
           && commandLine.Contains("--utility-sub-type=audio.mojom.AudioService", StringComparison.OrdinalIgnoreCase)
           && UsesProfile(commandLine, profileDirectory);

    /// <summary>Compared as a whole value, so <c>profile</c> is not taken for <c>profile-2</c>.</summary>
    public static bool UsesProfile(string commandLine, string profileDirectory)
    {
        var wanted = NormalizePath(profileDirectory);

        return UserDataDirs(commandLine).Any(value => string.Equals(NormalizePath(value), wanted, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> UserDataDirs(string commandLine)
    {
        const string Switch = "--user-data-dir=";

        var index = 0;

        while ((index = commandLine.IndexOf(Switch, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var start = index + Switch.Length;
            string value;

            if (start < commandLine.Length && commandLine[start] == '"')
            {
                var close = commandLine.IndexOf('"', start + 1);
                value = close < 0 ? commandLine[(start + 1)..] : commandLine[(start + 1)..close];
            }
            else
            {
                var space = commandLine.IndexOf(' ', start);
                value = space < 0 ? commandLine[start..] : commandLine[start..space];
            }

            yield return value;
            index = start;
        }
    }

    private static string NormalizePath(string path)
        => path.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
}
