namespace DipTick.Models.Harness;

/// <summary>
/// The permission presets, each shown with what it changes before it is written:
/// Locked (everything asks or is never done), Standard (the starter settings), Trusted (reads, model
/// calls, searching and the network check allowed; writes, the web, programs, media and connectors ask).
/// Folders and hosts are never touched by a preset.
/// </summary>
internal static class Presets
{
    public static IReadOnlyList<string> Names { get; } = ["locked", "standard", "trusted"];

    /// <summary>A preset's three lists; null for a name that is none of them.</summary>
    public static (IReadOnlyList<string> Allow, IReadOnlyList<string> Ask, IReadOnlyList<string> Deny)? Of(string name) => name switch
    {
        "locked" => (
            [],
            ["model.call", "fs.read", "db.read", "net.probe", "web.search"],
            ["fs.write", "fs.delete", "net.fetch", "db.write", "shell.run", "media.make", "mcp.call"]),
        "standard" => (
            ["model.call", "web.search"],
            ["fs.read", "fs.write", "net.fetch", "net.probe", "db.read", "media.make", "mcp.call"],
            ["fs.delete", "db.write", "shell.run"]),
        "trusted" => (
            ["model.call", "fs.read", "db.read", "web.search", "net.probe"],
            ["fs.write", "fs.delete", "net.fetch", "db.write", "shell.run", "media.make", "mcp.call"],
            []),
        _ => null,
    };

    /// <summary>
    /// Which preset the permissions are, or null when they are custom: the same three lists, whatever
    /// the order.
    /// </summary>
    public static string? Match(Permissions permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        foreach (var name in Names)
        {
            var (allow, ask, deny) = Of(name)!.Value;
            if (Same(allow, permissions.Allow) && Same(ask, permissions.Ask) && Same(deny, permissions.Deny))
            {
                return name;
            }
        }

        return null;
    }

    private static bool Same(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == b.Count && a.Select(Capabilities.Canonical).Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(b.Select(Capabilities.Canonical).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How strict a list is, for telling a loosening from a tightening: never (2), ask me first (1),
    /// allowed (0); a rule no list names is never (no rule allows it).
    /// </summary>
    public static int Strictness(string list) => list switch
    {
        "allow" => 0,
        "ask" => 1,
        _ => 2,
    };
}
