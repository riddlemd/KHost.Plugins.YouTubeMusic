using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace KHost.Plugins.YouTubeMusic.Control;

public static class YouTubeMusicControllerFactory
{
    public static IYouTubeMusicController ForCurrentPlatform(ILogger logger, string profileDirectory)
    {
        if (!OperatingSystem.IsWindows())
            return new UnsupportedYouTubeMusicController(UnsupportedReason(RuntimeInformation.OSDescription));

#if WINDOWS_MEDIA_SESSION
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            return new Windows.WindowsYouTubeMusicController(logger, profileDirectory);
#endif

        return new UnsupportedYouTubeMusicController(
            "This build has no Windows media session support. Install the Windows build of the plugin.");
    }

    public static string UnsupportedReason(string platform)
        => $"YouTube Music break music runs on Windows only for now; there is no backend for {platform}.";
}
