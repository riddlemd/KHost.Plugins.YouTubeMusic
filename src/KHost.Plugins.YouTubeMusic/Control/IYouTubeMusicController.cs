namespace KHost.Plugins.YouTubeMusic.Control;

/// <summary>How far the one-time setup has got on this machine.</summary>
public enum SetupStatus
{
    /// <summary>This platform has no backend.</summary>
    Unsupported,
    BrowserNotFound,

    /// <summary>Edge is here, but the YouTube Music app is not installed in the plugin's profile.</summary>
    AppNotInstalled,
    Ready,
}

/// <summary>Drives the YouTube Music app on this machine. Reports raw session reads; deciding what
/// they mean (skip transients, adverts, unasked pauses) is <see cref="SessionTracker"/>'s.</summary>
public interface IYouTubeMusicController
{
    /// <summary>Why nothing here can play, or null where the backend works.</summary>
    string? Unavailable { get; }

    /// <summary>Raised when the session may have moved. Carries nothing: read it back.</summary>
    event EventHandler? SessionChanged;

    Task StartWatchingAsync(CancellationToken cancellationToken = default);

    /// <summary>The app's session now; <see cref="SessionSnapshot.None"/> when it has none, null
    /// when the backend could not look.</summary>
    Task<SessionSnapshot?> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the plugin's own browser instance is up, app window or not.</summary>
    bool IsBrowserRunning { get; }

    SetupStatus GetSetupStatus();

    /// <summary>Opens the app window, at <paramref name="startUrl"/> when given.</summary>
    Task<bool> LaunchAppAsync(string? startUrl, CancellationToken cancellationToken = default);

    /// <summary>Opens the plugin's profile as an ordinary browser window, for signing in and
    /// installing the app.</summary>
    Task<bool> OpenSetupAsync(CancellationToken cancellationToken = default);

    Task<bool> PlayAsync(CancellationToken cancellationToken = default);
    Task<bool> PauseAsync(CancellationToken cancellationToken = default);
    Task<bool> SkipAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets the app's own mixer level, 0 to 1. False when it has no audio session yet,
    /// which is normal until it first makes a sound.</summary>
    Task<bool> SetLevelAsync(float level, CancellationToken cancellationToken = default);
}
