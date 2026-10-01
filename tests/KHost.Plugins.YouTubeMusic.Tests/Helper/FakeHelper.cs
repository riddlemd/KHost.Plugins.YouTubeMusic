using KHost.Plugins.YouTubeMusic.Helper;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace KHost.Plugins.YouTubeMusic.Tests.Helper;

/// <summary>Stands in for the helper app at the far end of real pipes, answering the way
/// main.swift does, so a test drives <see cref="HelperConnection"/> over actual streams.</summary>
internal sealed class FakeHelper : IAsyncDisposable
{
    private readonly AnonymousPipeServerStream _toPlugin = new(PipeDirection.Out);
    private readonly AnonymousPipeServerStream _fromPlugin = new(PipeDirection.In);
    private readonly AnonymousPipeClientStream _pluginReads;
    private readonly AnonymousPipeClientStream _pluginWrites;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public FakeHelper(bool sayHello = true)
    {
        _pluginReads = new AnonymousPipeClientStream(PipeDirection.In, _toPlugin.ClientSafePipeHandle);
        _pluginWrites = new AnonymousPipeClientStream(PipeDirection.Out, _fromPlugin.ClientSafePipeHandle);
        Connection = new HelperConnection(_pluginReads, _pluginWrites, NullLogger.Instance);

        if (sayHello)
            Send("""{"event":"hello","protocol":1,"version":"1.0.0","owner":"plugin","pid":4242}""");

        _ = Task.Run(ServeAsync);
        Connection.Start();
    }

    public HelperConnection Connection { get; }

    /// <summary>Every request as it arrived, in order.</summary>
    public ConcurrentQueue<JsonElement> Requests { get; } = new();

    public IEnumerable<string> Commands => Requests.Select(request => request.GetProperty("cmd").GetString()!);

    /// <summary>What "state" answers with: the page script's own JSON.</summary>
    public string State { get; set; } = """{"page":true,"player":true,"state":1,"paused":false,"title":"Hello","artist":"Adele","signedIn":true}""";

    /// <summary>Replaces the default answer: given the command and the request, the whole reply line,
    /// or null to say nothing.</summary>
    public Func<string, JsonElement, string?>? Answer { get; set; }

    public void Send(string line)
    {
        _writeGate.Wait();

        try
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            _toPlugin.Write(bytes);
            _toPlugin.Flush();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>The helper quitting: its output closes, which the plugin reads as the end.</summary>
    public void Quit() => _toPlugin.Dispose();

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync();
        _toPlugin.Dispose();
        _fromPlugin.Dispose();
    }

    private async Task ServeAsync()
    {
        try
        {
            using var reader = new StreamReader(_fromPlugin, Encoding.UTF8);

            while (await reader.ReadLineAsync() is { } line)
            {
                var request = JsonDocument.Parse(line).RootElement.Clone();
                Requests.Enqueue(request);

                var command = request.GetProperty("cmd").GetString()!;
                var reply = Answer is { } answer ? answer(command, request) : DefaultAnswer(command, request);

                if (reply is not null)
                    Send(reply);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The plugin's end closed.
        }
    }

    private string DefaultAnswer(string command, JsonElement request)
    {
        var id = request.GetProperty("id").GetInt64();

        return command switch
        {
            HelperCommand.State => $$$"""{"id":{{{id}}},"ok":true,"result":{{{State}}}}""",
            HelperCommand.Play or HelperCommand.Pause or HelperCommand.Next or HelperCommand.Level or HelperCommand.KeepAlive
                => $$$"""{"id":{{{id}}},"ok":true,"result":{"page":true,"ok":true}}""",
            _ => $$$"""{"id":{{{id}}},"ok":true}""",
        };
    }
}
