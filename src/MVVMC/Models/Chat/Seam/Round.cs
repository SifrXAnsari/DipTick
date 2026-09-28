using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DipTick.Models.Chat.Store;
using DipTick.Models.Harness;
using DipTick.Models.Protocol;
using DipTick.Models.Tools;

namespace DipTick.Models.Chat.Seam;

/// <summary>
/// How one agent's turn ended: its line's state, its words, its number, what it used and cost, DipTick's
/// words for why when it did not end whole, and whether its call waited for a yes nobody was there to give.
/// </summary>
internal sealed record TurnResult(string Seat, string Status, string Text, int? Seq, int? TokensIn, int? TokensOut, bool Estimated, long? CostMicros, string Why, bool NeededPerson = false);

/// <summary>How a round ended: its number (0 when none was opened), its plan, its turns, why it closed, and the number of what the person said.</summary>
internal sealed record RoundResult(int Round, Plan Plan, IReadOnlyList<TurnResult> Turns, string EndReason, int? SaidSeq);

/// <summary>Who hears what a round does, as it does it: the command line, a screen's events.</summary>
internal interface IRoundWatcher
{
    void Said(int round, int seq, Plan plan, ChatMode mode);

    void TurnStarted(string seat, SeatConfig agent, PlanStep step, int stepIndex);

    void Delta(string seat, string text);

    /// <summary>A tool call an agent made in its turn, done: its line's number, the tool, its line as the chat keeps it (what it did, then what it was asked and gave), and how it ended.</summary>
    void Tool(string seat, int seq, string tool, string text, string status);

    void TurnEnded(TurnResult result);

    void Act(string seat, int seq, ParsedAct act);

    void Notice(string text);

    void Ended(string reason);
}

/// <summary>
/// What a round is run with: the settings, the store, who the audit log names, one gate and one
/// engine per agent, the audit log for acts, who watches, the stop (Esc, Ctrl+C) and the clock.
/// </summary>
/// <param name="MainTimeoutSeconds">How long the model that answers you may take, as the command line's --timeout may set it for this run.</param>
/// <param name="Private">Whether the chat is private: what its tools find is kept in this process's memory only.</param>
/// <param name="Tools">Whether agents are offered tools at all in this round; false where no tools may run.</param>
internal sealed record RoundHarness(
    MasterConfig Config,
    IChatStore Store,
    string Who,
    Func<SeatConfig, Action<Decision>, Gate> GateFor,
    Func<SeatConfig, IChatEngine> EngineFor,
    AuditLog Log,
    IRoundWatcher Watcher,
    CancellationToken Stop,
    int? MainTimeoutSeconds = null,
    bool Private = false,
    bool Tools = true);

/// <summary>
/// A round: the plan fixed before the first call; each step's turns composed first, so none sees
/// another's answer, then run at once, each through its own gate as its own agent; every turn kept
/// as it streams and numbered as it ends; the acts of finished answers read, recorded and delivered
/// at their addressee's next turn; a round ended early by <c>[pass]</c> twice, <c>[hand back]</c>, an
/// ask to the person, a repeat, a step its next one depends on not finishing, a budget, or the
/// person. Nothing an agent writes opens, lengthens or steers a round. Within a turn, an agent may
/// call the tools it is offered: each call through its tool's own gate question, every call in the
/// audit log and in the chat, its result back in an envelope, at most 20 a turn.
/// </summary>
internal sealed class RoundRunner(RoundHarness harness)
{
    /// <summary>A turn with less than this to write is not started: the round has spent its tokens.</summary>
    public const int FewestTokens = 256;

    /// <summary>What DipTick says when an agent's model will not take tools and answers without them.</summary>
    private const string NoToolsHere = "its model would not take tools here, so it answered without them";

    private static readonly TimeSpan KeepEvery = TimeSpan.FromSeconds(2);

