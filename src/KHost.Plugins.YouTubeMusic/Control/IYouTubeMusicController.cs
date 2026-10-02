namespace KHost.Plugins.YouTubeMusic.Control;

/// <summary>How far the one-time setup has got on this machine.</summary>
public enum SetupStatus
{
    /// <summary>This platform has no backend.</summary>
    Unsupported,
    BrowserNotFound,

    /// <summary>Windows only: Edge is here, but the app is not installed in the plugin's profile.</summary>
    AppNotInstalled,

    /// <summary>Set up; on macOS, also signed in to a Google account.</summary>
    Ready,

    /// <summary>macOS only: the plugin carries no YouTube Music app for this Mac (a build made
    /// without one), and none was installed before.</summary>
    HelperMissing,

    /// <summary>macOS only: plays, but signed out, so adverts reach the room. Signing in is the last
    /// step of setup, not a condition of playing.</summary>
    NotSignedIn,
}

/// <summary>Setup button labels that other text names, so the instruction and the button cannot drift apart.</summary>
internal static class SetupButtonLabel
{
    /// <summary>The setup button while <see cref="SetupStatus.NotSignedIn"/> (macOS only).</summary>
    public const string SignIn = "Sign in to YouTube Music";
}

/// <summary>Drives the YouTube Music app on this machine. Reports raw session reads; deciding what
/// they mean (skip transients, adverts, unasked pauses) is <see cref="SessionTracker"/>'s.</summary>
public interface IYouTubeMusicController
{
    /// <summary>Why nothing here can play, or null where the backend works.</summary>
    string? Unavailable { get; }

    /// <summary>The browser the app runs in, as the host knows it ("Microsoft Edge"); on macOS the
    /// plugin's own YouTube Music app.</summary>
    string BrowserName { get; }

    /// <summary>The same, short, as in "this plugin's Edge profile".</summary>
    string BrowserShortName { get; }

    /// <summary>What the Plugins page says while <see cref="GetSetupStatus"/> is
    /// <see cref="SetupStatus.AppNotInstalled"/> or <see cref="SetupStatus.NotSignedIn"/>.</summary>
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
    /// installing the app; on macOS, the YouTube Music window at music.youtube.com.</summary>
    Task<bool> OpenSetupAsync(CancellationToken cancellationToken = default);

    /// <summary>Puts the app window in front of the host, launching it if need be. Unlike
    /// <see cref="LaunchAppAsync"/>, which starts it behind the karaoke screen for the music.</summary>
    Task<bool> ShowAppAsync(CancellationToken cancellationToken = default)
        => LaunchAppAsync(startUrl: null, cancellationToken);

    Task<bool> PlayAsync(CancellationToken cancellationToken = default);
    Task<bool> PauseAsync(CancellationToken cancellationToken = default);
    Task<bool> SkipAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets the app's own mixer level, 0 to 1. False when it has no audio session yet,
    /// which is normal until it first makes a sound.</summary>
    Task<bool> SetLevelAsync(float level, CancellationToken cancellationToken = default);
}
