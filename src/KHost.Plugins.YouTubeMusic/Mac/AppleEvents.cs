using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace KHost.Plugins.YouTubeMusic.Mac;

/// <summary>What an Apple Event to Chrome came back with.</summary>
public enum AppleEventStatus
{
    Ok,

    /// <summary>The script ran but answered nothing a string can hold ("missing value"): a page
    /// mid-navigation, or a window whose tab threw.</summary>
    NoResult,

    /// <summary>The host app may not send Apple Events to Chrome (System Settings → Privacy &amp;
    /// Security → Automation), or has never been asked.</summary>
    NotPermitted,
    Failed,
}

/// <summary>Apple Events sent to one Chrome process by its pid. Addressed by pid because every
/// Chrome on the machine shares one bundle id: <c>tell application "Google Chrome"</c>, and even
/// JXA's <c>Application(pid)</c>, reach whichever instance LaunchServices picks, which may be the
/// host's own browser.</summary>
[SupportedOSPlatform("macos")]
internal static class AppleEvents
{
    // Event codes from Chrome's own scripting.sdef.
    private static readonly uint ChromeSuite = Code("CrSu");
    private static readonly uint ExecuteEvent = Code("ExJa");
    private static readonly uint JavaScriptParameter = Code("JvSc");
    private static readonly uint ActiveTab = Code("acTa");
    private static readonly uint WindowClass = Code("cwin");

    private static readonly uint CoreSuite = Code("core");
    private static readonly uint CountEvent = Code("cnte");
    private static readonly uint ObjectClassKeyword = Code("kocl");
    private static readonly uint DirectObject = Code("----");
    private static readonly uint ErrorNumberKeyword = Code("errn");

    private static readonly uint TypeKernelProcessId = Code("kpid");
    private static readonly uint TypeNull = Code("null");
    private static readonly uint TypeSInt32 = Code("long");
    private static readonly uint TypeType = Code("type");
    private static readonly uint TypeUtf8Text = Code("utf8");
    private static readonly uint TypeWildCard = Code("****");
    private static readonly uint FormAbsolutePosition = Code("indx");
    private static readonly uint FormPropertyId = Code("prop");
    private static readonly uint PropertyClass = Code("prop");

    private const int WaitReply = 3;
    private const int TicksPerSecond = 60;

    internal const int NotPermittedError = -1743;
    internal const int WouldRequireConsentError = -1744;
    internal const int ProcessNotFoundError = -600;

    private const string CoreServices = "/System/Library/Frameworks/CoreServices.framework/CoreServices";

    /// <summary>Asks whether this process may script Chrome, showing the macOS consent prompt only
    /// when <paramref name="askUserIfNeeded"/>. Returns 0 when allowed, else
    /// <see cref="NotPermittedError"/>, <see cref="WouldRequireConsentError"/> or
    /// <see cref="ProcessNotFoundError"/>.</summary>
    /// <remarks>Never sends anything to Chrome: tccd answers.</remarks>
    public static int DeterminePermission(int pid, bool askUserIfNeeded)
    {
        var target = ProcessTarget(pid);

        try
        {
            return AEDeterminePermissionToAutomateTarget(ref target, TypeWildCard, TypeWildCard, askUserIfNeeded);
        }
        finally
        {
            AEDisposeDesc(ref target);
        }
    }

    public static (AppleEventStatus Status, int Count) CountWindows(int pid, TimeSpan timeout)
    {
        var target = ProcessTarget(pid);
        var nothing = Null();
        var windowType = TypeCode(WindowClass);

        try
        {
            Check(AECreateAppleEvent(CoreSuite, CountEvent, ref target, -1, 0, out var appleEvent));

            try
            {
                Check(AEPutParamDesc(ref appleEvent, DirectObject, ref nothing));
                Check(AEPutParamDesc(ref appleEvent, ObjectClassKeyword, ref windowType));

                var (status, reply) = Send(ref appleEvent, timeout);

                if (status != AppleEventStatus.Ok)
                    return (status, 0);

                try
                {
                    return ReadInt32(ref reply) is { } count ? (AppleEventStatus.Ok, count) : (AppleEventStatus.NoResult, 0);
                }
                finally
                {
                    AEDisposeDesc(ref reply);
                }
            }
            finally
            {
                AEDisposeDesc(ref appleEvent);
            }
        }
        finally
        {
            AEDisposeDesc(ref windowType);
            AEDisposeDesc(ref nothing);
            AEDisposeDesc(ref target);
        }
    }

