using KHost.Plugins.YouTubeMusic.Edge;
using Microsoft.Extensions.Logging;

namespace KHost.Plugins.YouTubeMusic.Tests.Edge;

public class EdgeOutputTests
{
    private readonly RecordingLogger _logger = new();

    [Fact]
    public async Task DrainAsync_EveryLine_IsLoggedAtDebugOnly()
    {
        await EdgeOutput.DrainAsync(
            new StringReader("[1234:ERROR:gpu_init.cc(1)] Passthrough is not supported\nsecond line\n"), _logger, "stderr");

        Assert.Equal(
            ["Edge stderr: [1234:ERROR:gpu_init.cc(1)] Passthrough is not supported", "Edge stderr: second line"],
            _logger.Entries.Select(e => e.Message));
        Assert.All(_logger.Entries, e => Assert.Equal(LogLevel.Debug, e.Level));
    }

    [Fact]
    public async Task DrainAsync_BlankLines_AreSkipped()
    {
        await EdgeOutput.DrainAsync(new StringReader("\n  \nreal\n"), _logger, "stdout");

        Assert.Equal(["Edge stdout: real"], _logger.Entries.Select(e => e.Message));
    }

    // A pipe that breaks under it must not fault the launch's continuation.
    [Fact]
    public async Task DrainAsync_ReaderFails_EndsQuietlyAtDebug()
    {
        await EdgeOutput.DrainAsync(new FailingReader(), _logger, "stderr");

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
    }

    private sealed class FailingReader : TextReader
    {
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
            => ValueTask.FromException<string?>(new IOException("The pipe has been ended."));

        public override Task<string?> ReadLineAsync() => throw new IOException("The pipe has been ended.");
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
