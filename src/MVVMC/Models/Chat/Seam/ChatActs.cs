using DipTick.Models.Protocol;

namespace DipTick.Models.Chat.Seam;

/// <summary>
/// The acts an agent's answer puts in a chat's round: the parser's
/// strict reading of the one block at the end, then a chat's own rules. In a plain round only
/// <c>ask</c> counts; it goes to agents at the table, to <c>team</c> or to <c>person</c>, never to its
/// writer. An act opens, extends and approves nothing. Pure.
/// </summary>
internal static class ChatActs
{
    /// <summary>The kind the parser gives a whole block it voided, for the words and the store.</summary>
    public const string Block = "block";

    /// <summary>Each act of the answer, counted or rejected with its code; a voided block is one entry of kind <see cref="Block"/>; no block, none.</summary>
    public static IReadOnlyList<ParsedAct> Judge(string answer, string author, bool finished, IReadOnlyList<string> table)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(table);
        var block = ActParser.Parse(answer, finished);
        if (!block.Found)
        {
            return [];
        }

        if (block.BlockError is { } code)
        {
            return [new ParsedAct(null, Block, code)];
        }

        var (acts, _) = PhaseTable.Apply(Phase.Chat, block.Acts);
        return [.. acts.Select(a => a is { Accepted: true, Act: AskAct ask } ? Check(a, ask, author, table) : a)];
    }

    /// <summary>What DipTick read of an answer's block, in words, for the agents it is quoted to and for the person; empty when it had none.</summary>
    public static string Read(string answer, string author, bool finished, IReadOnlyList<string> table) =>
        string.Join("; ", Judge(answer, author, finished, table).Select(Words));

    /// <summary>One act in words: an accepted question, or what was not accepted and why.</summary>
    public static string Words(ParsedAct act)
    {
        ArgumentNullException.ThrowIfNull(act);
        return act switch
        {
            { Kind: Block } => $"the act block was not accepted: {act.Rejected}",
            { Accepted: true, Act: AskAct ask } => $"ASK to {string.Join(", ", ask.To)}: \"{ask.Q}\"",
            _ => $"{act.Kind.ToUpperInvariant()} not accepted: {act.Rejected}",
        };
    }

    private static ParsedAct Check(ParsedAct act, AskAct ask, string author, IReadOnlyList<string> table) =>
        ask.To.Contains(author, StringComparer.Ordinal) ? act with { Rejected = Codes.AsksItself }
        : ask.To.Any(t => t is not ("team" or "person") && !table.Contains(t)) ? act with { Rejected = Codes.NoSuchAgent }
        : act;
}
