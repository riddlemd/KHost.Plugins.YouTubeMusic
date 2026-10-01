using KHost.Plugins.YouTubeMusic.Control;

namespace KHost.Plugins.YouTubeMusic.Tests.Control;

public class SignInWatchTests
{
    private readonly SignInWatch _watch = new();

    [Fact]
    public void Observe_SignedOutWhileWanted_FiresOnceUntilSignedInAgain()
    {
        _watch.Observe(true, wanted: true);

        Assert.True(_watch.Observe(false, wanted: true));
        Assert.False(_watch.Observe(false, wanted: true));

        _watch.Observe(true, wanted: true);
        Assert.True(_watch.Observe(false, wanted: true));
    }

    [Fact]
    public void Observe_NeverSignedIn_NeverFires()
        => Assert.False(_watch.Observe(false, wanted: true));

    [Fact]
    public void Observe_SignedOutWhileIdle_FiresAtTheNextWantedRead()
    {
        _watch.Observe(true, wanted: false);

        Assert.False(_watch.Observe(false, wanted: false));
        Assert.True(_watch.Observe(false, wanted: true));
    }

    // A read that cannot tell (the page mid-load, Windows always) is not a sign-out.
    [Fact]
    public void Observe_Unknown_NeitherFiresNorForgets()
    {
        _watch.Observe(true, wanted: true);

        Assert.False(_watch.Observe(null, wanted: true));
        Assert.True(_watch.SignedIn);
        Assert.True(_watch.Observe(false, wanted: true));
    }
}
