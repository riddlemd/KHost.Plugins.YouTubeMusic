using KHost.Plugins.YouTubeMusic.Edge;
using KHost.Plugins.YouTubeMusic.Helper;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KHost.Plugins.YouTubeMusic.Control;

public static class YouTubeMusicControllerFactory
{
    /// <param name="configuredProfileDirectory">The Edge profile setting as entered; blank picks the
    /// default. macOS has no profile to choose: the helper app's data store is its own.</param>
    /// <param name="binDirectory">The host's shared bin/, where the macOS helper app is installed.</param>
    /// <param name="pluginDirectory">Where this plugin's files are; defaults to this assembly's folder,
    /// which for a plugin is not the host's AppContext.BaseDirectory.</param>
    public static IYouTubeMusicController ForCurrentPlatform(
        ILogger logger, string? configuredProfileDirectory, string binDirectory, string? pluginDirectory = null)
    {
        if (OperatingSystem.IsMacOS())
            return ForMac(logger, binDirectory, pluginDirectory ?? PluginFolder());

        if (!OperatingSystem.IsWindows())
            return new UnsupportedYouTubeMusicController(UnsupportedReason(RuntimeInformation.OSDescription));

#if WINDOWS_MEDIA_SESSION
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            return new Windows.WindowsYouTubeMusicController(logger, EdgeProfile.Resolve(configuredProfileDirectory));
#endif

        return new UnsupportedYouTubeMusicController(
            "This build has no Windows media session support. Install the Windows build of the plugin.");
    }

    public static string UnsupportedReason(string platform)
        => $"YouTube Music break music runs on Windows and macOS; there is no backend for {platform} yet.";

    [SupportedOSPlatform("macos")]
    private static IYouTubeMusicController ForMac(ILogger logger, string binDirectory, string pluginDirectory)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var socket = HelperApp.SocketPath(home);

        var install = HelperInstaller.Install(
            HelperApp.ShippedApp(pluginDirectory), binDirectory, () => new ProcessHelperTransport(logger, null, socket).IsRunning);

        switch (install.Outcome)
        {
            case HelperInstallOutcome.Installed or HelperInstallOutcome.Updated:
                logger.LogInformation("Installed the YouTube Music app at {Path}", install.AppPath);
                break;
            case HelperInstallOutcome.KeptRunning:
                logger.LogInformation("A newer YouTube Music app is waiting; it is installed once the running one quits");
                break;
            case HelperInstallOutcome.Failed:
                logger.LogWarning("Could not install the YouTube Music app into {Bin}: {Error}", binDirectory, install.Error);
                break;
            case HelperInstallOutcome.Missing:
                logger.LogWarning("This build of the plugin carries no YouTube Music app for macOS");
                break;
        }

        var transport = new ProcessHelperTransport(logger, install.AppPath, socket);

        return new HelperYouTubeMusicController(logger, transport, () => Directory.Exists(HelperApp.DataStoreDirectory(home)));
    }

    private static string PluginFolder()
        => Path.GetDirectoryName(typeof(YouTubeMusicControllerFactory).Assembly.Location) is { Length: > 0 } folder
            ? folder
            : AppContext.BaseDirectory;
}
