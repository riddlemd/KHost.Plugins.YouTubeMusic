using KHost.Plugins.YouTubeMusic.Edge;

namespace KHost.Plugins.YouTubeMusic.Tests.Edge;

public class ForegroundReturnTests
{
    private const nint Screen = 0x100;
    private const nint EdgeApp = 0x200;
    private const nint Notepad = 0x300;

    private readonly Queue<nint> _foreground = new();
    private readonly List<nint> _broughtToFront = [];
    private int _polls;

    private Task<bool> HandBackAsync(nint before, bool bringSucceeds = true) => ForegroundReturn.HandBackAsync(
        before,
        () => _foreground.Count > 1 ? _foreground.Dequeue() : _foreground.Peek(),
        window => window == EdgeApp,
        window =>
        {
            _broughtToFront.Add(window);
            return bringSucceeds;
        },
        (_, _) =>
        {
            _polls++;
            return Task.CompletedTask;
        });

    // The karaoke screen was in front; the relaunched app window came up over it.
    [Fact]
    public async Task HandBackAsync_EdgeTakesTheForeground_GivesItBack()
    {
        _foreground.Enqueue(Screen);
        _foreground.Enqueue(Screen);
        _foreground.Enqueue(EdgeApp);

        Assert.True(await HandBackAsync(Screen));
        Assert.Equal([Screen], _broughtToFront);
    }

    [Fact]
    public async Task HandBackAsync_NoWindowBetweenTheTwo_KeepsWatching()
    {
        _foreground.Enqueue(0);
        _foreground.Enqueue(EdgeApp);

        Assert.True(await HandBackAsync(Screen));
        Assert.Equal([Screen], _broughtToFront);
    }

    // The host switched to something of their own: theirs to keep.
    [Fact]
    public async Task HandBackAsync_SomethingElseTakesIt_LeavesItAlone()
    {
        _foreground.Enqueue(Notepad);
        _foreground.Enqueue(EdgeApp);

        Assert.False(await HandBackAsync(Screen));
        Assert.Empty(_broughtToFront);
    }

    [Fact]
    public async Task HandBackAsync_NothingWasInFront_DoesNotWatch()
    {
        _foreground.Enqueue(EdgeApp);

        Assert.False(await HandBackAsync(0));
        Assert.Equal(0, _polls);
        Assert.Empty(_broughtToFront);
    }

    [Fact]
    public async Task HandBackAsync_EdgeNeverTakesIt_StopsAfterTheWatch()
    {
        _foreground.Enqueue(Screen);

        Assert.False(await HandBackAsync(Screen));
        Assert.Equal((int)(ForegroundReturn.Watch / ForegroundReturn.Poll), _polls);
        Assert.Empty(_broughtToFront);
    }

    [Fact]
    public async Task HandBackAsync_WindowsRefuses_SaysSo()
    {
        _foreground.Enqueue(EdgeApp);

        Assert.False(await HandBackAsync(Screen, bringSucceeds: false));
        Assert.Equal([Screen], _broughtToFront);
    }
}
