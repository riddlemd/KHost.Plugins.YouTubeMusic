namespace KHost.Plugins.YouTubeMusic.Control;

/// <summary>Stands in where there is no backend. Every command does nothing and reports failure,
/// so the console shows the bed stopped rather than playing.</summary>
public sealed class UnsupportedYouTubeMusicController(string reason) : IYouTubeMusicController
{
    public string? Unavailable { get; } = reason;

    public string BrowserName => "a browser";

    public string BrowserShortName => "browser";

    public string NotSetUpWarning => Unavailable!;

    public event EventHandler? SessionChanged { add { } remove { } }

    public bool IsBrowserRunning => false;

    public Task StartWatchingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>No session rather than null: nothing can play here, which is a definite answer.</summary>
    public Task<SessionSnapshot?> ReadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<SessionSnapshot?>(SessionSnapshot.None);

    public SetupStatus GetSetupStatus() => SetupStatus.Unsupported;

    public Task<bool> LaunchAppAsync(string? startUrl, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<bool> OpenSetupAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<bool> PlayAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<bool> PauseAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<bool> SkipAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<bool> SetLevelAsync(float level, CancellationToken cancellationToken = default) => Task.FromResult(false);
}
