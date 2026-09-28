namespace DipTick.Models.Harness;

/// <summary>
/// The one door every agent's request goes through: decide, ask a person when the policy says to,
/// record the outcome, and answer yes or no.
/// </summary>
/// <remarks>
/// <para>
/// The model is told only yes or no. The reason goes to the person and the audit log: a refusal
/// that explains itself is an oracle a model can question until it maps the rules.
/// </para>
/// <para>
/// The question shown to the person is made safe first (<see cref="SafeText"/>): a target the agent
/// chose cannot redraw the line so a write reads as a read. <paramref name="confirm"/> answers
/// true (yes), false (no) or null (nobody could be asked), and the record says which; "a person
/// said no" is never written when no person was there.
/// </para>
/// </remarks>
/// <param name="seat">The agent this gate speaks for, or null for the main model's own verbs.</param>
/// <param name="decided">Told each decision once it is recorded, so a verb can say how a turn ended (denied, or no one to ask); it changes nothing.</param>
internal sealed class Gate(Permissions permissions, RunFlags flags, AuditLog log, string who, TextWriter output, Func<string, bool?> confirm, SeatConfig? seat = null, Action<Decision>? decided = null)
{
    /// <summary>The MAC of the audit line the last request wrote, so what the request led to can name the line that allowed it; null before any.</summary>
    public string? LastHash { get; private set; }

    /// <summary>The chat's own rules, read at each request, so an "Allow for this chat" given a moment ago counts at once; none outside a chat.</summary>
    public Func<IReadOnlyList<ChatRule>>? ChatRules { get; init; }

    /// <summary>
    /// A screen's approval with three answers: Deny, Allow once, Allow for this
    /// chat; null for nobody there. Without it, <paramref name="confirm"/>'s yes or no is asked.
    /// </summary>
    public Func<string, Approval?>? Approve { get; init; }

    /// <summary>
    /// Keeps "Allow for this chat" as the chat's rule for exactly what was asked, and says whether it
    /// was kept; a rule that could not be kept allows this once only.
    /// </summary>
    public Func<string, bool>? AllowForChat { get; init; }

    /// <summary>The agent this gate speaks for, or null for the main model's own verbs.</summary>
    public SeatConfig? Seat => seat;

    /// <summary>The run's switches the gate decides under: under --dry-run nothing consequential is offered either.</summary>
    public RunFlags Flags => flags;

    /// <summary>The permissions the gate decides by.</summary>
    public Permissions Permissions => permissions;

    /// <summary>What this gate has let through, for <see cref="Continue"/>: one gate serves one turn.</summary>
    private readonly HashSet<(Capability Cap, string? Target)> _allowed = [];

    private readonly Lock _lock = new();

    /// <summary>
    /// The next request of an answer this gate already let through (a model called again with its tool
    /// calls' results): written in the audit log as the same answer, and not
    /// asked about again; the person's yes, or the rule, was for the answer. Anything this gate has not
    /// let through before is a request like any other.
    /// </summary>
    public bool Continue(string verb, Capability cap, string? target)
    {
        bool known;
        lock (_lock)
        {
            known = _allowed.Contains((cap, target));
        }

        // Running code is asked about every time: no earlier yes carries over to it.
        if (!known || cap == Capability.ShellRun)
        {
            return Request(verb, cap, target);
        }

        try
        {
            LastHash = log.Record(who, verb, cap, target, new Decision(Verdict.Allow, "the same answer, after its tool calls"), seat is null ? null : $"agent:{seat.Id}").Hash;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            output.WriteLine($"diptick: the audit log cannot be written ({SafeText.Line(ex.Message)}); nothing is allowed until it is fixed.");
            return false;
        }
    }

    /// <summary>True only when the work may be done now. Under --dry-run a consequential request is false.</summary>
    public bool Request(string verb, Capability cap, string? target) => Request(verb, cap, target, vouchedBy: null);

    /// <summary>
    /// As <see cref="Request(string, Capability, string?)"/>, with what vouches for the target: an
    /// address first seen in the person's messages, a search result or a
    /// page fetched in the chat. It answers the settings' own ask, and nothing else: an agent's own ask
    /// list or the chat's ask still asks, and a deny still denies.
    /// </summary>
    public bool Request(string verb, Capability cap, string? target, string? vouchedBy) => Request(verb, cap, target, vouchedBy, detail: null);