    /// <summary>Runs <paramref name="javaScript"/> in the active tab of window
    /// <paramref name="windowIndex"/> (1 is frontmost) and returns what it evaluated to.</summary>
    public static (AppleEventStatus Status, string? Result) ExecuteJavaScript(int pid, int windowIndex, string javaScript, TimeSpan timeout)
    {
        var target = ProcessTarget(pid);

        try
        {
            Check(AECreateAppleEvent(ChromeSuite, ExecuteEvent, ref target, -1, 0, out var appleEvent));

            try
            {
                var tab = ActiveTabOfWindow(windowIndex);

                try
                {
                    Check(AEPutParamDesc(ref appleEvent, DirectObject, ref tab));
                }
                finally
                {
                    AEDisposeDesc(ref tab);
                }

                var script = Text(javaScript);

                try
                {
                    Check(AEPutParamDesc(ref appleEvent, JavaScriptParameter, ref script));
                }
                finally
                {
                    AEDisposeDesc(ref script);
                }

                var (status, reply) = Send(ref appleEvent, timeout);

                if (status != AppleEventStatus.Ok)
                    return (status, null);

                try
                {
                    return ReadText(ref reply) is { } text ? (AppleEventStatus.Ok, text) : (AppleEventStatus.NoResult, null);
                }
                finally
                {
                    AEDisposeDesc(ref reply);
                }
            }
            finally
            {
                AEDisposeDesc(ref appleEvent);
            }
        }
        finally
        {
            AEDisposeDesc(ref target);
        }
    }

    internal static uint Code(string fourCharacters)
    {
        if (fourCharacters.Length != 4)
            throw new ArgumentException("An Apple Event code is exactly four characters.", nameof(fourCharacters));

        return fourCharacters.Aggregate(0u, (code, character) => (code << 8) | (byte)character);
    }

    private static (AppleEventStatus Status, AEDesc Reply) Send(ref AEDesc appleEvent, TimeSpan timeout)
    {
        var ticks = (nint)Math.Max(1, timeout.TotalSeconds * TicksPerSecond);
        var sent = AESendMessage(ref appleEvent, out var reply, WaitReply, ticks);

        if (sent != 0)
        {
            AEDisposeDesc(ref reply);
            return (StatusFor(sent), default);
        }

        // Chrome reports a script error as an error number in an otherwise successful reply.
        if (AEGetParamDesc(ref reply, ErrorNumberKeyword, TypeSInt32, out var error) == 0)
        {
            var number = ReadInt32Desc(ref error);
            AEDisposeDesc(ref error);

            if (number is { } code and not 0)
            {
                AEDisposeDesc(ref reply);
                return (StatusFor(code), default);
            }
        }

        return (AppleEventStatus.Ok, reply);
    }

    private static AppleEventStatus StatusFor(int error) => error switch
    {
        NotPermittedError or WouldRequireConsentError => AppleEventStatus.NotPermitted,
        _ => AppleEventStatus.Failed,
    };

    private static AEDesc ActiveTabOfWindow(int windowIndex)
    {
        var application = Null();
        var index = Int32(windowIndex);
        Check(CreateObjSpecifier(WindowClass, ref application, FormAbsolutePosition, ref index, true, out var window));

        var property = TypeCode(ActiveTab);
        Check(CreateObjSpecifier(PropertyClass, ref window, FormPropertyId, ref property, true, out var tab));

        return tab;
    }

    private static AEDesc ProcessTarget(int pid)
    {
        Check(AECreateDesc(TypeKernelProcessId, ref pid, sizeof(int), out var desc));
        return desc;
    }

    private static AEDesc Null()
    {
        Check(AECreateDesc(TypeNull, IntPtr.Zero, 0, out var desc));
        return desc;
    }

