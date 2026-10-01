namespace KHost.Plugins.YouTubeMusic.Control;

/// <summary>The media session's own status, narrowed to what the provider decides on.</summary>
public enum SessionPlayback
{
    /// <summary>No YouTube Music session at all, or one that has closed.</summary>
    None,

    /// <summary>Between tracks. Reported for a moment during every skip.</summary>
    Changing,
    Stopped,
    Paused,
    Playing,
}

/// <summary>One read of the app's media session, as the OS reports it.</summary>
/// <param name="Position">Where the track was at <paramref name="LastUpdated"/>, not now.</param>
/// <param name="Duration">Zero when the session did not say.</param>
/// <param name="IsAdvert">The backend could see an advert was on, whatever it was titled. Windows'
/// media session cannot say, so there only <see cref="SessionRules.IsAdvert"/> decides.</param>
/// <param name="SignedIn">Whether the page is signed in to a Google account; null where the backend
/// cannot tell (Windows always) or the page has not said yet.</param>
public sealed record SessionSnapshot(
    SessionPlayback Playback,
    string? Title = null,
    string? Artist = null,
    TimeSpan Position = default,
    DateTimeOffset LastUpdated = default,
    TimeSpan Duration = default,
    bool IsAdvert = false,
    bool? SignedIn = null)
{
    public static readonly SessionSnapshot None = new(SessionPlayback.None);

    /// <summary>The playhead at <paramref name="now"/>. The OS reports the timeline only when it
    /// changes, so a playing track is carried forward from its last report and held at its end.</summary>
    public TimeSpan PositionAt(DateTimeOffset now)
    {
        if (Playback != SessionPlayback.Playing || LastUpdated == default)
            return Position;

        var elapsed = now - LastUpdated;

        // A clock that reads earlier than the report is skew, never a rewind.
        var position = elapsed > TimeSpan.Zero ? Position + elapsed : Position;

        return Duration > TimeSpan.Zero && position > Duration ? Duration : position;
    }
}
