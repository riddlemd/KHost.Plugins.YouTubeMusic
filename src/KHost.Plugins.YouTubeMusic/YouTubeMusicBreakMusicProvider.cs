using KHost.Abstractions.Exceptions;
using KHost.Abstractions.Messaging;
using KHost.Abstractions.Messaging.Messages;
using KHost.Abstractions.Models;
using KHost.Abstractions.Services;
using KHost.Plugins.YouTubeMusic.Audio;
using KHost.Plugins.YouTubeMusic.Control;
using Microsoft.Extensions.Logging;

namespace KHost.Plugins.YouTubeMusic;

/// <summary>Break music out of YouTube Music on this machine: the app installed in Edge on Windows,
/// KHost's own YouTube Music app on macOS. The host carries none of this audio, so nothing here
/// reaches a screen or a Cast device.</summary>
public sealed class YouTubeMusicBreakMusicProvider : IBreakMusicProvider, IPluginButtonHandler, IDisposable
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

    /// <summary>The audio session appears only once the app first makes a sound, and a cold Edge
    /// start can take several seconds past the media session to make one.</summary>
    internal static readonly TimeSpan LevelRetry = TimeSpan.FromSeconds(1);
    internal const int LevelRetryAttempts = 10;

    /// <summary>The host sets no level of its own: every output runs through the room's mixer, so
    /// the app always plays at full and only the fades move it.</summary>
    internal const float FullLevel = 1f;

    private static readonly TimeSpan SetupStatusLifetime = TimeSpan.FromSeconds(5);

    /// <summary>The app is installed in Edge (or signed in on macOS) out of band, and nothing raises
    /// an event for it. Each check is a folder test, or one read of Edge's Preferences while the app
    /// is missing.</summary>
    internal static readonly TimeSpan SetupStatusPoll = TimeSpan.FromSeconds(5);

    /// <summary>Only the macOS app reports a sign-in, so this names the button macOS shows signed out.</summary>
    internal const string SignedOutMessage = $"YouTube Music: signed out — press \"{SetupButtonLabel.SignIn}\" on the Plugins page";

    private readonly ILogger<YouTubeMusicBreakMusicProvider> _logger;
    private readonly IPluginContext _context;
    private readonly IMessageBroker? _broker;
    private readonly IFlashService? _flash;
    private readonly IYouTubeMusicController _controller;
    private readonly SessionTracker _tracker = new();
    private readonly SignInWatch _signIn = new();
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly string? _startUrl;
    private readonly bool _launchIfNotRunning;
    private readonly IBreakMusicSettings _breakMusic;
    private readonly bool _recoverUnexpectedPause;

    // The host changes this while running, so each fade reads it fresh.
    private TimeSpan Fade => TimeSpan.FromTicks(Math.Max(0, _breakMusic.FadeDuration.Ticks));

    /// <summary>One operation moves the level at a time. Taking it first cuts short whatever ramp is
    /// running, so two ramps never fight and the newcomer carries on from where that one stopped.</summary>
    private readonly SemaphoreSlim _levelGate = new(1, 1);
    private readonly object _rampSync = new();
    private CancellationTokenSource _rampCancel = new();

    private readonly object _warningSync = new();

    /// <summary>The row's unfinished-setup warning while it shows; 0 when there is none.</summary>
    private int _notSetUpWarning;
    private Task _fadeIn = Task.CompletedTask;

    /// <summary>The app had a media session at the last read. Windows restores the level it last
    /// kept for msedge.exe onto every new audio session, so each appearance is pushed to full.</summary>
    private bool _sessionPresent;

    /// <summary>The venue has asked for music and not since paused or stopped it.</summary>
    private volatile bool _wanted;

    /// <summary>The last level the mixer took; NaN until it has taken one.</summary>
    private float _applied = float.NaN;

    private readonly object _statusSync = new();
    private (SetupStatus Status, DateTimeOffset ReadAt)? _setupStatus;

    /// <summary>The last status read, kept apart from the cache so forgetting that is not a change.</summary>
    private SetupStatus? _knownStatus;

    /// <summary>Held for the provider's life: a collected timer stops firing.</summary>
    private ITimer? _setupStatusTimer;

    public YouTubeMusicBreakMusicProvider(
        ILogger<YouTubeMusicBreakMusicProvider> logger, IPluginContext context, IMessageBroker broker,
        IFlashService flashService, IHostDirectories directories, IBreakMusicSettings breakMusic)
        : this(logger, context, controller: null, breakMusic, broker, flashService, binDirectory: directories.BinDirectory)
    {
    }

    internal YouTubeMusicBreakMusicProvider(
        ILogger<YouTubeMusicBreakMusicProvider> logger,
        IPluginContext context,
        IYouTubeMusicController? controller,
        IBreakMusicSettings breakMusic,
        IMessageBroker? broker = null,
        IFlashService? flashService = null,
        TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        string? binDirectory = null)
    {
        _logger = logger;
        _context = context;
        _breakMusic = breakMusic;
        _broker = broker;
        _flash = flashService;
        _time = time ?? TimeProvider.System;
        _delay = delay ?? ((span, token) => Task.Delay(span, _time, token));

        var settings = context.BindSettings<YouTubeMusicSettings>();

        _startUrl = PlaylistUrl.Normalize(settings.PlaylistUrl);
        _launchIfNotRunning = settings.LaunchIfNotRunning;
        _recoverUnexpectedPause = settings.RecoverUnexpectedPause;

        _controller = controller ?? YouTubeMusicControllerFactory.ForCurrentPlatform(
            logger, settings.ProfileDirectory, binDirectory ?? throw new ArgumentNullException(nameof(binDirectory)));

        if (!string.IsNullOrWhiteSpace(settings.PlaylistUrl) && _startUrl is null)
        {
            _context.AddWarning(
                $"'{settings.PlaylistUrl}' is not a YouTube Music playlist link, so break music will "
                + "resume whatever the app already has loaded instead.");
        }

        if (_controller.Unavailable is { } reason)
        {
            _context.AddWarning(reason);
            return;
        }

        // The first read warns when setup is unfinished; every later move is noticed the same way.
        SetupStatusNow();
        _setupStatusTimer = _time.CreateTimer(_ => PollSetupStatus(), null, SetupStatusPoll, SetupStatusPoll);

        _controller.SessionChanged += (_, _) => _ = RefreshAsync();

        // Not awaited: the console must not wait on another app to come up.
        _ = _controller.StartWatchingAsync();
    }

    public string DisplayName => "YouTube Music";

    public string SourceName => nameof(YouTubeMusicBreakMusicProvider);

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

            var status = SetupStatusNow();

            if (status == SetupStatus.Unsupported)
            {
                throw new KHostException(
                    "YouTube Music: break music only runs on Windows and macOS.",
                    suggestion: "",
                    "KH-YTMUSIC-UNSUPPORTED");
            }

            if (status == SetupStatus.HelperMissing)
            {
                throw new KHostException(
                    "YouTube Music: this build of the plugin has no YouTube Music app for macOS.",
                    "Install a release of the plugin built with the macOS app, then restart KHost.",
                    "KH-YTMUSIC-NO-HELPER");
            }

            throw new KHostException(
                $"YouTube Music: couldn't start break music — {_controller.BrowserName} isn't installed on this machine.",
                $"Install {_controller.BrowserName}, then try again.",
                "KH-YTMUSIC-NO-BROWSER");
        }

        _wanted = true;

        var rampToken = await TakeLevelAsync(cancellationToken);

        try
        {
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

            // A session already there is resumed where it stands: this is also how the host brings
            // the bed back after every singer, and reloading the playlist would restart it from the top.
            if (before.Playback != SessionPlayback.None)
                return await PlayRisingAsync(rampToken, cancellationToken);

            return await LaunchAndPlayAsync(rampToken, cancellationToken);
        }
        finally
        {
            _levelGate.Release();
        }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        _wanted = false;
        _tracker.NotePauseRequested(_time.GetUtcNow());
        await FadeOutThenPauseAsync(fade: true, cancellationToken);
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        _wanted = true;
        _tracker.NotePlayRequested();

        var rampToken = await TakeLevelAsync(cancellationToken);

        try
        {
            await PlayRisingAsync(rampToken, cancellationToken);
        }
        finally
        {
            _levelGate.Release();
        }
    }

    /// <summary>Fades over the configured length whenever the host asks for any fade; the host's
    /// own figure is a hint, and this plugin's setting is what the host chose for it.</summary>
    public Task StopAsync(TimeSpan? fadeDuration = null, CancellationToken cancellationToken = default)
    {
        _wanted = false;
        return FadeOutThenPauseAsync(fadeDuration > TimeSpan.Zero, cancellationToken);
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

    /// <summary>The host never calls this; it stays because the contract carries it.</summary>
    public Task SetVolumeAsync(float volume, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task InvokeButtonAsync(string key, CancellationToken cancellationToken = default)
    {
        switch (key)
        {
            case SetupButton:
                if (!await _controller.OpenSetupAsync(cancellationToken))
                {
                    _logger.LogWarning("YouTube Music setup could not open {Browser}", _controller.BrowserName);
                    _flash?.Show($"YouTube Music: couldn't open {_controller.BrowserName} for setup.", FlashType.Warning);
                }
                break;

            case OpenButton:
                if (!await _controller.ShowAppAsync(cancellationToken))
                {
                    _logger.LogWarning("YouTube Music could not be launched from the Plugins page");
                    _flash?.Show($"YouTube Music: couldn't open {_controller.BrowserName} to launch the app.", FlashType.Warning);
                }
                break;

            default:
                return;
        }

        ForgetSetupStatus();
    }

    public PluginButtonState DescribeButton(string key)
    {
        var status = SetupStatusNow();

        return key switch
        {
            SetupButton => status switch
            {
                SetupStatus.Unsupported => new PluginButtonState { Enabled = false, Label = "YouTube Music: Windows and macOS only" },
                SetupStatus.BrowserNotFound => new PluginButtonState { Enabled = false, Label = $"{_controller.BrowserName} not found" },
                SetupStatus.HelperMissing => new PluginButtonState { Enabled = false, Label = "YouTube Music app missing from this plugin build" },
                SetupStatus.Ready => new PluginButtonState { Label = "Set up YouTube Music again" },

                // Plays already, signed out and so with adverts; signing in is the rest of setup.
                SetupStatus.NotSignedIn => new PluginButtonState { Label = SetupButtonLabel.SignIn },
                _ => PluginButtonState.Default,
            },
            OpenButton => new PluginButtonState { Visible = CanPlay(status) },
            _ => PluginButtonState.Default,
        };
    }

    public void Dispose() => _setupStatusTimer?.Dispose();

    private static bool CanPlay(SetupStatus status) => status is SetupStatus.Ready or SetupStatus.NotSignedIn;

    /// <summary>Cached briefly: the row is redrawn often, and the check reads the profile's files.</summary>
    private SetupStatus SetupStatusNow()
    {
        SetupStatus status;
        SetupStatus? before;

        lock (_statusSync)
        {
            var now = _time.GetUtcNow();

            if (_setupStatus is { } cached && now - cached.ReadAt < SetupStatusLifetime)
                return cached.Status;

            status = _controller.GetSetupStatus();
            _setupStatus = (status, now);
            before = _knownStatus;
            _knownStatus = status;
        }

        if (before != status)
            OnSetupStatusMoved(before, status);

        return status;
    }

    private void ForgetSetupStatus()
    {
        lock (_statusSync)
            _setupStatus = null;
    }

    private void PollSetupStatus()
    {
        try
        {
            ForgetSetupStatus();
            SetupStatusNow();
        }
        catch (Exception ex)
        {
            // A timer callback that throws takes the host down with it.
            _logger.LogDebug(ex, "Could not re-read YouTube Music's setup");
        }
    }

    /// <summary>The row warns while setup is unfinished and takes the warning back once it is.</summary>
    private void OnSetupStatusMoved(SetupStatus? before, SetupStatus status)
    {
        var unfinished = status is SetupStatus.AppNotInstalled or SetupStatus.NotSignedIn;

        // Its own lock: the host announces the change, and a redraw re-enters through the status read.
        lock (_warningSync)
        {
            if (unfinished && _notSetUpWarning == 0)
            {
                _notSetUpWarning = _context.AddWarning(_controller.NotSetUpWarning);
            }
            else if (!unfinished && _notSetUpWarning != 0)
            {
                _context.ClearWarning(_notSetUpWarning);
                _notSetUpWarning = 0;
            }
        }

        // The Plugins page re-reads DescribeButton only when it redraws, and this is what redraws it.
        if (before is not null)
            _broker?.Announce(new PluginsChanged());
    }

    private async Task<bool> LaunchAndPlayAsync(CancellationToken rampToken, CancellationToken cancellationToken)
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

        if (!CanPlay(SetupStatusNow()))
        {
            _logger.LogWarning(
                "YouTube Music is not set up in this plugin's {Browser} profile; press \"Set up YouTube Music\" on the Plugins page",
                _controller.BrowserShortName);
            throw new KHostException(
                $"YouTube Music: not set up in this plugin's {_controller.BrowserShortName} profile. Press \"Set up YouTube Music\" on the Plugins page.",
                "Press \"Set up YouTube Music\" on the Plugins page, then try again.",
                "KH-YTMUSIC-NOT-SET-UP");
        }

        if (!await _controller.LaunchAppAsync(_startUrl, cancellationToken))
        {
            _logger.LogWarning("YouTube Music could not be launched");
            throw new KHostException(
                $"YouTube Music: couldn't open {_controller.BrowserName} to start break music.",
                "Try again.",
                "KH-YTMUSIC-LAUNCH-FAILED");
        }

        // Measured on the clock, not summed from the polls: each read can take a while during a cold
        // Edge start, and summing let those stack the wait well past SessionWait.
        var deadline = _time.GetUtcNow() + SessionWait;
        SessionSnapshot? session = null;

        for (var remaining = SessionWait; remaining > TimeSpan.Zero; remaining = deadline - _time.GetUtcNow())
        {
            await _delay(remaining < SessionPoll ? remaining : SessionPoll, cancellationToken);

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
            // A fresh launch sounds before anything can reach its level, so the rise starts from
            // the moment it is heard.
            var steps = FadeCurve.RiseSteps(Fade);

            if (steps.Count > 0 && await SetLevelAsync(0f, cancellationToken))
                BeginRise(steps, rampToken);
            else
                await ApplyLevelAsync(cancellationToken);

            _logger.LogInformation("Break music playing from YouTube Music");
            return true;
        }

        return await PlayRisingAsync(rampToken, cancellationToken);
    }

    /// <summary>Silent before play, so the first thing the room hears is the bottom of the rise.</summary>
    private async Task<bool> PlayRisingAsync(CancellationToken rampToken, CancellationToken cancellationToken)
    {
        var steps = FadeCurve.RiseSteps(Fade);

        // A mixer with nothing to hold yet (Edge before its first sound) plays at the level instead.
        var rising = steps.Count > 0 && await SetLevelAsync(0f, cancellationToken);

        // A refusal leaves the mixer silent; it goes back to full before the throw reaches the
        // host, or the next start would be silent too.
        if (!await _controller.PlayAsync(cancellationToken))
        {
            if (rising)
                await SetLevelAsync(FullLevel, CancellationToken.None);

            _logger.LogWarning("YouTube Music refused to play");
            throw new KHostException(
                "YouTube Music: refused to play.",
                "Try again.",
                "KH-YTMUSIC-PLAY-REFUSED");
        }

        if (rising)
            BeginRise(steps, rampToken);
        else
            await ApplyLevelAsync(cancellationToken);

        _logger.LogInformation("Break music playing from YouTube Music");
        return true;
    }

    /// <summary>Not awaited by the caller: the host waits on start and resume, and a pause pressed
    /// during the rise has to be able to cut it short.</summary>
    private void BeginRise(IReadOnlyList<float> steps, CancellationToken rampToken)
        => _fadeIn = RiseAsync(steps, rampToken);

    private async Task RiseAsync(IReadOnlyList<float> steps, CancellationToken rampToken)
    {
        try
        {
            foreach (var gain in steps)
            {
                await _delay(FadeCurve.StepInterval, rampToken);
                await SetLevelAsync(FullLevel * gain, rampToken);
            }
        }
        catch (OperationCanceledException) when (rampToken.IsCancellationRequested)
        {
            // Cut short by the next operation, which goes on from the level this left.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "YouTube Music's fade-in stopped part way");
        }
    }

    /// <summary>Paused even when cancelled or cut short mid-fade: the alternative is a bed left
    /// playing half-faded under a singer.</summary>
    private async Task FadeOutThenPauseAsync(bool fade, CancellationToken cancellationToken)
    {
        var rampToken = await TakeLevelAsync(cancellationToken);

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, rampToken);
            var faded = false;

            try
            {
                var playing = (await _controller.ReadAsync(cancellationToken))?.Playback == SessionPlayback.Playing;
                IReadOnlyList<float> steps = playing && fade ? FadeCurve.GainSteps(Fade) : [];

                // From wherever a rise cut short left it.
                var from = float.IsNaN(_applied) ? FullLevel : _applied;

                // The first set doubles as the check that the mixer can be reached at all; a fade
                // with no session to ride would only hold the singer's start for nothing.
                if (steps.Count > 0 && await SetLevelAsync(from, linked.Token))
                {
                    faded = true;

                    foreach (var gain in steps)
                    {
                        await _delay(FadeCurve.StepInterval, linked.Token);
                        await SetLevelAsync(from * gain, linked.Token);
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && rampToken.IsCancellationRequested)
            {
                // Cut short by the next operation, which runs once the pause has landed.
            }
            finally
            {
                await PauseThenRestoreLevelAsync(faded);
            }
        }
        finally
        {
            _levelGate.Release();
        }
    }

    /// <returns>The token that cuts short a ramp this operation starts.</returns>
    private async Task<CancellationToken> TakeLevelAsync(CancellationToken cancellationToken)
    {
        lock (_rampSync)
            _rampCancel.Cancel();

        await _levelGate.WaitAsync(cancellationToken);

        // Cancelled above; never faults.
        await _fadeIn;

        lock (_rampSync)
        {
            _rampCancel = new CancellationTokenSource();
            return _rampCancel.Token;
        }
    }

    private async Task<bool> SetLevelAsync(float level, CancellationToken cancellationToken)
    {
        if (!await _controller.SetLevelAsync(level, cancellationToken))
            return false;

        _applied = level;
        return true;
    }

    private async Task ApplyLevelAsync(CancellationToken cancellationToken)
    {
        if (await SetLevelAsync(FullLevel, cancellationToken))
            return;

        _ = Task.Run(() => PushFullLevelAsync(firstTryNow: false), CancellationToken.None);
    }

    /// <summary>Retried while the audio session has yet to appear. Stands aside for any operation
    /// holding the level and for a running fade-in: each ends at full by itself, and a set from here
    /// in the middle of one would jump it.</summary>
    private async Task PushFullLevelAsync(bool firstTryNow)
    {
        try
        {
            for (var attempt = 0; attempt < LevelRetryAttempts; attempt++)
            {
                if (attempt > 0 || !firstTryNow)
                    await _delay(LevelRetry, CancellationToken.None);

                if (!_levelGate.Wait(0))
                    return;

                try
                {
                    if (!_fadeIn.IsCompleted)
                        return;

                    if (await SetLevelAsync(FullLevel, CancellationToken.None))
                        return;
                }
                finally
                {
                    _levelGate.Release();
                }
            }

            _logger.LogDebug("YouTube Music's audio session never appeared to take its level");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not put YouTube Music at full level");
        }
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
        await SetLevelAsync(FullLevel, CancellationToken.None);
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

        NoteSignIn(snapshot?.SignedIn);
        NoteSessionPresence(snapshot);

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

    private void NoteSessionPresence(SessionSnapshot? snapshot)
    {
        // Null is a read that failed, which says nothing about whether the session is there.
        if (snapshot is null)
            return;

        var present = snapshot.Playback != SessionPlayback.None;
        var appeared = present && !_sessionPresent;
        _sessionPresent = present;

        // Not awaited, and safe to start inline: the first try never waits on the level gate.
        if (appeared)
            _ = PushFullLevelAsync(firstTryNow: true);
    }

    /// <summary>Keeps playing signed out: the host is told once, and the setup button moves to the sign-in.</summary>
    private void NoteSignIn(bool? signedIn)
    {
        var before = _signIn.SignedIn;

        if (_signIn.Observe(signedIn, _wanted))
        {
            _logger.LogWarning("YouTube Music was signed out while break music was wanted; playing on signed out");
            _flash?.Show(SignedOutMessage, FlashType.Warning);
        }

        if (_signIn.SignedIn != before)
            ForgetSetupStatus();
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
