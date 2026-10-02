using Microsoft.Extensions.Logging;

namespace KHost.Plugins.YouTubeMusic.Control;

/// <summary>How loudly to log finding the app's media session. Windows drops the session for about
/// 250ms on every track change, so a find that soon after a loss is routine, not news.</summary>
public sealed class SessionSightings
{
    public static readonly TimeSpan RoutineGap = TimeSpan.FromSeconds(2);

    private readonly object _sync = new();
    private DateTimeOffset? _lostAt;

    public void NoteLost(DateTimeOffset now)
    {
        lock (_sync)
            _lostAt = now;
    }

    /// <returns>Debug for a find within <see cref="RoutineGap"/> of a loss; Information for the
    /// first find, and for one after a longer gap.</returns>
    public LogLevel NoteFound(DateTimeOffset now)
    {
        lock (_sync)
        {
            var routine = _lostAt is { } lost && now - lost <= RoutineGap;
            _lostAt = null;

            return routine ? LogLevel.Debug : LogLevel.Information;
        }
    }
}
