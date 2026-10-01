using KHost.Abstractions.Exceptions;
using KHost.Abstractions.Messaging;
using KHost.Abstractions.Messaging.Messages;
using KHost.Abstractions.Models;
using KHost.Abstractions.Services;
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
                return Task.CompletedTask;
            });
    }

    [Fact]
    public void RendersThroughHost_IsFalse_BecauseTheSoundLeavesEdgesOwnOutput()
        => Assert.False(Build().RendersThroughHost);

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

        Assert.True(await Build(new YouTubeMusicSettings { PlaylistUrl = Playlist }).StartAsync());
        Assert.Equal(["play", "level:1"], _controller.Calls);
    }

    [Fact]
    public async Task StartAsync_NoSession_LaunchesTheAppAtThePlaylistAndWaitsForIt()
    {
        _controller.Snapshot = SessionSnapshot.None;
        _controller.OnCommand = command => command.StartsWith("launch:", StringComparison.Ordinal) ? Playing : null;

        Assert.True(await Build(new YouTubeMusicSettings { PlaylistUrl = Playlist }).StartAsync());
        Assert.Equal(["launch:https://music.youtube.com/watch?list=PLbed", "level:1"], _controller.Calls);
    }

    [Fact]
    public async Task StartAsync_LaunchedAppLoadsPaused_PressesPlay()
    {
        _controller.Snapshot = SessionSnapshot.None;
        _controller.OnCommand = command => command.StartsWith("launch:", StringComparison.Ordinal) ? Paused : null;

        Assert.True(await Build().StartAsync());
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

        _context.Received(1).ReportWarning("Windows only");
        await Assert.ThrowsAsync<KHostException>(() => provider.StartAsync());
        Assert.Equal(BreakMusicPlayback.Stopped, await provider.ReadPlaybackAsync());
        Assert.Empty(_controller.Calls);
    }

    [Fact]
    public void Construct_NotAPlaylist_Warns()
    {
        Build(new YouTubeMusicSettings { PlaylistUrl = "https://example.com/" });

        _context.Received(1).ReportWarning(Arg.Is<string>(m => m.Contains("not a YouTube Music playlist link")));
    }

    // The fade is what the host waits on before the singer's song starts; it has to come down in
    // steps, stop, and leave the mixer where the venue set it for the next start.
    [Fact]
    public async Task StopAsync_WithAFade_StepsDownThenPausesThenRestoresTheLevel()
    {
        _controller.Snapshot = Playing;
        _controller.OnCommand = command => command == "pause" ? Paused : null;

        var provider = Build(new YouTubeMusicSettings { FadeMilliseconds = 300 });
        await provider.SetVolumeAsync(0.5f);
        _controller.Calls.Clear();

        await provider.StopAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["level:0.5", "level:0.05", "level:0.005", "level:0", "pause", "level:0.5"], _controller.Calls);
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
    public async Task SkipAsync_FromPause_SkipsThenPlays()
    {
        _controller.Snapshot = Paused;
        _controller.OnCommand = command => command == "skip" ? new SessionSnapshot(SessionPlayback.Playing, "Temptation", "New Order") : null;

        var provider = Build();
        await provider.SkipAsync();

        Assert.Equal(["skip", "play"], _controller.Calls);
        Assert.Equal("Temptation", provider.CurrentTrack!.Title);
    }

    [Fact]
    public async Task SkipAsync_WhilePlaying_OnlySkips()
    {
        _controller.Snapshot = Playing;
        _controller.OnCommand = command => command == "skip" ? new SessionSnapshot(SessionPlayback.Playing, "Temptation", "New Order") : null;

        await Build().SkipAsync();

        Assert.Equal(["skip"], _controller.Calls);
    }

    [Fact]
    public async Task SetVolumeAsync_OutOfRange_IsClampedOntoTheMixer()
    {
        var provider = Build();

        await provider.SetVolumeAsync(1.7f);
        await provider.SetVolumeAsync(0.25f);

        Assert.Equal(["level:1", "level:0.25"], _controller.Calls);
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

        Assert.Equal(["play"], _controller.Calls);
    }

    [Fact]
    public void SessionChanged_PausedByItselfWithRecoveryOff_LeavesItPaused()
    {
        Build(new YouTubeMusicSettings { RecoverUnexpectedPause = false });
        _controller.Snapshot = Playing;
        _controller.RaiseSessionChanged();

        _controller.Snapshot = Paused;
        _controller.RaiseSessionChanged();

        Assert.Empty(_controller.Calls);
    }

    [Fact]
    public async Task SessionChanged_PausedByTheHost_IsNotRecovered()
    {
        var provider = Build();
        _controller.Snapshot = Playing;
        _controller.RaiseSessionChanged();

        await provider.PauseAsync();
        _controller.Snapshot = Paused;
        _controller.RaiseSessionChanged();

        Assert.Equal(["pause"], _controller.Calls);
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

        Assert.Equal(["pause"], _controller.Calls);
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
        _context.Received(1).ReportWarning(Arg.Is<string>(m => m.Contains("Set up YouTube Music")));
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
    public void DescribeButton_NotPermitted_OffersToAskAgainAndHidesOpen()
    {
        _controller.BrowserName = "Google Chrome";
        _controller.Status = SetupStatus.NotPermitted;

        var provider = Build();
        var setup = provider.DescribeButton(YouTubeMusicBreakMusicProvider.SetupButton);

        Assert.True(setup.Enabled);
        Assert.Equal("Allow KHost to control Google Chrome", setup.Label);
        Assert.False(provider.DescribeButton(YouTubeMusicBreakMusicProvider.OpenButton).Visible);
    }

    [Fact]
    public void DescribeButton_BrowserMissing_NamesThatBrowser()
    {
        _controller.BrowserName = "Google Chrome";
        _controller.Status = SetupStatus.BrowserNotFound;

        Assert.Equal("Google Chrome not found", Build().DescribeButton(YouTubeMusicBreakMusicProvider.SetupButton).Label);
    }

    [Fact]
    public void Constructor_NotSetUp_WarnsWithTheBackendsOwnInstructions()
    {
        _controller.Status = SetupStatus.AppNotInstalled;
        _controller.NotSetUpWarning = "Press setup; Chrome opens.";

        Build();

        _context.Received(1).ReportWarning("Press setup; Chrome opens.");
    }

    [Fact]
    public async Task StartAsync_NotPermittedAndSessionUnreadable_ThrowsNotPermittedWithoutLaunching()
    {
        _controller.BrowserName = "Google Chrome";
        _controller.Status = SetupStatus.NotPermitted;
        _controller.Snapshot = null;

        var ex = await Assert.ThrowsAsync<KHostException>(() => Build().StartAsync());

        Assert.Equal("KH-YTMUSIC-NOT-PERMITTED", ex.ReferenceCode);
        Assert.Equal("YouTube Music: KHost isn't allowed to control Google Chrome.", ex.WhatHappened);
        Assert.Empty(_controller.Calls);
    }

    [Fact]
    public async Task StartAsync_NotPermittedWithNothingOpen_ThrowsNotPermittedWithoutLaunching()
    {
        _controller.Status = SetupStatus.NotPermitted;

        var ex = await Assert.ThrowsAsync<KHostException>(() => Build().StartAsync());

        Assert.Equal("KH-YTMUSIC-NOT-PERMITTED", ex.ReferenceCode);
        Assert.Empty(_controller.Calls);
    }

    // Permission can only be read once Chrome is up, so a refusal surfaces after the launch.
    [Fact]
    public async Task StartAsync_RefusalSeenOnlyAfterLaunch_ThrowsNotPermittedRatherThanATimeout()
    {
        _controller.OnCommand = command =>
        {
            if (command.StartsWith("launch", StringComparison.Ordinal))
                _controller.Status = SetupStatus.NotPermitted;

            return null;
        };
        _controller.Snapshot = SessionSnapshot.None;
        var provider = Build(new YouTubeMusicSettings { PlaylistUrl = Playlist });

        var ex = await Assert.ThrowsAsync<KHostException>(() => provider.StartAsync());

        Assert.Equal("KH-YTMUSIC-NOT-PERMITTED", ex.ReferenceCode);
    }

    [Fact]
    public async Task StartAsync_BrowserMissing_NamesThatBrowser()
    {
        _controller.BrowserName = "Google Chrome";
        _controller.Unavailable = "Google Chrome was not found";
        _controller.Status = SetupStatus.BrowserNotFound;

        var ex = await Assert.ThrowsAsync<KHostException>(() => Build().StartAsync());

        Assert.Equal("YouTube Music: couldn't start break music — Google Chrome isn't installed on this machine.", ex.WhatHappened);
    }

    [Fact]
    public async Task StartAsync_NotSetUp_NamesTheBrowsersProfile()
    {
        _controller.BrowserShortName = "Chrome";
        _controller.Status = SetupStatus.AppNotInstalled;

        var ex = await Assert.ThrowsAsync<KHostException>(() => Build().StartAsync());

        Assert.Equal(
            "YouTube Music: not set up in this plugin's Chrome profile. Press \"Set up YouTube Music\" on the Plugins page.",
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

        Assert.Equal(["setup", "launch:"], _controller.Calls);
        Assert.True(provider.DescribeButton(YouTubeMusicBreakMusicProvider.OpenButton).Visible);
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
}

public class YouTubeMusicControllerFactoryTests
{
    // Constructing it only looks for Chrome; nothing is launched or scripted.
    [Fact]
    public void ForCurrentPlatform_MacOS_IsTheChromeController()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var controller = YouTubeMusicControllerFactory.ForCurrentPlatform(NullLogger.Instance, "/tmp/khost-ytm-factory-test");

        Assert.IsType<KHost.Plugins.YouTubeMusic.Mac.MacYouTubeMusicController>(controller);
        Assert.Equal("Google Chrome", controller.BrowserName);
    }

    [Fact]
    public void ForCurrentPlatform_NeitherWindowsNorMacOS_IsUnavailableWithAReason()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            return;

        var controller = YouTubeMusicControllerFactory.ForCurrentPlatform(NullLogger.Instance, "/tmp/profile");

        Assert.Contains("Windows and macOS only", controller.Unavailable);
        Assert.Equal(SetupStatus.Unsupported, controller.GetSetupStatus());
    }

    [Fact]
    public void UnsupportedReason_NamesBothBackendsAndThePlatform()
        => Assert.Equal(
            "YouTube Music break music runs on Windows and macOS only for now; there is no backend for Ubuntu 24.04.",
            YouTubeMusicControllerFactory.UnsupportedReason("Ubuntu 24.04"));
}
