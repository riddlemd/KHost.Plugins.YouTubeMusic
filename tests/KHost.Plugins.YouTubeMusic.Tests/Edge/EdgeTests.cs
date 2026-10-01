using KHost.Plugins.YouTubeMusic.Edge;

namespace KHost.Plugins.YouTubeMusic.Tests.Edge;

public class EdgeArgumentsTests
{
    private const string Profile = @"C:\Users\host\AppData\Local\KHost\youtube-music-profile";

    [Fact]
    public void ForApp_WithStartUrl_OpensTheInstalledAppAtThatUrl()
    {
        var arguments = EdgeArguments.ForApp(Profile, "https://music.youtube.com/watch?list=PLx");

        Assert.Contains($"--user-data-dir={Profile}", arguments);
        Assert.Contains("--app-id=cinhimbnkkaeohfgghhklpknlkffjgod", arguments);
        Assert.Contains("--app-launch-url-for-shortcuts-menu-item=https://music.youtube.com/watch?list=PLx", arguments);
    }

    // --app=<url> opens an anonymous window whose media session is plain MSEdge, which is never matched.
    [Fact]
    public void ForApp_Always_NeverOpensAnAnonymousAppWindow()
        => Assert.DoesNotContain(EdgeArguments.ForApp(Profile, "https://music.youtube.com/watch?list=PLx"), a => a.StartsWith("--app=", StringComparison.Ordinal));

    [Fact]
    public void ForApp_NoStartUrl_LeavesTheAppWhereItWas()
        => Assert.DoesNotContain(EdgeArguments.ForApp(Profile, null), a => a.StartsWith("--app-launch-url", StringComparison.Ordinal));

    [Fact]
    public void ForApp_Always_KeepsPlayingUnderAnotherWindow()
    {
        var arguments = EdgeArguments.ForApp(Profile, null);

        Assert.Contains("--disable-features=CalculateNativeWinOcclusion", arguments);
        Assert.Contains("--disable-backgrounding-occluded-windows", arguments);
        Assert.Contains("--disable-sync", arguments);
        Assert.Contains("--no-first-run", arguments);
    }

    [Fact]
    public void ForSetup_Always_OpensAnOrdinaryWindowOnTheProfileAtYouTubeMusic()
    {
        var arguments = EdgeArguments.ForSetup(Profile);

        Assert.Contains($"--user-data-dir={Profile}", arguments);
        Assert.Equal("https://music.youtube.com/", arguments[^1]);
        Assert.DoesNotContain(arguments, a => a.StartsWith("--app", StringComparison.Ordinal));
    }
}

public class EdgeCommandLineTests
{
    private const string Profile = @"C:\Users\host\AppData\Local\KHost\youtube-music-profile";

    private const string AudioService =
        "\"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe\" --type=utility "
        + "--utility-sub-type=audio.mojom.AudioService --lang=en-US --service-sandbox-type=audio "
        + "--user-data-dir=\"C:\\Users\\host\\AppData\\Local\\KHost\\youtube-music-profile\" --field-trial-handle=1";

    [Fact]
    public void IsAudioServiceFor_OurProfilesAudioService_Matches()
        => Assert.True(EdgeCommandLine.IsAudioServiceFor(AudioService, Profile));

    // The host's own browsing has an audio service too; turning it down would mute their Edge.
    [Fact]
    public void IsAudioServiceFor_AnotherProfilesAudioService_DoesNotMatch()
        => Assert.False(EdgeCommandLine.IsAudioServiceFor(AudioService.Replace("youtube-music-profile", "youtube-music-profile-2"), Profile));

    [Fact]
    public void IsAudioServiceFor_DefaultProfileWithNoUserDataDir_DoesNotMatch()
        => Assert.False(EdgeCommandLine.IsAudioServiceFor(
            "msedge.exe --type=utility --utility-sub-type=audio.mojom.AudioService", Profile));

    [Fact]
    public void IsAudioServiceFor_RendererOfOurProfile_DoesNotMatch()
        => Assert.False(EdgeCommandLine.IsAudioServiceFor(
            $"msedge.exe --type=renderer --user-data-dir=\"{Profile}\"", Profile));

