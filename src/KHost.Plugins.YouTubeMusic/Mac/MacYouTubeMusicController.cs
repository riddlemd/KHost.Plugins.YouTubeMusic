using KHost.Plugins.YouTubeMusic.Chrome;
using KHost.Plugins.YouTubeMusic.Control;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace KHost.Plugins.YouTubeMusic.Mac;

/// <summary>YouTube Music in a Chrome app window on the plugin's own profile, driven by scripts sent
/// to that one Chrome process as Apple Events.</summary>
/// <remarks>Once the host has installed the app, its window is drawn by Chrome's app shim under a
/// "YouTube Music" Dock icon, but it is still the browser process that answers the scripts, so the
/// pid from <c>SingletonLock</c> stays the one to address.
/// <para>Chrome raises nothing a plugin can hear, so the page is polled; the reads land in the
/// same <see cref="SessionSnapshot"/> the Windows media session gives, and
/// <see cref="SessionTracker"/> decides what they mean on both.</para></remarks>
[SupportedOSPlatform("macos")]
internal sealed class MacYouTubeMusicController : IYouTubeMusicController
{
    /// <summary>A read is one script call of ~10ms. Once a second hears an unasked pause well inside
    /// <see cref="SessionTracker.OwnPauseWindow"/>, and a skip's empty moment is still held back by
    /// <see cref="SessionTracker.TransientWindow"/>.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LaunchWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LaunchPoll = TimeSpan.FromMilliseconds(250);

    private const string AutomationSettingsUrl = "x-apple.systempreferences:com.apple.preference.security?Privacy_Automation";

    private readonly ILogger _logger;
    private readonly string _profileDirectory;
    private readonly string? _bundle;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly KeepAliveSchedule _keepAlive = new(KeepAliveSchedule.DefaultInterval);
    private readonly Lock _stateLock = new();

    private int _windowIndex = 1;
    private int? _permission;
    private int _watching;
    private (SessionPlayback Playback, string? Title, string? Artist, bool Advert)? _lastSeen;
    private bool _warnedNotPermitted;
    private bool _warnedScriptsRefused;

    public MacYouTubeMusicController(ILogger logger, string profileDirectory)
    {
        _logger = logger;
        _profileDirectory = profileDirectory;
        _bundle = ChromeLocator.Find(
            ChromeLocator.Candidates(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Spotlight()),
            File.Exists);

        Unavailable = _bundle is null
            ? "Google Chrome was not found on this machine, and YouTube Music break music plays through it on macOS."
            : null;
    }

    public event EventHandler? SessionChanged;

    public string? Unavailable { get; }

    public string BrowserName => "Google Chrome";

    public string BrowserShortName => "Chrome";

    public string NotSetUpWarning =>
        "YouTube Music is not set up in this plugin's Chrome profile yet. Press \"Set up YouTube Music\": "
        + "Chrome opens on music.youtube.com to sign in, and macOS asks once whether KHost may control Chrome.";

    public bool IsBrowserRunning => RunningPid() is not null;

    public SetupStatus GetSetupStatus()
    {
        if (_bundle is null)
            return SetupStatus.BrowserNotFound;

        if (!ChromeProfile.HasBeenOpened(_profileDirectory))
            return SetupStatus.AppNotInstalled;

        // Asked of the plugin's own Chrome when it is up; otherwise the last answer stands, since
        // permission can only be read against a running target.
        var permission = RunningPid() is { } pid ? CheckPermission(pid) : _permission;

        return MacSetup.StatusFor(
            notPermitted: permission is AppleEvents.NotPermittedError or AppleEvents.WouldRequireConsentError,
            appInstalled: ChromeProfile.HasYouTubeMusicApp(_profileDirectory));
    }

