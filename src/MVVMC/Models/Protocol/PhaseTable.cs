namespace DipTick.Models.Protocol;

/// <summary>The steps of a motion or a work run, and a chat's plain rounds.</summary>
internal enum Phase
{
    Draft,
    Review,
    Synth,
    Vote,
    Revise,
    Work,

    /// <summary>A round of a chat, not a motion: only a question to another agent counts.</summary>
    Chat,
}

/// <summary>Which acts each step allows, and which it requires.</summary>
internal static class PhaseTable
{
    private static readonly Dictionary<Phase, string[]> Allowed = new()
    {
        [Phase.Chat] = ["ask"],
        [Phase.Draft] = ["propose", "ask", "box"],
        [Phase.Review] = ["critique", "ask", "box"],
        [Phase.Synth] = ["propose", "ask"],
        [Phase.Vote] = ["agree", "object", "ask"],
        [Phase.Revise] = ["amend", "ask"],
        [Phase.Work] = ["tick", "confirm", "dispute", "box", "ask"],
    };

    public static bool Allows(Phase phase, string kind) => Allowed[phase].Contains(kind, StringComparer.Ordinal);

    /// <summary>
    /// Rejects every act the step does not allow, then says what the step still lacks, or null.
    /// Draft and synthesis need exactly one propose (synthesis's with basedOn), a vote exactly one
    /// agree or object, a revision one amend.
    /// </summary>
    public static (IReadOnlyList<ParsedAct> Acts, string? Missing) Apply(Phase phase, IReadOnlyList<ParsedAct> acts)
    {
        ArgumentNullException.ThrowIfNull(acts);
        var judged = acts.Select(a => a.Accepted && !Allows(phase, a.Kind) ? a with { Rejected = Codes.NotThisPhase } : a).ToList();
        var counted = judged.Where(a => a.Accepted).Select(a => a.Act).ToList();

        string? missing = phase switch
        {
            Phase.Draft when counted.OfType<ProposeAct>().Count() != 1 => "exactly one propose",
            Phase.Synth when counted.OfType<ProposeAct>().Count(p => p.BasedOn.Count > 0) != 1 => "exactly one propose with basedOn",
            Phase.Vote when counted.Count(a => a is AgreeAct or ObjectAct) != 1 => "exactly one agree or object",
            Phase.Revise when counted.OfType<AmendAct>().Count() != 1 => "exactly one amend",
            _ => null,
        };

        return (judged, missing);
    }
}