    /// <summary>
    /// As above, with what the request carries shown to the person when they are asked (a connector
    /// tool's arguments): one line, cut at <see cref="MostDetail"/> characters,
    /// and never recorded; the audit log keeps only what the rule judged.
    /// </summary>
    public bool Request(string verb, Capability cap, string? target, string? vouchedBy, string? detail)
    {
        var decision = Policy.Decide(cap, target, permissions, flags, seat, ChatRules?.Invoke(), vouchedBy);
        if (decision.Verdict == Verdict.Ask)
        {
            var shown = string.IsNullOrEmpty(target) ? Capabilities.Name(cap) : $"{Capabilities.Name(cap)} {SafeText.Line(target)}";
            // Code to run is shown whole and as written; anything else, on one line, cut short.
            var with = detail is null ? string.Empty
                : cap == Capability.ShellRun ? $"\n{SafeText.Block(detail)}\n"
                : $" With: {Clip(SafeText.Line(detail))}.";
            var question = $"{(seat is null ? "The model" : SafeText.Line(seat.Name))} asks to {Capabilities.Describe(cap)}: {shown}.{with} Allow? [y/N] ";
            decision = Approve is { } three ? Answered(three(question), decision, cap, target) : confirm(question) switch
            {
                true => new Decision(Verdict.Allow, decision.Reason + "; a person said yes"),
                false => new Decision(Verdict.Deny, decision.Reason + "; a person said no"),
                null => new Decision(Verdict.Deny, decision.Reason + ", and there is no one to ask", NeedsPerson: true),
            };
        }

        // No record, no action: if the log cannot be written, nothing is allowed.
        try
        {
            LastHash = log.Record(who, verb, cap, target, decision, seat is null ? null : $"agent:{seat.Id}").Hash;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            output.WriteLine($"diptick: the audit log cannot be written ({SafeText.Line(ex.Message)}); nothing is allowed until it is fixed.");
            return false;
        }

        if (decision.Verdict == Verdict.DryRun)
        {
            output.WriteLine($"[dry run] would {Capabilities.Name(cap)} {SafeText.Line(target)}".TrimEnd());
        }

        decided?.Invoke(decision);
        if (decision.Verdict == Verdict.Allow)
        {
            lock (_lock)
            {
                _allowed.Add((cap, target));
            }
        }

        return decision.Verdict == Verdict.Allow;
    }

    /// <summary>The most of a request's detail shown in its question.</summary>
    public const int MostDetail = 600;

    private static string Clip(string text) => text.Length <= MostDetail ? text : text[..MostDetail] + "…";

    /// <summary>
    /// The rule "Allow for this chat" keeps for a request: exactly what was asked where the
    /// capability names a target (a connector's tool, a file, a host to probe), the page's host for a
    /// fetch, the capability alone where it names none.
    /// </summary>
    public static string RuleFor(Capability cap, string? target)
    {
        var name = Capabilities.Name(cap);
        if (string.IsNullOrEmpty(target))
        {
            return name;
        }

        return cap == Capability.NetFetch && Uri.TryCreate(target, UriKind.Absolute, out var uri)
            ? $"{name}:{uri.Scheme}://{uri.Authority}/*"
            : $"{name}:{target}";
    }

    private Decision Answered(Approval? answer, Decision asked, Capability cap, string? target) => answer switch
    {
        Approval.Once => new Decision(Verdict.Allow, asked.Reason + "; a person said yes"),
        Approval.ForChat when cap == Capability.ShellRun => new Decision(Verdict.Allow, asked.Reason + "; a person said yes (running code asks every time, so only this once)"),
        Approval.ForChat when AllowForChat?.Invoke(RuleFor(cap, target)) == true => new Decision(Verdict.Allow, asked.Reason + "; a person said yes for this chat"),
        Approval.ForChat => new Decision(Verdict.Allow, asked.Reason + "; a person said yes (this chat's rule could not be kept, so only this once)"),
        Approval.Deny => new Decision(Verdict.Deny, asked.Reason + "; a person said no"),
        _ => new Decision(Verdict.Deny, asked.Reason + ", and there is no one to ask", NeedsPerson: true),
    };
}
