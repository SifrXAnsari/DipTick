using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace DipTick.Models.Harness;

/// <summary>What DipTick's own event log is now: whether it is there and as DipTick set it, how full, since when, and any archives.</summary>
internal sealed record ProtectedLogState(bool Exists, bool Retains, ulong MaxSize, string? Access, bool Full, ulong FileSize, ulong Records, DateTimeOffset? Since, IReadOnlyList<string> Archives, string? Problem)
{
    /// <summary>Whether it is there and holds what DipTick set: never overwritten, at its size, with its access.</summary>
    public bool Protected => Exists && Retains && Access == ProtectedLog.Access && MaxSize == ProtectedLog.MaxSize;

    /// <summary>The state in one sentence, as verify and <c>audit protect --status</c> say it.</summary>
    public string Words
    {
        get
        {
            if (!Exists)
            {
                return "the anchors are in the Application log, which overwrites itself: 'diptick audit protect', run as administrator, gives DipTick a log of its own";
            }

            if (!Protected)
            {
                return $"DipTick's own event log is there but not as DipTick set it ({Problem ?? "its size, retention or access differ"}): 'diptick audit protect', run as administrator, sets it again";
            }

            var used = string.Create(CultureInfo.InvariantCulture, $"{FileSize / (1024 * 1024)} MB of {MaxSize / (1024 * 1024)} MB used");
            var since = Since is { } at ? string.Create(CultureInfo.InvariantCulture, $" since {at.ToLocalTime():yyyy-MM-dd}") : string.Empty;
            var archives = Archives.Count == 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $", and {Archives.Count} archive{(Archives.Count == 1 ? string.Empty : "s")} kept");
            return Full
                ? $"DipTick's own event log is full ({used}): new anchors are not kept until an administrator runs 'diptick audit protect --archive'; every anchor it holds stays{archives}"
                : $"DipTick's own event log keeps every anchor{since}, never overwritten, cleared only by an administrator ({used}{archives})";
        }
    }
}

/// <summary>
/// DipTick's own event log: a classic log named DipTick, with DipTick's event source, that never
/// overwrites and that only Administrators and SYSTEM may clear. Made once by
/// <c>diptick audit protect</c> run as administrator; until then, and without it, the anchors go to
/// the Application log.
/// </summary>
/// <remarks>
/// <para>
/// From Microsoft Learn, "Eventlog Key": a custom log sets its own <c>MaxSize</c>, <c>Retention</c> ("If this
/// value is 0xFFFFFFFF or any nonzero value, records are never overwritten. When the log file reaches its
/// maximum size, you must clear the log manually; otherwise, new events are discarded") and access
/// (<c>CustomSD</c>: Clear 0x4, Read 0x1, Write 0x2). A log's settings are set as <c>wevtutil</c> sets them:
/// <c>EvtOpenChannelConfig</c>, <c>EvtSetChannelConfigProperty</c> (Access 5, Retention 6, AutoBackup 7,
/// MaxSize 8), then <c>EvtSaveChannelConfig</c>, which applies them. <c>EvtGetLogInfo</c>'s EvtLogFull (7)
/// says when "another event cannot be written". <c>EvtClearLog</c> "removes all events from the specified
/// channel and writes them to the target log file", which is how an archive is made.
/// </para>
/// <para>
/// Its access: SYSTEM all (0xf0007, as the Application log gives it), Administrators read, write and clear,
/// interactive and batch users read and write, Event Log Readers read; no entry for app containers, so the
/// sandbox cannot write it. A program running as the person can still write to it, and fill it: then new
/// anchors stop, which verify says, and every anchor already there stays.
/// </para>
/// </remarks>
internal static partial class ProtectedLog
{
    /// <summary>The log's name, and its one source's.</summary>
    public const string Name = "DipTick";

    /// <summary>The log's size: 1 GiB, a multiple of 64 KiB.</summary>
    public const ulong MaxSize = 1UL << 30;

    /// <summary>Who may do what with it, in SDDL (its owner and group are not used, but must be there).</summary>
    public const string Access = "O:BAG:SYD:(A;;0xf0007;;;SY)(A;;0x7;;;BA)(A;;0x3;;;IU)(A;;0x3;;;S-1-5-3)(A;;0x1;;;S-1-5-32-573)";

    /// <summary>The event id of the note that says the log was set up, beside the anchors' own.</summary>
    public const int SetUpEvent = 2;

    private const string EventLogKey = @"SYSTEM\CurrentControlSet\Services\EventLog";

    // winevt.h: EVT_CHANNEL_CONFIG_PROPERTY_ID, EVT_LOG_PROPERTY_ID, EVT_VARIANT_TYPE, EvtOpenChannelPath.
    private const int ConfigAccess = 5;
    private const int ConfigRetention = 6;
    private const int ConfigAutoBackup = 7;
    private const int ConfigMaxSize = 8;
    private const int LogCreationTime = 0;
    private const int LogFileSize = 3;
    private const int LogRecords = 5;
    private const int LogFull = 7;
    private const uint VarString = 1;
    private const uint VarUInt64 = 10;
    private const uint VarBoolean = 13;
    private const uint VarFileTime = 17;
    private const uint OpenChannelPath = 0x1;
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>Where archives go: a folder under ProgramData that only administrators write.</summary>
    public static string ArchiveFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DipTick", "anchors");

