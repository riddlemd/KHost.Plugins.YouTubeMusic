using KHost.Abstractions.Exceptions;
using KHost.Abstractions.Messaging;
using KHost.Abstractions.Messaging.Messages;
using KHost.Abstractions.Models;
using KHost.Abstractions.Services;
using KHost.Plugins.YouTubeMusic.Audio;
using KHost.Plugins.YouTubeMusic.Control;
using Microsoft.Extensions.Logging.Abstractions;

namespace KHost.Plugins.YouTubeMusic.Tests;

public class YouTubeMusicBreakMusicProviderTests
{
    private const string Playlist = "https://music.youtube.com/playlist?list=PLbed";

    private static readonly SessionSnapshot Playing = new(SessionPlayback.Playing, "Blue Monday", "New Order");
    private static readonly SessionSnapshot Paused = Playing with { Playback = SessionPlayback.Paused };

    private readonly FakeYouTubeMusicController _controller = new();
    private readonly IPluginContext _context = Substitute.For<IPluginContext>();
    private readonly IMessageBroker _broker = Substitute.For<IMessageBroker>();
    private readonly IFlashService _flash = Substitute.For<IFlashService>();
    private readonly ManualClock _clock = new();

    /// <summary>Called with each delay the provider takes, after the clock has moved past it.</summary>
    private Action<TimeSpan>? _onDelay;

    /// <summary>A task to wait on instead of returning at once; null lets the delay pass.</summary>
    private Func<TimeSpan, CancellationToken, Task?>? _holdDelay;

    private YouTubeMusicBreakMusicProvider Build(YouTubeMusicSettings? settings = null)
    {
        _context.BindSettings<YouTubeMusicSettings>().Returns(settings ?? new YouTubeMusicSettings());

        return new YouTubeMusicBreakMusicProvider(
            NullLogger<YouTubeMusicBreakMusicProvider>.Instance, _context, _controller, _broker, _flash, _clock,
            (span, token) =>
            {
                _clock.Advance(span);
                _onDelay?.Invoke(span);
                token.ThrowIfCancellationRequested();
                return _holdDelay?.Invoke(span, token) ?? Task.CompletedTask;
            });
    }

    /// <summary>Holds the <paramref name="nth"/> fade step (1-based) until its token is cancelled.</summary>
    private void HoldFadeStep(int nth)
    {
        var seen = 0;
        _holdDelay = (span, token) =>
            span == FadeCurve.StepInterval && ++seen == nth ? Task.Delay(Timeout.Infinite, token) : null;
    }

