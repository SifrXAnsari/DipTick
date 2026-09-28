using System.Globalization;
using System.Text;
using System.Text.Json;
using DipTick.Models.Harness;
using DipTick.Models.Sandbox;

namespace DipTick.Models.Connectors;

/// <summary>
/// Where a local connector runs: in an AppContainer of its own with the
/// network its entry names and the folders it names, and nothing else of yours (<see cref="On"/>); or as
/// you, with your files, your network and your rights (off: the entry's <c>"sandbox": false</c>, or its
/// <c>"on": false</c>, which keeps what the sandbox named for when it is on again).
/// </summary>
/// <remarks>
/// Windows grants a container its network by kind, not by host (App capability declarations: internetClient
/// has "No local network access"; privateNetworkClientServer "does not provide access to the Internet"), and
/// a folder by that folder's access list: the shape follows what Windows can hold, where VS Code's sandbox,
/// on macOS and Linux only, names domains.
/// </remarks>
internal sealed record ConnectorSandbox(bool On, string Network, IReadOnlyList<string> Read, IReadOnlyList<string> Write)
{
    public const string None = "none";
    public const string Internet = "internet";
    public const string Local = "local";
    public const string Both = "both";

    /// <summary>The most folders one connector may name, reading and writing together.</summary>
    public const int MostFolders = 16;

    /// <summary>What an entry with no <c>sandbox</c> gets: its own container, no network, no folders.</summary>
    public static ConnectorSandbox Default { get; } = new(true, None, [], []);

    /// <summary>The network kinds an entry may name.</summary>
    public static IReadOnlyList<string> Networks { get; } = [None, Internet, Local, Both];

    /// <summary>The capabilities its container is started with.</summary>
    public IReadOnlyList<string> Capabilities => Network switch
    {
        Internet => [AppContainer.InternetClient],
        Local => [AppContainer.PrivateNetworkClientServer],
        Both => [AppContainer.InternetClient, AppContainer.PrivateNetworkClientServer],
        _ => [],
    };

    /// <summary>Its network in words.</summary>
    public string NetworkWords => Network switch
    {
        Internet => "the internet, not your own networks",
        Local => "your home and work networks, not the internet",
        Both => "the internet and your home and work networks",
        _ => "no network",
    };

    /// <summary>The whole of it in one line: what the list, the consent and Settings show.</summary>
    public string Words
    {
        get
        {
            if (!On)
            {
                return "runs as you, with your files, your network and your rights";
            }

            var folders = new List<string>();
            folders.AddRange(Read.Select(f => $"reads {f}"));
            folders.AddRange(Write.Select(f => $"reads and writes {f}"));
            return $"in its own sandbox: {NetworkWords}; {(folders.Count == 0 ? "none of your folders" : string.Join("; ", folders))}";
        }
    }

    /// <summary>The whole of it in a few words, for a line with little room (a menu's choice): its network and how many of your folders.</summary>
    public string Brief => !On
        ? "runs as you"
        : $"{NetworkWords}; {(Read.Count + Write.Count) switch { 0 => "none of your folders", 1 => "one of your folders", var n => string.Create(CultureInfo.InvariantCulture, $"{n} of your folders") }}";

    /// <summary>What the yes is given to, in the consent hash: off, or the network and each folder with its access, as Windows compares paths.</summary>
    public string ConsentText()
    {
        if (!On)
        {
            return "sandbox=off";
        }

        var text = new StringBuilder("sandbox=on;network=").Append(Network);
        foreach (var folder in Read.Select(f => f.ToUpperInvariant()).Order(StringComparer.Ordinal))
        {
            text.Append(";read=").Append(folder);
        }

        foreach (var folder in Write.Select(f => f.ToUpperInvariant()).Order(StringComparer.Ordinal))
        {
            text.Append(";write=").Append(folder);
        }

        return text.ToString();
    }