    /// <summary>Whether the log is there: its key in the event log's settings, which anyone may read.</summary>
    public static bool Exists
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"{EventLogKey}\{Name}");
                return key is not null;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }
    }

    /// <summary>Whether this process runs as an administrator, elevated.</summary>
    public static bool Elevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Why this process cannot make the log, or archive it, or null: either needs an administrator, elevated.</summary>
    public static string? AdministratorProblem(bool archive) =>
        Elevated ? null
        : archive ? "only an administrator can archive the log: run 'diptick audit protect --archive' from a terminal opened as administrator; nothing was changed"
        : "only an administrator can make a log: run 'diptick audit protect' from a terminal opened as administrator; nothing was changed";

    /// <summary>The archives kept, oldest first.</summary>
    public static IReadOnlyList<string> Archives()
    {
        try
        {
            return Directory.Exists(ArchiveFolder) ? [.. Directory.EnumerateFiles(ArchiveFolder, "*.evtx").Order(StringComparer.OrdinalIgnoreCase)] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The log as it is now; nothing is changed.</summary>
    public static ProtectedLogState State()
    {
        var archives = Archives();
        if (!Exists)
        {
            return new ProtectedLogState(false, false, 0, null, false, 0, 0, null, archives, null);
        }

        var config = EvtOpenChannelConfig(0, Name, 0);
        if (config == 0)
        {
            return new ProtectedLogState(true, false, 0, null, false, 0, 0, null, archives, $"Windows has not taken the log up yet ({Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture)})");
        }

        bool retains;
        ulong maxSize;
        string? access;
        try
        {
            retains = ReadConfig(config, ConfigRetention) is { Type: VarBoolean } r && r.Boolean != 0;
            maxSize = ReadConfig(config, ConfigMaxSize) is { Type: VarUInt64 } m ? m.UInt64 : 0;
            access = ReadConfigString(config, ConfigAccess);
        }
        finally
        {
            _ = EvtClose(config);
        }

        var log = EvtOpenLog(0, Name, OpenChannelPath);
        if (log == 0)
        {
            return new ProtectedLogState(true, retains, maxSize, access, false, 0, 0, null, archives, "its file could not be read");
        }

        try
        {
            var full = ReadLog(log, LogFull) is { Type: VarBoolean } f && f.Boolean != 0;
            var size = ReadLog(log, LogFileSize) is { Type: VarUInt64 } s ? s.UInt64 : 0;
            var records = ReadLog(log, LogRecords) is { Type: VarUInt64 } n ? n.UInt64 : 0;
            DateTimeOffset? since = ReadLog(log, LogCreationTime) is { Type: VarFileTime } t ? DateTimeOffset.FromFileTime((long)t.UInt64) : null;
            var problem = !retains ? "it overwrites itself" : access != Access ? "its access is not DipTick's" : maxSize != MaxSize ? "its size is not DipTick's" : null;
            return new ProtectedLogState(true, retains, maxSize, access, full, size, records, since, archives, problem);
        }
        finally
        {
            _ = EvtClose(log);
        }
    }

    /// <summary>
    /// Makes the log, or sets it again: run as administrator. Its key and its source's in the event log's
    /// settings (the source taken from any other log that had it), then its size, retention and access set
    /// through the Windows Event Log API and read back, then a note that it was set up. Null when done; else
    /// why not, with nothing half set that verify would take for protection.
    /// </summary>
    public static string? Protect()
    {
        if (AdministratorProblem(archive: false) is { } notAdministrator)
        {
            return notAdministrator;
        }

        try
        {
            using var logs = Registry.LocalMachine.OpenSubKey(EventLogKey, writable: true) ?? throw new IOException("the event log's settings are not there");
            foreach (var other in logs.GetSubKeyNames().Where(n => !n.Equals(Name, StringComparison.OrdinalIgnoreCase)))
            {
                using var otherLog = logs.OpenSubKey(other, writable: true);
                if (otherLog?.OpenSubKey(Name) is { } stray)
                {
                    stray.Dispose();
                    otherLog.DeleteSubKeyTree(Name);
                }
            }

            using var own = logs.CreateSubKey(Name, writable: true);
            own.SetValue("MaxSize", unchecked((int)MaxSize), RegistryValueKind.DWord);
            own.SetValue("Retention", -1, RegistryValueKind.DWord);
            own.SetValue("AutoBackupLogFiles", 0, RegistryValueKind.DWord);
            own.SetValue("CustomSD", Access, RegistryValueKind.String);
            own.CreateSubKey(Name, writable: true).Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return $"the log could not be made: {SafeText.Line(ex.Message)}";
        }

        // The service takes a new log up on its own; the settings are set once it has.
        nint handle = 0;
        for (var tries = 0; tries < 20; tries++)
        {
            handle = EvtOpenChannelConfig(0, Name, 0);
            if (handle != 0)
            {
                break;
            }

            Thread.Sleep(500);
        }

        if (handle == 0)
        {
            return "the log's settings are written, but Windows has not taken the log up yet: run 'diptick audit protect' again in a minute";
        }

        try
        {
            var text = Marshal.StringToHGlobalUni(Access);
            try
            {
                if (!Set(handle, ConfigAccess, new EvtVariant { Pointer = text, Type = VarString })
                    || !Set(handle, ConfigRetention, new EvtVariant { Boolean = 1, Type = VarBoolean })
                    || !Set(handle, ConfigAutoBackup, new EvtVariant { Boolean = 0, Type = VarBoolean })
                    || !Set(handle, ConfigMaxSize, new EvtVariant { UInt64 = MaxSize, Type = VarUInt64 })
                    || !EvtSaveChannelConfig(handle, 0))
                {
                    return $"Windows would not take the log's settings: {new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()).Message}";
                }
            }
            finally
            {
                Marshal.FreeHGlobal(text);
            }
        }
        finally
        {
            _ = EvtClose(handle);
        }

        var state = State();
        if (!state.Protected)
        {
            return $"the log is there, but reading it back shows {state.Problem ?? "something else than was set"}";
        }

        EventLogWitness.Note(SetUpEvent, "DipTick audit log protected v1");
        return null;
    }

    /// <summary>
    /// The log saved whole to a new archive under <see cref="ArchiveFolder"/> (only administrators write
    /// there), then cleared: run as administrator, when it is full. Returns the archive, or why not.
    /// </summary>
    public static (string? Archive, string? Problem) Archive()
    {
        if (AdministratorProblem(archive: true) is { } notAdministrator)
        {
            return (null, notAdministrator);
        }

        if (!Exists)
        {
            return (null, "DipTick has no log of its own yet: 'diptick audit protect' makes it");
        }

        try
        {
            var folder = new DirectoryInfo(ArchiveFolder);
            folder.Create();
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            folder.SetAccessControl(security);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"its archive folder could not be made: {SafeText.Line(ex.Message)}");
        }

        var archive = Path.Combine(ArchiveFolder, string.Create(CultureInfo.InvariantCulture, $"anchors-{DateTime.Now:yyyyMMdd-HHmmss}.evtx"));
        return EvtClearLog(0, Name, archive, 0)
            ? (archive, null)
            : (null, $"Windows would not save and clear the log: {new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()).Message}");
    }

    private static bool Set(nint config, int property, EvtVariant value) => EvtSetChannelConfigProperty(config, property, 0, ref value);

    private static EvtVariant? ReadConfig(nint config, int property) => Read((size, buffer) => (EvtGetChannelConfigProperty(config, property, 0, size, buffer, out var used), used));

    private static string? ReadConfigString(nint config, int property)
    {
        _ = EvtGetChannelConfigProperty(config, property, 0, 0, 0, out var needed);
        if (needed == 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!EvtGetChannelConfigProperty(config, property, 0, needed, buffer, out _))
            {
                return null;
            }

            var value = Marshal.PtrToStructure<EvtVariant>(buffer);
            return value.Type == VarString && value.Pointer != 0 ? Marshal.PtrToStringUni(value.Pointer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static EvtVariant? ReadLog(nint log, int property) => Read((size, buffer) => (EvtGetLogInfo(log, property, size, buffer, out var used), used));

    /// <summary>One variant read: asked for its size first, then read into a buffer of that size.</summary>
    private static EvtVariant? Read(Func<uint, nint, (bool Done, uint Used)> call)
    {
        var (done, needed) = call(0, 0);
        if (!done && Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer)
        {
            return null;
        }

        var size = Math.Max(needed, (uint)Marshal.SizeOf<EvtVariant>());
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            return call(size, buffer).Done ? Marshal.PtrToStructure<EvtVariant>(buffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary><c>EVT_VARIANT</c>: an eight-byte union, its count and its type.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct EvtVariant
    {
        [FieldOffset(0)]
        public ulong UInt64;

        [FieldOffset(0)]
        public int Boolean;

        [FieldOffset(0)]
        public nint Pointer;

        [FieldOffset(8)]
        public uint Count;

        [FieldOffset(12)]
        public uint Type;
    }

    [LibraryImport("wevtapi.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint EvtOpenChannelConfig(nint session, string channel, uint flags);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtGetChannelConfigProperty(nint config, int property, uint flags, uint bufferSize, nint buffer, out uint used);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtSetChannelConfigProperty(nint config, int property, uint flags, ref EvtVariant value);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtSaveChannelConfig(nint config, uint flags);

    [LibraryImport("wevtapi.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint EvtOpenLog(nint session, string path, uint flags);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtGetLogInfo(nint log, int property, uint bufferSize, nint buffer, out uint used);

    [LibraryImport("wevtapi.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtClearLog(nint session, string channel, string? target, uint flags);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtClose(nint handle);
}
