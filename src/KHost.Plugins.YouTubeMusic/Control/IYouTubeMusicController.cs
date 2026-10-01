namespace KHost.Plugins.YouTubeMusic.Control;

/// <summary>How far the one-time setup has got on this machine.</summary>
public enum SetupStatus
{
    /// <summary>This platform has no backend.</summary>
    Unsupported,
    BrowserNotFound,

    /// <summary>The browser is here, but the plugin's profile is not ready: on Windows the app is
    /// not installed in it, on macOS it has never been opened.</summary>
    AppNotInstalled,
    Ready,

    /// <summary>macOS only: the host app may not script the browser (Privacy &amp; Security →
    /// Automation), or has not been asked yet.</summary>
    NotPermitted,

    /// <summary>macOS only: plays, but the app is not installed in the profile, so its window has no
    /// Dock icon of its own and the host cannot reach it from the Dock. Installing it is the last
    /// step of setup, not a condition of playing.</summary>
    ReadyWithoutApp,
}

/// <summary>Drives the YouTube Music app on this machine. Reports raw session reads; deciding what
/// they mean (skip transients, adverts, unasked pauses) is <see cref="SessionTracker"/>'s.</summary>
public interface IYouTubeMusicController
{
    /// <summary>Why nothing here can play, or null where the backend works.</summary>
    string? Unavailable { get; }

    /// <summary>The browser the app runs in, as the host knows it ("Microsoft Edge").</summary>
    string BrowserName { get; }

    /// <summary>The same, short, as in "this plugin's Edge profile".</summary>
    string BrowserShortName { get; }

    /// <summary>What the Plugins page says while <see cref="GetSetupStatus"/> is
    /// <see cref="SetupStatus.AppNotInstalled"/>.</summary>
    string NotSetUpWarning { get; }

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
    /// installing the app. On macOS this is also when the host is asked to let KHost control
    /// Chrome.</summary>
    Task<bool> OpenSetupAsync(CancellationToken cancellationToken = default);

    Task<bool> PlayAsync(CancellationToken cancellationToken = default);
    Task<bool> PauseAsync(CancellationToken cancellationToken = default);
    Task<bool> SkipAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets the app's own mixer level, 0 to 1. False when it has no audio session yet,
    /// which is normal until it first makes a sound.</summary>
    Task<bool> SetLevelAsync(float level, CancellationToken cancellationToken = default);
}
