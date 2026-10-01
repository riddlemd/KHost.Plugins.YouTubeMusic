using System.Security.Cryptography;
using System.Text;

namespace KHost.Plugins.YouTubeMusic.Helper;

public enum HelperInstallOutcome
{
    /// <summary>Neither shipped with the plugin nor installed before.</summary>
    Missing,

    Installed,
    Updated,
    Unchanged,

    /// <summary>The plugin carries none (a build made without a Mac), but an earlier one is there.</summary>
    KeptInstalled,

    /// <summary>A different copy is running, so it was left in place until it is not.</summary>
    KeptRunning,
    Failed,
}

/// <param name="AppPath">The app to launch; null when there is none.</param>
public sealed record HelperInstallResult(HelperInstallOutcome Outcome, string? AppPath, string? Error = null);

/// <summary>Copies the app the plugin carries into the host's shared bin/, under this plugin's own
/// folder, and only when it differs from what is already there.</summary>
/// <remarks>Launched from bin/ rather than the plugin folder because an update replaces the plugin
/// folder whole, which would pull the app out from under a running copy, and because a stable path
/// is what keeps the Dock icon the same app across updates.</remarks>
public static class HelperInstaller
{
    private const UnixFileMode Executable =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <param name="shippedApp">The app beside the plugin's files; may not exist.</param>
    /// <param name="isRunning">Asked only when an update is due.</param>
    public static HelperInstallResult Install(string shippedApp, string binDirectory, Func<bool> isRunning)
    {
        var installed = HelperApp.InstalledApp(binDirectory);
        var hasInstalled = File.Exists(HelperApp.ExecutableIn(installed));

        try
        {
            if (!File.Exists(HelperApp.ExecutableIn(shippedApp)))
            {
                return hasInstalled
                    ? new HelperInstallResult(HelperInstallOutcome.KeptInstalled, MakeRunnable(installed))
                    : new HelperInstallResult(HelperInstallOutcome.Missing, null);
            }

            if (hasInstalled && TreeHash(installed) == TreeHash(shippedApp))
                return new HelperInstallResult(HelperInstallOutcome.Unchanged, MakeRunnable(installed));

            // Swapping the bundle under a running copy would leave its resources from one build and
            // its code from another; the next start after it quits picks the new one up.
            if (hasInstalled && isRunning())
                return new HelperInstallResult(HelperInstallOutcome.KeptRunning, installed);

            var folder = Path.Combine(binDirectory, HelperApp.InstallFolder);
            var staging = Path.Combine(folder, $".staging-{Guid.NewGuid():N}");
            var staged = Path.Combine(staging, HelperApp.BundleName);

            try
            {
                CopyTree(shippedApp, staged);
                MakeRunnable(staged);

                if (hasInstalled)
                {
                    // Moved aside first, so a failure part way leaves one whole copy or the other.
                    var aside = Path.Combine(staging, "previous.app");
                    Directory.Move(installed, aside);
                    Directory.Move(staged, installed);
                }
                else
                {
                    Directory.Move(staged, installed);
                }
            }
            finally
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
            }

            return new HelperInstallResult(hasInstalled ? HelperInstallOutcome.Updated : HelperInstallOutcome.Installed, installed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HelperInstallResult(
                HelperInstallOutcome.Failed, File.Exists(HelperApp.ExecutableIn(installed)) ? installed : null, ex.Message);
        }
    }

    /// <summary>Every file's path and bytes, in a fixed order: two trees with the same hash are the
    /// same app, signature included.</summary>
    public static string TreeHash(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => (Path: path, Relative: Path.GetRelativePath(root, path).Replace('\\', '/')))
            .OrderBy(file => file.Relative, StringComparer.Ordinal);

        foreach (var (path, relative) in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]);
            hash.AppendData(File.ReadAllBytes(path));
            hash.AppendData([0]);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
    }

    /// <summary>A release zip made on Windows carries no Unix modes, so the executable bit is set
    /// here rather than trusted to the unzip.</summary>
    private static string MakeRunnable(string app)
    {
        if (!OperatingSystem.IsWindows())
        {
            var macOs = Path.Combine(app, "Contents", "MacOS");

            if (Directory.Exists(macOs))
            {
                foreach (var file in Directory.EnumerateFiles(macOs))
                    File.SetUnixFileMode(file, Executable);
            }
        }

        return app;
    }
}
