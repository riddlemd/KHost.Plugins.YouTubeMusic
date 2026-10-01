namespace KHost.Plugins.YouTubeMusic.Edge;

/// <summary>Finds <c>msedge.exe</c>.</summary>
public static class EdgeLocator
{
    /// <summary>The registered App Paths entry first, which follows a relocated install; then the
    /// fixed install folders, x86 first because that is where Edge installs even on 64-bit.</summary>
    public static IEnumerable<string> Candidates(string? appPathsEntry, string? programFilesX86, string? programFiles, string? localAppData)
    {
        if (!string.IsNullOrWhiteSpace(appPathsEntry))
            yield return appPathsEntry.Trim().Trim('"');

        foreach (var root in new[] { programFilesX86, programFiles, localAppData })
        {
            if (!string.IsNullOrWhiteSpace(root))
                yield return Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");
        }
    }

    public static string? Find(IEnumerable<string> candidates, Func<string, bool> exists)
        => candidates.FirstOrDefault(exists);
}