    private static AEDesc Int32(int value)
    {
        Check(AECreateDesc(TypeSInt32, ref value, sizeof(int), out var desc));
        return desc;
    }

    private static AEDesc TypeCode(uint code)
    {
        var value = (int)code;
        Check(AECreateDesc(TypeType, ref value, sizeof(int), out var desc));
        return desc;
    }

    private static AEDesc Text(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        Check(AECreateDesc(TypeUtf8Text, bytes, bytes.Length, out var desc));
        return desc;
    }

    private static string? ReadText(ref AEDesc reply)
    {
        // A "missing value" will not coerce to text; that is an answer, not a failure.
        var got = AEGetParamDesc(ref reply, DirectObject, TypeUtf8Text, out var text);

        if (got != 0)
            return null;

        try
        {
            var size = AEGetDescDataSize(ref text);
            var buffer = new byte[size];

            Check(AEGetDescData(ref text, buffer, size));

            return Encoding.UTF8.GetString(buffer);
        }
        finally
        {
            AEDisposeDesc(ref text);
        }
    }

    private static int? ReadInt32(ref AEDesc reply)
    {
        if (AEGetParamDesc(ref reply, DirectObject, TypeSInt32, out var number) != 0)
            return null;

        try
        {
            return ReadInt32Desc(ref number);
        }
        finally
        {
            AEDisposeDesc(ref number);
        }
    }

    private static int? ReadInt32Desc(ref AEDesc desc)
    {
        var buffer = new byte[sizeof(int)];

        return AEGetDescData(ref desc, buffer, buffer.Length) == 0 ? BitConverter.ToInt32(buffer) : null;
    }

    private static void Check(int status)
    {
        if (status != 0)
            throw new InvalidOperationException($"Apple Event call failed ({status}).");
    }

    // The Carbon headers declare this under #pragma pack(2): 12 bytes, the handle at offset 4.
    // Default packing puts it at 8, and a struct copy then drops half the handle.
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct AEDesc
    {
        public uint DescriptorType;
        public IntPtr DataHandle;
    }

    [DllImport(CoreServices)]
    private static extern short AECreateDesc(uint typeCode, ref int data, nint dataSize, out AEDesc result);

    [DllImport(CoreServices)]
    private static extern short AECreateDesc(uint typeCode, byte[] data, nint dataSize, out AEDesc result);

    [DllImport(CoreServices)]
    private static extern short AECreateDesc(uint typeCode, IntPtr data, nint dataSize, out AEDesc result);

    [DllImport(CoreServices)]
    private static extern short AECreateAppleEvent(
        uint eventClass, uint eventId, ref AEDesc target, short returnId, int transactionId, out AEDesc result);

    [DllImport(CoreServices)]
    private static extern short AEPutParamDesc(ref AEDesc appleEvent, uint keyword, ref AEDesc desc);

    [DllImport(CoreServices)]
    private static extern short CreateObjSpecifier(
        uint desiredClass, ref AEDesc container, uint keyForm, ref AEDesc keyData,
        [MarshalAs(UnmanagedType.U1)] bool disposeInputs, out AEDesc result);

    [DllImport(CoreServices)]
    private static extern int AESendMessage(ref AEDesc appleEvent, out AEDesc reply, int sendMode, nint timeoutInTicks);

    [DllImport(CoreServices)]
    private static extern short AEGetParamDesc(ref AEDesc appleEvent, uint keyword, uint desiredType, out AEDesc result);

    [DllImport(CoreServices)]
    private static extern nint AEGetDescDataSize(ref AEDesc desc);

    [DllImport(CoreServices)]
    private static extern short AEGetDescData(ref AEDesc desc, byte[] buffer, nint maximumSize);

    [DllImport(CoreServices)]
    private static extern short AEDisposeDesc(ref AEDesc desc);

    [DllImport(CoreServices)]
    private static extern int AEDeterminePermissionToAutomateTarget(
        ref AEDesc target, uint eventClass, uint eventId, [MarshalAs(UnmanagedType.U1)] bool askUserIfNeeded);
}
