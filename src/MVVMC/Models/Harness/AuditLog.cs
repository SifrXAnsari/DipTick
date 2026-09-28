using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DipTick.Models.Harness;

/// <summary>One decision, as the audit log keeps it.</summary>
internal sealed record AuditEntry(
    DateTimeOffset When,
    string Who,
    string Verb,
    string Capability,
    string Target,
    string Verdict,
    string Reason,
    string Previous,
    string Hash)
{
    /// <summary>
    /// Which agent asked, as <c>agent:&lt;seat&gt;</c> (<c>ai:&lt;seat&gt;</c> on lines an earlier
    /// version wrote), or null for the main model's own verbs. Left out of
    /// the line when null, so lines written without it still verify.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Actor { get; init; }
}

/// <summary>
/// What verifying the log found: the problem, or null when the log is whole; the newest anchor the
/// Windows event log kept for it, if any; and whether the event log could be read.
/// </summary>
internal sealed record AuditCheck(string? Problem, WitnessedAnchor? Witnessed, bool WitnessRead)
{
    /// <summary>The anchors the event log kept that this log does not match and no one has accepted: an earlier log, deleted or cut.</summary>
    public IReadOnlyList<WitnessedAnchor> Unaccepted { get; init; } = [];

    /// <summary>The earlier log here that the person said they deleted (<c>diptick audit accept</c>), or null.</summary>
    public EarlierLog? Earlier { get; init; }
}

/// <summary>An earlier log at the same path, accepted as deleted: the most lines the event log saw it with, until when, and when the person said so.</summary>
internal sealed record EarlierLog(int Lines, DateTimeOffset Until, DateTimeOffset Accepted);

/// <summary>
/// Every decision the harness makes, appended to <c>audit.jsonl</c>, one JSON object a line.
/// </summary>
/// <remarks>
/// <para>
/// Windows Server's audit policy is the reference: record the decision and who it was for,
/// whether it was allowed or not, and keep the record where the thing audited cannot quietly
/// rewrite it.
/// </para>
/// <para>
/// <b>Keyed and anchored.</b> Each line carries an HMAC-SHA256 over itself and the line before,
/// under a key only this Windows account can unwrap (DPAPI, CurrentUser scope, in
/// <c>audit.key</c>). A plain hash chain can be rewritten end to end by anyone who can write the
/// file; this one cannot be without the key. <c>audit.anchor</c> holds the count and the last
/// MAC, itself MACed, so cutting lines off the end is caught too. Verify says where it breaks.
/// </para>
/// <para>
/// <b>Witnessed outside its folder.</b> The anchor is also written to the Windows
/// Application event log (<see cref="EventLogWitness"/>), so deleting the log, its key and its
/// anchor together, which would start a clean chain that verifies, still shows: verify compares
/// the log with every anchor the event log kept for it. A person who deleted it themselves says so
/// with <c>diptick audit accept</c>, a line of the log like any other; from then on the anchors
/// seen before that line are the earlier log's, and verify names that log instead of failing.
/// Windows keeps its own record the same way: clearing the Security log writes event 1102, "The
/// audit log was cleared", with who cleared it (Microsoft Learn, event 1102).
/// </para>
/// <para>
/// <b>Never written:</b> a prompt, a model's answer, file contents, a key, a URL's credentials,
/// a URL's query (only its length and a digest, so a fetch that carried data out can still be
/// matched later without the log holding the data). Every field is bounded, so no request can
/// make a line too long to read back.
/// </para>
/// </remarks>
internal sealed class AuditLog
{
    public const string DataVariable = "DIPTICK_DATA_DIR";
    public const string FileName = "audit.jsonl";

    /// <summary>The verb and the capability of the line <c>diptick audit accept</c> writes.</summary>
    public const string AcceptVerb = "audit accept";

    /// <inheritdoc cref="AcceptVerb"/>
    public const string AcceptCapability = "audit";

