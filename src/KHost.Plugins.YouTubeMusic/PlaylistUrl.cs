using System.Text.RegularExpressions;

namespace KHost.Plugins.YouTubeMusic;

/// <summary>Turns whatever a host pastes into the one URL that starts a playlist playing.</summary>
public static partial class PlaylistUrl
{
    private static readonly string[] YouTubeHosts =
        ["music.youtube.com", "www.youtube.com", "youtube.com", "m.youtube.com", "youtu.be"];

    /// <summary>A watch URL with no video plays the list from its first track; the
    /// <c>/playlist</c> page only shows it, and nothing outside the page can press its play button.</summary>
    public static string ForList(string listId) => $"https://music.youtube.com/watch?list={listId}";

    /// <summary>The playing URL for <paramref name="input"/>, or null when it names no playlist.</summary>
    /// <remarks>A single track is refused: a bed that ends after one song is not a bed. Share tokens
    /// (<c>si</c>) and the video a link was copied from are dropped.</remarks>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var text = input.Trim();

        if (IsListId(text))
            return ForList(StripBrowsePrefix(text));

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !YouTubeHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            return null;

        var list = QueryValue(uri.Query, "list");

        // music.youtube.com/browse/VLPL... is how the app's own address bar names a playlist.
        if (list is null && uri.AbsolutePath.StartsWith("/browse/VL", StringComparison.Ordinal))
            list = uri.AbsolutePath["/browse/".Length..];

        if (list is null || !IsListId(list))
            return null;

        return ForList(StripBrowsePrefix(list));
    }

    private static string StripBrowsePrefix(string id)
        => id.StartsWith("VL", StringComparison.Ordinal) && id.Length > 2 ? id[2..] : id;

    /// <summary>Known list prefixes only, so a bare video id or a typo is refused rather than
    /// handed to the app as a list it will not find.</summary>
    private static bool IsListId(string text) => ListIdPattern().IsMatch(text);

    private static string? QueryValue(string query, string key)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');

            if (separator > 0 && pair[..separator] == key)
                return Uri.UnescapeDataString(pair[(separator + 1)..]);
        }

        return null;
    }

    [GeneratedRegex("^(VL)?(PL|OLAK5uy_|RDCLAK|RD|LM$|UU|FL)[A-Za-z0-9_-]*$")]
    private static partial Regex ListIdPattern();
}