    /// <summary>
    /// An entry's <c>sandbox</c>, read: false for off; an object with what it names, on unless its <c>on</c> is
    /// false (off, with what it named kept for when it is on again); left out or true for the default. A
    /// problem leaves the entry out.
    /// </summary>
    public static ConnectorSandbox? Parse(JsonElement entry, out string? problem)
    {
        problem = null;
        if (!entry.TryGetProperty("sandbox", out var value) || value.ValueKind == JsonValueKind.True)
        {
            return Default;
        }

        if (value.ValueKind == JsonValueKind.False)
        {
            return Default with { On = false };
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            problem = "\"sandbox\" is false, or an object with network, read and write; left out.";
            return null;
        }

        var on = true;
        if (value.TryGetProperty("on", out var switched))
        {
            if (switched.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                problem = "its sandbox's \"on\" is true or false; left out.";
                return null;
            }

            on = switched.ValueKind == JsonValueKind.True;
        }

        var network = value.TryGetProperty("network", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? None : None;
        if (!Networks.Contains(network, StringComparer.Ordinal))
        {
            problem = $"its sandbox's network is none, internet, local or both, not {SafeText.Line(network)}; left out.";
            return null;
        }

        var read = Folders(value, "read");
        var write = Folders(value, "write");
        if (read.Count + write.Count > MostFolders)
        {
            problem = string.Create(CultureInfo.InvariantCulture, $"its sandbox names more than {MostFolders} folders; left out.");
            return null;
        }

        if (read.Concat(write).FirstOrDefault(f => !Path.IsPathFullyQualified(f)) is { } relative)
        {
            problem = $"its sandbox names {SafeText.Line(relative)}, which is not a full path; left out.";
            return null;
        }

        return new ConnectorSandbox(on, network, read, write);
    }

    /// <summary>Written into its entry: false when off and naming nothing; else its network and folders, with <c>"on": false</c> when off.</summary>
    public void WriteTo(Utf8JsonWriter json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (!On && Network == None && Read.Count == 0 && Write.Count == 0)
        {
            json.WriteBoolean("sandbox", false);
            return;
        }

        json.WriteStartObject("sandbox");
        if (!On)
        {
            json.WriteBoolean("on", false);
        }

        json.WriteString("network", Network);
        WriteFolders(json, "read", Read);
        WriteFolders(json, "write", Write);
        json.WriteEndObject();
    }

    private static List<string> Folders(JsonElement value, string name) =>
        value.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => Path.TrimEndingDirectorySeparator(e.GetString()!)).Distinct(StringComparer.OrdinalIgnoreCase)]
            : [];

    private static void WriteFolders(Utf8JsonWriter json, string name, IReadOnlyList<string> folders)
    {
        if (folders.Count == 0)
        {
            return;
        }

        json.WriteStartArray(name);
        foreach (var folder in folders)
        {
            json.WriteStringValue(folder);
        }

        json.WriteEndArray();
    }
}

/// <summary>
/// A sandboxed connector's place on this PC: its home in DipTick's data, its container's grants on
/// the folders its entry names, kept in a record beside its home so each is taken back when the entry stops
/// naming it or the connector goes, and the environment it starts with.
/// </summary>
internal static class ConnectorPlace
{
    /// <summary>The job's caps for a connector: every UI restriction, this many processes and this much memory, and no CPU cap.</summary>
    public const int MostProcesses = 64;

    /// <summary>The most committed memory a connector's processes may hold together.</summary>
    public const long MostMemory = 4L * 1024 * 1024 * 1024;

    /// <summary>A connector's home: its working folder, and its profile, settings and temporary files when sandboxed.</summary>
    public static string Home(string connector) => Path.Combine(AuditLog.DataFolder(), "connectors", connector);

    /// <summary>
    /// The record of what a connector's container was given: beside its home, never in it, since its container
    /// may write its home and a record it could rewrite would let it keep a folder you took back. A name is
    /// a-z, 0-9 and - only, so the record's name is never a home's.
    /// </summary>
    internal static string GrantsRecord(string connector) => Path.Combine(AuditLog.DataFolder(), "connectors", connector + ".grants.json");

    /// <summary>
    /// Why a folder cannot be given to a connector, or null: it must be there, be a folder and not a link,
    /// sit on a drive that keeps access lists (NTFS or ReFS), and be neither DipTick's own settings or data
    /// nor hold them, since a connector that could write there could approve what DipTick asks you about.
    /// </summary>
    public static string? FolderProblem(string folder, string? settingsFolder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!Path.IsPathFullyQualified(folder) || folder.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return $"{folder} is not a full path on a drive of this PC";
        }

        DirectoryInfo info;
        try
        {
            info = new DirectoryInfo(folder);
            if (!info.Exists)
            {
                return $"{folder} is not there";
            }

            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return $"{folder} is a link to somewhere else; name the folder itself";
            }

            var format = new DriveInfo(info.Root.FullName).DriveFormat;
            if (format is not ("NTFS" or "ReFS"))
            {
                return $"{folder} is on a {format} drive, which keeps no access lists, so it cannot be given to one program alone";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"{folder} could not be looked at: {SafeText.Line(ex.Message)}";
        }

