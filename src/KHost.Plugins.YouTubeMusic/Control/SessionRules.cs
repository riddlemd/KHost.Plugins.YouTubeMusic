namespace KHost.Plugins.YouTubeMusic.Control;

/// <summary>Which media session is ours, and which of its tracks are adverts.</summary>
public static class SessionRules
{
    /// <summary>Edge registers an installed web app as <c>music.youtube.com-&lt;hash&gt;!App</c>.
    /// The hash varies by install, so it is never matched; plain <c>MSEdge</c> is a browser tab
    /// playing anything at all, and is never ours.</summary>
    public static bool IsYouTubeMusicApp(string? appUserModelId)
        => appUserModelId is { } id
           && id.StartsWith("music.youtube.com-", StringComparison.OrdinalIgnoreCase)
           && id.EndsWith("!App", StringComparison.OrdinalIgnoreCase);

    /// <summary>YouTube Music reports an advert as a track. Only the generic forms are recognisable:
    /// one titled with an advertiser's own name passes for a song.</summary>
    public static bool IsAdvert(string? title, string? artist)
        => string.Equals(title?.Trim(), "Video Ad", StringComparison.OrdinalIgnoreCase)
           || (artist?.TrimStart().StartsWith("YouTube Ads", StringComparison.OrdinalIgnoreCase) ?? false);
}
