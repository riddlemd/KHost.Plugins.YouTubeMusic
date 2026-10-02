using KHost.Plugins.YouTubeMusic.Control;

namespace KHost.Plugins.YouTubeMusic.Tests;

/// <summary>Records commands in order, so a test can assert what reached the app and that nothing
/// else did. Reads are kept off <see cref="Calls"/>: they are not commands.</summary>
public sealed class FakeYouTubeMusicController : IYouTubeMusicController
{
    public List<string> Calls { get; } = [];

    public string? Unavailable { get; set; }

    /// <summary>Edge's names by default, so every message asserted below is the Windows wording.</summary>
    public string BrowserName { get; set; } = "Microsoft Edge";

    public string BrowserShortName { get; set; } = "Edge";

    public string NotSetUpWarning { get; set; } =
        "YouTube Music is not installed in this plugin's Edge profile yet. Press \"Set up "
        + "YouTube Music\", sign in, then install it from the \"App available\" icon in Edge's address bar.";

    public event EventHandler? SessionChanged;

    public void RaiseSessionChanged() => SessionChanged?.Invoke(this, EventArgs.Empty);

    public bool IsBrowserRunning { get; set; }

    public SetupStatus Status { get; set; } = SetupStatus.Ready;

    public SessionSnapshot? Snapshot { get; set; } = SessionSnapshot.None;

    /// <summary>Reads handed out before <see cref="Snapshot"/>.</summary>
    public Queue<SessionSnapshot?> QueuedSnapshots { get; } = new();

    /// <summary>Applied to <see cref="Snapshot"/> on each command, the way the app would move.</summary>
    public Func<string, SessionSnapshot?>? OnCommand { get; set; }

    public bool CanSetLevel { get; set; } = true;

    /// <summary>False makes every command (play, pause, skip, launch, setup) report failure, the
    /// way a real backend does when it cannot reach the app.</summary>
    public bool CommandsSucceed { get; set; } = true;

    public Task StartWatchingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<SessionSnapshot?> ReadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(QueuedSnapshots.Count > 0 ? QueuedSnapshots.Dequeue() : Snapshot);

    public bool StatusThrows { get; set; }

    public SetupStatus GetSetupStatus() => StatusThrows ? throw new IOException("Preferences locked") : Status;

    public Task<bool> LaunchAppAsync(string? startUrl, CancellationToken cancellationToken = default)
        => Command($"launch:{startUrl}");

    public Task<bool> OpenSetupAsync(CancellationToken cancellationToken = default) => Command("setup");
    public Task<bool> ShowAppAsync(CancellationToken cancellationToken = default) => Command("show");
    public Task<bool> PlayAsync(CancellationToken cancellationToken = default) => Command("play");
    public Task<bool> PauseAsync(CancellationToken cancellationToken = default) => Command("pause");
    public Task<bool> SkipAsync(CancellationToken cancellationToken = default) => Command("skip");

    public Task<bool> SetLevelAsync(float level, CancellationToken cancellationToken = default)
    {
        Calls.Add($"level:{level:0.###}");
        return Task.FromResult(CanSetLevel);
    }

    private Task<bool> Command(string name)
    {
        Calls.Add(name);

        if (OnCommand?.Invoke(name) is { } next)
            Snapshot = next;

        return Task.FromResult(CommandsSucceed);
    }
}

/// <summary>A clock that moves only when told, so every window is exact. Its timers fire inside
/// <see cref="Advance"/>, on the caller's thread, for every period the advance carries past.</summary>
public sealed class ManualClock : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];

    public DateTimeOffset Now { get; set; } = new(2026, 10, 1, 20, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan span)
    {
        Now += span;

        ManualTimer[] timers;

        lock (_timers)
            timers = [.. _timers];

        foreach (var timer in timers)
            timer.FireDue(Now);
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);

        lock (_timers)
            _timers.Add(timer);

        return timer;
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        private readonly object _sync = new();
        private DateTimeOffset? _next;
        private TimeSpan _period;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_sync)
            {
                _next = dueTime == Timeout.InfiniteTimeSpan ? null : clock.Now + dueTime;
                _period = period;
            }

            return true;
        }

        public void FireDue(DateTimeOffset now)
        {
            while (true)
            {
                lock (_sync)
                {
                    if (_next is not { } next || next > now)
                        return;

                    _next = _period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan ? next + _period : null;
                }

                callback(state);
            }
        }

        public void Dispose()
        {
            lock (_sync)
                _next = null;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
