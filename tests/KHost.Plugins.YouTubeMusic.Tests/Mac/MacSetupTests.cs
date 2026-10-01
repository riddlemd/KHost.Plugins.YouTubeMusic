using KHost.Plugins.YouTubeMusic.Control;
using KHost.Plugins.YouTubeMusic.Mac;

namespace KHost.Plugins.YouTubeMusic.Tests.Mac;

public class MacSetupTests
{
    [Theory]
    [InlineData(false, true, SetupStatus.Ready)]
    [InlineData(false, false, SetupStatus.ReadyWithoutApp)]
    [InlineData(true, true, SetupStatus.NotPermitted)]
    [InlineData(true, false, SetupStatus.NotPermitted)]
    public void StatusFor_ConsentBeforeTheApp(bool notPermitted, bool appInstalled, SetupStatus expected)
        => Assert.Equal(expected, MacSetup.StatusFor(notPermitted, appInstalled));
}