        (string? Folder, string What)[] owns = [(settingsFolder, "settings"), (AuditLog.DataFolder(), "data"), (Unpacked.Folder(), "unpacked libraries")];
        foreach (var (own, what) in owns)
        {
            if (own is null)
            {
                continue;
            }

            var mine = Path.GetFullPath(own);
            if (Overlaps(info.FullName, mine))
            {
                return Inside(info.FullName, mine)
                    ? $"{folder} is inside DipTick's own {what} folder, which no connector is given"
                    : $"{folder} is, or holds, DipTick's own {what}, which no connector is given";
            }
        }

        return null;
    }

    /// <summary>
    /// Makes the connector's place ready before it starts: its home, Modify for its container, with the
    /// temporary folder Windows gives a container inside it; each folder its entry names granted, and each
    /// grant it no longer names taken back.
    /// </summary>
    /// <exception cref="ConnectorException">A folder could not be granted, and why.</exception>
    public static void Prepare(Connector connector, nint sid, string? settingsFolder)
    {
        ArgumentNullException.ThrowIfNull(connector);
        var home = Home(connector.Name);
        try
        {
            Directory.CreateDirectory(home);
            AppContainer.Grant(sid, home, write: true);
            Directory.CreateDirectory(TemporaryFolder(connector.Name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConnectorException($"{connector.Name}'s own folder could not be made ready: {SafeText.Line(ex.Message)}", ex);
        }

        var wanted = connector.Box.Read.Select(f => (Folder: f, Write: false)).Concat(connector.Box.Write.Select(f => (Folder: f, Write: true))).ToList();
        foreach (var (folder, _) in wanted)
        {
            if (FolderProblem(folder, settingsFolder) is { } problem)
            {
                throw new ConnectorException($"{connector.Name}: {problem}");
            }
        }

        var granted = Granted(connector.Name);
        foreach (var old in granted.Where(g => !wanted.Any(w => Same(w, g))).ToList())
        {
            if (Take(connector.Name, sid, old.Folder) is { } problem)
            {
                throw new ConnectorException(problem);
            }

            granted.Remove(old);
            Keep(connector.Name, granted);
        }

        foreach (var (folder, write) in wanted.Where(w => !granted.Any(g => Same(w, g))).ToList())
        {
            try
            {
                // One entry per folder: a change from reading to writing takes the old one back first.
                AppContainer.Revoke(sid, folder);
                AppContainer.Grant(sid, folder, write);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                throw new ConnectorException($"{connector.Name} could not be given {folder}: {SafeText.Line(ex.Message)}", ex);
            }

            granted.RemoveAll(g => string.Equals(g.Folder, folder, StringComparison.OrdinalIgnoreCase));
            granted.Add((folder, write));
            Keep(connector.Name, granted);
        }

        Keep(connector.Name, granted);
    }

    /// <summary>
    /// The grants a connector no longer needs taken back at once: those its new sandbox does not name, or all
    /// of them when it runs as you. What could not be taken back is said.
    /// </summary>
    public static IReadOnlyList<string> Narrow(string connector, ConnectorSandbox box)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(box);
        var said = new List<string>();
        var granted = Granted(connector);
        if (granted.Count == 0)
        {
            return said;
        }

        var (sid, problem) = AppContainer.For(AppContainer.ForConnector(connector));
        if (sid == 0)
        {
            said.Add(problem ?? $"{connector}'s sandbox could not be found to take back what it was given");
            return said;
        }

        var wanted = box.On ? box.Read.Select(f => (Folder: f, Write: false)).Concat(box.Write.Select(f => (Folder: f, Write: true))).ToList() : [];
        foreach (var old in granted.Where(g => !wanted.Any(w => Same(w, g))).ToList())
        {
            if (Take(connector, sid, old.Folder) is { } left)
            {
                said.Add(left);
                continue;
            }

            granted.Remove(old);
        }

        try
        {
            Keep(connector, granted);
        }
        catch (ConnectorException ex)
        {
            said.Add(ex.Message);
        }

        return said;
    }

    /// <summary>
    /// Everything of a connector's place taken back: its grants on your folders, its container's profile,
    /// and its home. What could not be undone is said.
    /// </summary>
    public static IReadOnlyList<string> Remove(string connector)
    {
        ArgumentNullException.ThrowIfNull(connector);
        var said = new List<string>();
        var name = AppContainer.ForConnector(connector);

        // Only a container that was given something is looked up to take it back: none is made just to be removed.
        var granted = Granted(connector);
        var takenBack = granted.Count == 0;
        if (granted.Count > 0 && AppContainer.For(name) is { Sid: not 0 } made)
        {
            takenBack = true;
            foreach (var (folder, _) in granted)
            {
                if (Take(connector, made.Sid, folder) is { } problem)
                {
                    said.Add(problem);
                    takenBack = false;
                }
            }
        }
        else if (!takenBack)
        {
            said.Add($"{connector}'s sandbox could not be found to take back what it was given");
        }

        // Removing a profile that is not there "returns success" (DeleteAppContainerProfile).
        if (AppContainer.Delete(name) is { } left)
        {
            said.Add(left);
        }

        try
        {
            var home = Home(connector);
            if (Directory.Exists(home))
            {
                Directory.Delete(home, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            said.Add($"its folder in DipTick's data could not be removed: {SafeText.Line(ex.Message)}");
        }

        // The record goes once everything in it was taken back; kept otherwise, as the list of what is left.
        if (takenBack)
        {
            try
            {
                File.Delete(GrantsRecord(connector));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                said.Add($"its record of what it was given could not be removed: {SafeText.Line(ex.Message)}");
            }
        }
        else
        {
            said.Add($"what could not be taken back stays listed in {GrantsRecord(connector)}");
        }

        return said;
    }

    /// <summary>
    /// The variables a sandboxed connector starts with: those it would have, then its profile, settings and
    /// temporary files in its home (as a run's are), then its entry's own, which the person wrote.
    /// </summary>
    public static Dictionary<string, string> Environment(IReadOnlyDictionary<string, string> launched, Connector connector)
    {
        ArgumentNullException.ThrowIfNull(launched);
        ArgumentNullException.ThrowIfNull(connector);
        var home = Home(connector.Name);
        var temporary = TemporaryFolder(connector.Name);
        var variables = new Dictionary<string, string>(launched, StringComparer.OrdinalIgnoreCase);
        variables.Remove("HOMEDRIVE");
        variables.Remove("HOMEPATH");
        variables["USERPROFILE"] = home;
        variables["HOME"] = home;
        variables["APPDATA"] = home;
        variables["LOCALAPPDATA"] = home;
        variables["TEMP"] = temporary;
        variables["TMP"] = temporary;

        // What the entry sets itself stands: the launch holds it expanded.
        foreach (var name in connector.Env.Keys)
        {
            if (launched.TryGetValue(name, out var value))
            {
                variables[name] = value;
            }
        }

        return variables;
    }

    /// <summary>The temporary folder Windows gives the container (under LOCALAPPDATA's Packages\&lt;name&gt;\AC), in its home.</summary>
    public static string TemporaryFolder(string connector) => Path.Combine(Home(connector), "Packages", AppContainer.ForConnector(connector), "AC", "Temp");

    /// <summary>The same folder with the same access, as Windows compares paths.</summary>
    private static bool Same((string Folder, bool Write) a, (string Folder, bool Write) b) =>
        a.Write == b.Write && string.Equals(Path.TrimEndingDirectorySeparator(a.Folder), Path.TrimEndingDirectorySeparator(b.Folder), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether one folder is the other, or inside it, either way round.</summary>
    private static bool Overlaps(string a, string b) => Inside(a, b) || Inside(b, a) || string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="folder"/> is inside <paramref name="root"/>, and not the same folder.</summary>
    private static bool Inside(string folder, string root)
    {
        var inner = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        var outer = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return inner.Length > outer.Length && inner.StartsWith(outer, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Take(string connector, nint sid, string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                AppContainer.Revoke(sid, folder);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return $"{connector}'s access to {folder} could not be taken back: {SafeText.Line(ex.Message)}";
        }
    }

    /// <summary>The grants DipTick made for a connector, as it kept them; none when it kept none.</summary>
    private static List<(string Folder, bool Write)> Granted(string connector)
    {
        var path = GrantsRecord(connector);
        var granted = new List<(string, bool)>();
        try
        {
            if (!File.Exists(path))
            {
                return granted;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("grants", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var grant in list.EnumerateArray().Where(g => g.ValueKind == JsonValueKind.Object))
                {
                    if (grant.TryGetProperty("folder", out var folder) && folder.ValueKind == JsonValueKind.String && folder.GetString() is { Length: > 0 } text)
                    {
                        granted.Add((text, grant.TryGetProperty("write", out var write) && write.ValueKind == JsonValueKind.True));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A record that cannot be read names nothing to take back; the grants the entry names are made again.
            granted.Clear();
        }

        return granted;
    }

    private static void Keep(string connector, List<(string Folder, bool Write)> granted)
    {
        var path = GrantsRecord(connector);
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteStartArray("grants");
            foreach (var (folder, write) in granted)
            {
                json.WriteStartObject();
                json.WriteString("folder", folder);
                json.WriteBoolean("write", write);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        try
        {
            var temporary = path + "." + System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(temporary, buffer.ToArray());
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConnectorException($"{connector}'s record of what it was given could not be written: {SafeText.Line(ex.Message)}", ex);
        }
    }
}
