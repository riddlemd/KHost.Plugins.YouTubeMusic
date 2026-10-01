using System.Text.Json;

namespace KHost.Plugins.YouTubeMusic.Mac;

/// <summary>The commands the page script answers. Names cross into JavaScript as strings.</summary>
public static class PageCommand
{
    public const string State = "state";
    public const string Play = "play";
    public const string Pause = "pause";
    public const string Next = "next";
    public const string Level = "level";
    public const string KeepAlive = "keepAlive";
    public const string Navigate = "navigate";
}

/// <summary>Builds the JavaScript sent to the page: the embedded <c>youtube-music-page.js</c> with
/// one command's arguments spliced in.</summary>
public static class PageScript
{
    private const string ResourceName = "KHost.Plugins.YouTubeMusic.Mac.youtube-music-page.js";
    private const string ArgumentsMarker = "/*ARGUMENTS*/";

    private static readonly Lazy<string> Template = new(ReadTemplate);

    /// <param name="appOnly">Answer only from an app window, so a YouTube Music tab left open in
    /// the setup window is never driven while the app window exists.</param>
    public static string For(string command, object? argument = null, bool appOnly = false)
        => Compose(Template.Value, command, argument, appOnly);

    /// <summary>Arguments go in as JSON literals, so nothing a setting holds can break out of them.</summary>
    public static string Compose(string template, string command, object? argument, bool appOnly)
    {
        var index = template.LastIndexOf(ArgumentsMarker, StringComparison.Ordinal);

        if (index < 0)
            throw new InvalidOperationException("The page script has no arguments marker.");

        var arguments = $"({JsonSerializer.Serialize(command)}, {JsonSerializer.Serialize(argument)}, {(appOnly ? "true" : "false")})";

        return string.Concat(template.AsSpan(0, index), arguments, template.AsSpan(index + ArgumentsMarker.Length));
    }

    private static string ReadTemplate()
    {
        using var stream = typeof(PageScript).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource {ResourceName}.");
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}
