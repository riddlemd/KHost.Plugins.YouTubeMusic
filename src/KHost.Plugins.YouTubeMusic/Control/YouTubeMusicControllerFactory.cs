using KHost.Plugins.YouTubeMusic.Chrome;
using KHost.Plugins.YouTubeMusic.Edge;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace KHost.Plugins.YouTubeMusic.Control;

public static class YouTubeMusicControllerFactory
{
    /// <param name="configuredProfileDirectory">The setting as entered; blank picks the platform's
    /// own default, since an Edge and a Chrome profile cannot be the same folder.</param>
    public static IYouTubeMusicController ForCurrentPlatform(ILogger logger, string? configuredProfileDirectory)
    {
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return new Mac.MacYouTubeMusicController(logger, ChromeProfile.Resolve(configuredProfileDirectory, home));
        }

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
        => $"YouTube Music break music runs on Windows and macOS only for now; there is no backend for {platform}.";
}