    public Task StartWatchingAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _watching, 1) == 0)
            _ = Task.Run(() => WatchAsync(cancellationToken), CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task<SessionSnapshot?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var (outcome, reading) = await RunAsync(PageCommand.State, null, appOnlyStrict: false, cancellationToken);

        return outcome switch
        {
            Outcome.NoBrowser or Outcome.NoPage => SessionSnapshot.None,
            Outcome.Answered when reading is not null => PageState.ToSnapshot(reading, DateTimeOffset.UtcNow),

            // Null, not None: "could not look" and "nothing there" lead to different decisions.
            _ => null,
        };
    }

    public async Task<bool> LaunchAppAsync(string? startUrl, CancellationToken cancellationToken = default)
    {
        if (_bundle is null)
            return false;

        var url = ChromeArguments.IsYouTubeMusicUrl(startUrl) ? startUrl : null;

        // A second app window would be a second player; the one already open is pointed at the
        // list instead, or left alone when there is none to start.
        if (IsBrowserRunning)
        {
            var (outcome, reading) = url is null
                ? await RunAsync(PageCommand.State, null, appOnlyStrict: true, cancellationToken)
                : await RunAsync(PageCommand.Navigate, url, appOnlyStrict: true, cancellationToken);

            if (outcome == Outcome.Answered && reading is not null)
                return url is null || reading.Ok;
        }

        var arguments = ChromeArguments.ForApp(_profileDirectory, url, ChromeProfile.HasYouTubeMusicApp(_profileDirectory));

        return await LaunchAsync(arguments, background: true, cancellationToken);
    }

    public async Task<bool> OpenSetupAsync(CancellationToken cancellationToken = default)
    {
        if (_bundle is null)
            return false;

        if (!await LaunchAsync(ChromeArguments.ForSetup(_profileDirectory), background: false, cancellationToken))
            return false;

        // Asked here because the host is at the machine having pressed the button; asked anywhere
        // else, the prompt could land over the room's screen mid-show.
        _ = Task.Run(AskPermissionAsync, CancellationToken.None);

        return true;
    }

    public async Task<bool> PlayAsync(CancellationToken cancellationToken = default)
    {
        var done = await CommandAsync(PageCommand.Play, null, cancellationToken);

        // Play also marks the page active.
        if (done)
            _keepAlive.MarkSent(DateTimeOffset.UtcNow);

        return done;
    }

    public Task<bool> PauseAsync(CancellationToken cancellationToken = default)
        => CommandAsync(PageCommand.Pause, null, cancellationToken);

    public Task<bool> SkipAsync(CancellationToken cancellationToken = default)
        => CommandAsync(PageCommand.Next, null, cancellationToken);

    /// <summary>The page's own volume, so the host's other sound is never touched.</summary>
    public Task<bool> SetLevelAsync(float level, CancellationToken cancellationToken = default)
        => CommandAsync(PageCommand.Level, Math.Round(Math.Clamp(level, 0f, 1f), 4), cancellationToken);

    private async Task<bool> CommandAsync(string command, object? argument, CancellationToken cancellationToken)
    {
        var (outcome, reading) = await RunAsync(command, argument, appOnlyStrict: false, cancellationToken);

        return outcome == Outcome.Answered && reading is { Ok: true };
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await PollOnceAsync(cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Could not poll YouTube Music in Chrome");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped with the host.
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        // Nothing is sent while the plugin's Chrome is down or may not be scripted: the poll must
        // never be what raises the consent prompt.
        if (RunningPid() is not { } pid || CheckPermission(pid) != 0)
        {
            Notice(SessionSnapshot.None);
            return;
        }

        var snapshot = await ReadAsync(cancellationToken);

        if (snapshot is null)
            return;

        Notice(snapshot);

        var now = DateTimeOffset.UtcNow;

        if (_keepAlive.IsDue(now, playing: snapshot.Playback == SessionPlayback.Playing))
        {
            var (outcome, reading) = await RunAsync(PageCommand.KeepAlive, null, appOnlyStrict: false, cancellationToken);

            if (outcome == Outcome.Answered && reading is { Ok: true })
                _keepAlive.MarkSent(now);
        }
    }

    /// <summary>Raises <see cref="SessionChanged"/> when what the host is shown would move.</summary>
    private void Notice(SessionSnapshot snapshot)
    {
        var seen = (snapshot.Playback, snapshot.Title, snapshot.Artist, snapshot.IsAdvert);

        lock (_stateLock)
        {
            if (_lastSeen == seen)
                return;

            _lastSeen = seen;
        }

        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<(Outcome Outcome, PageReading? Reading)> RunAsync(
        string command, object? argument, bool appOnlyStrict, CancellationToken cancellationToken)
    {
        if (RunningPid() is not { } pid)
            return (Outcome.NoBrowser, null);

        await _gate.WaitAsync(cancellationToken);

        try
        {
            // The calls block on Chrome's reply; kept off the caller's thread.
            return await Task.Run(() => Run(pid, command, argument, appOnlyStrict), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not send {Command} to YouTube Music in Chrome", command);
            return (Outcome.Failed, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private (Outcome, PageReading?) Run(int pid, string command, object? argument, bool appOnlyStrict)
    {
        if (CheckPermission(pid) != 0)
        {
            WarnNotPermitted();
            return (Outcome.NotPermitted, null);
        }

        var (counted, count) = AppleEvents.CountWindows(pid, EventTimeout);

        if (counted == AppleEventStatus.NotPermitted)
            return (Outcome.NotPermitted, null);

        if (counted != AppleEventStatus.Ok)
            return (Outcome.Failed, null);

        var failed = false;
        bool[] passes = appOnlyStrict ? [true] : [true, false];

        foreach (var appOnly in passes)
        {
            var script = PageScript.For(command, argument, appOnly);

            foreach (var index in WindowOrder(count))
            {
                var (status, json) = AppleEvents.ExecuteJavaScript(pid, index, script, EventTimeout);

                if (status == AppleEventStatus.NotPermitted)
                    return (Outcome.NotPermitted, null);

                if (status == AppleEventStatus.Failed)
                {
                    failed = true;
                    continue;
                }

                if (PageState.Parse(json) is not { Page: true } reading)
                    continue;

                _windowIndex = index;

                if (reading.Error is { } error)
                    _logger.LogDebug("YouTube Music's page answered {Command} with an error: {Error}", command, error);

                return (Outcome.Answered, reading);
            }
        }

        if (failed)
            WarnScriptsRefused();

        return failed ? (Outcome.Failed, null) : (Outcome.NoPage, null);
    }

    /// <summary>The window that answered last first: windows renumber as focus moves, but rarely.</summary>
    private IEnumerable<int> WindowOrder(int count)
    {
        var first = _windowIndex;

        if (first >= 1 && first <= count)
            yield return first;

        for (var index = 1; index <= count; index++)
        {
            if (index != first)
                yield return index;
        }
    }

    private int? RunningPid()
    {
        if (ChromeProfile.ReadLockPid(_profileDirectory) is not { } pid)
            return null;

        try
        {
            using var process = Process.GetProcessById(pid);

            // A crash leaves the lock behind, and its pid may since belong to anything.
            return !process.HasExited && process.ProcessName == ChromeLocator.ExecutableName ? pid : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Never prompts. A grant is kept for the process's life; anything else is asked again,
    /// since the host may fix it in System Settings at any moment.</summary>
    private int CheckPermission(int pid)
    {
        if (_permission == 0)
            return 0;

        var permission = AppleEvents.DeterminePermission(pid, askUserIfNeeded: false);

        if (permission != AppleEvents.ProcessNotFoundError)
            _permission = permission;

        return permission;
    }

    private async Task AskPermissionAsync()
    {
        try
        {
            var waited = TimeSpan.Zero;
            int? pid;

            while ((pid = RunningPid()) is null && waited < LaunchWait)
            {
                await Task.Delay(LaunchPoll);
                waited += LaunchPoll;
            }

            if (pid is null)
            {
                _logger.LogWarning("Chrome did not come up on the plugin's profile, so KHost could not ask to control it");
                return;
            }

            // Blocks while the macOS prompt is up.
            var permission = await Task.Run(() => AppleEvents.DeterminePermission(pid.Value, askUserIfNeeded: true));
            _permission = permission;

            switch (permission)
            {
                case 0:
                    _logger.LogInformation("KHost may control Google Chrome; YouTube Music break music is ready");
                    break;

                case AppleEvents.NotPermittedError:
                    // Refused once, macOS never asks again; only the settings pane can change it.
                    _logger.LogWarning(
                        "KHost is not allowed to control Google Chrome. Turn it on in System Settings → Privacy & Security → Automation");
                    OpenAutomationSettings();
                    break;

                default:
                    _logger.LogWarning("Could not ask whether KHost may control Google Chrome ({Status})", permission);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not ask whether KHost may control Google Chrome");
        }
    }

    private void WarnNotPermitted()
    {
        if (_warnedNotPermitted)
            return;

        _warnedNotPermitted = true;
        _logger.LogWarning(
            "KHost may not control Google Chrome yet. Press \"Set up YouTube Music\" on the Plugins page, or allow it in "
            + "System Settings → Privacy & Security → Automation");
    }

    private void WarnScriptsRefused()
    {
        if (_warnedScriptsRefused || ChromeProfile.AppleEventsAllowed(_profileDirectory))
            return;

        _warnedScriptsRefused = true;
        _logger.LogWarning(
            "Chrome refused KHost's scripts: \"Allow JavaScript from Apple Events\" is off in the plugin's profile. "
            + "Quit that Chrome window and press \"Set up YouTube Music\" again; the plugin turns it on before Chrome starts");
    }

    private async Task<bool> LaunchAsync(IReadOnlyList<string> arguments, bool background, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_profileDirectory);

            // Only while the profile's Chrome is down: a running Chrome rewrites the file from memory.
            if (!IsBrowserRunning)
                ChromeProfile.AllowAppleEvents(_profileDirectory);

            // Through open(1) so Chrome is its own app rather than a child of the host, which would
            // otherwise inherit its console and answer for its privacy prompts. -n because the host's
            // own Chrome may be running, and without it the arguments would be handed to that one.
            var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            start.ArgumentList.Add("-n");

            // Started behind the karaoke screen rather than over it.
            if (background)
                start.ArgumentList.Add("-g");

            start.ArgumentList.Add("-a");
            start.ArgumentList.Add(_bundle!);
            start.ArgumentList.Add("--args");

            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);

            using var process = Process.Start(start);

            if (process is null)
                return false;

            await process.WaitForExitAsync(cancellationToken);

            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not start Google Chrome at {Path}", _bundle);
            return false;
        }
    }

    private void OpenAutomationSettings()
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo("/usr/bin/open", AutomationSettingsUrl) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not open System Settings at Automation");
        }
    }

    /// <summary>Lazy: Spotlight is asked only when Chrome is in neither Applications folder.</summary>
    private static IEnumerable<string> Spotlight()
    {
        string output;

        try
        {
            var start = new ProcessStartInfo("/usr/bin/mdfind", $"kMDItemCFBundleIdentifier=={ChromeLocator.BundleId}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };

            using var process = Process.Start(start);

            if (process is null)
                yield break;

            output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return line;
    }

    private enum Outcome
    {
        Answered,
        NoBrowser,
        NoPage,
        NotPermitted,
        Failed,
    }
}
