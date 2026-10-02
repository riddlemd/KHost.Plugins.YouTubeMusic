using KHost.Plugins.YouTubeMusic.Control;
using Microsoft.Extensions.Logging;

namespace KHost.Plugins.YouTubeMusic.Helper;

/// <summary>YouTube Music in KHost's own helper app on macOS: one Dock icon, its own signed-in
/// WebKit data store, driven over JSON lines.</summary>
/// <remarks>The helper raises nothing on its own, so the page is polled once a second; the reads
/// land in the same <see cref="SessionSnapshot"/> the Windows media session gives, and
/// <see cref="SessionTracker"/> decides what they mean on both.</remarks>
internal sealed class HelperYouTubeMusicController : IYouTubeMusicController
{
    /// <summary>A read is one script call of a few ms. Once a second hears an unasked pause well
    /// inside <see cref="SessionTracker.OwnPauseWindow"/>.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    internal static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger;
    private readonly IHelperTransport _transport;
    private readonly Func<bool> _dataStoreExists;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly KeepAliveSchedule _keepAlive = new(KeepAliveSchedule.DefaultInterval);
    private readonly Lock _stateLock = new();

    private HelperConnection? _connection;
    private int _watching;
    private bool? _signedIn;
    private (SessionPlayback Playback, string? Title, string? Artist, bool Advert, bool? SignedIn)? _lastSeen;
    private bool _warnedProtocol;

    public HelperYouTubeMusicController(
        ILogger logger, IHelperTransport transport, Func<bool> dataStoreExists, TimeProvider? time = null)
    {
        _logger = logger;
        _transport = transport;
        _dataStoreExists = dataStoreExists;
        _time = time ?? TimeProvider.System;

        Unavailable = transport.AppAvailable
            ? null
            : "This build of the YouTube Music plugin carries no YouTube Music app for macOS. Install a release built with it "
              + "(see the plugin's README).";
    }

    public event EventHandler? SessionChanged;

    public string? Unavailable { get; }

    public string BrowserName => "the YouTube Music app";

    public string BrowserShortName => "YouTube Music app";

    public string NotSetUpWarning =>
        $"YouTube Music is not signed in yet. Press \"{SetupButtonLabel.SignIn}\" and sign in in the YouTube Music window; "
        + "until then it plays signed out, with adverts.";

    public bool IsBrowserRunning => _connection is { IsOpen: true } || _transport.IsRunning;

    public SetupStatus GetSetupStatus()
        => HelperSetup.StatusFor(_transport.AppAvailable, SignedIn, _dataStoreExists());

    private bool? SignedIn
    {
        get { lock (_stateLock) return _signedIn; }
        set { lock (_stateLock) _signedIn = value; }
    }