    /// <summary>A fade-in runs on after the call that began it returns.</summary>
    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var waited = 0; !condition() && waited < 5000; waited += 10)
            await Task.Delay(10);

        Assert.True(condition(), "Timed out waiting for the level to settle.");
    }

    private static readonly YouTubeMusicSettings NoFade = new() { FadeMilliseconds = 0 };

    /// <summary>300ms: three steps, 0.1, 0.01 and 0 down; 0.01, 0.1 and 1 up.</summary>
    private static readonly YouTubeMusicSettings ShortFade = new() { FadeMilliseconds = 300 };

    [Fact]
    public async Task StartAsync_AlreadyPlaying_SendsNoCommandButTheLevel()
    {
        _controller.Snapshot = Playing;

        Assert.True(await Build().StartAsync());
        Assert.Equal(["level:1"], _controller.Calls);
    }

    // This is also how the host brings the bed back after every singer: reloading the playlist
    // would restart it from its first track each time.
    [Fact]
    public async Task StartAsync_SessionPaused_PlaysWhereItStandsWithoutRelaunching()
    {
        _controller.Snapshot = Paused;

        Assert.True(await Build(new YouTubeMusicSettings { PlaylistUrl = Playlist, FadeMilliseconds = 0 }).StartAsync());
        Assert.Equal(["play", "level:1"], _controller.Calls);
    }

    [Fact]
    public async Task StartAsync_NoSession_LaunchesTheAppAtThePlaylistAndWaitsForIt()
    {
        _controller.Snapshot = SessionSnapshot.None;
        _controller.OnCommand = command => command.StartsWith("launch:", StringComparison.Ordinal) ? Playing : null;

        Assert.True(await Build(new YouTubeMusicSettings { PlaylistUrl = Playlist, FadeMilliseconds = 0 }).StartAsync());
        Assert.Equal(["launch:https://music.youtube.com/watch?list=PLbed", "level:1"], _controller.Calls);
    }

    [Fact]
    public async Task StartAsync_LaunchedAppLoadsPaused_PressesPlay()
    {
        _controller.Snapshot = SessionSnapshot.None;
        _controller.OnCommand = command => command.StartsWith("launch:", StringComparison.Ordinal) ? Paused : null;

        Assert.True(await Build(NoFade).StartAsync());
        Assert.Equal(["launch:", "play", "level:1"], _controller.Calls);
    }

    [Fact]
    public async Task StartAsync_LaunchedAppNeverMakesASession_GivesUpAfterTheWait()
    {
        _controller.Snapshot = SessionSnapshot.None;
        var started = _clock.Now;

        await Assert.ThrowsAsync<KHostException>(() => Build().StartAsync());
        Assert.Equal(["launch:"], _controller.Calls);
        Assert.Equal(YouTubeMusicBreakMusicProvider.SessionWait, _clock.Now - started);
    }

    [Fact]
    public async Task StartAsync_NotRunningAndNotAllowedToLaunch_DoesNothing()
    {
        _controller.Snapshot = SessionSnapshot.None;

        await Assert.ThrowsAsync<KHostException>(
            () => Build(new YouTubeMusicSettings { LaunchIfNotRunning = false }).StartAsync());
        Assert.Empty(_controller.Calls);
    }

    [Fact]
    public async Task StartAsync_RunningButNotAllowedToLaunch_StillOpensTheAppInIt()
    {
        _controller.Snapshot = SessionSnapshot.None;
        _controller.IsBrowserRunning = true;
        _controller.OnCommand = command => command.StartsWith("launch:", StringComparison.Ordinal) ? Playing : null;

        Assert.True(await Build(new YouTubeMusicSettings { LaunchIfNotRunning = false }).StartAsync());
        Assert.Contains("launch:", _controller.Calls);
    }

    [Fact]
    public async Task StartAsync_AppNotInstalled_DoesNotLaunch()
    {
        _controller.Snapshot = SessionSnapshot.None;
        _controller.Status = SetupStatus.AppNotInstalled;

        await Assert.ThrowsAsync<KHostException>(() => Build().StartAsync());
        Assert.Empty(_controller.Calls);
    }

    // Opening the app on every try while the session cannot be seen stacks a window per attempt.
    [Fact]
    public async Task StartAsync_CannotSeeTheSession_DoesNotLaunch()
    {
        _controller.Snapshot = null;

        await Assert.ThrowsAsync<KHostException>(() => Build().StartAsync());
        Assert.Empty(_controller.Calls);
    }

    [Fact]
    public async Task Unavailable_Always_WarnsAndRefusesToStart()
    {
        _controller.Unavailable = "Windows only";

        var provider = Build();

        _context.Received(1).AddWarning("Windows only");
        await Assert.ThrowsAsync<KHostException>(() => provider.StartAsync());
        Assert.Equal(BreakMusicPlayback.Stopped, await provider.ReadPlaybackAsync());
        Assert.Empty(_controller.Calls);
    }

    [Fact]
    public void Construct_NotAPlaylist_Warns()
    {
        Build(new YouTubeMusicSettings { PlaylistUrl = "https://example.com/" });

        _context.Received(1).AddWarning(Arg.Is<string>(m => m.Contains("not a YouTube Music playlist link")));
    }

    // The fade is what the host waits on before the singer's song starts; it has to come down in
    // steps, stop, and leave the mixer at full for the next start.
    [Fact]
    public async Task StopAsync_WithAFade_StepsDownThenPausesThenRestoresFullLevel()
    {
        _controller.Snapshot = Playing;
        _controller.OnCommand = command => command == "pause" ? Paused : null;

        await Build(ShortFade).StopAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["level:1", "level:0.1", "level:0.01", "level:0", "pause", "level:1"], _controller.Calls);
    }

    // The host no longer sets a level; one arriving from an old caller must not reach the mixer.
    [Fact]
    public async Task SetVolumeAsync_Anything_LeavesTheMixerAlone()
    {
        _controller.Snapshot = Playing;
        _controller.OnCommand = command => command == "pause" ? Paused : null;
        var provider = Build(ShortFade);

        await provider.SetVolumeAsync(0.15f);
        await provider.StopAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["level:1", "level:0.1", "level:0.01", "level:0", "pause", "level:1"], _controller.Calls);
    }

    // Windows puts the level it last kept for msedge.exe back onto every new audio session.
    [Fact]
    public void SessionChanged_SessionAppears_PushesFullLevel()
    {
        Build();

        _controller.Snapshot = Playing;
        _controller.RaiseSessionChanged();

        Assert.Equal(["level:1"], _controller.Calls);
    }

    [Fact]
    public void SessionChanged_SessionStillThere_DoesNotPushAgain()
    {
        Build();
        _controller.Snapshot = Playing;
        _controller.RaiseSessionChanged();

        _controller.Snapshot = Playing with { Title = "Temptation" };
        _controller.RaiseSessionChanged();

        Assert.Equal(["level:1"], _controller.Calls);
    }

    // A restarted Edge is a new audio session, and the remembered level comes back with it.
    [Fact]
    public void SessionChanged_SessionComesBack_PushesFullLevelAgain()
    {
        Build();
        _controller.Snapshot = Playing;
        _controller.RaiseSessionChanged();

        _controller.Snapshot = SessionSnapshot.None;
        _clock.Advance(SessionTracker.TransientWindow + TimeSpan.FromSeconds(1));
        _controller.RaiseSessionChanged();
        _controller.Snapshot = Playing;
        _controller.RaiseSessionChanged();

        Assert.Equal(["level:1", "level:1"], _controller.Calls);
    }

    [Fact]
    public void SessionChanged_ReadFails_IsNotTakenForTheSessionGoing()
    {
        Build();
        _controller.Snapshot = Playing;
        _controller.RaiseSessionChanged();

        _controller.Snapshot = null;
        _controller.RaiseSessionChanged();
        _controller.Snapshot = Playing;
        _controller.RaiseSessionChanged();

        Assert.Equal(["level:1"], _controller.Calls);
    }

    // The session appearing as the audio does: the mixer may not have it yet.
    [Fact]
    public async Task SessionChanged_SessionAppearsBeforeItsAudio_RetriesUntilTheMixerTakesFullLevel()
    {
        Build();
        _controller.CanSetLevel = false;
        var retries = 0;
        _onDelay = span =>
        {
            if (span == YouTubeMusicBreakMusicProvider.LevelRetry && ++retries == 2)
                _controller.CanSetLevel = true;
        };

        _controller.Snapshot = Playing;
        _controller.RaiseSessionChanged();
        await UntilAsync(() => _controller.Calls.Count == 3);

        Assert.Equal(["level:1", "level:1", "level:1"], _controller.Calls);
    }

    [Fact]
    public async Task SessionChanged_MixerNeverTakesIt_GivesUpAfterTheRetries()
    {
        Build();
        _controller.CanSetLevel = false;

        _controller.Snapshot = Playing;
        _controller.RaiseSessionChanged();
        await UntilAsync(() => _controller.Calls.Count >= YouTubeMusicBreakMusicProvider.LevelRetryAttempts);
        await Task.Delay(50);

        Assert.Equal(YouTubeMusicBreakMusicProvider.LevelRetryAttempts, _controller.Calls.Count);
    }

    // A jump to full in the middle of a fade-out would put a burst of the bed under the singer.
    [Fact]
    public async Task SessionChanged_SessionAppearsDuringAFadeOut_LeavesTheFadeAlone()
    {
        _controller.Snapshot = Playing;
        _controller.OnCommand = command => command == "pause" ? Paused : null;
        var release = new TaskCompletionSource();
        var seen = 0;
        _holdDelay = (span, _) => span == FadeCurve.StepInterval && ++seen == 2 ? release.Task : null;
        var provider = Build(ShortFade);

        var pausing = provider.PauseAsync();
        await UntilAsync(() => _controller.Calls.Count == 2);
        _controller.RaiseSessionChanged();
        release.SetResult();
        await pausing;

        Assert.Equal(["level:1", "level:0.1", "level:0.01", "level:0", "pause", "level:1"], _controller.Calls);
    }

    [Fact]
    public async Task SessionChanged_SessionAppearsDuringTheFadeIn_LeavesTheRiseAlone()
    {
        _controller.Snapshot = Paused;
        _controller.OnCommand = command => command == "play" ? Playing : null;
        var release = new TaskCompletionSource();
        var seen = 0;
        _holdDelay = (span, _) => span == FadeCurve.StepInterval && ++seen == 2 ? release.Task : null;
        var provider = Build(ShortFade);

        await provider.ResumeAsync();
        _controller.RaiseSessionChanged();
        release.SetResult();
        await UntilAsync(() => _controller.Calls.Count == 5);

        Assert.Equal(["level:0", "play", "level:0.01", "level:0.1", "level:1"], _controller.Calls);
    }

    [Fact]
    public async Task StartAsync_MixerNotReadyYet_RetriesFullLevelOnceTheStartIsDone()
    {
        _controller.Snapshot = Playing;
        _controller.CanSetLevel = false;
        var retry = new TaskCompletionSource();
        _holdDelay = (span, _) => span == YouTubeMusicBreakMusicProvider.LevelRetry ? retry.Task : null;
        var provider = Build();

        Assert.True(await provider.StartAsync());
        _controller.CanSetLevel = true;
        retry.SetResult();
        await UntilAsync(() => _controller.Calls.Count == 2);

        Assert.Equal(["level:1", "level:1"], _controller.Calls);
    }

    [Fact]
    public async Task StopAsync_NoFadeAsked_PausesAtOnce()
    {
        _controller.Snapshot = Playing;

        await Build().StopAsync();

        Assert.Equal(["pause"], _controller.Calls);
    }

    [Fact]
    public async Task StopAsync_FadeSetToZero_PausesAtOnce()
    {
        _controller.Snapshot = Playing;

        await Build(new YouTubeMusicSettings { FadeMilliseconds = 0 }).StopAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["pause"], _controller.Calls);
    }

    // With no audio session there is nothing to ride; fading would only hold the singer's start.
    [Fact]
    public async Task StopAsync_MixerUnreachable_PausesWithoutWaitingOutAFade()
    {
        _controller.Snapshot = Playing;
        _controller.CanSetLevel = false;
        var started = _clock.Now;

        await Build().StopAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["level:1", "pause"], _controller.Calls);
        Assert.Equal(started, _clock.Now);
    }

    [Fact]
    public async Task StopAsync_NotPlaying_PausesWithoutAFade()
    {
        _controller.Snapshot = Paused;

        await Build().StopAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["pause"], _controller.Calls);
    }

    // A cancelled suspend must still leave the bed stopped, not half-faded under a singer.
    [Fact]
    public async Task StopAsync_CancelledMidFade_StillPausesAndRestoresTheLevel()
    {
        _controller.Snapshot = Playing;
        _controller.OnCommand = command => command == "pause" ? Paused : null;
        using var cancel = new CancellationTokenSource();
        _onDelay = _ => cancel.Cancel();

        var provider = Build();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.StopAsync(TimeSpan.FromSeconds(2), cancel.Token));

        Assert.Equal("pause", _controller.Calls[^2]);
        Assert.Equal("level:1", _controller.Calls[^1]);
    }

    [Fact]
    public async Task PauseAsync_PlayingWithAFade_StepsDownThenPausesThenRestoresFullLevel()
    {
        _controller.Snapshot = Playing;
        _controller.OnCommand = command => command == "pause" ? Paused : null;

        await Build(ShortFade).PauseAsync();

        Assert.Equal(["level:1", "level:0.1", "level:0.01", "level:0", "pause", "level:1"], _controller.Calls);
    }

    [Fact]
    public async Task PauseAsync_FadeSetToZero_PausesAtOnce()
    {
        _controller.Snapshot = Playing;

        await Build(NoFade).PauseAsync();

        Assert.Equal(["pause"], _controller.Calls);
    }

    [Fact]
    public async Task PauseAsync_NotPlaying_PausesWithoutAFade()
    {
        _controller.Snapshot = Paused;

        await Build(ShortFade).PauseAsync();

        Assert.Equal(["pause"], _controller.Calls);
    }

    [Fact]
    public async Task PauseAsync_CancelledMidFade_StillPausesAndRestoresTheLevel()
    {
        _controller.Snapshot = Playing;
        _controller.OnCommand = command => command == "pause" ? Paused : null;
        using var cancel = new CancellationTokenSource();
        _onDelay = _ => cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Build(ShortFade).PauseAsync(cancel.Token));

        Assert.Equal(["level:1", "pause", "level:1"], _controller.Calls);
    }

    // Silent before play, so the room's first sound is the bottom of the rise, not a burst at full.
    [Fact]
    public async Task ResumeAsync_WithAFade_SilencesThenPlaysThenStepsUpToFullLevel()
    {
        _controller.Snapshot = Paused;

        await Build(ShortFade).ResumeAsync();
        await UntilAsync(() => _controller.Calls.Count == 5);

        Assert.Equal(["level:0", "play", "level:0.01", "level:0.1", "level:1"], _controller.Calls);
    }

    [Fact]
    public async Task ResumeAsync_FadeSetToZero_PlaysAtTheLevel()
    {
        _controller.Snapshot = Paused;

        await Build(NoFade).ResumeAsync();

        Assert.Equal(["play", "level:1"], _controller.Calls);
    }

    [Fact]
    public async Task StartAsync_SessionPausedWithAFade_RisesFromSilence()
    {
        _controller.Snapshot = Paused;

        Assert.True(await Build(ShortFade).StartAsync());
        Assert.Equal(["level:0", "play", "level:0.01", "level:0.1", "level:1"], _controller.Calls);
    }

    // A fresh launch sounds on its own; the rise starts from the moment it is heard.
    [Fact]
    public async Task StartAsync_LaunchedAppAlreadySounding_CutsToSilenceAndRises()
    {
        _controller.Snapshot = SessionSnapshot.None;
        _controller.OnCommand = command => command.StartsWith("launch:", StringComparison.Ordinal) ? Playing : null;

        Assert.True(await Build(ShortFade).StartAsync());
        Assert.Equal(["launch:", "level:0", "level:0.01", "level:0.1", "level:1"], _controller.Calls);
    }

    // Left at silence, the next attempt would play and nobody would hear it.
    [Fact]
    public async Task StartAsync_PlayRefusedWithAFade_RestoresTheLevelBeforeThrowing()
    {
        _controller.Snapshot = Paused;
        _controller.CommandsSucceed = false;

        await Assert.ThrowsAsync<KHostException>(() => Build(ShortFade).StartAsync());

        Assert.Equal(["level:0", "play", "level:1"], _controller.Calls);
    }

    [Fact]
    public async Task ResumeAsync_PlayRefusedWithAFade_RestoresTheLevelBeforeThrowing()
    {
        _controller.Snapshot = Paused;
        _controller.CommandsSucceed = false;

        await Assert.ThrowsAsync<KHostException>(() => Build(ShortFade).ResumeAsync());

        Assert.Equal(["level:0", "play", "level:1"], _controller.Calls);
    }

    // The rise runs on after resume returns; a pause pressed during it must win, from where it got.
    [Fact]
    public async Task PauseAsync_DuringTheFadeIn_CutsItShortAndFadesFromWhereItGot()
    {
        _controller.Snapshot = Paused;
        _controller.OnCommand = command => command switch { "play" => Playing, "pause" => Paused, _ => null };
        var provider = Build(ShortFade);
        HoldFadeStep(2);

        await provider.ResumeAsync();
        await provider.PauseAsync();

        Assert.Equal(
            ["level:0", "play", "level:0.01", "level:0.01", "level:0.001", "level:0", "level:0", "pause", "level:1"],
            _controller.Calls);
    }

    [Fact]
    public async Task ResumeAsync_DuringTheFadeOut_LetsThePauseLandThenRisesFromSilence()
    {
        _controller.Snapshot = Playing;
        _controller.OnCommand = command => command switch { "play" => Playing, "pause" => Paused, _ => null };
        var provider = Build(ShortFade);
        HoldFadeStep(2);

        var pausing = provider.PauseAsync();
        await UntilAsync(() => _controller.Calls.Count == 2);
        await provider.ResumeAsync();
        await pausing;

        Assert.Equal(
            ["level:1", "level:0.1", "pause", "level:1", "level:0", "play", "level:0.01", "level:0.1", "level:1"],
            _controller.Calls);
    }

    // The level the first read of the session pushes follows the commands; it is not one of them.
    [Fact]
    public async Task SkipAsync_FromPause_SkipsThenPlays()
    {
        _controller.Snapshot = Paused;
        _controller.OnCommand = command => command == "skip" ? new SessionSnapshot(SessionPlayback.Playing, "Temptation", "New Order") : null;

        var provider = Build();
        await provider.SkipAsync();

        Assert.Equal(["skip", "play", "level:1"], _controller.Calls);
        Assert.Equal("Temptation", provider.CurrentTrack!.Title);
    }

    [Fact]
    public async Task SkipAsync_WhilePlaying_OnlySkips()
    {
        _controller.Snapshot = Playing;
        _controller.OnCommand = command => command == "skip" ? new SessionSnapshot(SessionPlayback.Playing, "Temptation", "New Order") : null;

        await Build().SkipAsync();

        Assert.Equal(["skip", "level:1"], _controller.Calls);
    }

    [Fact]
    public async Task SessionChanged_NewTrack_AnnouncesItUnderThisSource()
    {
        var provider = Build();

        _controller.Snapshot = Playing;
        _controller.RaiseSessionChanged();

        _broker.Received(1).Announce(Arg.Is<BreakMusicTrackChanged>(m => m.ProviderSourceName == provider.SourceName));
        Assert.Equal("Blue Monday", provider.CurrentTrack!.Title);
        Assert.Equal(BreakMusicPlayback.Playing, await provider.ReadPlaybackAsync());
    }

    [Fact]
    public void SessionChanged_NothingMoved_AnnouncesNothing()
    {
        Build();

        _controller.Snapshot = Playing;
        _controller.RaiseSessionChanged();
        _controller.RaiseSessionChanged();

        _broker.Received(1).Announce(Arg.Any<BreakMusicTrackChanged>());
    }

    [Fact]
    public void SessionChanged_Advert_NamesNoTrack()
    {
        var provider = Build();

        _controller.Snapshot = new SessionSnapshot(SessionPlayback.Playing, "Video Ad", "YouTube Ads 310");
        _controller.RaiseSessionChanged();

        Assert.Null(provider.CurrentTrack);
    }

    // "Are you still there?" pauses the bed after a long stretch with nobody touching the page.
    [Fact]
    public void SessionChanged_PausedByItself_PressesPlayAfterTheGrace()
    {
        Build();
        _controller.Snapshot = Playing;
        _controller.RaiseSessionChanged();

        _controller.Snapshot = Paused;
        _controller.RaiseSessionChanged();

        Assert.Equal(["level:1", "play"], _controller.Calls);
    }

    [Fact]
    public void SessionChanged_PausedByItselfWithRecoveryOff_LeavesItPaused()
    {
        Build(new YouTubeMusicSettings { RecoverUnexpectedPause = false });
        _controller.Snapshot = Playing;
        _controller.RaiseSessionChanged();

        _controller.Snapshot = Paused;
        _controller.RaiseSessionChanged();

        Assert.Equal(["level:1"], _controller.Calls);
    }

    [Fact]
    public async Task SessionChanged_PausedByTheHost_IsNotRecovered()
    {
        var provider = Build(NoFade);
        _controller.Snapshot = Playing;
        _controller.RaiseSessionChanged();

        await provider.PauseAsync();
        _controller.Snapshot = Paused;
        _controller.RaiseSessionChanged();

        Assert.Equal(["level:1", "pause"], _controller.Calls);
    }

    // The host stopping the bed for a singer during the grace has the last word over the recovery.
    [Fact]
    public void SessionChanged_HostStopsDuringTheGrace_IsNotRecovered()
    {
        var provider = Build();
        _controller.Snapshot = Playing;
        _controller.RaiseSessionChanged();

        _onDelay = span =>
        {
            if (span == YouTubeMusicBreakMusicProvider.RecoveryGrace)
                provider.PauseAsync().GetAwaiter().GetResult();
        };

        _controller.Snapshot = Paused;
        _controller.RaiseSessionChanged();

        Assert.Equal(["level:1", "pause"], _controller.Calls);
    }

    [Fact]
    public void DescribeButton_Unsupported_DisablesSetupAndHidesOpen()
    {
        _controller.Unavailable = "Windows only";
        _controller.Status = SetupStatus.Unsupported;

        var provider = Build();

        Assert.False(provider.DescribeButton(YouTubeMusicBreakMusicProvider.SetupButton).Enabled);
        Assert.False(provider.DescribeButton(YouTubeMusicBreakMusicProvider.OpenButton).Visible);
    }

    [Fact]
    public void DescribeButton_AppNotInstalled_OffersSetupAndHidesOpen()
    {
        _controller.Status = SetupStatus.AppNotInstalled;

        var provider = Build();

        Assert.Equal(PluginButtonState.Default, provider.DescribeButton(YouTubeMusicBreakMusicProvider.SetupButton));
        Assert.False(provider.DescribeButton(YouTubeMusicBreakMusicProvider.OpenButton).Visible);
        _context.Received(1).AddWarning(Arg.Is<string>(m => m.Contains("Set up YouTube Music")));
    }

    [Fact]
    public async Task InvokeButtonAsync_SetupWhenReady_SaysNothing()
    {
        await Build().InvokeButtonAsync(YouTubeMusicBreakMusicProvider.SetupButton);

        Assert.Equal(["setup"], _controller.Calls);
        _flash.DidNotReceive().Show(Arg.Any<string>(), Arg.Any<FlashType>());
    }

    [Fact]
    public void DescribeButton_Ready_ShowsOpen()
        => Assert.True(Build().DescribeButton(YouTubeMusicBreakMusicProvider.OpenButton).Visible);

    [Fact]
    public void DescribeButton_Unsupported_SaysWhereItRuns()
    {
        _controller.Unavailable = "Linux";
        _controller.Status = SetupStatus.Unsupported;

        Assert.Equal("YouTube Music: Windows and macOS only", Build().DescribeButton(YouTubeMusicBreakMusicProvider.SetupButton).Label);
    }

    [Fact]
    public void DescribeButton_BrowserMissing_NamesThatBrowser()
    {
        _controller.Status = SetupStatus.BrowserNotFound;

        Assert.Equal("Microsoft Edge not found", Build().DescribeButton(YouTubeMusicBreakMusicProvider.SetupButton).Label);
    }

    [Fact]
    public void Constructor_NotSetUp_WarnsWithTheBackendsOwnInstructions()
    {
        _controller.Status = SetupStatus.AppNotInstalled;
        _controller.NotSetUpWarning = "Press setup; Edge opens.";

        Build();

        _context.Received(1).AddWarning("Press setup; Edge opens.");
    }

    [Fact]
    public async Task StartAsync_BrowserMissing_NamesThatBrowser()
    {
        _controller.Unavailable = "Microsoft Edge was not found";
        _controller.Status = SetupStatus.BrowserNotFound;

        var ex = await Assert.ThrowsAsync<KHostException>(() => Build().StartAsync());

        Assert.Equal("YouTube Music: couldn't start break music — Microsoft Edge isn't installed on this machine.", ex.WhatHappened);
    }

    [Fact]
    public async Task StartAsync_NotSetUp_NamesTheBrowsersProfile()
    {
        _controller.Status = SetupStatus.AppNotInstalled;

        var ex = await Assert.ThrowsAsync<KHostException>(() => Build().StartAsync());

        Assert.Equal(
            "YouTube Music: not set up in this plugin's Edge profile. Press \"Set up YouTube Music\" on the Plugins page.",
            ex.WhatHappened);
    }

    [Fact]
    public void DescribeButton_EdgeMissing_DisablesSetup()
    {
        _controller.Status = SetupStatus.BrowserNotFound;

        Assert.False(Build().DescribeButton(YouTubeMusicBreakMusicProvider.SetupButton).Enabled);
    }

    // The host installs the app in Edge after pressing setup; the row has to notice on its next draw.
    [Fact]
    public async Task InvokeButtonAsync_Setup_OpensSetupAndRereadsTheStatus()
    {
        _controller.Status = SetupStatus.AppNotInstalled;
        var provider = Build();

        await provider.InvokeButtonAsync(YouTubeMusicBreakMusicProvider.SetupButton);
        _controller.Status = SetupStatus.Ready;
        await provider.InvokeButtonAsync(YouTubeMusicBreakMusicProvider.OpenButton);

        Assert.Equal(["setup", "show"], _controller.Calls);
        Assert.True(provider.DescribeButton(YouTubeMusicBreakMusicProvider.OpenButton).Visible);
    }

    // Installing the app from Edge's own address bar raises nothing the plugin can hear.
    [Fact]
    public void SetupStatusPoll_AppInstalledOutOfBand_RedrawsThePluginsRow()
    {
        _controller.Status = SetupStatus.AppNotInstalled;
        var provider = Build();
        _broker.ClearReceivedCalls();

        _controller.Status = SetupStatus.Ready;
        _clock.Advance(YouTubeMusicBreakMusicProvider.SetupStatusPoll);

        _broker.Received(1).Announce(Arg.Any<PluginsChanged>());
        Assert.True(provider.DescribeButton(YouTubeMusicBreakMusicProvider.OpenButton).Visible);
    }

    // A row drawn just before the poll leaves a fresh cached read; the poll must look past it.
    [Fact]
    public async Task SetupStatusPoll_RowReadJustBefore_StillSeesTheChange()
    {
        _controller.Status = SetupStatus.AppNotInstalled;
        var provider = Build();
        _broker.ClearReceivedCalls();
        _clock.Advance(TimeSpan.FromSeconds(3));
        await provider.InvokeButtonAsync(YouTubeMusicBreakMusicProvider.SetupButton);
        provider.DescribeButton(YouTubeMusicBreakMusicProvider.SetupButton);

        _controller.Status = SetupStatus.Ready;
        _clock.Advance(YouTubeMusicBreakMusicProvider.SetupStatusPoll - TimeSpan.FromSeconds(3));

        _broker.Received(1).Announce(Arg.Any<PluginsChanged>());
    }

    [Fact]
    public void SetupStatusPoll_BeforeItsInterval_HasNotLookedYet()
    {
        _controller.Status = SetupStatus.AppNotInstalled;
        Build();
        _broker.ClearReceivedCalls();

        _controller.Status = SetupStatus.Ready;
        _clock.Advance(YouTubeMusicBreakMusicProvider.SetupStatusPoll - TimeSpan.FromMilliseconds(1));

        _broker.DidNotReceive().Announce(Arg.Any<PluginsChanged>());
    }

    [Fact]
    public void SetupStatusPoll_NothingMoved_AnnouncesNothing()
    {
        Build();

        _clock.Advance(YouTubeMusicBreakMusicProvider.SetupStatusPoll);
        _clock.Advance(YouTubeMusicBreakMusicProvider.SetupStatusPoll);

        _broker.DidNotReceive().Announce(Arg.Any<PluginsChanged>());
    }

    [Fact]
    public void SetupStatusPoll_KeepsLooking_AfterTheFirstChange()
    {
        _controller.Status = SetupStatus.AppNotInstalled;
        Build();
        _broker.ClearReceivedCalls();

        _controller.Status = SetupStatus.Ready;
        _clock.Advance(YouTubeMusicBreakMusicProvider.SetupStatusPoll);
        _controller.Status = SetupStatus.AppNotInstalled;
        _clock.Advance(YouTubeMusicBreakMusicProvider.SetupStatusPoll);

        _broker.Received(2).Announce(Arg.Any<PluginsChanged>());
    }

    // The warning was decided once at construction; an app removed later went unreported.
    [Fact]
    public void SetupStatusPoll_AppRemovedLater_WarnsThen()
    {
        _controller.NotSetUpWarning = "Press setup; Edge opens.";
        Build();
        _context.DidNotReceive().AddWarning("Press setup; Edge opens.");

        _controller.Status = SetupStatus.AppNotInstalled;
        _clock.Advance(YouTubeMusicBreakMusicProvider.SetupStatusPoll);

        _context.Received(1).AddWarning("Press setup; Edge opens.");
    }

    [Fact]
    public void SetupStatusPoll_AppInstalled_TakesTheWarningBackAndDoesNotWarnAgain()
    {
        _controller.Status = SetupStatus.AppNotInstalled;
        _controller.NotSetUpWarning = "Press setup; Edge opens.";
        _context.AddWarning("Press setup; Edge opens.").Returns(7);
        Build();

        _controller.Status = SetupStatus.Ready;
        _clock.Advance(YouTubeMusicBreakMusicProvider.SetupStatusPoll);
        _clock.Advance(YouTubeMusicBreakMusicProvider.SetupStatusPoll);

        _context.Received(1).AddWarning("Press setup; Edge opens.");
        _context.Received(1).ClearWarning(7);
    }

    [Fact]
    public void SetupStatusPoll_ControllerThrows_KeepsPolling()
    {
        _controller.Status = SetupStatus.AppNotInstalled;
        Build();
        _broker.ClearReceivedCalls();

        _controller.StatusThrows = true;
        _clock.Advance(YouTubeMusicBreakMusicProvider.SetupStatusPoll);
        _controller.StatusThrows = false;
        _controller.Status = SetupStatus.Ready;
        _clock.Advance(YouTubeMusicBreakMusicProvider.SetupStatusPoll);

        _broker.Received(1).Announce(Arg.Any<PluginsChanged>());
    }

    [Fact]
    public void Dispose_StopsThePoll()
    {
        _controller.Status = SetupStatus.AppNotInstalled;
        var provider = Build();
        _broker.ClearReceivedCalls();

        provider.Dispose();
        _controller.Status = SetupStatus.Ready;
        _clock.Advance(YouTubeMusicBreakMusicProvider.SetupStatusPoll);

        _broker.DidNotReceive().Announce(Arg.Any<PluginsChanged>());
    }

    [Fact]
    public async Task InvokeButtonAsync_UnknownKey_DoesNothing()
    {
        await Build().InvokeButtonAsync("nope");

        Assert.Empty(_controller.Calls);
    }

    [Fact]
    public async Task InvokeButtonAsync_SetupFails_Flashes()
    {
        _controller.CommandsSucceed = false;

        await Build().InvokeButtonAsync(YouTubeMusicBreakMusicProvider.SetupButton);

        _flash.Received(1).Show("YouTube Music: couldn't open Microsoft Edge for setup.", FlashType.Warning);
    }

    [Fact]
    public async Task InvokeButtonAsync_OpenFails_Flashes()
    {
        _controller.CommandsSucceed = false;

        await Build().InvokeButtonAsync(YouTubeMusicBreakMusicProvider.OpenButton);

        _flash.Received(1).Show("YouTube Music: couldn't open Microsoft Edge to launch the app.", FlashType.Warning);
    }

    [Fact]
    public async Task InvokeButtonAsync_Succeeds_DoesNotFlash()
    {
        await Build().InvokeButtonAsync(YouTubeMusicBreakMusicProvider.OpenButton);

        _flash.DidNotReceive().Show(Arg.Any<string>(), Arg.Any<FlashType>());
    }

    [Fact]
    public async Task StartAsync_Unsupported_ThrowsKHostException()
    {
        _controller.Unavailable = "Windows only";
        _controller.Status = SetupStatus.Unsupported;

        var ex = await Assert.ThrowsAsync<KHostException>(() => Build().StartAsync());

        Assert.Equal("YouTube Music: break music only runs on Windows and macOS.", ex.WhatHappened);
        Assert.Equal("KH-YTMUSIC-UNSUPPORTED", ex.ReferenceCode);
    }

    [Fact]
    public async Task StartAsync_EdgeNotFound_ThrowsKHostException()
    {
        _controller.Unavailable = "Microsoft Edge was not found on this machine, and YouTube Music break music plays through it.";
        _controller.Status = SetupStatus.BrowserNotFound;

        var ex = await Assert.ThrowsAsync<KHostException>(() => Build().StartAsync());

        Assert.Equal(
            "YouTube Music: couldn't start break music — Microsoft Edge isn't installed on this machine.",
            ex.WhatHappened);
        Assert.Equal("KH-YTMUSIC-NO-BROWSER", ex.ReferenceCode);
    }

    [Fact]
    public async Task StartAsync_CannotSeeTheSession_ThrowsKHostException()
    {
        _controller.Snapshot = null;

        var ex = await Assert.ThrowsAsync<KHostException>(() => Build().StartAsync());

        Assert.Equal(
            "YouTube Music: couldn't check what's playing, so break music wasn't started.",
            ex.WhatHappened);
        Assert.Equal("KH-YTMUSIC-READ-FAILED", ex.ReferenceCode);
    }

    [Fact]
    public async Task StartAsync_NotRunningAndNotAllowedToLaunch_ThrowsKHostException()
    {
        _controller.Snapshot = SessionSnapshot.None;

        var ex = await Assert.ThrowsAsync<KHostException>(
            () => Build(new YouTubeMusicSettings { LaunchIfNotRunning = false }).StartAsync());

        Assert.Contains("set not to launch", ex.WhatHappened);
        Assert.Equal("KH-YTMUSIC-NOT-RUNNING", ex.ReferenceCode);
    }

    [Fact]
    public async Task StartAsync_AppNotInstalled_ThrowsKHostException()
    {
        _controller.Snapshot = SessionSnapshot.None;
        _controller.Status = SetupStatus.AppNotInstalled;

        var ex = await Assert.ThrowsAsync<KHostException>(() => Build().StartAsync());

        Assert.Contains("not set up", ex.WhatHappened);
        Assert.Equal("KH-YTMUSIC-NOT-SET-UP", ex.ReferenceCode);
    }

    [Fact]
    public async Task StartAsync_LaunchFails_ThrowsKHostException()
    {
        _controller.Snapshot = SessionSnapshot.None;
        _controller.CommandsSucceed = false;

        var ex = await Assert.ThrowsAsync<KHostException>(() => Build().StartAsync());

        Assert.Equal("YouTube Music: couldn't open Microsoft Edge to start break music.", ex.WhatHappened);
        Assert.Equal("KH-YTMUSIC-LAUNCH-FAILED", ex.ReferenceCode);
    }

    [Fact]
    public async Task StartAsync_LaunchedAppNeverMakesASession_ThrowsKHostException()
    {
        _controller.Snapshot = SessionSnapshot.None;

        var ex = await Assert.ThrowsAsync<KHostException>(() => Build().StartAsync());

        Assert.Contains("nothing started playing", ex.WhatHappened);
        Assert.Equal("KH-YTMUSIC-SESSION-TIMEOUT", ex.ReferenceCode);
    }

    [Fact]
    public async Task StartAsync_PlayRefused_ThrowsKHostException()
    {
        _controller.Snapshot = Paused;
        _controller.CommandsSucceed = false;

        var ex = await Assert.ThrowsAsync<KHostException>(() => Build().StartAsync());

        Assert.Equal("YouTube Music: refused to play.", ex.WhatHappened);
        Assert.Equal("KH-YTMUSIC-PLAY-REFUSED", ex.ReferenceCode);
    }

    [Fact]
    public async Task StartAsync_StartFailureCauses_DoNotFlash()
    {
        _controller.Snapshot = Paused;
        _controller.CommandsSucceed = false;

        await Assert.ThrowsAsync<KHostException>(() => Build().StartAsync());

        _flash.DidNotReceive().Show(Arg.Any<string>(), Arg.Any<FlashType>());
    }

    // A host pressing play twice while the same cause persists should hear the reason twice — it is
    // a reply to their own action, not a one-time notice.
    [Fact]
    public async Task StartAsync_RepeatedFailureSameCause_EachCallThrows()
    {
        _controller.Snapshot = SessionSnapshot.None;

        var provider = Build(new YouTubeMusicSettings { LaunchIfNotRunning = false });

        await Assert.ThrowsAsync<KHostException>(() => provider.StartAsync());
        await Assert.ThrowsAsync<KHostException>(() => provider.StartAsync());
        await Assert.ThrowsAsync<KHostException>(() => provider.StartAsync());
    }

    [Fact]
    public async Task ResumeAsync_PlayRefused_ThrowsKHostException()
    {
        _controller.Snapshot = Paused;
        _controller.CommandsSucceed = false;

        var ex = await Assert.ThrowsAsync<KHostException>(() => Build().ResumeAsync());

        Assert.Equal("YouTube Music: refused to play.", ex.WhatHappened);
        Assert.Equal("KH-YTMUSIC-PLAY-REFUSED", ex.ReferenceCode);
    }

    [Fact]
    public async Task StartAsync_AlreadyPlaying_DoesNotFlash()
    {
        _controller.Snapshot = Playing;

        Assert.True(await Build().StartAsync());

        _flash.DidNotReceive().Show(Arg.Any<string>(), Arg.Any<FlashType>());
    }

    // "Are you still there?" recovery is a background retry, never a host-caused failure, so it
    // stays log-only whether it succeeds or not.
    [Fact]
    public void SessionChanged_PausedByItself_NeverFlashes()
    {
        Build();
        _controller.Snapshot = Playing;
        _controller.RaiseSessionChanged();

        _controller.Snapshot = Paused;
        _controller.RaiseSessionChanged();

        _flash.DidNotReceive().Show(Arg.Any<string>(), Arg.Any<FlashType>());
    }

    [Fact]
    public void SessionChanged_PausedByItselfAndRecoveryFails_StillDoesNotFlash()
    {
        Build();
        _controller.Snapshot = Playing;
        _controller.RaiseSessionChanged();

        _controller.CommandsSucceed = false;
        _controller.Snapshot = Paused;
        _controller.RaiseSessionChanged();

        _flash.DidNotReceive().Show(Arg.Any<string>(), Arg.Any<FlashType>());
    }

    private static readonly SessionSnapshot SignedIn = Playing with { SignedIn = true };
    private static readonly SessionSnapshot SignedOut = Playing with { SignedIn = false };

    [Fact]
    public void DescribeButton_HelperMissing_DisablesSetupAndHidesOpen()
    {
        _controller.Unavailable = "no app in this build";
        _controller.Status = SetupStatus.HelperMissing;

        var provider = Build();
        var setup = provider.DescribeButton(YouTubeMusicBreakMusicProvider.SetupButton);

        Assert.False(setup.Enabled);
        Assert.Equal("YouTube Music app missing from this plugin build", setup.Label);
        Assert.False(provider.DescribeButton(YouTubeMusicBreakMusicProvider.OpenButton).Visible);
    }

    [Fact]
    public async Task StartAsync_HelperMissing_ThrowsNoHelperWithoutTouchingTheApp()
    {
        _controller.Unavailable = "no app in this build";
        _controller.Status = SetupStatus.HelperMissing;

        var ex = await Assert.ThrowsAsync<KHostException>(() => Build().StartAsync());

        Assert.Equal("KH-YTMUSIC-NO-HELPER", ex.ReferenceCode);
        Assert.Empty(_controller.Calls);
    }

    [Fact]
    public void DescribeButton_NotSignedIn_OffersTheSignInAndShowsOpen()
    {
        _controller.Status = SetupStatus.NotSignedIn;

        var provider = Build();

        Assert.Equal("Sign in to YouTube Music", provider.DescribeButton(YouTubeMusicBreakMusicProvider.SetupButton).Label);
        Assert.True(provider.DescribeButton(YouTubeMusicBreakMusicProvider.OpenButton).Visible);
    }

    // Signed out still plays (with adverts): nothing may stop the show over a sign-in.
    [Fact]
    public async Task StartAsync_NotSignedIn_LaunchesAndPlays()
    {
        _controller.Snapshot = SessionSnapshot.None;
        _controller.Status = SetupStatus.NotSignedIn;
        _controller.OnCommand = command => command.StartsWith("launch:", StringComparison.Ordinal) ? Playing : null;

        Assert.True(await Build().StartAsync());
        Assert.Equal("launch:", _controller.Calls[0]);
    }

    [Fact]
    public void Constructor_NotSignedIn_WarnsWithTheBackendsOwnInstructions()
    {
        _controller.Status = SetupStatus.NotSignedIn;
        _controller.NotSetUpWarning = "Sign in, please.";

        Build();

        _context.Received(1).AddWarning("Sign in, please.");
    }

    [Fact]
    public void SetupStatusPoll_SignedInLater_ClearsTheNotSignedInWarningByItsId()
    {
        _controller.Status = SetupStatus.NotSignedIn;
        _controller.NotSetUpWarning = "Sign in, please.";
        _context.AddWarning("Sign in, please.").Returns(7);
        Build();
        _clock.Advance(YouTubeMusicBreakMusicProvider.SetupStatusPoll);
        _context.DidNotReceive().ClearWarning(Arg.Any<int>());

        _controller.Status = SetupStatus.Ready;
        _clock.Advance(YouTubeMusicBreakMusicProvider.SetupStatusPoll);

        _context.Received(1).ClearWarning(7);
    }

    // Installed but not yet signed in is still unfinished: the one warning stays, and stays clearable.
    [Fact]
    public void SetupStatusPoll_FromNotInstalledToNotSignedIn_KeepsTheOneWarning()
    {
        _controller.Status = SetupStatus.AppNotInstalled;
        _controller.NotSetUpWarning = "Sign in, please.";
        _context.AddWarning("Sign in, please.").Returns(7, 8);
        Build();

        _controller.Status = SetupStatus.NotSignedIn;
        _clock.Advance(YouTubeMusicBreakMusicProvider.SetupStatusPoll);
        _controller.Status = SetupStatus.Ready;
        _clock.Advance(YouTubeMusicBreakMusicProvider.SetupStatusPoll);

        _context.Received(1).AddWarning("Sign in, please.");
        _context.Received(1).ClearWarning(7);
    }

    // Signed out again later: the warning comes back, and a later sign-in clears that one.
    [Fact]
    public void SetupStatusPoll_SignedOutAfterASignIn_WarnsAgainUnderANewId()
    {
        _controller.Status = SetupStatus.NotSignedIn;
        _controller.NotSetUpWarning = "Sign in, please.";
        _context.AddWarning("Sign in, please.").Returns(7, 8);
        Build();

        _controller.Status = SetupStatus.Ready;
        _clock.Advance(YouTubeMusicBreakMusicProvider.SetupStatusPoll);
        _controller.Status = SetupStatus.NotSignedIn;
        _clock.Advance(YouTubeMusicBreakMusicProvider.SetupStatusPoll);
        _controller.Status = SetupStatus.Ready;
        _clock.Advance(YouTubeMusicBreakMusicProvider.SetupStatusPoll);

        _context.Received(2).AddWarning("Sign in, please.");
        Received.InOrder(() =>
        {
            _context.ClearWarning(7);
            _context.ClearWarning(8);
        });
    }

    // Signed in at start: nothing was shown, so there is nothing to clear.
    [Fact]
    public void SessionChanged_SignedInWithNoWarningShown_ClearsNothing()
    {
        _controller.Snapshot = SignedIn;
        var provider = Build();

        _controller.RaiseSessionChanged();

        _context.DidNotReceive().ClearWarning(Arg.Any<int>());
    }

    [Fact]
    public async Task InvokeButtonAsync_Open_ShowsTheAppRatherThanLaunchingItBehind()
    {
        await Build().InvokeButtonAsync(YouTubeMusicBreakMusicProvider.OpenButton);

        Assert.Equal(["show"], _controller.Calls);
    }

    [Fact]
    public async Task SessionChanged_SignedOutWhileTheVenueWantsMusic_FlashesOnceAndPlaysOn()
    {
        _controller.Snapshot = SignedIn;
        var provider = Build();
        await provider.StartAsync();
        _controller.Calls.Clear();

        _controller.Snapshot = SignedOut;
        _controller.RaiseSessionChanged();
        _controller.RaiseSessionChanged();

        _flash.Received(1).Show(YouTubeMusicBreakMusicProvider.SignedOutMessage, FlashType.Warning);
        Assert.DoesNotContain("pause", _controller.Calls);
    }

    [Fact]
    public async Task SessionChanged_SignedOutAfterAStop_SaysNothingUntilTheNextStart()
    {
        _controller.Snapshot = SignedIn;
        var provider = Build();
        await provider.StartAsync();
        await provider.StopAsync();

        _controller.Snapshot = SignedOut with { Playback = SessionPlayback.Paused };
        _controller.RaiseSessionChanged();
        _flash.DidNotReceive().Show(Arg.Any<string>(), Arg.Any<FlashType>());

        await provider.StartAsync();

        _flash.Received(1).Show(YouTubeMusicBreakMusicProvider.SignedOutMessage, FlashType.Warning);
    }

    // The flash tells the host which button to press, so it has to be the one the row shows signed out.
    [Fact]
    public void SignedOutMessage_NamesTheSignedOutSetupButton()
    {
        _controller.Status = SetupStatus.NotSignedIn;

        var label = Build().DescribeButton(YouTubeMusicBreakMusicProvider.SetupButton).Label;

        Assert.Equal("Sign in to YouTube Music", label);
        Assert.Contains($"\"{label}\"", YouTubeMusicBreakMusicProvider.SignedOutMessage);
    }

    // Never signed in is a setup state the button already shows, not news.
    [Fact]
    public async Task StartAsync_NeverSignedIn_DoesNotFlash()
    {
        _controller.Snapshot = SignedOut;

        await Build().StartAsync();
        _controller.RaiseSessionChanged();

        _flash.DidNotReceive().Show(Arg.Any<string>(), Arg.Any<FlashType>());
    }

    // The row would otherwise go on saying "set up again" for the length of the status cache.
    [Fact]
    public void DescribeButton_RightAfterASignOut_ReadsTheStatusAgain()
    {
        _controller.Snapshot = SignedIn;
        var provider = Build();
        _controller.RaiseSessionChanged();
        Assert.Equal("Set up YouTube Music again", provider.DescribeButton(YouTubeMusicBreakMusicProvider.SetupButton).Label);

        _controller.Status = SetupStatus.NotSignedIn;
        _controller.Snapshot = SignedOut;
        _controller.RaiseSessionChanged();

        Assert.Equal("Sign in to YouTube Music", provider.DescribeButton(YouTubeMusicBreakMusicProvider.SetupButton).Label);
    }
}
