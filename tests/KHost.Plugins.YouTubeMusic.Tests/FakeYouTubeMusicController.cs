using KHost.Plugins.YouTubeMusic.Control;

namespace KHost.Plugins.YouTubeMusic.Tests;

/// <summary>Records commands in order, so a test can assert what reached the app and that nothing
/// else did. Reads are kept off <see cref="Calls"/>: they are not commands.</summary>
public sealed class FakeYouTubeMusicController : IYouTubeMusicController
{
    public List<string> Calls { get; } = [];

    public string? Unavailable { get; set; }

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

    public SetupStatus GetSetupStatus() => Status;

    public Task<bool> LaunchAppAsync(string? startUrl, CancellationToken cancellationToken = default)
        => Command($"launch:{startUrl}");

    public Task<bool> OpenSetupAsync(CancellationToken cancellationToken = default) => Command("setup");
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

/// <summary>A clock that moves only when told, so every window is exact.</summary>
public sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 1, 20, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan span) => Now += span;
}
