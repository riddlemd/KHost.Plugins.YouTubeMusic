using KHost.Plugins.YouTubeMusic.Control;
using KHost.Plugins.YouTubeMusic.Helper;
using Microsoft.Extensions.Logging.Abstractions;

namespace KHost.Plugins.YouTubeMusic.Tests.Control;

public sealed class YouTubeMusicControllerFactoryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ytm-factory-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // Constructing it installs the app into bin/ and launches nothing.
    [Fact]
    public void ForCurrentPlatform_MacOS_IsTheHelperControllerWithTheAppInstalled()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var plugin = Path.Combine(_root, "plugin");
        CopyTree(HelperApp.ShippedApp(AppContext.BaseDirectory), HelperApp.ShippedApp(plugin));

        var controller = YouTubeMusicControllerFactory.ForCurrentPlatform(NullLogger.Instance, null, Path.Combine(_root, "bin"), plugin);

        Assert.IsType<HelperYouTubeMusicController>(controller);
        Assert.Null(controller.Unavailable);
        Assert.True(File.Exists(HelperApp.ExecutableIn(HelperApp.InstalledApp(Path.Combine(_root, "bin")))));
    }

    [Fact]
    public void ForCurrentPlatform_MacOSWithoutTheApp_SaysTheAppIsMissing()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var controller = YouTubeMusicControllerFactory.ForCurrentPlatform(
            NullLogger.Instance, null, Path.Combine(_root, "bin"), Path.Combine(_root, "empty-plugin"));

        Assert.NotNull(controller.Unavailable);
        Assert.Equal(SetupStatus.HelperMissing, controller.GetSetupStatus());
    }

    [Fact]
    public void ForCurrentPlatform_NeitherWindowsNorMacOS_IsUnavailableWithAReason()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            return;

        var controller = YouTubeMusicControllerFactory.ForCurrentPlatform(NullLogger.Instance, null, _root);

        Assert.Contains("runs on Windows and macOS", controller.Unavailable);
        Assert.Equal(SetupStatus.Unsupported, controller.GetSetupStatus());
    }

    [Fact]
    public void UnsupportedReason_NamesBothBackendsAndThePlatform()
        => Assert.Equal(
            "YouTube Music break music runs on Windows and macOS; there is no backend for Ubuntu 24.04 yet.",
            YouTubeMusicControllerFactory.UnsupportedReason("Ubuntu 24.04"));

    private static void CopyTree(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
