#if WINDOWS_MEDIA_SESSION
using KHost.Plugins.YouTubeMusic.Control;
using KHost.Plugins.YouTubeMusic.Edge;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.Versioning;
using Windows.Media.Control;

namespace KHost.Plugins.YouTubeMusic.Windows;

/// <summary>The YouTube Music app installed in Edge, driven through its own row on the system
/// media transport and its own process in the volume mixer.</summary>
/// <remarks>Deliberately thin: everything that decides anything is in the portable classes, since
/// none of this can run where the tests do.</remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class WindowsYouTubeMusicController : IYouTubeMusicController
{
    /// <summary>Play, pause and a new title each raise their own event within a few milliseconds;
    /// one read after the burst is enough, and short enough to hear an unasked pause promptly.</summary>
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(100);

    private readonly ILogger _logger;
    private readonly string _profileDirectory;
    private readonly string? _edgePath;
    private readonly TimeProvider _time;
    private readonly SessionSightings _sightings = new();

    /// <summary>Held for the controller's life: a collected manager raises nothing.</summary>
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _watched;
    private HashSet<int> _audioProcessIds = [];
    private int _raiseGeneration;

    public WindowsYouTubeMusicController(ILogger logger, string profileDirectory, TimeProvider? time = null)
    {
        _logger = logger;
        _profileDirectory = profileDirectory;
        _time = time ?? TimeProvider.System;
        _edgePath = EdgeLocator.Find(
            EdgeLocator.Candidates(
                ReadAppPathsEntry(),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            File.Exists);

        Unavailable = _edgePath is null
            ? "Microsoft Edge was not found on this machine, and YouTube Music break music plays through it."
            : null;
    }

    public event EventHandler? SessionChanged;

    public string? Unavailable { get; }

    public string BrowserName => "Microsoft Edge";

    public string BrowserShortName => "Edge";

    public string NotSetUpWarning =>
        "YouTube Music is not installed in this plugin's Edge profile yet. Press \"Set up "
        + "YouTube Music\", sign in, then install it from the \"App available\" icon in Edge's address bar.";

    public bool IsBrowserRunning
        => ProcessCommandLine.Find("msedge", line => EdgeCommandLine.IsBrowserFor(line, _profileDirectory)).Count > 0;

    public SetupStatus GetSetupStatus()
    {
        if (_edgePath is null)
            return SetupStatus.BrowserNotFound;

        return EdgeProfile.HasYouTubeMusicApp(_profileDirectory) ? SetupStatus.Ready : SetupStatus.AppNotInstalled;
    }

    public async Task StartWatchingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellationToken);
            _manager.SessionsChanged += (_, _) => Rebind();

            Rebind();
        }
        catch (Exception ex)
        {
            // Every command still reads before deciding; only the live display and the pause
            // recovery are lost.
            _logger.LogWarning(ex, "Could not watch YouTube Music's media session");
        }
    }

    public async Task<SessionSnapshot?> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var session = await FindSessionAsync(cancellationToken);

            if (session is null)
                return SessionSnapshot.None;

            var playback = session.GetPlaybackInfo().PlaybackStatus switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => SessionPlayback.Playing,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => SessionPlayback.Paused,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => SessionPlayback.Stopped,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => SessionPlayback.Changing,
                _ => SessionPlayback.None,
            };

            var properties = await session.TryGetMediaPropertiesAsync().AsTask(cancellationToken);
            var timeline = session.GetTimelineProperties();

            return new SessionSnapshot(
                playback,
                properties?.Title,
                properties?.Artist,
                timeline.Position,
                timeline.LastUpdatedTime,
                timeline.EndTime - timeline.StartTime);
        }
        catch (Exception ex)
        {
            // Null, not None: "could not look" and "nothing there" lead to different decisions.
            _logger.LogDebug(ex, "Could not read YouTube Music's media session");
            return null;
        }
    }

    public Task<bool> LaunchAppAsync(string? startUrl, CancellationToken cancellationToken = default)
        => Task.FromResult(Launch(EdgeArguments.ForApp(_profileDirectory, startUrl)));

    public Task<bool> OpenSetupAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Launch(EdgeArguments.ForSetup(_profileDirectory)));

    public async Task<bool> PlayAsync(CancellationToken cancellationToken = default)
        => await FindSessionAsync(cancellationToken) is { } session && await session.TryPlayAsync().AsTask(cancellationToken);

    public async Task<bool> PauseAsync(CancellationToken cancellationToken = default)
        => await FindSessionAsync(cancellationToken) is { } session && await session.TryPauseAsync().AsTask(cancellationToken);

    public async Task<bool> SkipAsync(CancellationToken cancellationToken = default)
        => await FindSessionAsync(cancellationToken) is { } session && await session.TrySkipNextAsync().AsTask(cancellationToken);

    public Task<bool> SetLevelAsync(float level, CancellationToken cancellationToken = default)
    {
        try
        {
            // The audio service is found once and reused across a fade's steps; a miss means it
            // restarted (or first appeared), so the lookup is redone once.
            if (CoreAudio.SetLevel(_audioProcessIds, level))
                return Task.FromResult(true);

            _audioProcessIds = ProcessCommandLine.Find(
                "msedge", line => EdgeCommandLine.IsAudioServiceFor(line, _profileDirectory));

            return Task.FromResult(CoreAudio.SetLevel(_audioProcessIds, level));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not set YouTube Music's level in the volume mixer");
            return Task.FromResult(false);
        }
    }

    private async Task<GlobalSystemMediaTransportControlsSession?> FindSessionAsync(CancellationToken cancellationToken)
    {
        var manager = _manager ?? await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellationToken);

        return manager.GetSessions().FirstOrDefault(s => SessionRules.IsYouTubeMusicApp(s.SourceAppUserModelId));
    }

    private void Rebind()
    {
        try
        {
            var session = _manager?.GetSessions().FirstOrDefault(s => SessionRules.IsYouTubeMusicApp(s.SourceAppUserModelId));

            if (!ReferenceEquals(session, _watched))
            {
                if (_watched is not null)
                    _sightings.NoteLost(_time.GetUtcNow());

                _watched = session;

                if (session is not null)
                {
                    _logger.Log(
                        _sightings.NoteFound(_time.GetUtcNow()),
                        "Found YouTube Music's media session ({AppId})", session.SourceAppUserModelId);
                    session.PlaybackInfoChanged += (_, _) => RaiseCoalesced();
                    session.MediaPropertiesChanged += (_, _) => RaiseCoalesced();
                }
            }

            // Appearing or going away is itself news.
            RaiseCoalesced();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not bind to YouTube Music's media session");
        }
    }

    private void RaiseCoalesced()
    {
        var generation = Interlocked.Increment(ref _raiseGeneration);

        _ = Task.Delay(CoalesceWindow).ContinueWith(
            _ =>
            {
                if (Volatile.Read(ref _raiseGeneration) == generation)
                    SessionChanged?.Invoke(this, EventArgs.Empty);
            },
            TaskScheduler.Default);
    }

    private bool Launch(IReadOnlyList<string> arguments)
    {
        if (_edgePath is null)
            return false;

        try
        {
            Directory.CreateDirectory(_profileDirectory);

            var start = new ProcessStartInfo(_edgePath) { UseShellExecute = false };

            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);

            Process.Start(start)?.Dispose();

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start Microsoft Edge at {Path}", _edgePath);
            return false;
        }
    }

    private static string? ReadAppPathsEntry()
    {
        const string Key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe";

        try
        {
            return (Registry.LocalMachine.OpenSubKey(Key)?.GetValue(null)
                    ?? Registry.CurrentUser.OpenSubKey(Key)?.GetValue(null)) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
#endif
