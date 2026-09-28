using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace DipTick.Models.Harness;

/// <summary>One anchor a witness kept: how many lines the log had, its last MAC, and when it was seen.</summary>
internal sealed record WitnessedAnchor(int Count, string Last, DateTimeOffset When);

/// <summary>
/// A record of the audit log's anchor kept outside the log's folder, so deleting the log, its
/// key and its anchor together still shows.
/// </summary>
internal interface IAnchorWitness
{
    /// <summary>The log at <paramref name="log"/> now has <paramref name="count"/> lines ending in <paramref name="last"/>.</summary>
    void Saw(string log, int count, string last);

    /// <summary>Writes what is still to be written: at exit.</summary>
    void Flush();

    /// <summary>The anchors kept for the log at <paramref name="log"/>, newest first, at most <paramref name="most"/>; null when they cannot be read.</summary>
    IReadOnlyList<WitnessedAnchor>? Anchors(string log, int most);

    /// <summary>
    /// When the oldest record the witness still holds, of anything, was made: how far back it can
    /// remember; null when it cannot say.
    /// </summary>
    DateTimeOffset? Reaches();
}

/// <summary>
/// The Windows event log as the audit log's witness. DipTick writes the
/// anchor there, the entry count and the last MAC and which log they are for: at most once a minute
/// while it writes, and when it closes. Without an install step the source is not registered: "If the
/// source name cannot be found, the event logging service uses the Application log", its events without
/// descriptions (RegisterEventSource, Microsoft Learn); the event's one string carries the anchor. Once
/// <c>diptick audit protect</c> has made DipTick's own log (<see cref="ProtectedLog"/>),
/// the source is registered there and the anchors go to it. Read back with the Windows Event Log API
/// (EvtQuery, newest first), from both logs and any archive. Both through Windows' own functions,
/// advapi32 and wevtapi: no package.
/// </summary>
/// <remarks>
/// <para>
/// Who can clear the Application log, from its access rules (<c>wevtutil gl Application</c>,
/// 2026-09-27): SYSTEM, Administrators and Server Operators; interactive users may read and write
/// it but not clear it, and app containers may only write. So a forged event is possible from an
/// app container, and verifying compares the log with every anchor kept, not the newest alone.
/// </para>
/// <para>
/// How long it remembers: the log is circular, 20 MB by default, the oldest events overwritten
/// first (<c>Get-WinEvent -ListLog Application</c>).
/// A program running as the person can write enough to push DipTick's anchors out sooner, so
/// verify says how far back the log reaches (<see cref="Reaches"/>) beside what it found.
/// </para>
/// </remarks>
internal sealed partial class EventLogWitness : IAnchorWitness
{
    /// <summary>The event source DipTick writes as.</summary>
    public const string Source = "DipTick";

    /// <summary>The one event id DipTick writes: an audit anchor.</summary>
    public const int AnchorEvent = 1;

    private const string Prefix = "DipTick audit anchor v1";

    // winbase.h: EVENTLOG_INFORMATION_TYPE. winevt.h: EvtQueryChannelPath, EvtQueryForwardDirection
    // (oldest first, the default), EvtQueryReverseDirection, EvtRenderEventXml. winerror.h:
    // ERROR_NO_MORE_ITEMS, ERROR_INSUFFICIENT_BUFFER.
    private const ushort InformationType = 0x0004;
    private const uint QueryChannelPath = 0x1;
    private const uint QueryFilePath = 0x2;
    private const uint QueryForwardDirection = 0x100;
    private const uint QueryReverseDirection = 0x200;
    private const uint RenderEventXml = 1;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorInsufficientBuffer = 122;

    private static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, (int Count, string Last)> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _written = new(StringComparer.OrdinalIgnoreCase);
    private bool _hooked;

    public void Saw(string log, int count, string last)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(last);
        bool now;
        lock (_lock)
        {
            _pending[log] = (count, last);
            now = !_written.TryGetValue(log, out var at) || DateTime.UtcNow - at >= Every;
            if (!_hooked)
            {
                // What was seen since the last write is written when this process ends.
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
                _hooked = true;
            }
        }

