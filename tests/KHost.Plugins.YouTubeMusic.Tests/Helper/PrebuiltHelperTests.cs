using KHost.Plugins.YouTubeMusic.Helper;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace KHost.Plugins.YouTubeMusic.Tests.Helper;

/// <summary>A release built on Windows or Linux ships helpers/macos/prebuilt; the build refuses it
/// once its stamp stops matching the sources, and this says so before a release is attempted.</summary>
public class PrebuiltHelperTests
{
    private static string HelperSources([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "helpers", "macos"));

    [Fact]
    public void Stamp_MatchesTheHelpersSources()
    {
        var sources = HelperSources();
        var expected = new[] { "main.swift", "youtube-music-page.js", "Info.plist.in", "build.sh" }
            .Select(name => $"{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(sources, name))))}  {name}");

        Assert.Equal(expected, File.ReadAllLines(Path.Combine(sources, "prebuilt", "sources.sha256")));
    }

    [Fact]
    public void Prebuilt_CarriesTheAppTheInstallerLooksFor()
    {
        var app = Path.Combine(HelperSources(), "prebuilt", HelperApp.BundleName);

        Assert.True(File.Exists(HelperApp.ExecutableIn(app)));
        Assert.True(File.Exists(Path.Combine(app, "Contents", "Resources", "youtube-music-page.js")));
        Assert.Contains($"<string>{HelperApp.BundleId}</string>", File.ReadAllText(Path.Combine(app, "Contents", "Info.plist")));
    }

    // The page script ships inside the app; the copy there must be the one in the sources.
    [Fact]
    public void Prebuilt_PageScript_IsTheSourceOne()
        => Assert.Equal(
            File.ReadAllText(Path.Combine(HelperSources(), "youtube-music-page.js")),
            File.ReadAllText(Path.Combine(HelperSources(), "prebuilt", HelperApp.BundleName, "Contents", "Resources", "youtube-music-page.js")));
}
