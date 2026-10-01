namespace KHost.Plugins.YouTubeMusic;

/// <summary>Typed view of the settings declared in manifest.json, kept in sync with it by hand.</summary>
public class YouTubeMusicSettings
{
    /// <summary>Blank resumes whatever the app already has loaded. Only read when the app has no
    /// media session yet: a bed already playing is never reloaded from the top.</summary>
    public string PlaylistUrl { get; set; } = "";

    public bool LaunchIfNotRunning { get; set; } = true;

    /// <summary>Milliseconds. Zero pauses at once.</summary>
    public int FadeMilliseconds { get; set; } = 1500;

    /// <summary>Also overrides a host pressing pause in the app's own window, which this plugin
    /// cannot tell apart from YouTube Music's idle prompt.</summary>
    public bool RecoverUnexpectedPause { get; set; } = true;

    /// <summary>Blank uses <see cref="Edge.EdgeProfile.DefaultDirectory"/> on Windows and
    /// <see cref="Chrome.ChromeProfile.DefaultDirectory"/> on macOS. Never point it at the host's own
    /// browser profile: two browsers on one user-data-dir refuse to start.</summary>
    public string ProfileDirectory { get; set; } = "";
}
