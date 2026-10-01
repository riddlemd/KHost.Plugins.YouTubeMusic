using KHost.Plugins.YouTubeMusic.Control;
using KHost.Plugins.YouTubeMusic.Helper;
using Microsoft.Extensions.Logging.Abstractions;

namespace KHost.Plugins.YouTubeMusic.Tests.Helper;

public sealed class HelperYouTubeMusicControllerTests : IAsyncDisposable
{
    private const string List = "https://music.youtube.com/watch?list=PLbed";

    private readonly FakeTransport _transport = new();
    private readonly List<FakeHelper> _helpers = [];
    private bool _dataStoreExists;

    private HelperYouTubeMusicController Build()
        => new(NullLogger.Instance, _transport, () => _dataStoreExists, new ManualClock());

    private FakeHelper NewHelper()
    {
        var helper = new FakeHelper();
        _helpers.Add(helper);
        return helper;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var helper in _helpers)
            await helper.DisposeAsync();
    }

    [Fact]
    public void Unavailable_NoAppToLaunch_SaysSoAndReportsHelperMissing()
    {
        _transport.AppAvailable = false;

        var controller = Build();

        Assert.NotNull(controller.Unavailable);
        Assert.Equal(SetupStatus.HelperMissing, controller.GetSetupStatus());
    }

    // Reads poll once a second for the whole night: they must never be what opens the app.
    [Fact]
    public async Task ReadAsync_NothingUp_IsNoSessionAndLaunchesNothing()
    {
        Assert.Equal(SessionSnapshot.None, await Build().ReadAsync());
        Assert.Empty(_transport.Launches);
    }

    [Fact]
    public async Task ReadAsync_PageAnswers_IsTheSnapshotWithTheSignIn()
    {
        var helper = NewHelper();
        _transport.Existing = helper.Connection;

        var snapshot = await Build().ReadAsync();

        Assert.Equal(SessionPlayback.Playing, snapshot!.Playback);
        Assert.Equal("Hello", snapshot.Title);
        Assert.True(snapshot.SignedIn);
    }

    [Fact]
    public async Task ReadAsync_PageSaysSignedOut_SetupStatusFollows()
    {
        _dataStoreExists = true;
        var helper = NewHelper();
        helper.State = """{"page":true,"state":-1,"title":"","signedIn":false}""";
        _transport.Existing = helper.Connection;
        var controller = Build();
        Assert.Equal(SetupStatus.Ready, controller.GetSetupStatus());

        await controller.ReadAsync();

        Assert.Equal(SetupStatus.NotSignedIn, controller.GetSetupStatus());
    }

    // Still up but silent is "could not look", which the provider treats differently from "nothing there".
    [Fact]
    public async Task ReadAsync_PageCannotBeRead_IsNull()
    {
        var helper = NewHelper();
        helper.Answer = (_, request) => $$$"""{"id":{{{request.GetProperty("id").GetInt64()}}},"ok":false,"error":"JavaScript exception"}""";
        _transport.Existing = helper.Connection;

        Assert.Null(await Build().ReadAsync());
    }

    [Fact]
    public async Task LaunchAppAsync_NothingUp_StartsBehindAtTheListHeldAtSilence()
    {
        _transport.LaunchWith = _ => NewHelper().Connection;

        Assert.True(await Build().LaunchAppAsync(List));

        Assert.Equal([new HelperLaunch(Background: true, List, InitialLevel: 0)], _transport.Launches);
    }

    // A second copy would be a second player; the one up is pointed at the list instead.
    [Fact]
    public async Task LaunchAppAsync_AlreadyUp_LoadsTheListIntoIt()
    {
        var helper = NewHelper();
        _transport.Existing = helper.Connection;

        Assert.True(await Build().LaunchAppAsync(List));

        Assert.Empty(_transport.Launches);
        Assert.Equal([HelperCommand.Load], helper.Commands);
        Assert.Equal(List, helper.Requests.Single().GetProperty("url").GetString());
        Assert.Equal(0, helper.Requests.Single().GetProperty("value").GetDouble());
    }

    // The copy launched found one already up (opened from the Dock) and left: drive that one.
    [Fact]
    public async Task LaunchAppAsync_LaunchFindsOneUp_DrivesThatOne()
    {
        var helper = NewHelper();
        _transport.LaunchWith = _ =>
        {
            _transport.Existing = helper.Connection;
            return null;
        };

        Assert.True(await Build().LaunchAppAsync(List));
        Assert.Single(_transport.Launches);
        Assert.Equal([HelperCommand.Load], helper.Commands);
    }

    [Fact]
    public async Task LaunchAppAsync_NotAMusicLink_LaunchesAtHome()
    {
        _transport.LaunchWith = _ => NewHelper().Connection;

        await Build().LaunchAppAsync("https://evil.example/watch?list=PL");

        Assert.Null(_transport.Launches.Single().Url);
    }

    [Fact]
    public async Task OpenSetupAsync_ShowsTheWindowInFrontAtHome()
    {
        var helper = NewHelper();
        _transport.LaunchWith = _ => helper.Connection;

        Assert.True(await Build().OpenSetupAsync());

        Assert.False(_transport.Launches.Single().Background);
        Assert.Equal([HelperCommand.Show], helper.Commands);
        Assert.True(helper.Requests.Single().GetProperty("home").GetBoolean());
    }

    [Fact]
    public async Task ShowAppAsync_Up_ShowsWithoutGoingHome()
    {
        var helper = NewHelper();
        _transport.Existing = helper.Connection;

        Assert.True(await Build().ShowAppAsync());

        Assert.False(helper.Requests.Single().GetProperty("home").GetBoolean());
    }

    [Fact]
    public async Task PlayAsync_NothingUp_FailsWithoutLaunching()
    {
        Assert.False(await Build().PlayAsync());
        Assert.Empty(_transport.Launches);
    }

    [Fact]
    public async Task SetLevelAsync_SendsTheLevelClampedAndRounded()
    {
        var helper = NewHelper();
        _transport.Existing = helper.Connection;
        var controller = Build();

        Assert.True(await controller.SetLevelAsync(0.123456f));
        Assert.True(await controller.SetLevelAsync(3f));

        Assert.Equal([0.1235, 1.0], helper.Requests.Select(request => request.GetProperty("value").GetDouble()));
    }

    // The page answering {"ok":false} (no player yet) is a command not done, whatever the helper said.
    [Fact]
    public async Task PauseAsync_PageHasNoPlayer_IsFalse()
    {
        var helper = NewHelper();
        helper.Answer = (_, request) => $$$"""{"id":{{{request.GetProperty("id").GetInt64()}}},"ok":true,"result":{"page":true,"ok":false}}""";
        _transport.Existing = helper.Connection;

        Assert.False(await Build().PauseAsync());
    }

    [Fact]
    public async Task HelperQuits_SessionChangedIsRaisedAndTheNextReadIsNoSession()
    {
        var helper = NewHelper();
        _transport.Existing = helper.Connection;
        var controller = Build();
        await controller.ReadAsync();
        var changed = new TaskCompletionSource();
        controller.SessionChanged += (_, _) => changed.TrySetResult();
        _transport.Existing = null;

        helper.Quit();

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SessionSnapshot.None, await controller.ReadAsync());
    }

    [Fact]
    public async Task PollOnceAsync_PlayingAndKeepAliveDue_MarksThePageActive()
    {
        var helper = NewHelper();
        _transport.Existing = helper.Connection;

        await Build().PollOnceAsync(CancellationToken.None);

        Assert.Equal([HelperCommand.State, HelperCommand.KeepAlive], helper.Commands);
    }

    // A paused bed is the host's choice; the keepalive would be the only thing touching the page.
    [Fact]
    public async Task PollOnceAsync_Paused_SendsNoKeepAlive()
    {
        var helper = NewHelper();
        helper.State = """{"page":true,"state":2,"paused":true,"title":"Hello"}""";
        _transport.Existing = helper.Connection;

        await Build().PollOnceAsync(CancellationToken.None);

        Assert.Equal([HelperCommand.State], helper.Commands);
    }

    private sealed class FakeTransport : IHelperTransport
    {
        public bool AppAvailable { get; set; } = true;

        public bool IsRunning => Existing is { IsOpen: true };

        public HelperConnection? Existing { get; set; }

        public Func<HelperLaunch, HelperConnection?>? LaunchWith { get; set; }

        public List<HelperLaunch> Launches { get; } = [];

        public Task<HelperConnection?> ConnectExistingAsync(CancellationToken cancellationToken)
            => Task.FromResult(Existing is { IsOpen: true } ? Existing : null);

        public Task<HelperConnection?> LaunchAsync(HelperLaunch launch, CancellationToken cancellationToken)
        {
            Launches.Add(launch);
            return Task.FromResult(LaunchWith?.Invoke(launch));
        }
    }
}
