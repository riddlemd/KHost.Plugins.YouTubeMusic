namespace KHost.Plugins.YouTubeMusic.Tests;

public class PlaylistUrlTests
{
    [Theory]
    [InlineData("https://music.youtube.com/playlist?list=PLabc123_-x&si=SHARE", "PLabc123_-x")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ&list=OLAK5uy_abc", "OLAK5uy_abc")]
    [InlineData("https://www.youtube.com/playlist?list=PLxyz", "PLxyz")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?list=RDCLAK5uy_q", "RDCLAK5uy_q")]
    [InlineData("https://music.youtube.com/browse/VLPLbrowse1", "PLbrowse1")]
    [InlineData("  PLbare  ", "PLbare")]
    [InlineData("VLPLprefixed", "PLprefixed")]
    [InlineData("LM", "LM")]
    public void Normalize_PlaylistLink_ReturnsAWatchUrlThatStartsTheList(string input, string listId)
        => Assert.Equal($"https://music.youtube.com/watch?list={listId}", PlaylistUrl.Normalize(input));

    // A single track ends the bed after one song, and nothing outside the page can queue another.
    [Theory]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M?list=PLevil")]
    [InlineData("wss://music.youtube.com/playlist?list=PLabc")]
    [InlineData("https://music.youtube.com/playlist?list=not a list")]
    [InlineData("dQw4w9WgXcQ")]
    [InlineData("")]
    [InlineData(null)]
    public void Normalize_NotAPlaylist_ReturnsNull(string? input)
        => Assert.Null(PlaylistUrl.Normalize(input));
}
