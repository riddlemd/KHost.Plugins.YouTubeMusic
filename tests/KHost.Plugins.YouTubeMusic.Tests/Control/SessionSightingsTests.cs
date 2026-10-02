using KHost.Plugins.YouTubeMusic.Control;
using Microsoft.Extensions.Logging;

namespace KHost.Plugins.YouTubeMusic.Tests.Control;

public class SessionSightingsTests
{
    private readonly SessionSightings _sightings = new();
    private readonly ManualClock _clock = new();

    [Fact]
    public void NoteFound_FirstFind_IsInformation()
        => Assert.Equal(LogLevel.Information, _sightings.NoteFound(_clock.GetUtcNow()));

    // The track-change blink: gone for about 250ms, then back.
    [Fact]
    public void NoteFound_SoonAfterALoss_IsDebug()
    {
        _sightings.NoteFound(_clock.GetUtcNow());
        _sightings.NoteLost(_clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMilliseconds(250));

        Assert.Equal(LogLevel.Debug, _sightings.NoteFound(_clock.GetUtcNow()));
    }

    [Fact]
    public void NoteFound_RightAtTheEdgeOfTheGap_IsDebug()
    {
        _sightings.NoteLost(_clock.GetUtcNow());
        _clock.Advance(SessionSightings.RoutineGap);

        Assert.Equal(LogLevel.Debug, _sightings.NoteFound(_clock.GetUtcNow()));
    }

    // The app closed and was opened again: worth a line at the default level.
    [Fact]
    public void NoteFound_LongAfterALoss_IsInformation()
    {
        _sightings.NoteLost(_clock.GetUtcNow());
        _clock.Advance(SessionSightings.RoutineGap + TimeSpan.FromMilliseconds(1));

        Assert.Equal(LogLevel.Information, _sightings.NoteFound(_clock.GetUtcNow()));
    }

    // One loss excuses one find; a second find with no loss between is not a blink.
    [Fact]
    public void NoteFound_TwiceAfterOneLoss_OnlyTheFirstIsDebug()
    {
        _sightings.NoteLost(_clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMilliseconds(250));
        _sightings.NoteFound(_clock.GetUtcNow());

        Assert.Equal(LogLevel.Information, _sightings.NoteFound(_clock.GetUtcNow()));
    }
}