    [Fact]
    public void IsAudioServiceFor_OurProfilesNetworkService_DoesNotMatch()
        => Assert.False(EdgeCommandLine.IsAudioServiceFor(
            AudioService.Replace("audio.mojom.AudioService", "network.mojom.NetworkService"), Profile));

    // A Windows user name with a space is common; unquoted, the value would end at the space.
    [Fact]
    public void IsAudioServiceFor_QuotedProfileWithSpaces_Matches()
    {
        const string spaced = @"C:\Users\Jane Doe\AppData\Local\KHost\youtube-music-profile";

        Assert.True(EdgeCommandLine.IsAudioServiceFor(
            $"msedge.exe --type=utility --utility-sub-type=audio.mojom.AudioService --user-data-dir=\"{spaced}\" --x=1", spaced));
    }

    [Fact]
    public void IsBrowserFor_UnquotedTrailingSlash_Matches()
        => Assert.True(EdgeCommandLine.IsBrowserFor($"msedge.exe --user-data-dir={Profile}\\ --app-id=x", Profile));

    [Fact]
    public void IsBrowserFor_ChildProcess_DoesNotMatch()
        => Assert.False(EdgeCommandLine.IsBrowserFor($"msedge.exe --type=gpu-process --user-data-dir={Profile}", Profile));
}

public class EdgeProfileTests : IDisposable
{
    private readonly string _profile = Path.Combine(Path.GetTempPath(), $"khost-ytm-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_profile))
            Directory.Delete(_profile, recursive: true);
    }

    [Fact]
    public void HasYouTubeMusicApp_EmptyProfile_IsFalse()
        => Assert.False(EdgeProfile.HasYouTubeMusicApp(_profile));

    [Fact]
    public void HasYouTubeMusicApp_AppIconsPresent_IsTrue()
    {
        Directory.CreateDirectory(Path.Combine(_profile, "Default", "Web Applications", "Manifest Resources", EdgeArguments.YouTubeMusicAppId));

        Assert.True(EdgeProfile.HasYouTubeMusicApp(_profile));
    }

    [Fact]
    public void HasYouTubeMusicApp_ListedInPreferences_IsTrue()
    {
        Directory.CreateDirectory(Path.Combine(_profile, "Default"));
        File.WriteAllText(Path.Combine(_profile, "Default", "Preferences"), "{\"web_apps\":{\"" + EdgeArguments.YouTubeMusicAppId + "\":{}}}");

        Assert.True(EdgeProfile.HasYouTubeMusicApp(_profile));
    }

    [Fact]
    public void HasYouTubeMusicApp_PreferencesWithoutIt_IsFalse()
    {
        Directory.CreateDirectory(Path.Combine(_profile, "Default"));
        File.WriteAllText(Path.Combine(_profile, "Default", "Preferences"), """{"web_apps":{}}""");

        Assert.False(EdgeProfile.HasYouTubeMusicApp(_profile));
    }

    [Fact]
    public void Resolve_Blank_IsTheDefaultDirectory()
        => Assert.Equal(EdgeProfile.DefaultDirectory, EdgeProfile.Resolve("  "));

    [Fact]
    public void Resolve_Configured_IsThatDirectory()
        => Assert.Equal(_profile, EdgeProfile.Resolve(_profile));
}

public class EdgeLocatorTests
{
    [Fact]
    public void Candidates_Always_PutTheRegisteredPathFirst()
    {
        var candidates = EdgeLocator.Candidates("\"D:\\Edge\\msedge.exe\"", "X86", "X64", "Local").ToList();

        Assert.Equal("D:\\Edge\\msedge.exe", candidates[0]);
        Assert.Equal(Path.Combine("X86", "Microsoft", "Edge", "Application", "msedge.exe"), candidates[1]);
        Assert.Equal(4, candidates.Count);
    }

    [Fact]
    public void Find_FirstThatExists_Wins()
        => Assert.Equal("b", EdgeLocator.Find(["a", "b", "c"], path => path != "a"));

    [Fact]
    public void Find_NoneExists_IsNull()
        => Assert.Null(EdgeLocator.Find(["a"], _ => false));
}
