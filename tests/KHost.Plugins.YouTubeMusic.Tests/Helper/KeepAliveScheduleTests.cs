using KHost.Plugins.YouTubeMusic.Helper;

namespace KHost.Plugins.YouTubeMusic.Tests.Helper;

public class KeepAliveScheduleTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IsDue_NeverSentWhilePlaying_IsDue()
        => Assert.True(new KeepAliveSchedule(TimeSpan.FromMinutes(5)).IsDue(Start, playing: true));

    // A paused bed is the host's choice; marking the page active would not unpause it, but there is
    // no prompt to head off either.
    [Fact]
    public void IsDue_NotPlaying_IsNeverDue()
        => Assert.False(new KeepAliveSchedule(TimeSpan.FromMinutes(5)).IsDue(Start, playing: false));

    [Fact]
    public void IsDue_InsideTheInterval_IsNotDue()
    {
        var schedule = new KeepAliveSchedule(TimeSpan.FromMinutes(5));
        schedule.MarkSent(Start);

        Assert.False(schedule.IsDue(Start + TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1), playing: true));
    }

    [Fact]
    public void IsDue_AtTheInterval_IsDue()
    {
        var schedule = new KeepAliveSchedule(TimeSpan.FromMinutes(5));
        schedule.MarkSent(Start);

        Assert.True(schedule.IsDue(Start + TimeSpan.FromMinutes(5), playing: true));
    }

    [Fact]
    public void DefaultInterval_IsWellInsideTheHourThePromptHasBeenReportedAt()
        => Assert.InRange(KeepAliveSchedule.DefaultInterval, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(15));

    [Fact]
    public void Constructor_NonPositiveInterval_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new KeepAliveSchedule(TimeSpan.Zero));
}
