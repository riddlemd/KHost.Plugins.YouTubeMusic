using System.Text.Json;
using System.Text.Json.Serialization;

namespace KHost.Plugins.YouTubeMusic.Helper;

/// <summary>The commands the helper answers. Names cross into Swift and JavaScript as strings.</summary>
public static class HelperCommand
{
    public const string Hello = "hello";
    public const string State = "state";
    public const string Play = "play";
    public const string Pause = "pause";
    public const string Next = "next";
    public const string Level = "level";
    public const string KeepAlive = "keepAlive";
    public const string Load = "load";
    public const string Show = "show";
    public const string Hide = "hide";
    public const string Quit = "quit";
}

/// <summary>What the helper said on connecting.</summary>
/// <param name="Owner">"plugin" when a host launched it (and it quits with that host), "user" when
/// it was opened from the Dock or Finder.</param>
public sealed record HelperHello(int Protocol, string? Version, string? Owner, int Pid);

/// <summary>The answer to one request.</summary>
/// <param name="Result">The page script's own answer, for the commands it runs.</param>
public sealed record HelperReply(long Id, bool Ok, string? Error, JsonElement? Result);

/// <summary>One line from the helper: a reply, or an event (<see cref="Event"/> set).</summary>
public sealed record HelperMessage(string? Event, HelperReply? Reply, HelperHello? Hello);

/// <summary>JSON lines between the plugin and the helper app: one object per line, each way.</summary>
public static class HelperProtocol
{
    /// <summary>Moves only when a line changes meaning; a helper answering another number is not
    /// driven.</summary>
    public const int Version = 1;

    public const string HelloEvent = "hello";

    /// <summary>Sent by a copy that found another already up, just before it exits.</summary>
    public const string BusyEvent = "busy";

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <returns>One line, without its newline.</returns>
    public static string EncodeRequest(long id, string command, double? value = null, string? url = null, bool? home = null)
        => JsonSerializer.Serialize(new Request(id, command, value, url, home), Options);

    /// <summary>Null when <paramref name="line"/> is not a message at all; a helper writing anything
    /// else to its output (a framework's own chatter) is not a reason to drop the connection.</summary>
    public static HelperMessage? Decode(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (String(root, "event") is { } name)
            {
                var hello = name == HelloEvent
                    ? new HelperHello(Int(root, "protocol") ?? 0, String(root, "version"), String(root, "owner"), Int(root, "pid") ?? 0)
                    : null;

                return new HelperMessage(name, null, hello);
            }

            // TryGetInt64 throws rather than answering false for a string.
            if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number
                || !idElement.TryGetInt64(out var id))
                return null;

            var ok = root.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
            JsonElement? result = root.TryGetProperty("result", out var resultElement) ? resultElement.Clone() : null;

            return new HelperMessage(null, new HelperReply(id, ok, String(root, "error"), result), null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? String(JsonElement root, string name)
        => root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private static int? Int(JsonElement root, string name)
        => root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number
           && element.TryGetInt32(out var value) ? value : null;

    private sealed record Request(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("cmd")] string Command,
        [property: JsonPropertyName("value")] double? Value,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("home")] bool? Home);
}
