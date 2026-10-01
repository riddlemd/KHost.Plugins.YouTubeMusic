using KHost.Plugins.YouTubeMusic.Chrome;

namespace KHost.Plugins.YouTubeMusic.Tests.Chrome;

public class ChromeArgumentsTests
{
    private const string Profile = "/Users/host/Library/Application Support/KHost/youtube-music-chrome";
    private const string List = "https://music.youtube.com/watch?list=OLAK5uy_x";

    [Fact]
    public void ForApp_WithStartUrl_OpensAnAppWindowAtThatUrlOnThePluginsProfile()
    {
        var arguments = ChromeArguments.ForApp(Profile, List);

        Assert.Contains($"--user-data-dir={Profile}", arguments);
        Assert.Equal($"--app={List}", arguments[^1]);
    }

    [Fact]
    public void ForApp_NoStartUrl_OpensTheHomePage()
        => Assert.Equal("--app=https://music.youtube.com/", ChromeArguments.ForApp(Profile, null)[^1]);

    // Only YouTube Music is ever opened in the window, whatever reached the controller.
    [Theory]
    [InlineData("https://evil.example/watch?list=PLx")]
    [InlineData("http://music.youtube.com/watch?list=PLx")]
    [InlineData("not a url")]
    public void ForApp_StartUrlNotYouTubeMusic_OpensTheHomePageInstead(string startUrl)
        => Assert.Equal("--app=https://music.youtube.com/", ChromeArguments.ForApp(Profile, startUrl)[^1]);

    [Fact]
    public void ForApp_Always_KeepsPlayingWhenHiddenAndStartsWithoutAClick()
    {
        var arguments = ChromeArguments.ForApp(Profile, List);

        Assert.Contains("--autoplay-policy=no-user-gesture-required", arguments);
        Assert.Contains("--disable-backgrounding-occluded-windows", arguments);
        Assert.Contains("--disable-renderer-backgrounding", arguments);
        Assert.Contains("--disable-background-timer-throttling", arguments);
        Assert.Contains("--hide-crash-restore-bubble", arguments);
        Assert.Contains("--no-first-run", arguments);
    }

    [Fact]
    public void ForSetup_OpensAnOrdinaryWindowAtYouTubeMusic()
    {
        var arguments = ChromeArguments.ForSetup(Profile);

        Assert.Equal("https://music.youtube.com/", arguments[^1]);
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("--app", StringComparison.Ordinal));
        Assert.Contains($"--user-data-dir={Profile}", arguments);
    }
}

public class ChromeProfileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "khost-ytm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Resolve_Blank_IsUnderApplicationSupportInItsOwnFolder()
        => Assert.Equal(
            "/Users/host/Library/Application Support/KHost/youtube-music-chrome",
            ChromeProfile.Resolve("  ", "/Users/host"));

    [Fact]
    public void Resolve_Configured_IsThatFolder()
        => Assert.Equal("/Volumes/Kiosk/ytm", ChromeProfile.Resolve(" /Volumes/Kiosk/ytm ", "/Users/host"));

    [Theory]
    [InlineData("Ise-95489", 95489)]
    [InlineData("my-mac-mini.local-123", 123)]
    [InlineData("host-", null)]
    [InlineData("host-abc", null)]
    [InlineData("host-0", null)]
    [InlineData(null, null)]
    public void ParseLockTarget_TakesThePidAfterTheLastDash(string? target, int? expected)
        => Assert.Equal(expected, ChromeProfile.ParseLockTarget(target));

    [Fact]
    public void ReadLockPid_ReadsChromesLockLink()
    {
        Directory.CreateDirectory(_directory);
        File.CreateSymbolicLink(Path.Combine(_directory, "SingletonLock"), "Ise-4242");

        Assert.Equal(4242, ChromeProfile.ReadLockPid(_directory));
    }

    [Fact]
    public void ReadLockPid_NoLock_IsNull()
    {
        Directory.CreateDirectory(_directory);

        Assert.Null(ChromeProfile.ReadLockPid(_directory));
    }

    [Fact]
    public void WithAppleEventsAllowed_KeepsEverythingElse()
    {
        var updated = ChromeProfile.WithAppleEventsAllowed("""{"browser":{"window_placement":{"top":1}},"profile":{"name":"x"}}""");

        Assert.NotNull(updated);
        Assert.Contains("\"allow_javascript_apple_events\":true", updated);
        Assert.Contains("\"window_placement\":{\"top\":1}", updated);
        Assert.Contains("\"profile\":{\"name\":\"x\"}", updated);
    }

    [Fact]
    public void WithAppleEventsAllowed_AlreadyOn_LeavesTheFileAlone()
        => Assert.Null(ChromeProfile.WithAppleEventsAllowed("""{"browser":{"allow_javascript_apple_events":true}}"""));

    [Fact]
    public void WithAppleEventsAllowed_TurnedOff_TurnsItOn()
        => Assert.Contains(
            "\"allow_javascript_apple_events\":true",
            ChromeProfile.WithAppleEventsAllowed("""{"browser":{"allow_javascript_apple_events":false}}"""));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("[1,2]")]
    public void WithAppleEventsAllowed_NoUsableFile_StartsOne(string? json)
        => Assert.Equal("""{"browser":{"allow_javascript_apple_events":true}}""", ChromeProfile.WithAppleEventsAllowed(json));

    [Fact]
    public void AllowAppleEvents_FreshProfile_WritesPreferencesThatReadBackAsAllowed()
    {
        Assert.False(ChromeProfile.AppleEventsAllowed(_directory));

        ChromeProfile.AllowAppleEvents(_directory);

        Assert.True(ChromeProfile.AppleEventsAllowed(_directory));
    }

    [Fact]
    public void HasBeenOpened_OnlyOnceChromeWroteLocalState()
    {
        ChromeProfile.AllowAppleEvents(_directory);
        Assert.False(ChromeProfile.HasBeenOpened(_directory));

        File.WriteAllText(Path.Combine(_directory, "Local State"), "{}");
        Assert.True(ChromeProfile.HasBeenOpened(_directory));
    }
}

public class ChromeLocatorTests
{
    [Fact]
    public void Candidates_ApplicationsFoldersBeforeSpotlight()
        => Assert.Equal(
            ["/Applications/Google Chrome.app", "/Users/host/Applications/Google Chrome.app", "/Volumes/Apps/Google Chrome.app"],
            ChromeLocator.Candidates("/Users/host", [" /Volumes/Apps/Google Chrome.app ", ""]));

    [Fact]
    public void Candidates_SpotlightNotAskedWhenAnEarlierOneIsFound()
    {
        var asked = false;

        IEnumerable<string> Spotlight()
        {
            asked = true;
            yield return "/Volumes/Apps/Google Chrome.app";
        }

        var found = ChromeLocator.Find(ChromeLocator.Candidates("/Users/host", Spotlight()), _ => true);

        Assert.Equal("/Applications/Google Chrome.app", found);
        Assert.False(asked);
    }

    [Fact]
    public void Find_ChecksTheExecutableInsideTheBundle()
    {
        var found = ChromeLocator.Find(
            ["/Applications/Google Chrome.app", "/Users/host/Applications/Google Chrome.app"],
            path => path == "/Users/host/Applications/Google Chrome.app/Contents/MacOS/Google Chrome");

        Assert.Equal("/Users/host/Applications/Google Chrome.app", found);
    }

    [Fact]
    public void Find_NoneThere_IsNull()
        => Assert.Null(ChromeLocator.Find(["/Applications/Google Chrome.app"], _ => false));
}
