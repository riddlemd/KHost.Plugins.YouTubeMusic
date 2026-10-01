using KHost.Abstractions.Models;
using KHost.Plugins.YouTubeMusic.Control;

namespace KHost.Plugins.YouTubeMusic.Tests.Control;

public class SessionTrackerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 20, 0, 0, TimeSpan.Zero);

    private static readonly SessionSnapshot SongA = new(SessionPlayback.Playing, "Blue Monday", "New Order", Duration: TimeSpan.FromMinutes(7));
    private static readonly SessionSnapshot SongB = new(SessionPlayback.Playing, "Temptation", "New Order");
    private static readonly SessionSnapshot SkipGap = new(SessionPlayback.None);

    private readonly SessionTracker _tracker = new();

    [Fact]
    public void Observe_FirstTrack_NamesItAndReportsAChange()
    {
        var observation = _tracker.Observe(SongA, Start);

        Assert.True(observation.Changed);
        Assert.Equal(BreakMusicPlayback.Playing, observation.Playback);
        Assert.Equal("Blue Monday", _tracker.Track!.Title);
        Assert.Equal("New Order", _tracker.Track.Artist);
        Assert.Equal(TimeSpan.FromMinutes(7), _tracker.Track.Duration);
    }

    [Fact]
    public void Observe_SameTrackAgain_IsNoChange()
    {
        _tracker.Observe(SongA, Start);

        Assert.False(_tracker.Observe(SongA, Start.AddSeconds(1)).Changed);
    }

    // A skip reports a closed session with no title for about 200ms. Taken at face value the screen
    // drops its now-playing card and the console flickers to Stopped on every skip.
    [Fact]
    public void Observe_SkipTransientInsideTheWindow_HoldsTheTrackAndAsksForAnotherRead()
    {
        _tracker.Observe(SongA, Start);

        var observation = _tracker.Observe(SkipGap, Start.AddMilliseconds(200));

        Assert.True(observation.Pending);
        Assert.False(observation.Changed);
        Assert.Equal(BreakMusicPlayback.Playing, observation.Playback);
        Assert.Equal("Blue Monday", _tracker.Track!.Title);
    }

    [Fact]
    public void Observe_TransientThenNextTrack_ReportsOneChangeToTheNextTrack()
    {
        _tracker.Observe(SongA, Start);
        _tracker.Observe(SkipGap, Start.AddMilliseconds(200));

        var observation = _tracker.Observe(SongB, Start.AddMilliseconds(400));

        Assert.True(observation.Changed);
        Assert.Equal("Temptation", _tracker.Track!.Title);
    }

    [Fact]
    public void Observe_TransientOutlastingTheWindow_SettlesAsStopped()
    {
        _tracker.Observe(SongA, Start);
        _tracker.Observe(SkipGap, Start.AddMilliseconds(100));

        var observation = _tracker.Observe(SkipGap, Start.AddMilliseconds(100) + SessionTracker.TransientWindow);

        Assert.False(observation.Pending);
        Assert.True(observation.Changed);
        Assert.Equal(BreakMusicPlayback.Stopped, observation.Playback);
        Assert.Null(_tracker.Track);
    }

    [Fact]
    public void Observe_CannotSee_ReportsNullAndKeepsTheTrack()
    {
        _tracker.Observe(SongA, Start);

        var observation = _tracker.Observe(null, Start.AddSeconds(1));

        Assert.Null(observation.Playback);
        Assert.False(observation.Changed);
        Assert.Equal("Blue Monday", _tracker.Track!.Title);
    }

    // The room hears an advert, but the screen must not name "Video Ad" as though it were a song.
    [Fact]
    public void Observe_Advert_ReportsPlayingWithNoTrack()
    {
        _tracker.Observe(SongA, Start);

        var observation = _tracker.Observe(new SessionSnapshot(SessionPlayback.Playing, "Video Ad", "YouTube Ads 310"), Start.AddSeconds(1));

        Assert.True(observation.Changed);
        Assert.Equal(BreakMusicPlayback.Playing, observation.Playback);
        Assert.Null(_tracker.Track);
    }

    // On macOS the page itself says an advert is on, and the advert carries a real-looking title.
    [Fact]
    public void Observe_AdvertTheBackendMarked_ReportsPlayingWithNoTrackWhateverItIsTitled()
    {
        _tracker.Observe(SongA, Start);

        var observation = _tracker.Observe(
            new SessionSnapshot(SessionPlayback.Playing, "Ebenezer | Official Trailer 2", "Paramount Pictures", IsAdvert: true),
            Start.AddSeconds(1));

        Assert.True(observation.Changed);
        Assert.Equal(BreakMusicPlayback.Playing, observation.Playback);
        Assert.Null(_tracker.Track);
    }

    [Fact]
    public void Observe_PausedWithNoRequest_IsUnexpected()
    {
        _tracker.Observe(SongA, Start);

        var observation = _tracker.Observe(SongA with { Playback = SessionPlayback.Paused }, Start.AddMinutes(45));

        Assert.True(observation.UnexpectedPause);
    }

    [Fact]
    public void Observe_PausedJustAfterARequest_IsExpected()
    {
        _tracker.Observe(SongA, Start);
        _tracker.NotePauseRequested(Start.AddSeconds(10));

        var observation = _tracker.Observe(SongA with { Playback = SessionPlayback.Paused }, Start.AddSeconds(10).AddMilliseconds(50));

        Assert.False(observation.UnexpectedPause);
    }

    [Fact]
    public void Observe_PausedLongAfterARequest_IsUnexpected()
    {
        _tracker.Observe(SongA, Start);
        _tracker.NotePauseRequested(Start);

        var observation = _tracker.Observe(SongA with { Playback = SessionPlayback.Paused }, Start + SessionTracker.OwnPauseWindow + TimeSpan.FromMilliseconds(1));

        Assert.True(observation.UnexpectedPause);
    }

    [Fact]
    public void Observe_PausedAfterAPlayCancelledTheRequest_IsUnexpected()
    {
        _tracker.Observe(SongA, Start);
        _tracker.NotePauseRequested(Start);
        _tracker.NotePlayRequested();

        var observation = _tracker.Observe(SongA with { Playback = SessionPlayback.Paused }, Start.AddMilliseconds(50));

        Assert.True(observation.UnexpectedPause);
    }

    [Fact]
    public void Observe_PausedFromPaused_IsNotAnotherPause()
    {
        _tracker.Observe(SongA with { Playback = SessionPlayback.Paused }, Start);

        Assert.False(_tracker.Observe(SongA with { Playback = SessionPlayback.Paused }, Start.AddMinutes(1)).UnexpectedPause);
    }

    [Fact]
    public void PauseRequestedSince_RequestAfterTheMark_IsTrue_RequestBefore_IsFalse()
    {
        _tracker.NotePauseRequested(Start);

        Assert.True(_tracker.PauseRequestedSince(Start));
        Assert.False(_tracker.PauseRequestedSince(Start.AddMilliseconds(1)));
    }
}
