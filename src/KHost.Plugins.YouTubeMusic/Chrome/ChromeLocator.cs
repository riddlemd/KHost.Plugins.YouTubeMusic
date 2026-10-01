namespace KHost.Plugins.YouTubeMusic.Chrome;

/// <summary>Finds Google Chrome's app bundle on macOS.</summary>
public static class ChromeLocator
{
    public const string BundleId = "com.google.Chrome";

    /// <summary>The executable inside a bundle; what is started, and what its pid is checked against.</summary>
    public const string ExecutableName = "Google Chrome";

    /// <summary>The fixed install folders first, then whatever Spotlight knows of by bundle id, for a
    /// copy dragged somewhere else.</summary>
    public static IEnumerable<string> Candidates(string? home, IEnumerable<string> spotlightResults)
    {
        yield return "/Applications/Google Chrome.app";

        if (!string.IsNullOrWhiteSpace(home))
            yield return Path.Combine(home, "Applications", "Google Chrome.app");

        foreach (var result in spotlightResults)
        {
            if (!string.IsNullOrWhiteSpace(result))
                yield return result.Trim();
        }
    }

    public static string Executable(string bundle) => Path.Combine(bundle, "Contents", "MacOS", ExecutableName);

    /// <summary>The first bundle whose executable is there.</summary>
    public static string? Find(IEnumerable<string> candidates, Func<string, bool> exists)
        => candidates.FirstOrDefault(bundle => exists(Executable(bundle)));
}
