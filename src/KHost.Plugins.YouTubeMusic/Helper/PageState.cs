using KHost.Plugins.YouTubeMusic.Control;
using System.Text.Json;

namespace KHost.Plugins.YouTubeMusic.Helper;

/// <summary>One answer from the page script (<c>helpers/macos/youtube-music-page.js</c>), as the
/// helper passed it on.</summary>
/// <param name="Page">False when the helper's window is not on music.youtube.com (mid sign-in, say).</param>
/// <param name="State">The player's own state: -1 unstarted, 0 ended, 1 playing, 2 paused,
/// 3 buffering, 5 cued.</param>
/// <param name="Ad">The player is showing an advert. Its title and artist are the advert's.</param>
/// <param name="StillThere">"Are you still there?" is on screen.</param>
/// <param name="SignedIn">The page's own LOGGED_IN flag; null before the page has built it.</param>
public sealed record PageReading(
    bool Page,
    bool Ok = false,
    bool Player = false,
    int? State = null,
    bool? Paused = null,
    bool Ad = false,
    string? Title = null,
    string? Artist = null,
    double Position = 0,
    double Duration = 0,
    double? Volume = null,
    bool StillThere = false,
    bool? SignedIn = null,
    string? Error = null);

/// <summary>Reads the page script's answers and narrows them to a <see cref="SessionSnapshot"/>,
/// so the provider decides on the same shape it gets from Windows.</summary>
public static class PageState
{
    public const int Unstarted = -1;
    public const int Ended = 0;
    public const int Playing = 1;
    public const int Paused = 2;
    public const int Buffering = 3;
    public const int Cued = 5;

    /// <summary>Null when <paramref name="element"/> is not an answer at all.</summary>
    public static PageReading? Parse(JsonElement? element)
    {
        // Anything but an object fails to deserialise, which is caught below.
        if (element is not { } answer)
            return null;

        try
        {
            return answer.Deserialize<PageReading>(JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <param name="readAt">When the page was read: the position is the page's own, taken then.</param>
    public static SessionSnapshot ToSnapshot(PageReading reading, DateTimeOffset readAt)
    {
        // Mid sign-in the window is on Google's pages: nothing playing, and no word on the sign-in.
        if (!reading.Page)
            return SessionSnapshot.None;

        var playback = PlaybackOf(reading);

        // Carried with nothing loaded too: the home page says it as well as a song does.
        if (playback == SessionPlayback.None)
            return SessionSnapshot.None with { SignedIn = reading.SignedIn };

        return new SessionSnapshot(
            playback,
            Blank(reading.Title),
            Blank(reading.Artist),
            Seconds(reading.Position),
            readAt,
            Seconds(reading.Duration),
            IsAdvert: reading.Ad,
            SignedIn: reading.SignedIn);
    }

    /// <summary>The player's state, with the cases it gets wrong for the host corrected.</summary>
    public static SessionPlayback PlaybackOf(PageReading reading)
    {
        var hasTitle = !string.IsNullOrWhiteSpace(reading.Title);

        // During an advert the player reports the song waiting behind it as unstarted; the advert's
        // own element says whether anything is sounding.
        if (reading.Ad)
            return reading.Paused == true ? SessionPlayback.Paused : SessionPlayback.Playing;

        return reading.State switch
        {
            Playing => SessionPlayback.Playing,

            // Buffering after a play is playing as far as the room is concerned; reporting it as a
            // change would flicker the console on every slow segment. Paused and buffering is the
            // next track loading at a boundary.
            Buffering => reading.Paused == true ? SessionPlayback.Changing : SessionPlayback.Playing,
            Paused => SessionPlayback.Paused,

            // Also reported for a moment at every track boundary; SessionTracker holds it back.
            Ended => SessionPlayback.Stopped,
            Cued => hasTitle ? SessionPlayback.Paused : SessionPlayback.None,

            // A freshly opened page has no title and nothing loaded: no session yet, not a stop.
            Unstarted => hasTitle ? SessionPlayback.Changing : SessionPlayback.None,
            _ => hasTitle ? SessionPlayback.Changing : SessionPlayback.None,
        };
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static TimeSpan Seconds(double seconds)
        => double.IsFinite(seconds) && seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
}
