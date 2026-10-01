using KHost.Plugins.YouTubeMusic.Mac;
using System.Runtime.Versioning;

namespace KHost.Plugins.YouTubeMusic.Tests.Mac;

[SupportedOSPlatform("macos")]
public class AppleEventsTests
{
    // The codes in Chrome's scripting.sdef are four ASCII bytes, big-endian.
    [Theory]
    [InlineData("CrSu", 0x43725375u)]
    [InlineData("ExJa", 0x45784A61u)]
    [InlineData("----", 0x2D2D2D2Du)]
    public void Code_PacksFourCharactersBigEndian(string code, uint expected)
    {
        Assert.Equal(expected, AppleEvents.Code(code));
    }

    [Fact]
    public void Code_NotFourCharacters_Throws()
    {
        Assert.Throws<ArgumentException>(() => AppleEvents.Code("abc"));
    }
}
