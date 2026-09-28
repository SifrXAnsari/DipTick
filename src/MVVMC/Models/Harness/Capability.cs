namespace DipTick.Models.Harness;

/// <summary>
/// Everything an agent may ask the harness to do. The set is closed.
/// </summary>
/// <remarks>
/// Closed so that adding one is a source change somebody reviews, not a message a model
/// sends. <see cref="ShellRun"/> is the one that runs code: only ever in DipTick's sandbox, never
/// in a default allow, and asked about every time, whatever the rules say.
/// </remarks>
internal enum Capability
{
    ModelCall,
    FsRead,
    FsWrite,
    FsDelete,
    NetFetch,
    DbRead,
    DbWrite,
    ShellRun,

    /// <summary>The network check: a name looked up, a connection opened, closed again; nothing carried.</summary>
    NetProbe,

    /// <summary>A search through the service the person chose: the query leaves the PC.</summary>
    WebSearch,

    /// <summary>Making an image or a video: it costs money and makes content.</summary>
    MediaMake,

    /// <summary>A connector's tool (MCP): whatever the tool does, its server does.</summary>
    McpCall,
}

internal static class Capabilities
{
    private static readonly (Capability Cap, string Name, bool ChangesState, Scope Scope)[] Table =
    [
        (Capability.ModelCall, "model.call", false, Scope.None),
        (Capability.FsRead, "fs.read", false, Scope.Path),
        (Capability.FsWrite, "fs.write", true, Scope.Path),
        (Capability.FsDelete, "fs.delete", true, Scope.Path),
        (Capability.NetFetch, "net.fetch", true, Scope.Host),
        (Capability.DbRead, "db.read", false, Scope.None),
        (Capability.DbWrite, "db.write", true, Scope.None),
        (Capability.ShellRun, "shell.run", true, Scope.None),
        (Capability.NetProbe, "net.probe", false, Scope.None),
        (Capability.WebSearch, "web.search", true, Scope.None),
        (Capability.MediaMake, "media.make", true, Scope.None),
        (Capability.McpCall, "mcp.call", true, Scope.None),
    ];

    /// <summary>
    /// Former names a rule may still carry: read wherever a rule is read, never written.
    /// <c>ai.call</c> is <c>model.call</c> now.
    /// </summary>
    private static readonly (string Former, string Now)[] Renamed =
    [
        ("ai.call", "model.call"),
        ("ai.*", "model.*"),
    ];

    /// <summary>What each capability lets an agent do, in words, for the permission controls and the approvals.</summary>
    private static readonly Dictionary<Capability, string> Words = new()
    {
        [Capability.ModelCall] = "call a model",
        [Capability.FsRead] = "read files",
        [Capability.FsWrite] = "write files",
        [Capability.FsDelete] = "delete files",
        [Capability.NetFetch] = "fetch pages from the web",
        [Capability.DbRead] = "read the database",
        [Capability.DbWrite] = "write the database",
        [Capability.ShellRun] = "run programs",
        [Capability.NetProbe] = "check the network",
        [Capability.WebSearch] = "search the web",
        [Capability.MediaMake] = "make images and videos",
        [Capability.McpCall] = "use connectors' tools",
    };

    public static IEnumerable<string> Names => Table.Select(t => t.Name);

    /// <summary>Every capability, in the order the controls show them.</summary>
    public static IEnumerable<Capability> All => Table.Select(t => t.Cap);

    public static string Name(Capability cap) => Array.Find(Table, t => t.Cap == cap).Name;

    /// <summary>What a capability lets an agent do, in words: "read files".</summary>
    public static string Describe(Capability cap) => Words[cap];

    /// <summary>A rule as it is spelled now: a former name becomes its new one; anything else is returned as it came.</summary>
    public static string Canonical(string rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        foreach (var (former, now) in Renamed)
        {
            if (string.Equals(rule, former, StringComparison.OrdinalIgnoreCase))
            {
                return now;
            }
        }

        return rule;
    }

    /// <summary>Whether a rule names exactly <paramref name="cap"/>, under its name now or a former one; never a group, *, or a narrower rule.</summary>
    public static bool IsExactly(string rule, Capability cap)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return string.Equals(Canonical(rule), Name(cap), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether doing it is consequential: it changes something, or it sends data out. So
    /// --dry-run stops it, and --yes never answers it. net.fetch is one: a URL's path and query
    /// carry whatever the agent puts in them, to whoever runs the host.
    /// </summary>
    public static bool ChangesState(Capability cap) => Array.Find(Table, t => t.Cap == cap).ChangesState;

    /// <summary>What a target of this capability is checked against.</summary>
    public static Scope ScopeOf(Capability cap) => Array.Find(Table, t => t.Cap == cap).Scope;

    public static Capability? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var name = Canonical(text);
        foreach (var t in Table)
        {
            if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return t.Cap;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a rule names this capability: its exact name, <c>group.*</c>, or <c>*</c>; a former
    /// name counts as the name it became. A rule that names a target (<c>mcp.call:clickup/*</c>)
    /// names only that target, and so matches nothing here, where no target is given.
    /// </summary>
    public static bool Matches(string rule, Capability cap) => Matches(rule, cap, null);

    /// <summary>
    /// Whether a rule covers this capability for this target: a rule with no target covers every
    /// target; <c>name:target</c> covers that target exactly, and <c>name:prefix*</c> every target that
    /// starts with the prefix (<c>mcp.call:github/*</c>).
    /// </summary>
    public static bool Matches(string rule, Capability cap, string? target)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var (name, narrowed) = Split(rule);
        if (narrowed is not null)
        {
            if (target is null || !NameMatches(name, cap))
            {
                return false;
            }

            return narrowed.EndsWith('*')
                ? target.StartsWith(narrowed[..^1], StringComparison.OrdinalIgnoreCase)
                : string.Equals(narrowed, target, StringComparison.OrdinalIgnoreCase);
        }

        return NameMatches(name, cap);
    }

    /// <summary>Whether a rule speaks of this capability at all, for any target: <c>mcp.call:clickup/*</c> speaks of mcp.call.</summary>
    public static bool SpeaksOf(string rule, Capability cap)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return NameMatches(Split(rule).Name, cap);
    }

    /// <summary>Whether a rule is one <see cref="Matches(string, Capability, string?)"/> understands.</summary>
    public static bool IsRule(string rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var (name, narrowed) = Split(rule);
        if (narrowed is not null)
        {
            // Only a single capability takes a target; the target is plain, with at most one * at its end.
            var star = narrowed.IndexOf('*', StringComparison.Ordinal);
            return Parse(name) is not null && narrowed.Length is > 0 and <= 200 && !SafeText.HasControls(narrowed) && (star < 0 || star == narrowed.Length - 1);
        }

        var spelled = Canonical(name);
        return spelled == "*"
            || Parse(spelled) is not null
            || (spelled.EndsWith(".*", StringComparison.Ordinal) && Table.Any(t => t.Name.StartsWith(spelled[..^1], StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>A rule as its capability part and the target it is narrowed to, if any.</summary>
    private static (string Name, string? Target) Split(string rule)
    {
        var colon = rule.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? (rule, null) : (rule[..colon], rule[(colon + 1)..]);
    }

    private static bool NameMatches(string name, Capability cap)
    {
        var spelled = Canonical(name);
        var own = Name(cap);
        if (spelled == "*" || string.Equals(spelled, own, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return spelled.EndsWith(".*", StringComparison.Ordinal)
            && own.StartsWith(spelled[..^1], StringComparison.OrdinalIgnoreCase);
    }
}

internal enum Scope
{
    None,
    Path,
    Host,
}
