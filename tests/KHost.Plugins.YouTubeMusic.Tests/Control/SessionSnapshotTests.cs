using KHost.Plugins.YouTubeMusic.Control;

namespace KHost.Plugins.YouTubeMusic.Tests.Control;

public class SessionSnapshotTests
{
    private static readonly DateTimeOffset Reported = new(2026, 10, 1, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PositionAt_Playing_CarriesThePlayheadForwardFromTheReport()
    {
        var snapshot = new SessionSnapshot(SessionPlayback.Playing, "A", Position: TimeSpan.FromSeconds(30), LastUpdated: Reported, Duration: TimeSpan.FromMinutes(3));

        Assert.Equal(TimeSpan.FromSeconds(42), snapshot.PositionAt(Reported.AddSeconds(12)));
    }

    [Fact]
    public void PositionAt_Paused_HoldsWhereItWasReported()
    {
        var snapshot = new SessionSnapshot(SessionPlayback.Paused, "A", Position: TimeSpan.FromSeconds(30), LastUpdated: Reported, Duration: TimeSpan.FromMinutes(3));

        Assert.Equal(TimeSpan.FromSeconds(30), snapshot.PositionAt(Reported.AddSeconds(12)));
    }

    [Fact]
    public void PositionAt_PastTheEnd_StopsAtTheDuration()
    {
        var snapshot = new SessionSnapshot(SessionPlayback.Playing, "A", Position: TimeSpan.FromSeconds(170), LastUpdated: Reported, Duration: TimeSpan.FromMinutes(3));

        Assert.Equal(TimeSpan.FromMinutes(3), snapshot.PositionAt(Reported.AddSeconds(60)));
    }

    [Fact]
    public void PositionAt_ClockBehindTheReport_NeverRewinds()
    {
        var snapshot = new SessionSnapshot(SessionPlayback.Playing, "A", Position: TimeSpan.FromSeconds(30), LastUpdated: Reported, Duration: TimeSpan.FromMinutes(3));

        Assert.Equal(TimeSpan.FromSeconds(30), snapshot.PositionAt(Reported.AddSeconds(-5)));
    }
}
