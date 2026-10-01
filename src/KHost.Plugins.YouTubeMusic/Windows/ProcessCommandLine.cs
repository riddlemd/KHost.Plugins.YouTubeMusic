using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KHost.Plugins.YouTubeMusic.Windows;

/// <summary>Reads another process's command line.</summary>
/// <remarks><c>NtQueryInformationProcess</c> rather than WMI: WMI is a package and takes a second
/// or more per query, and this runs at the start of every fade.</remarks>
[SupportedOSPlatform("windows")]
internal static class ProcessCommandLine
{
    private const int ProcessCommandLineInformation = 60;
    private const int ProcessQueryLimitedInformation = 0x1000;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    /// <summary>Ids of every <paramref name="imageName"/> process whose command line passes
    /// <paramref name="match"/>. One that has exited, or will not open, is passed over.</summary>
    public static HashSet<int> Find(string imageName, Func<string?, bool> match)
    {
        var ids = new HashSet<int>();

        foreach (var process in Process.GetProcessesByName(imageName))
        {
            using (process)
            {
                if (match(Read(process.Id)))
                    ids.Add(process.Id);
            }
        }

        return ids;
    }

    public static string? Read(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);

        if (handle == IntPtr.Zero)
            return null;

        var buffer = IntPtr.Zero;

        try
        {
            var status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var length);

            if (status != StatusInfoLengthMismatch || length <= 0)
                return null;

            buffer = Marshal.AllocHGlobal(length);

            if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, length, out _) != 0)
                return null;

            var text = Marshal.PtrToStructure<UnicodeString>(buffer);

            return text.Buffer == IntPtr.Zero ? null : Marshal.PtrToStringUni(text.Buffer, text.Length / 2);
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);

            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, IntPtr information, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
