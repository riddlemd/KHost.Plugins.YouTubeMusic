using Microsoft.Extensions.Logging;

namespace KHost.Plugins.YouTubeMusic.Edge;

/// <summary>What Edge writes to its console. Left inherited, it lands in the host's own output and
/// so in the KHost log; it is Chromium's internal chatter, useful only when chasing an Edge fault.</summary>
public static class EdgeOutput
{
    /// <summary>Reads to the end, which is when Edge exits: a browser started here holds the pipe
    /// for its whole life. Never throws.</summary>
    public static async Task DrainAsync(TextReader reader, ILogger logger, string stream)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    logger.LogDebug("Edge {Stream}: {Line}", stream, line);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Stopped reading Edge's {Stream}", stream);
        }
    }
}