    /// <summary>How a tool call's arguments are shown in its line: indented, one field to a line.</summary>
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Runs one round of a chat: what the person said (null for a pass), addressed or not; <paramref name="once"/>
    /// is a mode for this round only; <paramref name="files"/> came with what was said.
    /// </summary>
    public async Task<RoundResult> RunAsync(string session, string? said, Address? address, ChatMode? once = null, IReadOnlyList<Files.Attached>? files = null)
    {
        files ??= [];
        ArgumentNullException.ThrowIfNull(session);
        var config = harness.Config;
        var store = harness.Store;
        var limits = config.Chat.Round;
        var table = Table.From(store.Table(session));
        var names = table.Select(s => s.Name).ToList();
        var mode = once ?? ChatMode.Parse(store.Find(session)?.Mode ?? "all", out _) ?? ChatMode.All;
        if (mode.Check(names) is { } gone)
        {
            harness.Watcher.Notice($"The chat's mode ({mode}) {gone}; everyone answers this round.");
            mode = ChatMode.All;
        }

        var plan = Planner.For(mode, address, names, limits.MaxAiTurns);
        if (names.Count == 0)
        {
            // Nobody to answer yet: what was said is kept, and the chat says how to seat someone.
            return Empty(session, said, plan);
        }

        if (plan.Steps.Count == 0)
        {
            return new RoundResult(0, plan, [], plan.Line, null);
        }

        var round = store.OpenRound(session, mode.ToString(), plan.Seats);
        var saidRow = store.Begin(session, round, 0, Author.Person, said is null ? MessageKind.Pass : MessageKind.Say, said ?? string.Empty, MessageStatus.Done);
        var saidSeq = store.Messages(session).First(m => m.Id == saidRow).Seq!.Value;
        foreach (var file in files)
        {
            // Kept with the line they came with; a private chat's store is this process's memory.
            store.AddAttachment(session, new StoredAttachment(0, saidSeq, Author.Person, file.Name, file.MediaType, file.Bytes.Length, file.Sha256, file.Bytes, null, DateTimeOffset.UtcNow));
        }

        harness.Watcher.Said(round, saidSeq, plan, mode);

        using var clock = CancellationTokenSource.CreateLinkedTokenSource(harness.Stop);
        clock.CancelAfter(TimeSpan.FromSeconds(limits.MaxRoundSeconds));
        var results = new List<TurnResult>();
        var used = 0;
        var passes = 0;
        var end = "plan-complete";
        var today = DateTime.Now.ToString("dddd, yyyy-MM-dd", CultureInfo.InvariantCulture);
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            if (harness.Stop.IsCancellationRequested)
            {
                end = MessageStatus.Stopped;
                break;
            }

            if (clock.IsCancellationRequested)
            {
                end = "budget:seconds";
                break;
            }

            var left = limits.MaxRoundTokens - used;
            if (left < FewestTokens)
            {
                end = "budget:tokens";
                break;
            }

            // Every request of the step is composed before any turn starts, so none sees another's answer.
            var lines = store.Messages(session);
            var acts = store.Acts(session);
            var brief = store.Brief(session);
            var seen = Seen.From(lines, store.Sources(session));
            var turns = new List<Task<TurnResult>>();
            foreach (var name in step.Seats)
            {
                var seat = table.First(s => s.Name == name);
                if (Table.Resolve(config, seat) is not { Filled: true } agent)
                {
                    var why = $"{name} is not set up any more, so it could not answer.";
                    store.Notice(session, round, why);
                    harness.Watcher.Notice(why);
                    results.Add(new TurnResult(name, MessageStatus.Failed, string.Empty, null, null, null, false, null, why));
                    continue;
                }

                // Each agent's own gate, and the tools it may be offered through it.
                var verdicts = new Verdicts();
                var gate = harness.GateFor(agent, d => verdicts.Last = d);
                var context = new ToolContext(config, session, agent, gate, store, harness.Private, seen);
                var tools = harness.Tools ? await AgentTools.ForAsync(context, clock.Token).ConfigureAwait(false) : ToolOffer.None;

                var cap = Math.Max(1, Math.Min(agent.MaxAnswerTokens, left / step.Seats.Count));
                var facts = new BriefingFacts(name, Person(config), Peers(config, table, name), step, plan, cap, config.UserBrief, agent.Brief, brief)
                {
                    Today = today,
                    Tools = tools.Any,
                    Research = tools.CanResearch,
                    ResearchByProvider = tools.Provider.Count > 0,
                    Skills = SkillLines(tools),
                    SkillTexts = tools.Own.Any(t => t.Name == Skills.LoadSkillTool.ToolName) ? [] : SkillBodies(config, store, session),
                };
                var request = Composer.For(new ComposeFacts(
                    name, Person(config), step, round, lines, names, Mailbox(acts, lines, name), cap, limits.MaxQuotedChars,
                    (int)Math.Min(int.MaxValue, (long)agent.ContextTokens * 4), omitted => Briefing.For(facts, omitted)));
                if (files.Count > 0)
                {
                    // Each agent is sent what its engine and model take of the files; what it is not sent, it is told of, and so is the chat.
                    var sees = await ToolSupport.CanSeeAsync(agent, clock.Token).ConfigureAwait(false);
                    var reads = await ToolSupport.CanReadPdfAsync(agent, clock.Token).ConfigureAwait(false);
                    var (sent, notSent) = Files.Attachments.For(agent, files, sees, reads);
                    request = Files.Attachments.Attach(request, files, sent, notSent);
                    if (notSent.Count > 0)
                    {
                        var why = $"{name} is not sent {string.Join("; ", notSent)}.";
                        store.Notice(session, round, why);
                        harness.Watcher.Notice(why);
                    }
                }
                turns.Add(TurnAsync(new Turn(session, round, i, step, seat, agent, request, context, tools, verdicts), clock.Token));
            }

            var ended = await Task.WhenAll(turns).ConfigureAwait(false);
            results.AddRange(ended);
            used += ended.Sum(r => r.TokensOut ?? 0);

            var handBack = false;
            foreach (var turn in ended.Where(t => t.Status == MessageStatus.Done && t.Seq is not null))
            {
                handBack |= Turns.HandsBack(turn.Text);
                handBack |= RecordActs(session, round, turn, names, table.First(s => s.Name == turn.Seat));
            }

            if (harness.Stop.IsCancellationRequested || ended.Any(t => t.Status == MessageStatus.Stopped))
            {
                end = MessageStatus.Stopped;
                break;
            }

            if (handBack)
            {
                end = "hand-back";
                break;
            }

            if (clock.IsCancellationRequested)
            {
                end = "budget:seconds";
                break;
            }

            if (i == plan.Steps.Count - 1 || ended.Length != 1)
            {
                continue;
            }

            // One agent's step with another after it: the next depends on it (a chain, a review, a debate).
            var only = ended[0];
            passes = only.Status == MessageStatus.Passed ? passes + 1 : 0;
            if (only.Status == MessageStatus.Passed && step.Role == StepRole.Reviewer)
            {
                // The reviewer found nothing to change: the revise step has nothing to answer.
                end = "passed";
                break;
            }

            if (step.Role == StepRole.Stack && passes >= 2)
            {
                end = "passed";
                break;
            }

            if (step.Role == StepRole.Stack && only.Status == MessageStatus.Repeat)
            {
                end = MessageStatus.Repeat;
                break;
            }

            if (only.Status is not (MessageStatus.Done or MessageStatus.Passed))
            {
                end = "dependency";
                break;
            }
        }