    public Task StartWatchingAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _watching, 1) == 0)
            _ = Task.Run(() => WatchAsync(cancellationToken), CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task<SessionSnapshot?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (await ConnectAsync(launch: null, cancellationToken) is not { Connection: var connection })
            return SessionSnapshot.None;

        var reply = await connection.SendAsync(HelperCommand.State, CommandTimeout, cancellationToken: cancellationToken);

        if (reply is null)
        {
            // Gone while asked is nothing there; still there but silent is "could not look".
            return connection.IsOpen ? null : SessionSnapshot.None;
        }

        if (!reply.Ok || PageState.Parse(reply.Result) is not { } reading)
        {
            _logger.LogDebug("The YouTube Music app could not read the page: {Error}", reply.Error);
            return null;
        }

        if (reading.SignedIn is { } signedIn)
            SignedIn = signedIn;

        return PageState.ToSnapshot(reading, _time.GetUtcNow());
    }

    public async Task<bool> LaunchAppAsync(string? startUrl, CancellationToken cancellationToken = default)
    {
        var url = IsMusicUrl(startUrl) ? startUrl : null;

        // Held at silence until the provider's first level: a list that autoplays on a cold start
        // would otherwise sound at full before the fade-in can take it.
        var connected = await ConnectAsync(new HelperLaunch(Background: true, url, InitialLevel: 0), cancellationToken);

        if (connected is not { } found)
            return false;

        // A second window would be a second player; the one already up is pointed at the list instead,
        // held at silence the same way, since a copy opened from the Dock started without the hold.
        if (found.Launched || url is null)
            return true;

        return (await found.Connection.SendAsync(
            HelperCommand.Load, CommandTimeout, value: 0, url: url, cancellationToken: cancellationToken))?.Ok == true;
    }

    public Task<bool> OpenSetupAsync(CancellationToken cancellationToken = default) => ShowAsync(home: true, cancellationToken);

    public Task<bool> ShowAppAsync(CancellationToken cancellationToken = default) => ShowAsync(home: false, cancellationToken);

    public async Task<bool> PlayAsync(CancellationToken cancellationToken = default)
    {
        var done = await PageCommandAsync(HelperCommand.Play, null, cancellationToken);

        // Play also marks the page active.
        if (done)
            _keepAlive.MarkSent(_time.GetUtcNow());

        return done;
    }

    public Task<bool> PauseAsync(CancellationToken cancellationToken = default)
        => PageCommandAsync(HelperCommand.Pause, null, cancellationToken);

    public Task<bool> SkipAsync(CancellationToken cancellationToken = default)
        => PageCommandAsync(HelperCommand.Next, null, cancellationToken);

    /// <summary>The page's own volume, so nothing else on the Mac gets quieter.</summary>
    public Task<bool> SetLevelAsync(float level, CancellationToken cancellationToken = default)
        => PageCommandAsync(HelperCommand.Level, Math.Round(Math.Clamp(level, 0f, 1f), 4), cancellationToken);

    private async Task<bool> ShowAsync(bool home, CancellationToken cancellationToken)
    {
        if (await ConnectAsync(new HelperLaunch(Background: false), cancellationToken) is not { Connection: var connection })
            return false;

        return (await connection.SendAsync(HelperCommand.Show, CommandTimeout, home: home, cancellationToken: cancellationToken))?.Ok == true;
    }

    /// <summary>Never launches: a command to a helper that is not up has nothing to act on.</summary>
    private async Task<bool> PageCommandAsync(string command, double? value, CancellationToken cancellationToken)
    {
        if (await ConnectAsync(launch: null, cancellationToken) is not { Connection: var connection })
            return false;

        var reply = await connection.SendAsync(command, CommandTimeout, value: value, cancellationToken: cancellationToken);

        if (reply is { Ok: true } && PageState.Parse(reply.Result) is { Page: true, Ok: true })
            return true;

        _logger.LogDebug("The YouTube Music app did not do {Command}: {Error}", command, reply?.Error ?? "no answer");
        return false;
    }

    /// <param name="launch">How to start one when none is up; null only looks for one.</param>
    private async Task<(HelperConnection Connection, bool Launched)?> ConnectAsync(HelperLaunch? launch, CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true } open)
            return (open, false);

        await _connectGate.WaitAsync(cancellationToken);

        try
        {
            if (_connection is { IsOpen: true } raced)
                return (raced, false);

            var launched = false;
            var connection = await _transport.ConnectExistingAsync(cancellationToken);

            if (connection is null && launch is not null)
            {
                connection = await _transport.LaunchAsync(launch, cancellationToken);
                launched = connection is not null;

                // It found a copy already up and left; that one is the one to drive.
                connection ??= await _transport.ConnectExistingAsync(cancellationToken);
            }

            if (connection is null)
                return null;

            if (connection.Hello is { } hello && hello.Protocol != HelperProtocol.Version && !_warnedProtocol)
            {
                _warnedProtocol = true;
                _logger.LogWarning(
                    "The YouTube Music app speaks protocol {Theirs}, this plugin {Ours}; quit it so the plugin's own copy starts",
                    hello.Protocol, HelperProtocol.Version);
            }

            connection.Closed += OnClosed;
            _connection = connection;

            return (connection, launched);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (ReferenceEquals(_connection, sender))
            _connection = null;

        _logger.LogInformation("The YouTube Music app closed");
        Notice(SessionSnapshot.None);
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval, _time);

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
                    _logger.LogDebug(ex, "Could not poll the YouTube Music app");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped with the host.
        }
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var snapshot = await ReadAsync(cancellationToken);

        if (snapshot is null)
            return;

        Notice(snapshot);

        var now = _time.GetUtcNow();

        if (_keepAlive.IsDue(now, playing: snapshot.Playback == SessionPlayback.Playing)
            && await PageCommandAsync(HelperCommand.KeepAlive, null, cancellationToken))
        {
            _keepAlive.MarkSent(now);
        }
    }

    /// <summary>Raises <see cref="SessionChanged"/> when what the host is shown would move.</summary>
    private void Notice(SessionSnapshot snapshot)
    {
        var seen = (snapshot.Playback, snapshot.Title, snapshot.Artist, snapshot.IsAdvert, snapshot.SignedIn);

        lock (_stateLock)
        {
            if (_lastSeen == seen)
                return;

            _lastSeen = seen;
        }

        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsMusicUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && uri.Host.Equals("music.youtube.com", StringComparison.OrdinalIgnoreCase);
}
