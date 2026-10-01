using KHost.Plugins.YouTubeMusic.Control;

namespace KHost.Plugins.YouTubeMusic.Mac;

/// <summary>How far setup has got on macOS once Chrome is found and the profile has been opened.</summary>
public static class MacSetup
{
    /// <summary>A missing consent outranks a missing app: without it nothing plays at all, while
    /// without the app it plays in a window the Dock cannot find.</summary>
    public static SetupStatus StatusFor(bool notPermitted, bool appInstalled)
    {
        if (notPermitted)
            return SetupStatus.NotPermitted;

        return appInstalled ? SetupStatus.Ready : SetupStatus.ReadyWithoutApp;
    }
}
