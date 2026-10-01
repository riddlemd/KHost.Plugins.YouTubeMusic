using KHost.Abstractions.Models;

namespace KHost.Plugins.YouTubeMusic.Control;

/// <summary>What one read of the session meant.</summary>
/// <param name="Playback">Null when the read could not see, which is not Stopped.</param>
/// <param name="Changed">The track or the playback moved since the last settled read.</param>
/// <param name="Pending">The read was a transient being held back; read again after
/// <see cref="SessionTracker.TransientWindow"/> to settle it.</param>
/// <param name="UnexpectedPause">Playing went to Paused with no pause asked for.</param>
public sealed record SessionObservation(
    BreakMusicPlayback? Playback, bool Changed, bool Pending, bool UnexpectedPause);

/// <summary>Turns raw session reads into what the host is told. Thread-safe: the OS raises its
/// events on its own threads while the host's commands read on theirs.</summary>
public sealed class SessionTracker
{
    /// <summary>A skip reports a closed session with no title for about 200ms before the next
    /// track arrives; anything shorter than this is that, not the music stopping.</summary>
    public static readonly TimeSpan TransientWindow = TimeSpan.FromMilliseconds(750);

    /// <summary>The app reports a pause it was asked for within about 50ms. A pause later than
    /// this after the last request came from somewhere else.</summary>
    public static readonly TimeSpan OwnPauseWindow = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();

    private SessionSnapshot? _settled;
    private DateTimeOffset? _transientSince;
    private DateTimeOffset? _pauseRequestedAt;
    private BreakMusicTrack? _track;
    private BreakMusicPlayback? _playback;

    /// <summary>The settled track, or null while nothing is on or an advert plays.</summary>
    public BreakMusicTrack? Track { get { lock (_gate) return _track; } }

    public BreakMusicPlayback? Playback { get { lock (_gate) return _playback; } }

    public void NotePauseRequested(DateTimeOffset now)
    {
        lock (_gate)
            _pauseRequestedAt = now;
    }

    public void NotePlayRequested()
    {
        lock (_gate)
            _pauseRequestedAt = null;
    }

    /// <summary>Whether a pause has been asked for at or after <paramref name="since"/>; the
    /// recovery asks it so a host's own stop during the grace is never overridden.</summary>
    public bool PauseRequestedSince(DateTimeOffset since)
    {
        lock (_gate)
            return _pauseRequestedAt >= since;
    }

    public SessionObservation Observe(SessionSnapshot? snapshot, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (snapshot is null)
                return new SessionObservation(null, Changed: false, Pending: false, UnexpectedPause: false);

            if (IsTransient(snapshot) && HoldsATrack(_settled))
            {
                _transientSince ??= now;

                if (now - _transientSince < TransientWindow)
                    return new SessionObservation(_playback, Changed: false, Pending: true, UnexpectedPause: false);
            }
            else
            {
                _transientSince = null;
            }

            var previous = _settled;
            var previousTrack = _track;
            var previousPlayback = _playback;

            _settled = snapshot;
            _track = ToTrack(snapshot);
            _playback = ToPlayback(snapshot.Playback);

            var changed = previousPlayback != _playback
                || previousTrack?.Title != _track?.Title
                || previousTrack?.Artist != _track?.Artist;

            var unexpectedPause = previous?.Playback == SessionPlayback.Playing
                && snapshot.Playback == SessionPlayback.Paused
                && !(_pauseRequestedAt is { } asked && now - asked <= OwnPauseWindow);

            return new SessionObservation(_playback, changed, Pending: false, unexpectedPause);
        }
    }

    private static bool IsTransient(SessionSnapshot snapshot)
        => snapshot.Playback is SessionPlayback.None or SessionPlayback.Changing
           || string.IsNullOrWhiteSpace(snapshot.Title);

    private static bool HoldsATrack(SessionSnapshot? snapshot)
        => snapshot is { Playback: SessionPlayback.Playing or SessionPlayback.Paused }
           && !string.IsNullOrWhiteSpace(snapshot.Title);

    private static BreakMusicPlayback ToPlayback(SessionPlayback playback) => playback switch
    {
        SessionPlayback.Playing => BreakMusicPlayback.Playing,
        SessionPlayback.Paused => BreakMusicPlayback.Paused,
        _ => BreakMusicPlayback.Stopped,
    };

    private static BreakMusicTrack? ToTrack(SessionSnapshot snapshot)
    {
        if (snapshot.Playback is not (SessionPlayback.Playing or SessionPlayback.Paused)
            || string.IsNullOrWhiteSpace(snapshot.Title)
            || snapshot.IsAdvert
            || SessionRules.IsAdvert(snapshot.Title, snapshot.Artist))
            return null;

        return new BreakMusicTrack
        {
            Title = snapshot.Title,
            Artist = snapshot.Artist ?? string.Empty,
            Duration = snapshot.Duration > TimeSpan.Zero ? snapshot.Duration : null,
        };
    }
}