        if (now)
        {
            Write(log);
        }
    }

    public void Flush()
    {
        string[] logs;
        lock (_lock)
        {
            logs = [.. _pending.Keys];
        }

        foreach (var log in logs)
        {
            Write(log);
        }
    }

    /// <summary>
    /// The anchors kept for a log, newest first: in DipTick's own log where it has one, in
    /// the Application log, which held them before it and holds them without it, and in the archives an
    /// administrator made of DipTick's own. Null when the Application log itself cannot be read.
    /// </summary>
    public IReadOnlyList<WitnessedAnchor>? Anchors(string log, int most)
    {
        ArgumentNullException.ThrowIfNull(log);
        var which = LogId(log);
        if (Query("Application", QueryChannelPath, which, most) is not { } found)
        {
            return null;
        }

        if (ProtectedLog.Exists && Query(ProtectedLog.Name, QueryChannelPath, which, most) is { } own)
        {
            found.AddRange(own);
        }

        foreach (var archive in ProtectedLog.Archives())
        {
            if (Query(archive, QueryFilePath, which, most) is { } kept)
            {
                found.AddRange(kept);
            }
        }

        return [.. found.OrderByDescending(a => a.When).ThenByDescending(a => a.Count).Take(most)];
    }

    /// <summary>One place's anchors for a log, newest first, at most <paramref name="most"/>; null when it cannot be read.</summary>
    private static List<WitnessedAnchor>? Query(string path, uint kind, string which, int most)
    {
        var found = new List<WitnessedAnchor>();
        var query = EvtQuery(0, path, $"*[System[Provider[@Name='{Source}'] and (EventID={AnchorEvent.ToString(CultureInfo.InvariantCulture)})]]", kind | QueryReverseDirection);
        if (query == 0)
        {
            return null;
        }

        try
        {
            var events = new nint[16];
            while (found.Count < most)
            {
                if (!EvtNext(query, (uint)events.Length, events, 1000, 0, out var returned))
                {
                    return Marshal.GetLastPInvokeError() == ErrorNoMoreItems ? found : null;
                }

                for (var i = 0; i < returned; i++)
                {
                    try
                    {
                        if (Parse(Render(events[i]), which) is { } anchor && found.Count < most)
                        {
                            found.Add(anchor);
                        }
                    }
                    finally
                    {
                        _ = EvtClose(events[i]);
                    }
                }
            }

            return found;
        }
        finally
        {
            _ = EvtClose(query);
        }
    }

    /// <summary>The anchor as the event's string says it: the log it is for, by a digest of its path, never the path.</summary>
    internal static string Text(string log, int count, string last) =>
        string.Create(CultureInfo.InvariantCulture, $"{Prefix} log={LogId(log)} count={count} last={last}");

    /// <summary>A log's name in the event log: the first 16 hex digits of the SHA-256 of its full path, upper-cased, as Windows compares paths.</summary>
    internal static string LogId(string log) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(log).ToUpperInvariant())))[..16];

    public DateTimeOffset? Reaches()
    {
        var query = EvtQuery(0, "Application", "*", QueryChannelPath | QueryForwardDirection);
        if (query == 0)
        {
            return null;
        }

        try
        {
            var events = new nint[1];
            if (!EvtNext(query, 1, events, 1000, 0, out var returned) || returned == 0)
            {
                return null;
            }

            try
            {
                return Created(Render(events[0]));
            }
            finally
            {
                _ = EvtClose(events[0]);
            }
        }
        finally
        {
            _ = EvtClose(query);
        }
    }

    /// <summary>An anchor from an event's XML, when it is one of DipTick's and for the log named <paramref name="which"/>.</summary>
    internal static WitnessedAnchor? Parse(string? xml, string which)
    {
        if (Load(xml) is not { } loaded)
        {
            return null;
        }

        var data = loaded.Document.SelectSingleNode("/e:Event/e:EventData/e:Data", loaded.Names)?.InnerText;
        if (data is null || !data.StartsWith(Prefix + " ", StringComparison.Ordinal))
        {
            return null;
        }

        var fields = data[(Prefix.Length + 1)..].Split(' ').Select(f => f.Split('=', 2)).Where(f => f.Length == 2).ToDictionary(f => f[0], f => f[1], StringComparer.Ordinal);
        if (!fields.TryGetValue("log", out var log) || log != which
            || !fields.TryGetValue("count", out var countText) || !int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            || !fields.TryGetValue("last", out var last) || last.Length != 64)
        {
            return null;
        }

        return new WitnessedAnchor(count, last, Created(loaded) ?? DateTimeOffset.MinValue);
    }

    /// <summary>When the event in <paramref name="xml"/> was made, as the event log service stamped it; null when it does not say.</summary>
    internal static DateTimeOffset? Created(string? xml) => Load(xml) is { } loaded ? Created(loaded) : null;

    private static DateTimeOffset? Created((XmlDocument Document, XmlNamespaceManager Names) loaded) =>
        DateTimeOffset.TryParse(loaded.Document.SelectSingleNode("/e:Event/e:System/e:TimeCreated/@SystemTime", loaded.Names)?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    /// <summary>An event's XML as Windows renders it, read with no document type allowed; null when it is not XML.</summary>
    private static (XmlDocument Document, XmlNamespaceManager Names)? Load(string? xml)
    {
        if (string.IsNullOrEmpty(xml))
        {
            return null;
        }

        var document = new XmlDocument { XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        var names = new XmlNamespaceManager(document.NameTable);
        names.AddNamespace("e", "http://schemas.microsoft.com/win/2004/08/events/event");
        return (document, names);
    }

    private void Write(string log)
    {
        (int Count, string Last) anchor;
        lock (_lock)
        {
            if (!_pending.Remove(log, out anchor))
            {
                return;
            }

            _written[log] = DateTime.UtcNow;
        }

        // A log that cannot be witnessed is still a log: the file's own chain and anchor stand, and
        // verify says the event log holds nothing for it.
        Note(AnchorEvent, Text(log, anchor.Count, anchor.Last));
    }

    /// <summary>
    /// One event of DipTick's, its one string <paramref name="text"/>: to DipTick's own log when its source is
    /// registered there, else, the source not found, to the Application log.
    /// </summary>
    internal static void Note(int eventId, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var source = RegisterEventSource(null, Source);
        if (source == 0)
        {
            return;
        }

        try
        {
            var native = Marshal.StringToHGlobalUni(text);
            try
            {
                nint[] strings = [native];
                _ = ReportEvent(source, InformationType, 0, eventId, 0, 1, 0, strings, 0);
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }
        }
        finally
        {
            _ = DeregisterEventSource(source);
        }
    }

    private static string? Render(nint evt)
    {
        if (!EvtRender(0, evt, RenderEventXml, 0, 0, out var used, out _) && Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)used);
        try
        {
            return EvtRender(0, evt, RenderEventXml, used, buffer, out _, out _) ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [LibraryImport("advapi32.dll", EntryPoint = "RegisterEventSourceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint RegisterEventSource(string? server, string source);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeregisterEventSource(nint source);

    [LibraryImport("advapi32.dll", EntryPoint = "ReportEventW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReportEvent(nint source, ushort type, ushort category, int eventId, nint userSid, ushort stringCount, int dataSize, [In] nint[] strings, nint data);

    [LibraryImport("wevtapi.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint EvtQuery(nint session, string path, string query, uint flags);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtNext(nint resultSet, uint eventsSize, [Out] nint[] events, uint timeout, uint flags, out uint returned);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtRender(nint context, nint fragment, uint flags, uint bufferSize, nint buffer, out uint bufferUsed, out uint propertyCount);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtClose(nint handle);
}
