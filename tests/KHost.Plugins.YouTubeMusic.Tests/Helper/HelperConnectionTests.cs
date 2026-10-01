using KHost.Plugins.YouTubeMusic.Helper;

namespace KHost.Plugins.YouTubeMusic.Tests.Helper;

public class HelperConnectionTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task WaitForHelloAsync_HelperSaysHello_ReadsIt()
    {
        await using var helper = new FakeHelper();

        var hello = await helper.Connection.WaitForHelloAsync(Wait);

        Assert.Equal(new HelperHello(1, "1.0.0", "plugin", 4242), hello);
    }

    // A copy that found another up says "busy" and exits; the caller then connects to that one.
    [Fact]
    public async Task WaitForHelloAsync_Busy_IsNullAndSaysSo()
    {
        await using var helper = new FakeHelper(sayHello: false);
        helper.Send("""{"event":"busy"}""");

        Assert.Null(await helper.Connection.WaitForHelloAsync(Wait));
        Assert.True(helper.Connection.WasBusy);
    }

    // Replies are matched by id, not by order: a slow state read must not take a play's answer.
    [Fact]
    public async Task SendAsync_RepliesOutOfOrder_EachGetsItsOwn()
    {
        await using var helper = new FakeHelper();
        var held = new TaskCompletionSource<long>();
        helper.Answer = (command, request) =>
        {
            var id = request.GetProperty("id").GetInt64();

            if (command == HelperCommand.State)
            {
                held.SetResult(id);
                return null;
            }

            helper.Send($$$"""{"id":{{{id}}},"ok":false,"error":"second"}""");
            helper.Send($$$"""{"id":{{{held.Task.Result}}},"ok":true,"result":{"page":true}}""");
            return null;
        };

        var first = helper.Connection.SendAsync(HelperCommand.State, Wait);
        await held.Task;
        var second = await helper.Connection.SendAsync(HelperCommand.Play, Wait);

        Assert.Equal("second", second!.Error);
        Assert.True((await first)!.Ok);
    }

    [Fact]
    public async Task SendAsync_NoAnswerInTime_IsNullAndStaysOpen()
    {
        await using var helper = new FakeHelper();
        helper.Answer = (_, _) => null;

        Assert.Null(await helper.Connection.SendAsync(HelperCommand.State, TimeSpan.FromMilliseconds(100)));
        Assert.True(helper.Connection.IsOpen);
    }

    [Fact]
    public async Task HelperQuits_PendingRequestEndsNullAndClosedIsRaised()
    {
        await using var helper = new FakeHelper();
        var closed = new TaskCompletionSource();
        helper.Connection.Closed += (_, _) => closed.TrySetResult();
        helper.Answer = (_, _) =>
        {
            helper.Quit();
            return null;
        };

        // A minute to answer, five seconds to be released: only the close can end it in time.
        var reply = await helper.Connection.SendAsync(HelperCommand.State, TimeSpan.FromMinutes(1)).WaitAsync(Wait);

        Assert.Null(reply);
        await closed.Task.WaitAsync(Wait);
        Assert.False(helper.Connection.IsOpen);
    }

    // WebKit and AppKit write to stdout too; a line that is not a message must not end the connection.
    [Fact]
    public async Task ReadLoop_NonMessageLine_IsSkipped()
    {
        await using var helper = new FakeHelper();
        helper.Send("objc[1]: Class Foo is implemented in both");

        Assert.True((await helper.Connection.SendAsync(HelperCommand.Play, Wait))!.Ok);
    }
}
