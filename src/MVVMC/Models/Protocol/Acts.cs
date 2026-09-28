namespace DipTick.Models.Protocol;

/// <summary>
/// The acts an agent may put in its one <c>diptick-acts</c> block.
/// No act carries a seat: who wrote it comes from the turn, never from the text.
/// </summary>
internal abstract record Act(string Kind);

/// <summary>A reference to one version of a proposal: id, version, and a prefix of its hash.</summary>
internal readonly record struct VersionRef(string P, int V, string H);

internal sealed record ProposeAct(string Title, IReadOnlyList<string> Clauses, IReadOnlyList<string> Items, IReadOnlyList<VersionRef> BasedOn) : Act("propose");

internal sealed record CritiqueAct(VersionRef Ref, string Where, string Quote, string Flaw) : Act("critique");

internal sealed record AmendAct(VersionRef Ref, IReadOnlyList<string> Clauses, IReadOnlyList<string> Items, string Why) : Act("amend");

internal sealed record AgreeAct(VersionRef Ref, string Where, string Quote, string Risk) : Act("agree");

internal sealed record ObjectAct(VersionRef Ref, string Where, string Reason, string? Change, bool Redraft) : Act("object");

internal sealed record BoxAct(string Text, string? Owner) : Act("box");

internal sealed record TickAct(string B, string Msg, string Quote) : Act("tick");

internal sealed record ConfirmAct(string T, string Quote, string Checked) : Act("confirm");

internal sealed record DisputeAct(string T, string Reason) : Act("dispute");

internal sealed record AskAct(IReadOnlyList<string> To, string Q) : Act("ask");

/// <summary>Why an act, or a whole block, was not counted. The names are the codes the person sees.</summary>
internal static class Codes
{
    public const string BadJson = "bad-json";
    public const string UnknownField = "unknown-field";
    public const string UnknownAct = "unknown-act";
    public const string MissingField = "missing-field";
    public const string TooLarge = "too-large";
    public const string CutAnswer = "cut-answer";
    public const string NotThisPhase = "not-this-phase";
    public const string Conflict = "conflict";
    public const string StaleVersion = "stale-version";
    public const string UnseenHash = "unseen-hash";
    public const string BadWhere = "bad-where";
    public const string QuoteNotFound = "quote-not-found";
    public const string FlawTooThin = "flaw-too-thin";
    public const string FlawRepeated = "flaw-repeated";
    public const string RepeatVersion = "repeat-version";
    public const string BoxNotOpen = "box-not-open";
    public const string NotNeeded = "not-needed";
    public const string NotSeen = "not-seen";
    public const string OwnTick = "own-tick";
    public const string BadEvidence = "bad-evidence";

    /// <summary>An ask to an agent not at the table.</summary>
    public const string NoSuchAgent = "no-such-agent";

    /// <summary>An ask an agent put to itself.</summary>
    public const string AsksItself = "asks-itself";
}

/// <summary>One act as the parser left it: counted so far, or rejected with a code.</summary>
internal sealed record ParsedAct(Act? Act, string Kind, string? Rejected)
{
    public bool Accepted => Rejected is null;
}

/// <summary>What one answer's block came to.</summary>
/// <param name="Found">Whether there was a block at the end at all.</param>
/// <param name="BlockError">A code that voided the whole block, or null.</param>
/// <param name="Acts">Each act, accepted so far or rejected with its code.</param>
internal sealed record ActBlock(bool Found, string? BlockError, IReadOnlyList<ParsedAct> Acts)
{
    public static ActBlock None { get; } = new(false, null, []);

    public static ActBlock Void(string code) => new(true, code, []);
}
