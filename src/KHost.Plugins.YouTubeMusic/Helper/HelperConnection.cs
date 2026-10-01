using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text;

namespace KHost.Plugins.YouTubeMusic.Helper;

/// <summary>One conversation with the helper app over a pair of streams: its stdin and stdout when
/// this plugin launched it, or a Unix socket when it was already up. Replies are matched to
/// requests by id, so commands may overlap.</summary>
internal sealed class HelperConnection : IAsyncDisposable
{
    private readonly Stream _fromHelper;
    private readonly Stream _toHelper;
    private readonly ILogger _logger;
    private readonly IDisposable? _owner;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<HelperReply?>> _pending = new();
    private readonly TaskCompletionSource<HelperHello?> _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private long _nextId;
    private int _closed;

    /// <param name="owner">Disposed with the connection: the socket or the process handle. Disposing
    /// a process handle does not end the process; closing its stdin is what tells it to go.</param>
    public HelperConnection(Stream fromHelper, Stream toHelper, ILogger logger, IDisposable? owner = null)
    {
        _fromHelper = fromHelper;
        _toHelper = toHelper;
        _logger = logger;
        _owner = owner;
    }

    /// <summary>Raised once, from the reading thread, when the helper's end goes away.</summary>
    public event EventHandler? Closed;

    public bool IsOpen => Volatile.Read(ref _closed) == 0;

    /// <summary>True when the helper said another copy was already up, and exited.</summary>
    public bool WasBusy { get; private set; }

    public HelperHello? Hello => _hello.Task.IsCompletedSuccessfully ? _hello.Task.Result : null;

    public void Start() => _ = Task.Run(ReadLoopAsync);

    /// <returns>Null when the helper closed, said it was busy, or said nothing in time.</returns>
    public async Task<HelperHello?> WaitForHelloAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _hello.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <returns>Null when the helper did not answer in <paramref name="timeout"/> or went away.</returns>
    public async Task<HelperReply?> SendAsync(
        string command, TimeSpan timeout, double? value = null, string? url = null, bool? home = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsOpen)
            return null;

        var id = Interlocked.Increment(ref _nextId);
        var reply = new TaskCompletionSource<HelperReply?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;

        try
        {
            var line = Encoding.UTF8.GetBytes(HelperProtocol.EncodeRequest(id, command, value, url, home) + "\n");

            await _writeGate.WaitAsync(cancellationToken);

            try
            {
                await _toHelper.WriteAsync(line, cancellationToken);
                await _toHelper.FlushAsync(cancellationToken);
            }
            finally
            {
                _writeGate.Release();
            }

            return await reply.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            _logger.LogDebug("The YouTube Music app did not answer {Command} within {Timeout}", command, timeout);
            return null;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Could not send {Command} to the YouTube Music app", command);
            Close();
            return null;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            using var reader = new StreamReader(_fromHelper, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);

            while (await reader.ReadLineAsync(_closing.Token) is { } line)
            {
                if (HelperProtocol.Decode(line) is not { } message)
                {
                    _logger.LogDebug("YouTube Music app: {Line}", line);
                    continue;
                }

                if (message.Reply is { } reply)
                {
                    if (_pending.TryGetValue(reply.Id, out var waiting))
                        waiting.TrySetResult(reply);
                }
                else if (message.Event == HelperProtocol.HelloEvent)
                {
                    _hello.TrySetResult(message.Hello);
                }
                else if (message.Event == HelperProtocol.BusyEvent)
                {
                    WasBusy = true;
                    _hello.TrySetResult(null);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The far end went; handled below like an ordinary end of stream.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stopped reading the YouTube Music app's answers");
        }
        finally
        {
            Close();
        }
    }

    private void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;

        _closing.Cancel();
        _hello.TrySetResult(null);

        foreach (var waiting in _pending.Values)
            waiting.TrySetResult(null);

        // Closing what goes to the helper first: for a launched helper that is its stdin, its cue to quit.
        try { _toHelper.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        try { _fromHelper.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        _owner?.Dispose();

        Closed?.Invoke(this, EventArgs.Empty);
    }
}
