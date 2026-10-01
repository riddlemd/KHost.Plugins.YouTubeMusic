using KHost.Abstractions.Exceptions;
using KHost.Abstractions.Messaging;
using KHost.Abstractions.Messaging.Messages;
using KHost.Abstractions.Models;
using KHost.Abstractions.Services;
using KHost.Plugins.YouTubeMusic.Audio;
using KHost.Plugins.YouTubeMusic.Control;
using KHost.Plugins.YouTubeMusic.Edge;
using Microsoft.Extensions.Logging;

namespace KHost.Plugins.YouTubeMusic;

/// <summary>Break music out of the YouTube Music app installed in Edge on this machine. The host
/// carries none of this audio, so nothing here reaches a screen or a Cast device.</summary>
public sealed class YouTubeMusicBreakMusicProvider : IBreakMusicProvider, IPluginButtonHandler
{
    internal const string SetupButton = "setup";
    internal const string OpenButton = "open";

    /// <summary>A cold Edge start with a sign-in check can take most of this before the app makes
    /// its first sound and the session appears.</summary>
    internal static readonly TimeSpan SessionWait = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan SessionPoll = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan SkipSettleInterval = TimeSpan.FromMilliseconds(150);
    private const int SkipSettleAttempts = 10;

    /// <summary>Long enough for the idle prompt to finish drawing, so the play lands on it rather
    /// than racing it; short enough that the room barely notices the gap.</summary>
    internal static readonly TimeSpan RecoveryGrace = TimeSpan.FromSeconds(1);

    /// <summary>Edge keeps sounding what it had buffered for a moment after a pause is reported;
    /// restoring the level before the session says Paused can put a blip of it into the room.</summary>
    private static readonly TimeSpan PausedPoll = TimeSpan.FromMilliseconds(50);
    private const int PausedPollAttempts = 6;

    /// <summary>The audio session appears only once the app first makes a sound.</summary>
    private static readonly TimeSpan LevelRetry = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan SetupStatusLifetime = TimeSpan.FromSeconds(5);

    private readonly ILogger<YouTubeMusicBreakMusicProvider> _logger;
    private readonly IMessageBroker? _broker;
    private readonly IFlashService? _flash;
    private readonly IYouTubeMusicController _controller;
    private readonly SessionTracker _tracker = new();
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly string? _startUrl;
    private readonly bool _launchIfNotRunning;
    private readonly TimeSpan _fade;
    private readonly bool _recoverUnexpectedPause;

    private float _level = 1f;
    private (SetupStatus Status, DateTimeOffset ReadAt)? _setupStatus;

    public YouTubeMusicBreakMusicProvider(
        ILogger<YouTubeMusicBreakMusicProvider> logger, IPluginContext context, IMessageBroker broker,
        IFlashService flashService)
        : this(logger, context, controller: null, broker, flashService)
    {
    }

    internal YouTubeMusicBreakMusicProvider(
        ILogger<YouTubeMusicBreakMusicProvider> logger,
        IPluginContext context,
        IYouTubeMusicController? controller,
        IMessageBroker? broker = null,
        IFlashService? flashService = null,
        TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _logger = logger;
        _broker = broker;
        _flash = flashService;
        _time = time ?? TimeProvider.System;
        _delay = delay ?? ((span, token) => Task.Delay(span, _time, token));

        var settings = context.BindSettings<YouTubeMusicSettings>();

        _startUrl = PlaylistUrl.Normalize(settings.PlaylistUrl);
        _launchIfNotRunning = settings.LaunchIfNotRunning;
        _fade = TimeSpan.FromMilliseconds(Math.Max(0, settings.FadeMilliseconds));
        _recoverUnexpectedPause = settings.RecoverUnexpectedPause;

        _controller = controller ?? YouTubeMusicControllerFactory.ForCurrentPlatform(
            logger, EdgeProfile.Resolve(settings.ProfileDirectory));

        if (!string.IsNullOrWhiteSpace(settings.PlaylistUrl) && _startUrl is null)
        {
            context.ReportWarning(
                $"'{settings.PlaylistUrl}' is not a YouTube Music playlist link, so break music will "
                + "resume whatever the app already has loaded instead.");
        }

        if (_controller.Unavailable is { } reason)
        {
            context.ReportWarning(reason);
            return;
        }

        if (SetupStatusNow() == SetupStatus.AppNotInstalled)
        {
            context.ReportWarning(
                "YouTube Music is not installed in this plugin's Edge profile yet. Press \"Set up "
                + "YouTube Music\", sign in, then install it from the \"App available\" icon in Edge's address bar.");
        }

        _controller.SessionChanged += (_, _) => _ = RefreshAsync();

        // Not awaited: the console must not wait on another app to come up.
        _ = _controller.StartWatchingAsync();
    }

