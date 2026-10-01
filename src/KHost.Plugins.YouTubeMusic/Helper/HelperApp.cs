using KHost.Plugins.YouTubeMusic.Control;

namespace KHost.Plugins.YouTubeMusic.Helper;

/// <summary>Where the helper app is, and where it keeps what it keeps. Built by
/// <c>helpers/macos/build.sh</c>; the names here and there must agree.</summary>
public static class HelperApp
{
    /// <summary>Its data store (and so the Google sign-in) is keyed by this, so it never changes.</summary>
    public const string BundleId = "com.khost.youtube-music-helper";

    public const string BundleName = "YouTube Music.app";

    /// <summary>The executable inside the bundle, distinctly this plugin's in a process list.</summary>
    public const string ExecutableName = "khost-youtube-music-helper";

    /// <summary>The folder beside the plugin's own files that a build puts the app in.</summary>
    public const string ShippedFolder = "helper";

    /// <summary>The folder under the host's shared bin/ that the app is installed into: a name
    /// distinctly this plugin's, as the host asks of anything a plugin puts there.</summary>
    public const string InstallFolder = "khost.youtube-music";

    /// <summary>sockaddr_un holds 104 bytes, the terminator included.</summary>
    private const int MaxSocketPathBytes = 103;

    public static string ExecutableIn(string appPath) => Path.Combine(appPath, "Contents", "MacOS", ExecutableName);

    public static string ShippedApp(string pluginDirectory) => Path.Combine(pluginDirectory, ShippedFolder, BundleName);

    public static string InstalledApp(string binDirectory) => Path.Combine(binDirectory, InstallFolder, BundleName);

    /// <summary>The helper's control socket; null when the home folder's path is too long for one,
    /// in which case the helper skips it too and only a copy this plugin launched can be driven.</summary>
    public static string? SocketPath(string home, string bundleId = BundleId)
    {
        var path = Path.Combine(home, "Library", "Caches", bundleId, "khost.sock");
        return System.Text.Encoding.UTF8.GetByteCount(path) <= MaxSocketPathBytes ? path : null;
    }

    /// <summary>WebKit's persistent store for the app (cookies live beside it, under HTTPStorages).
    /// Absent until the app has first been opened.</summary>
    public static string DataStoreDirectory(string home, string bundleId = BundleId)
        => Path.Combine(home, "Library", "WebKit", bundleId);
}

/// <summary>How far setup has got on macOS.</summary>
public static class HelperSetup
{
    /// <param name="signedIn">The page's last word on it; null when it has not said this run.</param>
    /// <param name="dataStoreExists">Before the page has said, a data store that was never made is a
    /// sign-in that never happened; one that was is taken to still hold it until the page says not.</param>
    public static SetupStatus StatusFor(bool appAvailable, bool? signedIn, bool dataStoreExists)
    {
        if (!appAvailable)
            return SetupStatus.HelperMissing;

        return (signedIn ?? dataStoreExists) ? SetupStatus.Ready : SetupStatus.NotSignedIn;
    }
}
