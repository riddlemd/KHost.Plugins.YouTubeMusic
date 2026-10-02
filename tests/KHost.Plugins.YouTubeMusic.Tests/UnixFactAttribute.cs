namespace KHost.Plugins.YouTubeMusic.Tests;

/// <summary>A test that writes a file name Windows cannot hold, such as Finder's <c>Icon\r</c>.
/// Reported as skipped there rather than passing silently; the rule it covers is also tested by
/// name on every platform.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "NTFS cannot name a file with a carriage return; the marker only ever exists on a Mac.";
    }
}