    /// <summary>The folder a private chat's decisions go to, one file a day.</summary>
    public const string PrivateFolderName = "audit-private";

    /// <summary>How long a private day's log is kept by default, and the least and most <c>audit.privateDays</c> takes.</summary>
    public const int DefaultPrivateDays = 30;

    /// <inheritdoc cref="DefaultPrivateDays"/>
    public const int FewestPrivateDays = 15;

    /// <inheritdoc cref="DefaultPrivateDays"/>
    public const int MostPrivateDays = 30;

    private const string KeyName = "audit.key";
    private const string AnchorName = "audit.anchor";
    private const string DayFormat = "yyyy-MM-dd";
    private const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";
    private const int MaxTarget = 1024;
    private const int MaxReason = 512;
    private const int MaxName = 128;
    private const int MaxTail = 64 * 1024;

    /// <summary>The most anchors verify reads back from the event log, newest first.</summary>
    private const int MostAnchors = 1000;

    private static readonly byte[] Entropy = "DipTick audit key v1"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Where the anchor is kept outside the log's folder: the Windows event log.</summary>
    internal static IAnchorWitness Witness { get; set; } = new EventLogWitness();

    /// <summary>How far back the witness remembers: when the oldest record it still holds was made; null when it cannot say.</summary>
    public static DateTimeOffset? WitnessReaches() => Witness.Reaches();

    private readonly string _keyPath;
    private readonly string _anchorPath;

    /// <summary>Whether its anchor goes to the witness: the main log's does; a private day's never does, being deleted whole by design.</summary>
    private readonly bool _witnessed;

    private AuditLog(string folder, string fileName, string anchorName, bool witnessed)
    {
        Folder = folder;
        Path = System.IO.Path.Combine(folder, fileName);
        _keyPath = System.IO.Path.Combine(folder, KeyName);
        _anchorPath = System.IO.Path.Combine(folder, anchorName);
        _witnessed = witnessed;
    }

    public string Folder { get; }

    public string Path { get; }

