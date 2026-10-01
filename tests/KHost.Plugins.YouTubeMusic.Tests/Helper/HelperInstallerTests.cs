using KHost.Plugins.YouTubeMusic.Helper;

namespace KHost.Plugins.YouTubeMusic.Tests.Helper;

public sealed class HelperInstallerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ytm-installer-").FullName;
    private readonly string _bin;
    private readonly string _shipped;

    public HelperInstallerTests()
    {
        _bin = Path.Combine(_root, "bin");
        _shipped = HelperApp.ShippedApp(Path.Combine(_root, "plugin"));
        Directory.CreateDirectory(_bin);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Installed => HelperApp.InstalledApp(_bin);

    private void Ship(string build)
    {
        Directory.CreateDirectory(Path.Combine(_shipped, "Contents", "MacOS"));
        Directory.CreateDirectory(Path.Combine(_shipped, "Contents", "Resources"));
        File.WriteAllText(HelperApp.ExecutableIn(_shipped), "binary " + build);
        File.WriteAllText(Path.Combine(_shipped, "Contents", "Resources", "youtube-music-page.js"), "script " + build);
    }

    [Fact]
    public void Install_FirstTime_CopiesTheWholeAppUnderThePluginsOwnFolder()
    {
        Ship("1");

        var result = HelperInstaller.Install(_shipped, _bin, () => false);

        Assert.Equal(HelperInstallOutcome.Installed, result.Outcome);
        Assert.Equal(Installed, result.AppPath);
        Assert.Equal("binary 1", File.ReadAllText(HelperApp.ExecutableIn(Installed)));
        Assert.Equal("script 1", File.ReadAllText(Path.Combine(Installed, "Contents", "Resources", "youtube-music-page.js")));
    }

    // A zip made on Windows carries no Unix modes; without this the launch fails with EACCES.
    [Fact]
    public void Install_ExecutableArrivesWithoutItsBit_IsMadeRunnable()
    {
        if (OperatingSystem.IsWindows())
            return;

        Ship("1");
        File.SetUnixFileMode(HelperApp.ExecutableIn(_shipped), UnixFileMode.UserRead | UnixFileMode.UserWrite);

        HelperInstaller.Install(_shipped, _bin, () => false);

        Assert.True(File.GetUnixFileMode(HelperApp.ExecutableIn(Installed)).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void Install_SameBuildAgain_LeavesItAlone()
    {
        Ship("1");
        HelperInstaller.Install(_shipped, _bin, () => false);
        var written = File.GetLastWriteTimeUtc(HelperApp.ExecutableIn(Installed));
        File.SetLastWriteTimeUtc(HelperApp.ExecutableIn(Installed), written.AddHours(-1));

        var result = HelperInstaller.Install(_shipped, _bin, () => throw new InvalidOperationException("not asked when unchanged"));

        Assert.Equal(HelperInstallOutcome.Unchanged, result.Outcome);
        Assert.Equal(written.AddHours(-1), File.GetLastWriteTimeUtc(HelperApp.ExecutableIn(Installed)));
    }

    [Fact]
    public void Install_NewBuild_ReplacesTheOldWholeAndLeavesNoScratch()
    {
        Ship("1");
        HelperInstaller.Install(_shipped, _bin, () => false);
        File.WriteAllText(Path.Combine(Installed, "Contents", "Resources", "stale.js"), "from build 1 only");
        Ship("2");

        var result = HelperInstaller.Install(_shipped, _bin, () => false);

        Assert.Equal(HelperInstallOutcome.Updated, result.Outcome);
        Assert.Equal("binary 2", File.ReadAllText(HelperApp.ExecutableIn(Installed)));
        Assert.False(File.Exists(Path.Combine(Installed, "Contents", "Resources", "stale.js")));
        Assert.Equal([HelperApp.BundleName], Directory.GetFileSystemEntries(Path.Combine(_bin, HelperApp.InstallFolder)).Select(Path.GetFileName));
    }

    [Fact]
    public void Install_NewBuildWhileTheOldRuns_KeepsTheRunningOne()
    {
        Ship("1");
        HelperInstaller.Install(_shipped, _bin, () => false);
        Ship("2");

        var result = HelperInstaller.Install(_shipped, _bin, () => true);

        Assert.Equal(HelperInstallOutcome.KeptRunning, result.Outcome);
        Assert.Equal("binary 1", File.ReadAllText(HelperApp.ExecutableIn(Installed)));
    }

    // A build made without a Mac carries no app; the one installed by an earlier build still plays.
    [Fact]
    public void Install_NothingShippedButOneInstalled_KeepsIt()
    {
        Ship("1");
        HelperInstaller.Install(_shipped, _bin, () => false);
        Directory.Delete(_shipped, recursive: true);

        var result = HelperInstaller.Install(_shipped, _bin, () => false);

        Assert.Equal(HelperInstallOutcome.KeptInstalled, result.Outcome);
        Assert.Equal(Installed, result.AppPath);
    }

    [Fact]
    public void Install_NothingAnywhere_IsMissing()
        => Assert.Equal(new HelperInstallResult(HelperInstallOutcome.Missing, null), HelperInstaller.Install(_shipped, _bin, () => false));

    // bin/ is shared: ffmpeg and ffprobe are the host's, and nothing outside the plugin's folder moves.
    [Fact]
    public void Install_TouchesNothingElseInBin()
    {
        File.WriteAllText(Path.Combine(_bin, "ffmpeg"), "host's");
        Ship("1");
        HelperInstaller.Install(_shipped, _bin, () => false);
        Ship("2");
        HelperInstaller.Install(_shipped, _bin, () => false);

        Assert.Equal("host's", File.ReadAllText(Path.Combine(_bin, "ffmpeg")));
        Assert.Equal(["ffmpeg", HelperApp.InstallFolder], Directory.GetFileSystemEntries(_bin).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void TreeHash_SameBytesUnderAnotherRoot_Match_AndOneByteDiffers()
    {
        Ship("1");
        HelperInstaller.Install(_shipped, _bin, () => false);
        var before = HelperInstaller.TreeHash(_shipped);

        Assert.Equal(before, HelperInstaller.TreeHash(Installed));

        File.WriteAllText(Path.Combine(_shipped, "Contents", "Resources", "youtube-music-page.js"), "script 1!");
        Assert.NotEqual(before, HelperInstaller.TreeHash(_shipped));
    }
}
