using DipTick.Models.Chat.Store;
using DipTick.Models.Protocol;

namespace DipTick.Models.Chat.Seam;

/// <summary>A question one agent put to another with the <c>ask</c> act: its answer's number, who asked, to whom, and what.</summary>
internal sealed record Ask(int Seq, string From, IReadOnlyList<string> To, string Question);

/// <summary>
/// What one request is made from.
/// </summary>
/// <param name="Seat">The agent asked.</param>
/// <param name="Person">The person's name as the agents read it.</param>
/// <param name="Step">This turn's step.</param>
/// <param name="Round">The round this turn is in.</param>
/// <param name="Lines">Every line of the chat so far, as the store keeps them.</param>
/// <param name="Table">The names at the table, for reading the acts of quoted answers.</param>
/// <param name="Mailbox">Questions put to this agent since its last turn.</param>
/// <param name="MaxTokens">The turn's cap.</param>
/// <param name="MaxQuotedChars">How much of any one other agent's answer is quoted.</param>
/// <param name="ContextChars">How much of the chat this agent is sent, in characters.</param>
/// <param name="Briefing">The briefing, given how many earlier rounds are left out.</param>
internal sealed record ComposeFacts(
    string Seat,
    string Person,
    PlanStep Step,
    int Round,
    IReadOnlyList<StoredMessage> Lines,
    IReadOnlyList<string> Table,
    IReadOnlyList<Ask> Mailbox,
    int MaxTokens,
    int MaxQuotedChars,
    int ContextChars,
    Func<int, string> Briefing);

/// <summary>
/// One agent's request for one turn, a pure function
/// of the chat so far: the briefing as the system text; this agent's own answers as its own
/// (<c>assistant</c>); everything between two of its turns as one message from the person's side, the
/// person's lines under their name and number and other agents' answers in envelopes, their act blocks
/// read and never passed on; blind to answers from its own step; questions put to it, then the message it
/// is answering, last. The last message on the wire is always the person's side. When the chat is larger
/// than the agent is sent, whole rounds go from the front, never the first thing the person said, and
/// the briefing says how many. No agent ever summarises the chat for another.
/// </summary>
internal static class Composer
{
    public static ChatRequest For(ComposeFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var final = facts.Lines.Where(Counts).ToList();
        var said = final.LastOrDefault(l => l.Round == facts.Round && l.Author == Author.Person);
        var answered = facts.Step.Role == StepRole.Stack && facts.Step.Turn > 1
            ? final.LastOrDefault(l => l.Round == facts.Round && l.Author != Author.Person && l.Author != facts.Seat) ?? said
            : said;

        // In order, this agent's own answer before another's from the same step.
        var ordered = final
            .OrderBy(l => l.Round)
            .ThenBy(l => l.Step)
            .ThenBy(l => l.Author == Author.Person ? 0 : l.Author == facts.Seat ? 1 : 2)
            .ThenBy(l => l.Seq)
            .ToList();

        var omitted = 0;
        var kept = ordered;
        var request = Build(facts, kept, said, answered, omitted);
        var first = ordered.FirstOrDefault(l => l.Author == Author.Person);
        var earlier = ordered.Select(l => l.Round).Where(r => r < facts.Round).Distinct().Order().ToList();
        while (Size(request) > facts.ContextChars && omitted < earlier.Count)
        {
            var gone = earlier[omitted];
            omitted++;
            kept = [.. kept.Where(l => l.Round != gone || ReferenceEquals(l, first))];
            request = Build(facts, kept, said, answered, omitted);
        }

        return request;
    }

    /// <summary>Whether a line is part of what agents are sent: the person's words and finished answers; never DipTick's notices, nor an answer passed, stopped, failed, denied, refused or repeated.</summary>
    private static bool Counts(StoredMessage line) =>
        line.Seq is not null
        && (line.Author == Author.Person
            ? line.Status == MessageStatus.Done && line.Kind is MessageKind.Say or MessageKind.Pass
            : line.Author != Author.DipTick && line.Kind == MessageKind.Answer && line.Status is MessageStatus.Done or MessageStatus.Cut);

    private static ChatRequest Build(ComposeFacts facts, List<StoredMessage> lines, StoredMessage? said, StoredMessage? answered, int omitted)
    {
        var nonce = Envelope.Nonce();
        var messages = new List<WireMessage>();
        var pending = new List<string>();
        var ownSteps = lines.Where(l => l.Author == facts.Seat).Select(l => (l.Round, l.Step)).ToHashSet();
        void Flush()
        {
            if (pending.Count > 0)
            {
                messages.Add(new WireMessage(WireRole.User, string.Join("\n\n", pending)));
                pending.Clear();
            }
        }

        foreach (var line in lines)
        {
            if (ReferenceEquals(line, answered))
            {
                continue;
            }

            if (line.Author == facts.Seat)
            {
                Flush();
                messages.Add(new WireMessage(WireRole.Assistant, SafeText.ForModel(line.Text)));
            }
            else if (line.Author == Author.Person)
            {
                var question = facts.Step.Role == StepRole.Stack && ReferenceEquals(line, said) ? "the question" : string.Empty;
                pending.Add(PersonLine(facts.Person, line, question));
            }
            else
            {
                var blind = ownSteps.Contains((line.Round, line.Step)) ? "answered the same message at the same time as you; you had not seen it" : string.Empty;
                pending.Add(PeerLine(facts, line, nonce, blind));
            }
        }

        pending.AddRange(facts.Mailbox.Select(a => Envelope.Ask(a.From, a.Seq, a.To.Contains(facts.Seat) ? "you" : "the team", a.Question, nonce)));
        if (answered is not null)
        {
            pending.Add(answered.Author == Author.Person
                ? PersonLine(facts.Person, answered, "the message you are answering")
                : PeerLine(facts, answered, nonce, "the message you are answering"));
        }

        Flush();
        return new ChatRequest(facts.Briefing(omitted), messages, facts.MaxTokens, "chat");
    }

    private static string PersonLine(string person, StoredMessage line, string note) =>
        Envelope.Person(person, line.Seq ?? 0, line.Kind == MessageKind.Pass ? $"(no message: {person} passed the floor back to you)" : line.Text, note);

    /// <summary>Another agent's answer: its words in an envelope, how it ended said, and what DipTick read of its act block after it.</summary>
    private static string PeerLine(ComposeFacts facts, StoredMessage line, string nonce, string note)
    {
        var notes = string.Join(" · ", new[] { line.Status == MessageStatus.Cut ? "cut short" : string.Empty, note }.Where(n => n.Length > 0));
        var peer = Envelope.Peer(line.Author, line.Seq ?? 0, nonce, ActParser.Prose(line.Text), notes, facts.MaxQuotedChars);
        return ChatActs.Read(line.Text, line.Author, line.Status == MessageStatus.Done, facts.Table) is { Length: > 0 } acts
            ? peer + "\n" + Envelope.Acts(line.Author, line.Seq ?? 0, nonce, acts)
            : peer;
    }

    private static int Size(ChatRequest request) => request.System.Length + request.Messages.Sum(m => m.Text.Length);
}
