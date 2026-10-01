using System.Text.Json;
using System.Text.Json.Nodes;

namespace KHost.Plugins.YouTubeMusic.Chrome;

/// <summary>The Chrome profile this plugin owns on macOS, kept apart from the host's own browsing.</summary>
public static class ChromeProfile
{
    /// <summary>Its own name rather than the Edge profile's: the two browsers cannot share one.</summary>
    public const string DefaultFolderName = "youtube-music-chrome";

    /// <summary>The per-profile switch behind View → Developer → "Allow JavaScript from Apple
    /// Events". Without it every script the plugin sends is refused.</summary>
    public const string AppleEventsPreference = "allow_javascript_apple_events";

    /// <summary>Under Application Support rather than the plugin folder: an update replaces the
    /// plugin folder whole, and the sign-in must survive it.</summary>
    public static string DefaultDirectory(string home)
        => Path.Combine(home, "Library", "Application Support", "KHost", DefaultFolderName);

    public static string Resolve(string? configured, string home)
        => string.IsNullOrWhiteSpace(configured) ? DefaultDirectory(home) : Path.GetFullPath(configured.Trim());

    /// <summary>Chrome writes <c>Local State</c> the first time it opens a profile; the plugin never
    /// does, so it says setup has been run at least once.</summary>
    public static bool HasBeenOpened(string profileDirectory)
        => File.Exists(Path.Combine(profileDirectory, "Local State"));

    /// <summary>The pid of the Chrome running on this profile, from the lock Chrome holds on it
    /// (<c>SingletonLock</c> → <c>&lt;host&gt;-&lt;pid&gt;</c>). Null when there is no lock; the
    /// caller still checks the pid is alive, since a crash leaves the link behind.</summary>
    public static int? ReadLockPid(string profileDirectory)
    {
        try
        {
            var target = new FileInfo(Path.Combine(profileDirectory, "SingletonLock")).LinkTarget;
            return ParseLockTarget(target);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static int? ParseLockTarget(string? target)
    {
        var separator = target?.LastIndexOf('-') ?? -1;

        return separator >= 0 && int.TryParse(target![(separator + 1)..], out var pid) && pid > 0 ? pid : null;
    }

    /// <summary>Turns on "Allow JavaScript from Apple Events" in <paramref name="preferencesJson"/>,
    /// keeping everything else. Chrome rewrites the file while running, so this is only written
    /// while the profile's Chrome is not.</summary>
    /// <returns>The new file contents, or null when it is already on.</returns>
    public static string? WithAppleEventsAllowed(string? preferencesJson)
    {
        JsonObject root;

        try
        {
            root = (string.IsNullOrWhiteSpace(preferencesJson) ? null : JsonNode.Parse(preferencesJson) as JsonObject) ?? [];
        }
        catch (JsonException)
        {
            // Chrome rebuilds a Preferences file it cannot read; ours would be replaced the same way.
            root = [];
        }

        if (root["browser"] is not JsonObject browser)
        {
            browser = [];
            root["browser"] = browser;
        }

        if (browser[AppleEventsPreference] is JsonValue value && value.TryGetValue<bool>(out var on) && on)
            return null;

        browser[AppleEventsPreference] = true;

        return root.ToJsonString();
    }

    /// <summary>Writes the Apple Events preference into the profile's Preferences file.</summary>
    public static void AllowAppleEvents(string profileDirectory)
    {
        var defaultProfile = Path.Combine(profileDirectory, "Default");
        var preferences = Path.Combine(defaultProfile, "Preferences");

        Directory.CreateDirectory(defaultProfile);

        var updated = WithAppleEventsAllowed(File.Exists(preferences) ? File.ReadAllText(preferences) : null);

        if (updated is not null)
            File.WriteAllText(preferences, updated);
    }

    /// <summary>Whether the preference is on in the file Chrome last wrote.</summary>
    public static bool AppleEventsAllowed(string profileDirectory)
    {
        try
        {
            var preferences = Path.Combine(profileDirectory, "Default", "Preferences");
            return File.Exists(preferences) && WithAppleEventsAllowed(File.ReadAllText(preferences)) is null;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
