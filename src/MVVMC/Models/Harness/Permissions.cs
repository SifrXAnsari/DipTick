namespace DipTick.Models.Harness;

/// <summary>What the master config's <c>permissions</c> section says agents may do.</summary>
/// <param name="Allow">Rules done without asking.</param>
/// <param name="Ask">Rules that need a person's yes each time.</param>
/// <param name="Deny">Rules never done. Deny beats ask beats allow.</param>
/// <param name="Roots">Folders the file capabilities may touch. None means no file access at all.</param>
/// <param name="Hosts">Hosts <c>net.fetch</c> may reach: <c>example.com</c>, <c>*.example.com</c>, optionally <c>:port</c>. None means no network.</param>
/// <param name="Protected">DipTick's own paths, refused to every file capability whatever the roots say.</param>
/// <remarks>Anything no rule names is denied. There is no setting that changes that.</remarks>
internal sealed record Permissions(
    IReadOnlyList<string> Allow,
    IReadOnlyList<string> Ask,
    IReadOnlyList<string> Deny,
    IReadOnlyList<string> Roots,
    IReadOnlyList<string> Hosts,
    IReadOnlyList<string> Protected)
{
    /// <summary>With no config, or a permissions section that cannot be trusted: nothing is allowed.</summary>
    public static Permissions Closed { get; } = new([], [], [], [], [], []);
}

/// <summary>
/// One of a chat's own rules (<c>chat_policy</c>): a rule and what this chat says of
/// it. A chat can tighten anything; it can turn an Ask into an Allow for itself; it never loosens a deny.
/// </summary>
internal sealed record ChatRule(string Rule, Verdict Verdict);

/// <summary>A person's answer to an approval, on a screen that offers three: no, yes this once, yes for this chat.</summary>
internal enum Approval
{
    Deny,
    Once,
    ForChat,
}
