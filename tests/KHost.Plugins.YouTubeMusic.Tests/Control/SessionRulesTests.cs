using KHost.Plugins.YouTubeMusic.Control;

namespace KHost.Plugins.YouTubeMusic.Tests.Control;

public class SessionRulesTests
{
    [Theory]
    [InlineData("music.youtube.com-8A3F1C2B!App")]
    [InlineData("MUSIC.YOUTUBE.COM-anything-else!app")]
    public void IsYouTubeMusicApp_InstalledAppId_IsOurs(string id)
        => Assert.True(SessionRules.IsYouTubeMusicApp(id));

    // MSEdge is any tab in any Edge window: driving it would pause whatever the host was watching.
    [Theory]
    [InlineData("MSEdge")]
    [InlineData("music.youtube.com-8A3F1C2B")]
    [InlineData("www.youtube.com-8A3F1C2B!App")]
    [InlineData("Spotify.exe")]
    [InlineData(null)]
    public void IsYouTubeMusicApp_AnythingElse_IsNotOurs(string? id)
        => Assert.False(SessionRules.IsYouTubeMusicApp(id));

    [Theory]
    [InlineData("Video Ad", "Some Brand")]
    [InlineData(" video ad ", null)]
    [InlineData("Summer Sale", "YouTube Ads 310")]
    public void IsAdvert_GenericAdvert_IsDetected(string title, string? artist)
        => Assert.True(SessionRules.IsAdvert(title, artist));

    [Theory]
    [InlineData("Video Killed the Radio Star", "The Buggles")]
    [InlineData("Advertising Space", "Robbie Williams")]
    [InlineData("Song", null)]
    public void IsAdvert_Song_IsNotAnAdvert(string title, string? artist)
        => Assert.False(SessionRules.IsAdvert(title, artist));
}
