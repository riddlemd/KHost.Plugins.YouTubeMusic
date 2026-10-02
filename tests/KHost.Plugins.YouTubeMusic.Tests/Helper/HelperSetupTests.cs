using KHost.Plugins.YouTubeMusic.Control;
using KHost.Plugins.YouTubeMusic.Helper;

namespace KHost.Plugins.YouTubeMusic.Tests.Helper;

public class HelperSetupTests
{
    [Theory]
    [InlineData(false, true, true, SetupStatus.HelperMissing)]
    [InlineData(true, true, false, SetupStatus.Ready)]
    [InlineData(true, false, true, SetupStatus.NotSignedIn)]
    // Before the page has said: a store never made is a sign-in that never happened.
    [InlineData(true, null, false, SetupStatus.NotSignedIn)]
    [InlineData(true, null, true, SetupStatus.Ready)]
    public void StatusFor_MapsAppSignInAndStore(bool app, bool? signedIn, bool store, SetupStatus expected)
        => Assert.Equal(expected, HelperSetup.StatusFor(app, signedIn, store));

    // Separators compared as one: the path is only ever used on macOS, but Path.Combine joins with
    // the running platform's, and the suite runs on Windows too.
    [Fact]
    public void SocketPath_UnderTheAppsOwnCachesFolder()
        => Assert.Equal(
            "/Users/a/Library/Caches/com.khost.youtube-music-helper/khost.sock",
            HelperApp.SocketPath("/Users/a")?.Replace('\\', '/'));

    // sockaddr_un: 104 bytes with the terminator. Longer and bind() fails, so there is no socket.
    [Fact]
    public void SocketPath_TooLongForASocket_IsNull()
    {
        var fits = "/" + new string('u', 103 - "/Library/Caches/com.khost.youtube-music-helper/khost.sock".Length - 1);

        Assert.NotNull(HelperApp.SocketPath(fits));
        Assert.Null(HelperApp.SocketPath(fits + "u"));
    }

    [Fact]
    public void Paths_AgreeWithTheBuildScript()
    {
        Assert.Equal(Path.Combine("bin", "khost.youtube-music", "YouTube Music.app"), HelperApp.InstalledApp("bin"));
        Assert.Equal(Path.Combine("p", "helper", "YouTube Music.app"), HelperApp.ShippedApp("p"));
        Assert.Equal(Path.Combine("a.app", "Contents", "MacOS", "khost-youtube-music-helper"), HelperApp.ExecutableIn("a.app"));
        Assert.Equal(Path.Combine("/h", "Library", "WebKit", "com.khost.youtube-music-helper"), HelperApp.DataStoreDirectory("/h"));
    }
}
