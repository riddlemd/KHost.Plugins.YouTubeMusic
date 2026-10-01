namespace KHost.Plugins.YouTubeMusic.Mac;

/// <summary>When to tell the page someone is still there. YouTube Music pauses behind "Are you
/// still there?" after a long stretch with no input; marking the page active well inside that keeps
/// the prompt from ever being drawn.</summary>
public sealed class KeepAliveSchedule(TimeSpan interval)
{
    /// <summary>Well inside the prompt's own threshold, which has been reported at around an hour;
    /// each send costs one script call.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

    private DateTimeOffset? _lastSent;

    public TimeSpan Interval { get; } = interval > TimeSpan.Zero
        ? interval
        : throw new ArgumentOutOfRangeException(nameof(interval), "The interval must be positive.");

    /// <summary>Only while the music plays: a paused bed is the host's choice and must stay paused,
    /// and the prompt only interrupts something playing.</summary>
    public bool IsDue(DateTimeOffset now, bool playing)
        => playing && (_lastSent is not { } last || now - last >= Interval);

    public void MarkSent(DateTimeOffset now) => _lastSent = now;
}
