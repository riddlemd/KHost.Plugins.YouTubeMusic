using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace KHost.Plugins.YouTubeMusic.Helper;

/// <param name="Background">Started behind the karaoke screen, for the music; otherwise in front,
/// for the host to look at.</param>
/// <param name="Url">A music.youtube.com address to open at instead of the home page.</param>
/// <param name="InitialLevel">The level the page is held at until the first level command, so a
/// list that autoplays starts where the fade-in does.</param>
internal sealed record HelperLaunch(bool Background, string? Url = null, double? InitialLevel = null);

/// <summary>How the controller reaches the helper app: a copy already up, or a new one.</summary>
internal interface IHelperTransport
{
    /// <summary>Whether there is an app to launch at all.</summary>
    bool AppAvailable { get; }

    /// <summary>A quick check that some copy is up and answering.</summary>
    bool IsRunning { get; }

    /// <returns>Null when no copy is answering.</returns>
    Task<HelperConnection?> ConnectExistingAsync(CancellationToken cancellationToken);

    /// <returns>Null when it could not be started, or a copy turned out to be up already (it says
    /// so and exits), in which case <see cref="ConnectExistingAsync"/> reaches that one.</returns>
    Task<HelperConnection?> LaunchAsync(HelperLaunch launch, CancellationToken cancellationToken);
}

/// <summary>The real helper app: launched with its stdin and stdout as the channel, so it quits with
/// the host however the host ends, or reached over its Unix socket when the host did not launch it.</summary>
[SupportedOSPlatform("macos")]
internal sealed class ProcessHelperTransport(ILogger logger, string? appPath, string? socketPath) : IHelperTransport
{
    /// <summary>A cold start builds a WebKit process before it can answer; well under this.</summary>
    internal static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan SocketHelloTimeout = TimeSpan.FromSeconds(3);

    public bool AppAvailable => appPath is not null && File.Exists(HelperApp.ExecutableIn(appPath));

    public bool IsRunning
    {
        get
        {
            if (socketPath is null || !File.Exists(socketPath))
                return false;

            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                socket.Connect(new UnixDomainSocketEndPoint(socketPath));
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }
    }

    public async Task<HelperConnection?> ConnectExistingAsync(CancellationToken cancellationToken)
    {
        // A leftover file from a crash answers nothing; checked first so an idle poll costs no socket.
        if (socketPath is null || !File.Exists(socketPath))
            return null;

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
        }
        catch (SocketException)
        {
            socket.Dispose();
            return null;
        }

        var stream = new NetworkStream(socket, ownsSocket: true);
        var connection = new HelperConnection(stream, stream, logger);
        connection.Start();

        if (await connection.WaitForHelloAsync(SocketHelloTimeout, cancellationToken) is null)
        {
            await connection.DisposeAsync();
            return null;
        }

        return connection;
    }

    public async Task<HelperConnection?> LaunchAsync(HelperLaunch launch, CancellationToken cancellationToken)
    {
        if (!AppAvailable)
            return null;

        // Started directly rather than through open(1): the pipes are the channel, and a pipe that
        // closes when the host ends is what makes the app quit with it, crash or not.
        var start = new ProcessStartInfo(HelperApp.ExecutableIn(appPath!))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        start.ArgumentList.Add("--khost-stdio");

        if (launch.Background)
            start.ArgumentList.Add("--background");

        if (launch.Url is { } url)
        {
            start.ArgumentList.Add("--url");
            start.ArgumentList.Add(url);
        }

        if (launch.InitialLevel is { } level)
        {
            start.ArgumentList.Add("--initial-level");
            start.ArgumentList.Add(Math.Clamp(level, 0, 1).ToString("0.####", CultureInfo.InvariantCulture));
        }

        Process process;

        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("Process.Start returned nothing.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not start the YouTube Music app at {Path}", appPath);
            return null;
        }

        _ = Task.Run(() => PumpErrorsAsync(process), CancellationToken.None);

        var connection = new HelperConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, logger, process);
        connection.Start();

        var hello = await connection.WaitForHelloAsync(HelloTimeout, cancellationToken);

        if (hello is not null)
        {
            logger.LogInformation("Started the YouTube Music app (pid {Pid}, version {Version})", hello.Pid, hello.Version);
            return connection;
        }

        if (connection.WasBusy)
            logger.LogInformation("The YouTube Music app was already open; driving that one");
        else
            logger.LogWarning("The YouTube Music app started but did not answer within {Timeout}", HelloTimeout);

        // Closing its stdin is what ends a copy that never answered.
        await connection.DisposeAsync();
        return null;
    }

    private async Task PumpErrorsAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
                logger.LogDebug("{Line}", line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Gone with the process.
        }
    }
}
