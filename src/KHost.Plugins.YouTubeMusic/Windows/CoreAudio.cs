using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KHost.Plugins.YouTubeMusic.Windows;

/// <summary>Sets one process's level in the Windows volume mixer, through Core Audio's session API.</summary>
/// <remarks>Built-in COM interop rather than a package: six interfaces, and a plugin's dependencies
/// ship in its zip. Every vtable below is declared in full and in order, base members included,
/// because a <c>[ComImport]</c> interface does not inherit its base's slots.</remarks>
[SupportedOSPlatform("windows")]
internal static class CoreAudio
{
    private const int DeviceStateActive = 0x1;
    private const int ClsCtxAll = 0x17;

    /// <summary>Sets every audio session owned by one of <paramref name="processIds"/>, on every
    /// active output. False when none was found.</summary>
    /// <remarks>Edge opens one session per browser instance, with no name and a null grouping id,
    /// so the owning process is the only thing that tells ours from the host's own browser.</remarks>
    public static bool SetLevel(IReadOnlySet<int> processIds, float level)
    {
        if (processIds.Count == 0)
            return false;

        var found = false;
        var context = Guid.Empty;
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();

        try
        {
            Check(enumerator.EnumAudioEndpoints(DataFlow.Render, DeviceStateActive, out var devices));
            Check(devices.GetCount(out var deviceCount));

            for (uint d = 0; d < deviceCount; d++)
            {
                Check(devices.Item(d, out var device));

                var managerId = typeof(IAudioSessionManager2).GUID;
                Check(device.Activate(ref managerId, ClsCtxAll, IntPtr.Zero, out var managerObject));

                var manager = (IAudioSessionManager2)managerObject;
                Check(manager.GetSessionEnumerator(out var sessions));
                Check(sessions.GetCount(out var sessionCount));

                for (var s = 0; s < sessionCount; s++)
                {
                    Check(sessions.GetSession(s, out var session));

                    try
                    {
                        if (session.GetProcessId(out var processId) != 0 || !processIds.Contains((int)processId))
                            continue;

                        Check(((ISimpleAudioVolume)session).SetMasterVolume(Math.Clamp(level, 0f, 1f), ref context));
                        found = true;
                    }
                    finally
                    {
                        Release(session);
                    }
                }

                Release(sessions);
                Release(manager);
                Release(device);
            }

            Release(devices);
        }
        finally
        {
            Release(enumerator);
        }

        return found;
    }

    /// <summary>Released as it goes: a fade calls this every 100ms, and the finalizer would
    /// otherwise hold every device and session open until a collection.</summary>
    private static void Release(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
            Marshal.ReleaseComObject(instance);
    }

    private static void Check(int hresult) => Marshal.ThrowExceptionForHR(hresult);

    private enum DataFlow { Render = 0 }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(DataFlow dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(DataFlow dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        // IAudioSessionManager
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int streamFlags, out IntPtr sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int streamFlags, out IntPtr audioVolume);

        // IAudioSessionManager2
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
        [PreserveSig] int RegisterSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterSessionNotification(IntPtr notification);
        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int sessionCount);
        [PreserveSig] int GetSession(int sessionIndex, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingId);
        [PreserveSig] int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);

        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier(out IntPtr identifier);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr identifier);
        [PreserveSig] int GetProcessId(out uint processId);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