    /// <summary>The folder DipTick keeps its own data in: <c>DIPTICK_DATA_DIR</c>, else LocalAppData\DipTick.</summary>
    public static string DataFolder()
    {
        var root = Environment.GetEnvironmentVariable(DataVariable);
        return string.IsNullOrWhiteSpace(root)
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "DipTick")
            : System.IO.Path.GetFullPath(root);
    }

    public static AuditLog ForThisUser()
    {
        var folder = DataFolder();
        Directory.CreateDirectory(folder);
        return new AuditLog(folder, FileName, AnchorName, witnessed: true);
    }

    /// <summary>
    /// Today's log of private chats' decisions: <c>audit-private\&lt;date&gt;.jsonl</c>,
    /// each day its own keyed chain with its own anchor, under one key for the folder. Opening it first
    /// deletes the days older than <paramref name="days"/>, whole. The main log never holds a private
    /// chat's lines, so deleting a day never breaks its chain.
    /// </summary>
    public static AuditLog ForPrivate(int days) => ForPrivateOn(DateTimeOffset.Now, days);

    /// <summary><see cref="ForPrivate"/> on a given day.</summary>
    internal static AuditLog ForPrivateOn(DateTimeOffset now, int days)
    {
        var folder = PrivateFolder();
        Directory.CreateDirectory(folder);
        _ = SweepPrivate(now, days);
        var day = now.ToLocalTime().ToString(DayFormat, CultureInfo.InvariantCulture);
        return new AuditLog(folder, day + ".jsonl", day + ".anchor", witnessed: false);
    }

    /// <summary>Every private day's log there is, oldest first, after the days older than <paramref name="days"/> are deleted.</summary>
    public static IReadOnlyList<AuditLog> PrivateDays(int days) => PrivateDaysOn(DateTimeOffset.Now, days);

    /// <summary><see cref="PrivateDays"/> on a given day.</summary>
    internal static IReadOnlyList<AuditLog> PrivateDaysOn(DateTimeOffset now, int days)
    {
        _ = SweepPrivate(now, days);
        var folder = PrivateFolder();
        return !Directory.Exists(folder)
            ? []
            : [.. Directory.EnumerateFiles(folder, "*.jsonl")
                .Select(f => System.IO.Path.GetFileNameWithoutExtension(f))
                .Where(d => Day(d) is not null)
                .Order(StringComparer.Ordinal)
                .Select(d => new AuditLog(folder, d + ".jsonl", d + ".anchor", witnessed: false))];
    }

    /// <summary>
    /// Deletes each private day's log, and its anchor, once the day began more than
    /// <paramref name="days"/> days before <paramref name="now"/>, so no line is kept longer than
    /// that. A file another diptick holds open stays for the next sweep. Returns how many days' logs went.
    /// </summary>
    internal static int SweepPrivate(DateTimeOffset now, int days)
    {
        var folder = PrivateFolder();
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        var oldest = now.ToLocalTime().DateTime.AddDays(-Math.Clamp(days, FewestPrivateDays, MostPrivateDays));
        var gone = 0;

        // An anchor whose log went in an earlier sweep goes too.
        foreach (var file in Directory.EnumerateFiles(folder).ToList())
        {
            var extension = System.IO.Path.GetExtension(file);
            if (extension is not (".jsonl" or ".anchor") || Day(System.IO.Path.GetFileNameWithoutExtension(file)) is not { } day || day >= oldest)
            {
                continue;
            }

            try
            {
                File.Delete(file);
                gone += extension == ".jsonl" ? 1 : 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Open in another diptick that reads it (audit --private): it goes the next time.
            }
        }

        return gone;
    }

    /// <summary>The day a private log is for, from its name; null for any other file.</summary>
    public DateTime? PrivateDay => _witnessed ? null : Day(System.IO.Path.GetFileNameWithoutExtension(Path));

    private static string PrivateFolder() => System.IO.Path.Combine(DataFolder(), PrivateFolderName);

    private static DateTime? Day(string name) =>
        DateTime.TryParseExact(name, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

    public AuditEntry Record(string who, string verb, Capability cap, string? target, Decision decision, string? actor = null)
    {
        ArgumentNullException.ThrowIfNull(who);
        ArgumentNullException.ThrowIfNull(verb);
        return Append(who, verb, Capabilities.Name(cap), Redact(target), decision.Verdict.ToString(), decision.Reason, actor);
    }

    /// <summary>
    /// The person's word that <paramref name="earlier"/>, a log the Windows event log saw here, was
    /// theirs to delete (<c>diptick audit accept</c>): a line like any other, chained and keyed.
    /// From it on, verify counts the anchors seen before it as that earlier log's.
    /// </summary>
    public AuditEntry RecordAccepted(string who, string earlier)
    {
        ArgumentNullException.ThrowIfNull(who);
        ArgumentNullException.ThrowIfNull(earlier);
        return Append(who, AcceptVerb, AcceptCapability, Cap(SafeText.Line(earlier), MaxTarget), nameof(Verdict.Allow), "the person said they deleted the earlier log", null);
    }

    /// <summary>
    /// An act an agent's answer put in a chat: its kind as the
    /// capability (<c>act.ask</c>), where it points as the target, counted or not as the verdict, and
    /// the code when not; never the act's words. An act whose line cannot be written has no effect.
    /// </summary>
    public AuditEntry RecordAct(string who, string actor, string kind, string target, bool accepted, string? code)
    {
        ArgumentNullException.ThrowIfNull(who);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(target);
        return Append(who, "chat", "act." + Cap(SafeText.Line(kind), MaxName), Cap(SafeText.Line(target), MaxTarget),
            accepted ? nameof(Verdict.Allow) : nameof(Verdict.Deny), accepted ? "counted" : $"not counted: {code}", actor);
    }

    /// <summary>
    /// A tool call an agent made, written before the tool
    /// runs: the tool as the capability (<c>tool.calc</c>), the connector and tool with a SHA-256 of the
    /// arguments as the target, never the arguments. What the tool itself touches goes through the gate,
    /// line by line, as every request does.
    /// </summary>
    public AuditEntry RecordTool(string who, string actor, string tool, string? connector, string argumentsDigest)
    {
        ArgumentNullException.ThrowIfNull(who);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(argumentsDigest);
        var name = connector is null ? tool : $"{connector}/{tool}";
        return Append(who, "tool", "tool." + Cap(SafeText.Line(name), MaxName), Cap($"{SafeText.Line(name)} arguments sha256:{argumentsDigest}", MaxTarget),
            nameof(Verdict.Allow), "called", actor);
    }

    /// <summary>
    /// A change to what agents may do: a rule set or taken
    /// away, in the settings or in one chat, by the person; the rule as the target, what it became as the reason.
    /// </summary>
    public AuditEntry RecordPolicy(string who, string where, string rule, string change)
    {
        ArgumentNullException.ThrowIfNull(who);
        ArgumentNullException.ThrowIfNull(where);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(change);
        return Append(who, "policy", "policy." + Cap(SafeText.Line(where), MaxName), Cap(SafeText.Line(rule), MaxTarget), nameof(Verdict.Allow), Cap(SafeText.Line(change), MaxReason), null);
    }

    private AuditEntry Append(string who, string verb, string capability, string target, string verdict, string reason, string? actor)
    {
        // One writer at a time: exclusive for writing, readers may still read. A second diptick
        // waits rather than interleaving half-lines or forking the chain.
        using var stream = OpenForAppend();
        var key = Key(create: true)!;
        var (count, previous) = Tail(stream, key);

        var entry = new AuditEntry(
            DateTimeOffset.UtcNow,
            Cap(SafeText.Line(who), MaxName),
            Cap(SafeText.Line(verb), MaxName),
            capability,
            target,
            verdict,
            Cap(SafeText.Line(reason), MaxReason),
            previous,
            string.Empty)
        {
            Actor = actor is null ? null : Cap(SafeText.Line(actor), MaxName),
        };
        entry = entry with { Hash = Mac(key, entry) };

        stream.Seek(0, SeekOrigin.End);
        stream.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, Json) + "\n"));
        stream.Flush(flushToDisk: true);
        WriteAnchor(key, count + 1, entry.Hash);
        if (_witnessed)
        {
            Witness.Saw(Path, count + 1, entry.Hash);
        }

        return entry;
    }

    /// <summary>Every entry, oldest first, that <paramref name="keep"/> keeps.</summary>
    public IReadOnlyList<AuditEntry> ReadAll(Func<AuditEntry, bool> keep)
    {
        ArgumentNullException.ThrowIfNull(keep);
        if (!File.Exists(Path))
        {
            return [];
        }

        var entries = new List<AuditEntry>();
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (TryParse(line) is { } entry && keep(entry))
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    public IReadOnlyList<AuditEntry> Read(int most)
    {
        if (!File.Exists(Path))
        {
            return [];
        }

        var entries = new List<AuditEntry>();
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (TryParse(line) is { } entry)
            {
                entries.Add(entry);
                if (entries.Count > most)
                {
                    entries.RemoveAt(0);
                }
            }
        }

        return entries;
    }

    /// <summary>Null when the log is whole and agrees with every anchor the event log kept; otherwise where and how it breaks.</summary>
    public string? Verify() => Check().Problem;

    /// <summary>
    /// The log checked line by line against its chain, its key and its anchor, and against every
    /// anchor the Windows event log kept for it: an anchor there names a line the log must
    /// still have, with the MAC it had, so a log cut short, or deleted and begun again, shows. An
    /// anchor seen before the person's last <c>audit accept</c> line is the earlier log's they
    /// said they deleted: it is named in <see cref="AuditCheck.Earlier"/>, not reported.
    /// </summary>
    public AuditCheck Check()
    {
        var witnessed = _witnessed ? Witness.Anchors(Path, MostAnchors) : [];
        var read = witnessed is not null;
        var anchors = witnessed ?? [];
        var newest = anchors.Count == 0 ? null : anchors[0];
        var key = Key(create: false);
        var anchor = ReadAnchor(key);
        if (!File.Exists(Path))
        {
            if (newest is { } seen)
            {
                return new AuditCheck($"the log is missing, but the Windows event log saw it with {Lines(seen.Count)} on {When(seen)}: it was deleted", newest, read) { Unaccepted = anchors };
            }

            return new AuditCheck(key is null && anchor is null ? null : "the log is missing but its key or anchor is there: it was deleted", newest, read);
        }

        if (key is null)
        {
            return new AuditCheck("the log has no key (audit.key is missing or not this account's): it cannot be verified", newest, read);
        }

        // The anchors this log does not match: seen on a line it has with another MAC, or on a line past its end.
        var byLine = anchors.GroupBy(a => a.Count).ToDictionary(g => g.Key, g => g.ToList());
        var differ = new List<WitnessedAnchor>();
        DateTimeOffset? accepted = null;
        var expected = Genesis;
        var number = 0;
        using (var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            while (reader.ReadLine() is { } line)
            {
                number++;
                if (TryParse(line) is not { } entry || entry.Previous != expected || !Equal(entry.Hash, Mac(key, entry)))
                {
                    return new AuditCheck(string.Create(CultureInfo.InvariantCulture, $"line {number} does not match the line before it or its own MAC"), newest, read);
                }

                expected = entry.Hash;
                if (byLine.TryGetValue(number, out var seenHere))
                {
                    differ.AddRange(seenHere.Where(w => !Equal(w.Last, entry.Hash)));
                }

                if (entry.Verb == AcceptVerb && entry.Capability == AcceptCapability)
                {
                    accepted = entry.When;
                }
            }
        }

        if (anchor is not { } a)
        {
            if (number > 0)
            {
                return new AuditCheck("the anchor is missing or forged", newest, read);
            }
        }
        else if (a.Count != number || a.Last != expected)
        {
            return new AuditCheck(string.Create(CultureInfo.InvariantCulture, $"the anchor says {Lines(a.Count)} ending in {a.Last[..8]}…, the log has {number}: lines were cut off or added"), newest, read);
        }

        differ.AddRange(anchors.Where(w => w.Count > number));

        // What the person accepted is what the event log had seen when they said so; anything it saw after stands.
        var covered = accepted is { } at ? differ.Where(w => w.When <= at).ToList() : [];
        var open = differ.Except(covered).ToList();
        var earlier = covered.Count == 0 ? null : new EarlierLog(covered.Max(w => w.Count), covered.Max(w => w.When), accepted!.Value);
        string? problem = null;
        if (open.Where(w => w.Count > number).MaxBy(w => w.Count) is { } longer)
        {
            problem = string.Create(CultureInfo.InvariantCulture, $"the Windows event log saw {Lines(longer.Count)} on {When(longer)}, the log has {number}: lines were cut off, or the log was deleted and begun again");
        }
        else if (open.MinBy(w => w.Count) is { } other)
        {
            problem = string.Create(CultureInfo.InvariantCulture, $"line {other.Count} is not the line the Windows event log saw there on {When(other)}: the log was replaced");
        }

        return new AuditCheck(problem, newest, read) { Unaccepted = open, Earlier = earlier };
    }

    private static string When(WitnessedAnchor anchor) => anchor.When.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Lines(int count) => count == 1 ? "1 line" : count.ToString(CultureInfo.InvariantCulture) + " lines";

    /// <summary>A target as it may be kept: no credentials, no query, no control characters, bounded.</summary>
    public static string Redact(string? target)
    {
        if (string.IsNullOrEmpty(target))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            string host;
            try
            {
                host = uri.IdnHost;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or UriFormatException)
            {
                host = "(invalid host)";
            }

            var kept = $"{uri.Scheme}://{host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}{uri.AbsolutePath}";
            if (uri.Query.Length > 1)
            {
                var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uri.Query)))[..16];
                kept += string.Create(CultureInfo.InvariantCulture, $"?[query: {uri.Query.Length - 1} chars, sha256 {digest}]");
            }

            return Cap(SafeText.Line(kept), MaxTarget);
        }

        return Cap(SafeText.Line(target), MaxTarget);
    }

    private static string Cap(string text, int most) => text.Length <= most ? text : text[..(most - 1)] + "…";

    private static string Mac(byte[] key, AuditEntry entry)
    {
        var canonical = JsonSerializer.Serialize(entry with { Hash = string.Empty }, Json);
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool Equal(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    private static AuditEntry? TryParse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<AuditEntry>(line, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The account's audit key, unwrapped with DPAPI; made on first use when asked to.</summary>
    private byte[]? Key(bool create)
    {
        var path = _keyPath;
        if (File.Exists(path))
        {
            try
            {
                return ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                return create ? throw new InvalidDataException("audit.key does not open for this Windows account; the audit log cannot be continued.") : null;
            }
        }

        if (!create)
        {
            return null;
        }

        if (File.Exists(Path) && new FileInfo(Path).Length > 0)
        {
            throw new InvalidDataException("the audit log exists but its key does not; refusing to start a new chain over it. 'diptick audit verify' says more.");
        }

        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(path, ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser));
        return key;
    }

    private (int Count, string Last) Tail(FileStream stream, byte[] key)
    {
        var anchor = ReadAnchor(key);
        if (stream.Length == 0)
        {
            return anchor is { Count: > 0 }
                ? throw new InvalidDataException("the audit log is empty but its anchor is not: it was cut. 'diptick audit verify' says more.")
                : (0, Genesis);
        }

        var last = LastLine(stream);
        if (TryParse(last) is not { } entry || !Equal(entry.Hash, Mac(key, entry)) || anchor is null || anchor.Value.Last != entry.Hash)
        {
            throw new InvalidDataException("the audit log does not match its anchor, so it will not be added to. 'diptick audit verify' says where it breaks.");
        }

        return (anchor.Value.Count, entry.Hash);
    }

    private static string LastLine(FileStream stream)
    {
        var size = (int)Math.Min(stream.Length, MaxTail);
        var buffer = new byte[size];
        stream.Seek(-size, SeekOrigin.End);
        stream.ReadExactly(buffer);
        var text = Encoding.UTF8.GetString(buffer).TrimEnd('\n');
        var start = text.LastIndexOf('\n');
        if (start < 0 && size == MaxTail)
        {
            throw new InvalidDataException("the audit log's last line is longer than any line DipTick writes.");
        }

        return text[(start + 1)..];
    }

    private readonly record struct Anchor(int Count, string Last);

    private Anchor? ReadAnchor(byte[]? key)
    {
        var path = _anchorPath;
        if (key is null || !File.Exists(path))
        {
            return null;
        }

        var parts = File.ReadAllText(path).Trim().Split(' ');
        if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var count))
        {
            return null;
        }

        return Equal(parts[2], AnchorMac(key, count, parts[1])) ? new Anchor(count, parts[1]) : null;
    }

    private void WriteAnchor(byte[] key, int count, string last)
    {
        var path = _anchorPath;
        var temporary = path + ".new";
        File.WriteAllText(temporary, string.Create(CultureInfo.InvariantCulture, $"{count} {last} {AnchorMac(key, count, last)}"));
        File.Move(temporary, path, overwrite: true);
    }

    private static string AnchorMac(byte[] key, int count, string last) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"anchor|{count}|{last}"))));

    private FileStream OpenForAppend()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(Path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            }
            catch (IOException) when (attempt < 50)
            {
                Thread.Sleep(20);
            }
        }
    }
}