    public string DisplayName => "YouTube Music";

    public string SourceName => nameof(YouTubeMusicBreakMusicProvider);

    public bool RendersThroughHost => false;

    /// <summary>Null while an advert plays: the room hears it, but the screen does not name it.</summary>
    public BreakMusicTrack? CurrentTrack => _tracker.Track;

    public async Task<BreakMusicPlayback?> ReadPlaybackAsync(CancellationToken cancellationToken = default)
    {
        if (_controller.Unavailable is not null)
            return BreakMusicPlayback.Stopped;

        return Apply(await _controller.ReadAsync(cancellationToken)).Playback;
    }

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_controller.Unavailable is { } reason)
        {
            _logger.LogInformation("YouTube Music break music cannot start: {Reason}", reason);

            if (SetupStatusNow() == SetupStatus.Unsupported)
            {
                throw new KHostException(
                    "YouTube Music: break music only runs on Windows.",
                    suggestion: "",
                    "KH-YTMUSIC-UNSUPPORTED");
            }

            throw new KHostException(
                "YouTube Music: couldn't start break music — Microsoft Edge isn't installed on this machine.",
                "Install Microsoft Edge, then try again.",
                "KH-YTMUSIC-NO-BROWSER");
        }

        var before = await _controller.ReadAsync(cancellationToken);
        Apply(before);

        _tracker.NotePlayRequested();

        if (before is null)
        {
            // Opening the app again on every try would stack windows without ever seeing one.
            _logger.LogWarning("Cannot see YouTube Music's media session, so break music was not started");
            throw new KHostException(
                "YouTube Music: couldn't check what's playing, so break music wasn't started.",
                "Try again.",
                "KH-YTMUSIC-READ-FAILED");
        }

        if (before.Playback == SessionPlayback.Playing)
        {
            _logger.LogInformation("YouTube Music was already playing; leaving it as it is");
            await ApplyLevelAsync(cancellationToken);
            return true;
        }

        // A session already there is resumed where it stands: this is also how the host brings the
        // bed back after every singer, and reloading the playlist would restart it from the top.
        if (before.Playback != SessionPlayback.None)
            return await PlayAndLevelAsync(cancellationToken);

        return await LaunchAndPlayAsync(cancellationToken);
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        _tracker.NotePauseRequested(_time.GetUtcNow());
        await _controller.PauseAsync(cancellationToken);
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        _tracker.NotePlayRequested();
        await PlayAndLevelAsync(cancellationToken);
    }

    /// <summary>Fades over the configured length whenever the host asks for any fade; the host's
    /// own figure is a hint, and this plugin's setting is what the host chose for it.</summary>
    public async Task StopAsync(TimeSpan? fadeDuration = null, CancellationToken cancellationToken = default)
    {
        var playing = (await _controller.ReadAsync(cancellationToken))?.Playback == SessionPlayback.Playing;
        IReadOnlyList<float> steps = playing && fadeDuration > TimeSpan.Zero ? FadeCurve.GainSteps(_fade) : [];
        var faded = false;

        try
        {
            // The first set doubles as the check that the mixer can be reached at all; a fade with
            // no session to ride would only hold the singer's start for nothing.
            if (steps.Count > 0 && await _controller.SetLevelAsync(_level, cancellationToken))
            {
                faded = true;

                foreach (var gain in steps)
                {
                    await _delay(FadeCurve.StepInterval, cancellationToken);
                    await _controller.SetLevelAsync(_level * gain, cancellationToken);
                }
            }
        }
        finally
        {
            // Paused even when cancelled mid-fade: the alternative is a bed left playing half-faded
            // under a singer.
            await PauseThenRestoreLevelAsync(faded);
        }
    }

    public async Task SkipAsync(CancellationToken cancellationToken = default)
    {
        var before = await _controller.ReadAsync(cancellationToken);

        _tracker.NotePlayRequested();

        await _controller.SkipAsync(cancellationToken);

        // The host treats a skip from pause as playing afterwards, so it has to be.
        if (before?.Playback != SessionPlayback.Playing)
            await _controller.PlayAsync(cancellationToken);

        for (var attempt = 0; attempt < SkipSettleAttempts; attempt++)
        {
            await _delay(SkipSettleInterval, cancellationToken);

            var after = await _controller.ReadAsync(cancellationToken);
            var observation = Apply(after);

            if (!observation.Pending && after?.Title is { Length: > 0 } title && title != before?.Title)
                return;
        }
    }

    public async Task SetVolumeAsync(float volume, CancellationToken cancellationToken = default)
    {
        _level = Math.Clamp(volume, 0f, 1f);

        await _controller.SetLevelAsync(_level, cancellationToken);
    }

    public async Task InvokeButtonAsync(string key, CancellationToken cancellationToken = default)
    {
        switch (key)
        {
            case SetupButton:
                if (!await _controller.OpenSetupAsync(cancellationToken))
                {
                    _logger.LogWarning("YouTube Music setup could not open Microsoft Edge");
                    _flash?.Show("YouTube Music: couldn't open Microsoft Edge for setup.", FlashType.Warning);
                }
                break;

            case OpenButton:
                if (!await _controller.LaunchAppAsync(startUrl: null, cancellationToken))
                {
                    _logger.LogWarning("YouTube Music could not be launched from the Plugins page");
                    _flash?.Show("YouTube Music: couldn't open Microsoft Edge to launch the app.", FlashType.Warning);
                }
                break;

            default:
                return;
        }

        _setupStatus = null;
    }

    public PluginButtonState DescribeButton(string key)
    {
        var status = SetupStatusNow();

        return key switch
        {
            SetupButton => status switch
            {
                SetupStatus.Unsupported => new PluginButtonState { Enabled = false, Label = "YouTube Music: Windows only" },
                SetupStatus.BrowserNotFound => new PluginButtonState { Enabled = false, Label = "Microsoft Edge not found" },
                SetupStatus.Ready => new PluginButtonState { Label = "Set up YouTube Music again" },
                _ => PluginButtonState.Default,
            },
            OpenButton => new PluginButtonState { Visible = status == SetupStatus.Ready },
            _ => PluginButtonState.Default,
        };
    }

    /// <summary>Cached briefly: the row is redrawn often, and the check reads the profile's files.</summary>
    private SetupStatus SetupStatusNow()
    {
        var now = _time.GetUtcNow();

        if (_setupStatus is { } cached && now - cached.ReadAt < SetupStatusLifetime)
            return cached.Status;

        var status = _controller.GetSetupStatus();
        _setupStatus = (status, now);

        return status;
    }

    private async Task<bool> LaunchAndPlayAsync(CancellationToken cancellationToken)
    {
        if (!_launchIfNotRunning && !_controller.IsBrowserRunning)
        {
            _logger.LogInformation("YouTube Music is not running and this plugin is set not to open it");
            throw new KHostException(
                "YouTube Music: isn't open, and this plugin is set not to launch it. Turn on \"Launch if not "
                + "running\" in settings, or open YouTube Music yourself.",
                "Turn on \"Launch if not running\" in this plugin's settings, or open YouTube Music yourself, "
                + "then try again.",
                "KH-YTMUSIC-NOT-RUNNING");
        }

        if (SetupStatusNow() != SetupStatus.Ready)
        {
            _logger.LogWarning("YouTube Music is not set up in this plugin's Edge profile; press \"Set up YouTube Music\" on the Plugins page");
            throw new KHostException(
                "YouTube Music: not set up in this plugin's Edge profile. Press \"Set up YouTube Music\" on the Plugins page.",
                "Press \"Set up YouTube Music\" on the Plugins page, then try again.",
                "KH-YTMUSIC-NOT-SET-UP");
        }

        if (!await _controller.LaunchAppAsync(_startUrl, cancellationToken))
        {
            _logger.LogWarning("YouTube Music could not be launched");
            throw new KHostException(
                "YouTube Music: couldn't open Microsoft Edge to start break music.",
                "Try again.",
                "KH-YTMUSIC-LAUNCH-FAILED");
        }

        var waited = TimeSpan.Zero;
        SessionSnapshot? session = null;

        while (waited < SessionWait)
        {
            await _delay(SessionPoll, cancellationToken);
            waited += SessionPoll;

            session = await _controller.ReadAsync(cancellationToken);

            if (session is { Playback: not SessionPlayback.None })
                break;
        }

        Apply(session);

        if (session is not { Playback: not SessionPlayback.None })
        {
            _logger.LogWarning(
                _startUrl is null
                    ? "YouTube Music opened but nothing started. Set a playlist in this plugin's settings, or start one in the app"
                    : "YouTube Music opened the playlist but nothing started playing within {Wait}",
                SessionWait);

            if (_startUrl is null)
            {
                throw new KHostException(
                    "YouTube Music: opened, but nothing started playing. Set a playlist in this plugin's "
                    + "settings, or start one in the app.",
                    "Set a playlist in this plugin's settings, or start one in the app, then try again.",
                    "KH-YTMUSIC-SESSION-TIMEOUT");
            }

            throw new KHostException(
                "YouTube Music: opened the playlist, but nothing started playing. Check the playlist link in "
                + "this plugin's settings.",
                "Check the playlist link in this plugin's settings, then try again.",
                "KH-YTMUSIC-SESSION-TIMEOUT");
        }

        if (session.Playback == SessionPlayback.Playing)
        {
            await ApplyLevelAsync(cancellationToken);
            _logger.LogInformation("Break music playing from YouTube Music");
            return true;
        }

        return await PlayAndLevelAsync(cancellationToken);
    }

    private async Task<bool> PlayAndLevelAsync(CancellationToken cancellationToken)
    {
        if (!await _controller.PlayAsync(cancellationToken))
        {
            _logger.LogWarning("YouTube Music refused to play");
            throw new KHostException(
                "YouTube Music: refused to play.",
                "Try again.",
                "KH-YTMUSIC-PLAY-REFUSED");
        }

        await ApplyLevelAsync(cancellationToken);

        _logger.LogInformation("Break music playing from YouTube Music");
        return true;
    }

    private async Task ApplyLevelAsync(CancellationToken cancellationToken)
    {
        if (await _controller.SetLevelAsync(_level, cancellationToken))
            return;

        _ = Task.Run(async () =>
        {
            await _delay(LevelRetry, CancellationToken.None);
            await _controller.SetLevelAsync(_level, CancellationToken.None);
        }, CancellationToken.None);
    }

    private async Task PauseThenRestoreLevelAsync(bool faded)
    {
        _tracker.NotePauseRequested(_time.GetUtcNow());
        await _controller.PauseAsync(CancellationToken.None);

        if (!faded)
            return;

        for (var attempt = 0; attempt < PausedPollAttempts; attempt++)
        {
            if ((await _controller.ReadAsync(CancellationToken.None))?.Playback != SessionPlayback.Playing)
                break;

            await _delay(PausedPoll, CancellationToken.None);
        }

        // Restored for the next start; the mixer remembers a level, and silence would outlive us.
        await _controller.SetLevelAsync(_level, CancellationToken.None);
    }

    private async Task RefreshAsync()
    {
        try
        {
            Apply(await _controller.ReadAsync());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read YouTube Music's media session after it moved");
        }
    }

    private SessionObservation Apply(SessionSnapshot? snapshot)
    {
        var observation = _tracker.Observe(snapshot, _time.GetUtcNow());

        if (observation.Changed)
            _broker?.Announce(new BreakMusicTrackChanged(SourceName));

        if (observation.Pending)
        {
            _ = Task.Run(async () =>
            {
                await _delay(SessionTracker.TransientWindow, CancellationToken.None);
                await RefreshAsync();
            });
        }

        if (observation.UnexpectedPause && snapshot is not null)
            _ = RecoverFromPauseAsync(snapshot);

        return observation;
    }

    /// <summary>YouTube Music pauses on its own after a long stretch with nobody touching the page,
    /// behind "Are you still there?". Nothing outside the page can answer the prompt; pressing play
    /// through the media session is the attempt.</summary>
    private async Task RecoverFromPauseAsync(SessionSnapshot paused)
    {
        var noticed = _time.GetUtcNow();

        if (!_recoverUnexpectedPause)
        {
            _logger.LogInformation("YouTube Music paused by itself; leaving it paused, as this plugin is set to");
            return;
        }

        _logger.LogInformation(
            "YouTube Music paused by itself at {Position:mm\\:ss} of {Duration:mm\\:ss}; pressing play in {Grace}",
            paused.PositionAt(noticed), paused.Duration, RecoveryGrace);

        try
        {
            await _delay(RecoveryGrace, CancellationToken.None);

            // A host who stopped or paused the bed during the grace has the last word.
            if (_tracker.PauseRequestedSince(noticed))
                return;

            if ((await _controller.ReadAsync())?.Playback != SessionPlayback.Paused)
                return;

            var resumed = await _controller.PlayAsync();

            _logger.LogInformation(resumed
                ? "Pressed play on YouTube Music after it paused by itself"
                : "YouTube Music refused play after pausing by itself");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not press play on YouTube Music after it paused by itself");
        }
    }
}
