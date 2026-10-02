using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KHost.Plugins.YouTubeMusic.Windows;

/// <summary>Reads and sets the foreground window.</summary>
[SupportedOSPlatform("windows")]
internal static class ForegroundWindow
{
    public static nint Current() => GetForegroundWindow();

    public static bool BelongsToProcess(nint window, string processName)
    {
        try
        {
            if (GetWindowThreadProcessId(window, out var processId) == 0)
                return false;

            using var process = Process.GetProcessById((int)processId);
            return string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Windows refuses SetForegroundWindow to a process that is not in front, which KHost
    /// is not once Edge has taken it; sharing input with the foreground thread lifts that.</summary>
    public static bool BringToFront(nint window)
    {
        if (!IsWindow(window))
            return false;

        if (SetForegroundWindow(window))
            return true;

        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var ourThread = GetCurrentThreadId();

        if (foregroundThread == 0 || foregroundThread == ourThread || !AttachThreadInput(ourThread, foregroundThread, true))
            return false;

        try
        {
            return SetForegroundWindow(window);
        }
        finally
        {
            AttachThreadInput(ourThread, foregroundThread, false);
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