        store.CloseRound(session, round, end);
        harness.Watcher.Ended(end);
        return new RoundResult(round, plan, results, end, saidSeq);
    }

    /// <summary>What a person hears when no model is set up at all, in every screen.</summary>
    public const string NoModel = "No model is set up yet. Settings, Models, Provider picks one (it takes about a minute), or run 'diptick setup'.";

    /// <summary>A round at a table with nobody at it: what was said is kept, and a notice says how to seat someone.</summary>
    private RoundResult Empty(string session, string? said, Plan plan)
    {
        var store = harness.Store;
        var round = store.OpenRound(session, ChatMode.All.ToString(), []);
        var row = store.Begin(session, round, 0, Author.Person, said is null ? MessageKind.Pass : MessageKind.Say, said ?? string.Empty, MessageStatus.Done);
        var seq = store.Messages(session).First(m => m.Id == row).Seq!.Value;
        harness.Watcher.Said(round, seq, plan, ChatMode.All);
        var why = harness.Config.AnyAgent ? "Nobody sits at this chat's table. F4, or 'diptick chat seats add <agent>', seats one." : NoModel;
        store.Notice(session, round, why);
        harness.Watcher.Notice(why);
        store.CloseRound(session, round, "no-agents");
        harness.Watcher.Ended("no-agents");
        return new RoundResult(round, plan, [new TurnResult(Table.Main, MessageStatus.Failed, string.Empty, null, null, null, false, null, why)], "no-agents", seq);
    }

    /// <summary>The person's name as the agents read it: <c>user.name</c>, or "the person"; never the Windows account's.</summary>
    public static string Person(MasterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.UserName.Trim().Length > 0 ? config.UserName.Trim() : Briefing.Unnamed;
    }

    /// <summary>Questions put to an agent since its last answer: counted asks from the others, to it or to the team.</summary>
    public static IReadOnlyList<Ask> Mailbox(IReadOnlyList<StoredAct> acts, IReadOnlyList<StoredMessage> lines, string seat)
    {
        ArgumentNullException.ThrowIfNull(acts);
        ArgumentNullException.ThrowIfNull(lines);
        var since = lines.Where(l => l.Author == seat && l.Seq is not null && l.Kind == MessageKind.Answer).Select(l => l.Seq!.Value).DefaultIfEmpty(0).Max();
        return [.. acts
            .Where(a => a.Accepted && a.Kind == "ask" && a.Seat != seat && a.Seq > since)
            .Select(a => (Act: a, To: a.Target.Split(',', StringSplitOptions.RemoveEmptyEntries)))
            .Where(x => x.To.Contains(seat, StringComparer.Ordinal) || x.To.Contains("team", StringComparer.Ordinal))
            .Select(x => new Ask(x.Act.Seq, x.Act.Seat, x.To, x.Act.Text))];
    }

    /// <summary>The skills an agent offered <c>load_skill</c> is told of, by name and what each is for.</summary>
    private static IReadOnlyList<(string Name, string Description)> SkillLines(ToolOffer tools) =>
        tools.Own.OfType<Skills.LoadSkillTool>().FirstOrDefault() is { } load ? [.. load.Usable.Select(s => (s.Name, s.Description))] : [];

    /// <summary>Each skill on in the chat in full, for an agent that cannot ask for one.</summary>
    private static IReadOnlyList<string> SkillBodies(MasterConfig config, IChatStore store, string session) =>
        session.Length == 0 ? [] : [.. Skills.SkillChat.Usable(config, store.ChatSkills(session)).Select(Skills.SkillChat.Wrapped)];
    private static IReadOnlyList<Peer> Peers(MasterConfig config, IReadOnlyList<TableSeat> table, string self) =>
        [.. table.Where(s => s.Name != self).Select(s => Table.Resolve(config, s) is { } agent
            ? new Peer(s.Name, Engines.DisplayName(agent.Provider, agent.Endpoint), agent.Model)
            : new Peer(s.Name, "not set up", s.Model))];

    /// <summary>The last decision a turn's gate recorded, so the turn can say whether a refusal waited for a yes nobody gave.</summary>
    private sealed class Verdicts
    {
        public Decision? Last { get; set; }
    }

    /// <summary>One turn as it is set up before it starts: where it is, who takes it, what it is sent, what it may call.</summary>
    private sealed record Turn(string Session, int Round, int StepIndex, PlanStep Step, TableSeat Seat, SeatConfig Agent, ChatRequest Request, ToolContext Tools, ToolOffer Offered, Verdicts Verdicts);

    /// <summary>
    /// One agent's turn: its line begun, its answer streamed through its own gate, kept as it comes; the
    /// tools it calls run, their calls kept and their results sent back, until it answers without one or
    /// reaches the cap; then its line ended in the state its finish says.
    /// </summary>
    private async Task<TurnResult> TurnAsync(Turn t, CancellationToken roundToken)
    {
        var store = harness.Store;
        var (session, round, stepIndex, step, seat, agent, request) = (t.Session, t.Round, t.StepIndex, t.Step, t.Seat, t.Agent, t.Request);
        var row = store.Begin(session, round, stepIndex, seat.Name, MessageKind.Answer, string.Empty);
        harness.Watcher.TurnStarted(seat.Name, agent, step, stepIndex);

        var text = new StringBuilder();
        int? tokensIn = null, tokensOut = null;
        var status = MessageStatus.Done;
        var why = string.Empty;
        var gate = t.Tools.Gate;
        var sources = new List<int>();
        var madeFiles = new List<long>();

        if (Over(session, agent, request) is { } over)
        {
            status = MessageStatus.Denied;
            why = over;
        }
        else
        {
            var engine = harness.EngineFor(agent);
            var seconds = Table.IsMain(agent.Id) && harness.MainTimeoutSeconds is { } main ? main : agent.TimeoutSeconds;
            using var turn = CancellationTokenSource.CreateLinkedTokenSource(roundToken);
            turn.CancelAfter(TimeSpan.FromSeconds(seconds));
            var kept = Stopwatch.StartNew();
            var own = t.Offered.Own;
            var provider = t.Offered.Provider;
            var specs = own.Select(o => new ToolSpec(o.Name, o.Description, o.Parameters)).ToList();
            var messages = request.Messages.ToList();
            var calls = 0;
            try
            {
                for (var hop = 0; ; hop++)
                {
                    if (hop > 0 && Over(session, agent, request with { Messages = messages }) is { } afterTools)
                    {
                        status = MessageStatus.Cut;
                        why = afterTools;
                        break;
                    }

                    var asking = request with { Messages = messages, Tools = specs, ProviderTools = provider, Continues = hop > 0 };
                    var made = new List<ToolCall>();
                    var saidThisHop = new StringBuilder();
                    EchoPart? echo = null;
                    int? hopIn = null, hopOut = null;
                    var end = AnswerEnd.Done;
                    try
                    {
                        await foreach (var part in engine.StreamAsync(asking, gate, turn.Token).ConfigureAwait(false))
                        {
                            switch (part)
                            {
                                case TextPart piece:
                                    text.Append(piece.Text);
                                    saidThisHop.Append(piece.Text);
                                    harness.Watcher.Delta(seat.Name, piece.Text);
                                    if (kept.Elapsed >= KeepEvery)
                                    {
                                        store.Progress(row, text.ToString());
                                        kept.Restart();
                                    }

                                    break;
                                case UsagePart usage:
                                    hopIn = usage.TokensIn ?? hopIn;
                                    hopOut = usage.TokensOut ?? hopOut;
                                    break;
                                case ToolCallPart call:
                                    made.Add(call.Call);
                                    break;
                                case EchoPart blocks:
                                    echo = blocks;
                                    break;
                                case ProviderToolPart used:
                                    // A provider's own search or fetch ran inside the answer: shown, recorded, its pages kept.
                                    sources.AddRange(ProviderTool(t, used, row));
                                    break;
                                case CitationPart cited:
                                    // A provider's own tool read this and the answer cites it: a source, numbered in the chat.
                                    t.Tools.Seen.Add(cited.Address, SeenFrom.Page);
                                    sources.Add(store.AddSource(session, new StoredSource(0, null, cited.Address, cited.Title, SourceKind.Cited, DateTimeOffset.UtcNow, null, cited.CitedText)));
                                    break;
                                case FinishPart finish:
                                    end = finish.End;
                                    if (finish.End == AnswerEnd.Cut)
                                    {
                                        status = MessageStatus.Cut;
                                        why = finish.Why;
                                    }
                                    else if (finish.End == AnswerEnd.Refused)
                                    {
                                        status = MessageStatus.Refused;
                                        why = finish.Why;
                                    }

                                    break;
                            }
                        }
                    }
                    catch (ChatException ex) when (hop == 0 && (specs.Count > 0 || provider.Count > 0) && text.Length == 0 && ex.Message.Contains("tool", StringComparison.OrdinalIgnoreCase))
                    {
                        // A model its provider will not give tools to answers without them, and the chat says so.
                        specs = [];
                        own = [];
                        provider = [];
                        var note = $"{seat.Name}: {NoToolsHere}.";
                        store.Notice(session, round, note);
                        harness.Watcher.Notice(note);
                        hop = -1;
                        continue;
                    }

                    tokensIn = Add(tokensIn, hopIn);
                    tokensOut = Add(tokensOut, hopOut);
                    if (end != AnswerEnd.Tools || made.Count == 0)
                    {
                        break;
                    }

                    if (calls + made.Count > AgentTools.MostCallsATurn)
                    {
                        status = MessageStatus.Cut;
                        why = string.Create(CultureInfo.InvariantCulture, $"it asked for more than {AgentTools.MostCallsATurn} tool calls in one turn");
                        break;
                    }

                    calls += made.Count;
                    var results = await RunToolsAsync(t, own, made, row, sources, madeFiles, turn.Token).ConfigureAwait(false);

                    // The agent's message goes back as its provider sent it where words and calls cannot rebuild it.
                    messages.Add(new WireMessage(WireRole.Assistant, saidThisHop.ToString()) { Calls = made, Blocks = echo?.Blocks ?? [], BlocksFrom = echo?.Shape ?? string.Empty });
                    messages.Add(new WireMessage(WireRole.User, string.Empty) { Results = results });

                    // The words before the tools and the words after them stay apart.
                    if (text.Length > 0 && text[^1] != '\n')
                    {
                        text.Append("\n\n");
                        harness.Watcher.Delta(seat.Name, "\n\n");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                (status, why) = harness.Stop.IsCancellationRequested ? (MessageStatus.Stopped, "stopped.")
                    : roundToken.IsCancellationRequested ? (MessageStatus.Cut, "the round ran out of time (chat.maxRoundSeconds).")
                    : (MessageStatus.Failed, string.Create(CultureInfo.InvariantCulture, $"no answer within {seconds} s ({agent.Id}.timeoutSeconds)."));
            }
            catch (Exception ex) when (ex is ChatException or InvalidDataException)
            {
                status = t.Verdicts.Last is { Verdict: Verdict.Deny } ? MessageStatus.Denied : MessageStatus.Failed;
                why = ex.Message;
            }
            finally
            {
                (engine as IDisposable)?.Dispose();
            }
        }

        var words = text.ToString().TrimEnd();
        if (status == MessageStatus.Done && Turns.IsPass(words))
        {
            status = MessageStatus.Passed;
        }
        else if (status == MessageStatus.Done && store.Table(session).Count > 1 && IsRepeat(session, words))
        {
            // Only where agents answer each other: the guard is against a loop, and one agent alone cannot loop.
            status = MessageStatus.Repeat;
            why = "the same answer as one earlier in this chat, so it is not passed on.";
        }

        // What the provider did not count is estimated, a token for four characters, and says so.
        var estimated = tokensOut is null && words.Length > 0;
        tokensOut ??= words.Length == 0 ? null : (words.Length + 3) / 4;
        tokensIn ??= status is MessageStatus.Denied ? null : (request.System.Length + request.Messages.Sum(m => m.Text.Length) + 3) / 4;
        long? cost = agent.PriceIn is { } priceIn && agent.PriceOut is { } priceOut && (tokensIn is not null || tokensOut is not null)
            ? (long)Math.Round(((tokensIn ?? 0) * priceIn) + ((tokensOut ?? 0) * priceOut), MidpointRounding.AwayFromZero)
            : null;
        var seq = store.End(row, status, words, tokensIn, tokensOut, estimated, gate.LastHash, cost, Briefing.Version);
        if (sources.Count > 0)
        {
            store.NumberSources(session, sources, seq);
        }

        if (madeFiles.Count > 0)
        {
            // What the turn's tools made (an image, a video) shows with the answer.
            store.NumberAttachments(session, madeFiles, seq);
        }

        if (why.Length > 0 && status is not (MessageStatus.Done or MessageStatus.Passed))
        {
            store.Notice(session, round, $"{seat.Name}: {why}");
        }

        var result = new TurnResult(seat.Name, status, words, seq, tokensIn, tokensOut, estimated, cost, why, t.Verdicts.Last is { NeedsPerson: true });
        harness.Watcher.TurnEnded(result);
        return result;
    }

    private static int? Add(int? total, int? more) => more is { } m ? (total ?? 0) + m : total;

    /// <summary>
    /// The tool calls of one step of a turn, run together: each call written in the
    /// audit log before it runs (who, which tool, a SHA-256 of its arguments), capped in time and in what
    /// it gives back, kept as a line of the chat under the answer, and its result returned in DipTick's
    /// envelope, in the order the model made the calls.
    /// </summary>
    private async Task<List<ToolResult>> RunToolsAsync(Turn t, IReadOnlyList<IAgentTool> offered, List<ToolCall> calls, long answerRow, List<int> sources, List<long> attachments, CancellationToken cancel)
    {
        var done = await Task.WhenAll(calls.Select(call => OneToolAsync(t, offered, call, cancel))).ConfigureAwait(false);
        var nonce = Envelope.Nonce();
        var results = new List<ToolResult>();
        foreach (var (call, outcome, tool, hash, started, ended) in done)
        {
            var statusWord = outcome.End switch { ToolEnd.Done => MessageStatus.Done, ToolEnd.Denied => MessageStatus.Denied, _ => MessageStatus.Failed };
            var row = ToolRow(call, outcome);
            var seq = harness.Store.ToolLine(t.Session, t.Round, t.StepIndex, t.Seat.Name, row, statusWord, answerRow);
            harness.Store.RecordToolCall(t.Session, new StoredToolCall(seq, t.Seat.Name, tool?.Connector, call.Name, ToolArgs.Digest(call.Arguments), statusWord, hash, started, ended));
            harness.Watcher.Tool(t.Seat.Name, seq, call.Name, row, statusWord);
            sources.AddRange(outcome.Sources);
            attachments.AddRange(outcome.Attachments);
            var content = outcome.Content.Length <= AgentTools.MostResult
                ? outcome.Content
                : string.Create(CultureInfo.InvariantCulture, $"{outcome.Content[..AgentTools.MostResult]}\n[cut: {outcome.Content.Length - AgentTools.MostResult} more characters not shown]");
            results.Add(new ToolResult(call.Id, call.Name, Envelope.Tool(call.Name, nonce, content), outcome.End != ToolEnd.Done) { File = outcome.File });
        }

        return results;
    }

    /// <summary>
    /// A provider's own search or fetch that ran inside an answer: a tool row
    /// under the answer, its record, and its audit line written after the fact (the provider ran it, so
    /// there was nothing to ask first), with <c>provider:&lt;host&gt;</c> as its connector and a SHA-256
    /// of what it was asked; every page it found kept as a source of the chat and seen as from a search.
    /// Returns the sources' numbers.
    /// </summary>
    private List<int> ProviderTool(Turn t, ProviderToolPart used, long answerRow)
    {
        var store = harness.Store;
        var host = Engines.Endpoint(t.Agent.Provider, t.Agent.Endpoint)?.Host ?? t.Agent.Provider;
        var connector = $"provider:{host}";
        var digest = ToolArgs.Digest(used.Asked);
        string? hash = null;
        try
        {
            hash = harness.Log.RecordTool(harness.Who, $"agent:{t.Agent.Id}", used.Tool, connector, digest).Hash;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            var why = $"{t.Seat.Name}'s provider ran {used.Tool}, and the audit log could not record it ({SafeText.Line(ex.Message)}).";
            store.Notice(t.Session, t.Round, why);
            harness.Watcher.Notice(why);
        }

        var numbers = new List<int>();
        foreach (var page in used.Pages)
        {
            t.Tools.Seen.Add(page.Address, SeenFrom.Search);
            numbers.Add(store.AddSource(t.Session, new StoredSource(0, null, page.Address, page.Title, page.Read ? SourceKind.Provider : SourceKind.Search, DateTimeOffset.UtcNow, null, null)));
        }

        var status = used.Failed ? MessageStatus.Failed : MessageStatus.Done;
        var listed = string.Join('\n', used.Pages.Take(20).Select((p, i) => $"[{numbers[i]}] {p.Title}\n    {p.Address}"));
        var row = $"{used.Tool} · {used.Summary} (run by {SafeText.Line(host)})\nasked: {used.Asked}\ngave: {(listed.Length > 0 ? "\n" + listed : used.Summary)}";
        var now = DateTimeOffset.UtcNow;
        var seq = store.ToolLine(t.Session, t.Round, t.StepIndex, t.Seat.Name, row, status, answerRow);
        store.RecordToolCall(t.Session, new StoredToolCall(seq, t.Seat.Name, connector, used.Tool, digest, status, hash, now, now));
        harness.Watcher.Tool(t.Seat.Name, seq, used.Tool, row, status);
        return numbers;
    }

    /// <summary>One call: its tool found, its arguments read, its audit line written before anything runs, then the tool, within its time cap.</summary>
    private async Task<(ToolCall Call, ToolOutcome Outcome, IAgentTool? Tool, string? Hash, DateTimeOffset Started, DateTimeOffset Ended)> OneToolAsync(Turn t, IReadOnlyList<IAgentTool> offered, ToolCall call, CancellationToken cancel)
    {
        var started = DateTimeOffset.UtcNow;
        var tool = offered.FirstOrDefault(o => o.Name == call.Name);
        if (tool is null)
        {
            return (call, ToolOutcome.Failed($"there is no tool named {SafeText.Line(call.Name)} here"), null, null, started, DateTimeOffset.UtcNow);
        }

        if (ToolArgs.Parse(call.Arguments) is not { } arguments)
        {
            return (call, ToolOutcome.Failed("its arguments are not a JSON object"), tool, null, started, DateTimeOffset.UtcNow);
        }

        // No record, no action: a call the audit log cannot hold is not run.
        string hash;
        try
        {
            hash = harness.Log.RecordTool(harness.Who, $"agent:{t.Agent.Id}", call.Name, tool.Connector, ToolArgs.Digest(call.Arguments)).Hash;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            return (call, ToolOutcome.Failed("the audit log cannot be written, so nothing is run"), tool, null, started, DateTimeOffset.UtcNow);
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(tool.Limit);
        ToolOutcome outcome;
        try
        {
            outcome = await tool.RunAsync(t.Tools, arguments, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            outcome = ToolOutcome.Failed(string.Create(CultureInfo.InvariantCulture, $"it took longer than {tool.Limit.TotalSeconds} s"));
        }
        catch (Exception ex) when (ex is ChatException or IOException or HttpRequestException or UnauthorizedAccessException or InvalidOperationException or JsonException or FormatException or ArgumentException)
        {
            outcome = ToolOutcome.Failed(SafeText.Line(ex.Message));
        }

        return (call, outcome, tool, hash, started, DateTimeOffset.UtcNow);
    }

    /// <summary>A tool call's line in the chat: the tool and what came of it, then what it was asked and what it gave, folded.</summary>
    private static string ToolRow(ToolCall call, ToolOutcome outcome)
    {
        static string Clip(string text, int most) => text.Length <= most ? text : text[..most] + "…";
        var asked = call.Arguments;
        try
        {
            using var doc = JsonDocument.Parse(asked);
            asked = JsonSerializer.Serialize(doc.RootElement, Indented);
        }
        catch (JsonException)
        {
            // Shown as the model wrote it.
        }

        return $"{call.Name} · {outcome.Summary}\nasked: {Clip(asked, 2000)}\ngave: {Clip(outcome.Content, 4000)}";
    }

    /// <summary>Whether an answer says what an earlier finished answer in the chat already said.</summary>
    private bool IsRepeat(string session, string words)
    {
        var fingerprint = Turns.Fingerprint(words);
        return harness.Store.Messages(session)
            .Any(l => l.Kind == MessageKind.Answer && l.Status == MessageStatus.Done && l.Seq is not null && Turns.Fingerprint(l.Text) == fingerprint);
    }

    /// <summary>
    /// Why a turn may not start for money, or null: its worst case (what it is sent at its price in, and
    /// its whole cap at its price out) would take this chat, today or this month past a cap the person
    /// set. An agent with no prices cannot be counted, and a cap cannot see it.
    /// </summary>
    private string? Over(string session, SeatConfig agent, ChatRequest request)
    {
        var money = harness.Config.Spending;
        if (agent.PriceIn is not { } priceIn || agent.PriceOut is not { } priceOut || money is { CapChat: null, CapDay: null, CapMonth: null })
        {
            return null;
        }

        var sent = request.System.Length + request.Messages.Sum(m => m.Text.Length + m.Results.Sum(r => r.Content.Length) + m.Blocks.Sum(b => b.GetRawText().Length));
        var worst = (long)Math.Ceiling((((sent + 3) / 4) * priceIn) + (request.MaxTokens * priceOut));
        var now = DateTimeOffset.Now;
        var today = new DateTimeOffset(now.Date, now.Offset);
        foreach (var (cap, since, what) in new (decimal? Cap, DateTimeOffset? Since, string What)[]
        {
            (money.CapChat, null, "this chat"),
            (money.CapDay, today, "today"),
            (money.CapMonth, today.AddDays(1 - now.Day), "this month"),
        })
        {
            if (cap is { } most && harness.Store.Spent(what == "this chat" ? session : null, since) + worst > most * 1_000_000m)
            {
                return string.Create(CultureInfo.InvariantCulture, $"its answer could take {what} past the cap of {most} {money.Currency} (money.cap{(what == "this chat" ? "Chat" : what == "today" ? "Day" : "Month")}), so it was not asked.");
            }
        }

        return null;
    }

    /// <summary>
    /// Reads a finished answer's act block and keeps each act with its audit line; an act whose line
    /// cannot be written has no effect. Returns whether an ask went to the
    /// person, which hands the floor back after this step.
    /// </summary>
    private bool RecordActs(string session, int round, TurnResult turn, IReadOnlyList<string> names, TableSeat seat)
    {
        var toPerson = false;
        var agent = Table.Resolve(harness.Config, seat);
        foreach (var act in ChatActs.Judge(turn.Text, turn.Seat, finished: true, names))
        {
            var ask = act.Act as AskAct;
            var to = ask is null ? string.Empty : string.Join(",", ask.To);
            string hash;
            try
            {
                hash = harness.Log.RecordAct(harness.Who, $"agent:{agent?.Id ?? turn.Seat}", act.Kind, $"chat {session} #{turn.Seq!.Value.ToString(CultureInfo.InvariantCulture)} to {(to.Length == 0 ? "-" : to)}", act.Accepted, act.Rejected).Hash;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.Cryptography.CryptographicException)
            {
                var why = $"{turn.Seat}'s act was not kept: the audit log cannot be written ({SafeText.Line(ex.Message)}).";
                harness.Store.Notice(session, round, why);
                harness.Watcher.Notice(why);
                continue;
            }

            harness.Store.RecordAct(session, new StoredAct(turn.Seq!.Value, turn.Seat, act.Kind, to, ask?.Q ?? string.Empty, act.Accepted, act.Rejected, hash));
            harness.Watcher.Act(turn.Seat, turn.Seq.Value, act);
            toPerson |= act.Accepted && ask is not null && ask.To.Contains("person", StringComparer.Ordinal);
        }

        return toPerson;
    }
}
