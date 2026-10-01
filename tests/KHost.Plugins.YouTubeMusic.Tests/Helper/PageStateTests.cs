using KHost.Plugins.YouTubeMusic.Control;
using KHost.Plugins.YouTubeMusic.Helper;
using System.Text.Json;

namespace KHost.Plugins.YouTubeMusic.Tests.Helper;

public class PageStateTests
{
    private static readonly DateTimeOffset ReadAt = new(2026, 10, 1, 20, 0, 0, TimeSpan.Zero);

    private static readonly PageReading Song = new(
        Page: true, Player: true, State: PageState.Playing, Paused: false,
        Title: "Hello (Official Music Video)", Artist: "Adele", Position: 32.5, Duration: 367);

    // The shape youtube-music-page.js answers "state" with, fields the plugin does not read included.
    [Fact]
    public void Parse_TheScriptsAnswer_ReadsEveryField()
    {
        var reading = PageState.Parse(Json(
            """{"page":true,"player":true,"state":1,"paused":false,"ad":false,"title":"Hello","artist":"Adele","position":32.5,"duration":367,"volume":0.3,"stillThere":true,"signedIn":true,"levelHeld":false,"url":"https://music.youtube.com/watch?v=x","visibility":"hidden","windowVisible":false}"""));

        Assert.Equal(
            new PageReading(true, Player: true, State: 1, Paused: false, Title: "Hello", Artist: "Adele",
                Position: 32.5, Duration: 367, Volume: 0.3, StillThere: true, SignedIn: true),
            reading);
    }

    [Theory]
    [InlineData("\"missing value\"")]
    [InlineData("[1,2]")]
    [InlineData("{\"page\":\"yes\"}")]
    public void Parse_NotAnAnswer_IsNull(string json)
        => Assert.Null(PageState.Parse(Json(json)));

    [Fact]
    public void Parse_NoResult_IsNull()
        => Assert.Null(PageState.Parse(null));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ToSnapshot_CarriesTheSignIn(bool signedIn)
        => Assert.Equal(signedIn, PageState.ToSnapshot(Song with { SignedIn = signedIn }, ReadAt).SignedIn);

    // The home page has nothing loaded but still says whether the account is signed in.
    [Fact]
    public void ToSnapshot_NothingLoaded_StillCarriesTheSignIn()
    {
        var snapshot = PageState.ToSnapshot(Song with { State = PageState.Unstarted, Title = "", SignedIn = false }, ReadAt);

        Assert.Equal(SessionPlayback.None, snapshot.Playback);
        Assert.False(snapshot.SignedIn);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void ToSnapshot_PlayingSong_CarriesTrackTimelineAndReadTime()
        => Assert.Equal(
            new SessionSnapshot(SessionPlayback.Playing, "Hello (Official Music Video)", "Adele",
                TimeSpan.FromSeconds(32.5), ReadAt, TimeSpan.FromSeconds(367)),
            PageState.ToSnapshot(Song, ReadAt));

    // Whatever else came back with it: an answer from a window that is not the app is not ours.
    [Fact]
    public void ToSnapshot_NotAYouTubeMusicPage_IsNoSession()
        => Assert.Equal(SessionSnapshot.None, PageState.ToSnapshot(Song with { Page = false }, ReadAt));

    // Measured: during an advert the player reports the song behind it as unstarted (-1), and the
    // media session names the advert as if it were a song ("Ebenezer | Official Trailer 2").
    [Fact]
    public void ToSnapshot_Advert_IsPlayingAndMarkedAnAdvertWhateverItIsTitled()
    {
        var snapshot = PageState.ToSnapshot(
            Song with { Ad = true, State = PageState.Unstarted, Title = "Ebenezer | Official Trailer 2", Artist = "Paramount Pictures" },
            ReadAt);

        Assert.Equal(SessionPlayback.Playing, snapshot.Playback);
        Assert.True(snapshot.IsAdvert);
    }

    [Fact]
    public void ToSnapshot_AdvertPaused_IsPaused()
        => Assert.Equal(
            SessionPlayback.Paused,
            PageState.ToSnapshot(Song with { Ad = true, State = PageState.Unstarted, Paused = true }, ReadAt).Playback);

    [Theory]
    [InlineData(PageState.Playing, false, SessionPlayback.Playing)]
    [InlineData(PageState.Buffering, false, SessionPlayback.Playing)]
    [InlineData(PageState.Buffering, true, SessionPlayback.Changing)]
    [InlineData(PageState.Paused, true, SessionPlayback.Paused)]
    [InlineData(PageState.Ended, true, SessionPlayback.Stopped)]
    [InlineData(PageState.Cued, true, SessionPlayback.Paused)]
    [InlineData(PageState.Unstarted, true, SessionPlayback.Changing)]
    public void PlaybackOf_WithATitle_MapsThePlayersState(int state, bool paused, SessionPlayback expected)
        => Assert.Equal(expected, PageState.PlaybackOf(Song with { State = state, Paused = paused }));

    // A page just opened has nothing loaded: that is no session yet, which the provider waits out.
    [Theory]
    [InlineData(PageState.Unstarted)]
    [InlineData(PageState.Cued)]
    [InlineData(null)]
    public void ToSnapshot_NothingLoaded_IsNoSession(int? state)
        => Assert.Equal(SessionSnapshot.None, PageState.ToSnapshot(Song with { State = state, Title = " ", Artist = "" }, ReadAt));

    [Fact]
    public void ToSnapshot_BlankArtistAndNonsenseTimes_AreLeftOut()
    {
        // A stream's <video> reports an infinite duration.
        var snapshot = PageState.ToSnapshot(Song with { Artist = "  ", Position = double.NaN, Duration = double.PositiveInfinity }, ReadAt);

        Assert.Null(snapshot.Artist);
        Assert.Equal(TimeSpan.Zero, snapshot.Position);
        Assert.Equal(TimeSpan.Zero, snapshot.Duration);
    }
}
