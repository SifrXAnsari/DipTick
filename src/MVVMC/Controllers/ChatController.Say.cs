using System.Globalization;
using System.Text;
using System.Text.Json;
using DipTick.Models;
using DipTick.Models.Chat;
using DipTick.Models.Chat.Seam;
using DipTick.Models.Chat.Store;
using DipTick.Models.Harness;
using DipTick.Models.Protocol;

namespace DipTick.Controllers;

/// <summary><c>chat say</c> and <c>chat pass</c>: one round in a kept chat. <c>chat export</c>: a chat written to a file.</summary>
internal static partial class ChatController
{
    private const int MostSaid = 256 * 1024;
    private const string SayUsage = "usage: diptick chat say [--session <id|last|new>] [--to <name>|team|a,b] [--mode <mode>] [--research on|off] [--attach <file>]... [--literal] [--events] [--private] [--] <words…>  or  … -  (stdin)";

    /// <summary>A round's hold on its chat, renewed at half its life while answers stream, so a crash frees it soon.</summary>
    private static readonly TimeSpan LeaseLife = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonLines = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>What one round is to do, once the words and the chat are settled.</summary>
    /// <param name="Research">Research switched on or off for the chat before the round (<c>--research</c>); null leaves it.</param>
    /// <param name="Files">Files attached to what is said (<c>--attach</c>); none for a pass.</param>
    private sealed record TurnPlan(string Session, bool Fresh, string Title, string? Said, bool Events, bool Dry, bool Private, Address? Address, ChatMode? Once, bool? Research = null, IReadOnlyList<Models.Files.Attached>? Files = null);

    /// <summary>
    /// One round of a kept chat: the person's
    /// words, addressed or not, then the answers of the agents the plan names, each kept as it streams
    /// and numbered when it ends. The window runs this verb for every message, with <c>--events</c>,
    /// and draws the events.
    /// </summary>
    /// <remarks>
    /// Output: the answers as text (one agent's streamed as it comes, several each whole under its
    /// name), one object at the end (<c>--output json</c>), or JSON Lines as it happens
    /// (<c>--events</c>: round, said, turn, delta, act, notice, turn, end). Exit 0 when every answer
    /// came, whole or cut by a limit; 1 when one failed, stopped, was refused or denied, or the chat
    /// is busy; 2 on usage; 3 when a call needed a yes and nobody could give one.
    /// </remarks>
    /// <param name="isPrivate">A private chat (<c>--private</c>): its store is this process's memory, and its decisions go to the day's private audit log.</param>
    private static int Say(IChatStore store, Cli cli, VerbArgs words, bool isPrivate) => Say(store, cli, words, isPrivate, pass: false);

    /// <summary><c>chat pass</c>: a round with no new words, the person letting the agents go on.</summary>
    private static int Pass(IChatStore store, Cli cli, VerbArgs words, bool isPrivate) => Say(store, cli, words, isPrivate, pass: true);

    private static int Say(IChatStore store, Cli cli, VerbArgs words, bool isPrivate, bool pass)
    {
        var session = words.Option("--session") ?? (pass ? "last" : "new");
        var events = words.Switch("--events");
        var to = words.Option("--to");
        var modeText = words.Option("--mode");
        var researchText = words.Option("--research");
        var attachPaths = words.All("--attach");
        var literal = words.Switch("--literal");
        var fromStdin = !pass && words.Dash;
        var text = words.Words();
        if (words.Problem is { } problem)
        {
            return cli.Fail($"{problem} {SayUsage}", Exit.Usage);
        }

        ChatMode? once = null;
        if (modeText is not null && (once = ChatMode.Parse(modeText, out var modeProblem)) is null)
        {
            return cli.Fail($"--mode {modeProblem}.", Exit.Usage);
        }

        // Files are read and checked before anything is kept or sent: one that will not do stops it all.
        var files = new List<Models.Files.Attached>();
        foreach (var path in attachPaths)
        {
            try
            {
                files.Add(Models.Files.Attachments.Load(path));
            }
            catch (Models.Files.SafeFileException ex)
            {
                return cli.Fail($"{ex.Message}. Nothing was sent.", Exit.Usage);
            }
        }

        if (Models.Files.Attachments.MessageProblem(files) is { } tooMuch)
        {
            return cli.Fail($"{tooMuch}. Nothing was sent.", Exit.Usage);
        }

        if (pass && files.Count > 0)
        {
            return cli.Fail("a pass says nothing, so it carries no files. Nothing was sent.", Exit.Usage);
        }

        bool? research = researchText switch
        {
            null => null,
            "on" => true,
            "off" => false,
            _ => null,
        };
        if (researchText is not null && research is null)
        {
            return cli.Fail("--research takes on or off.", Exit.Usage);
        }

        if (pass)
        {
            if (text.Count > 0)
            {
                return cli.Fail("usage: diptick chat pass [--session <id|last>] [--mode <mode>] [--events]: the agents go on with no new words.", Exit.Usage);
            }

            if (store.Find(session) is not { Open: true } going)
            {
                return cli.Fail("there is no open chat to pass the floor back to.");
            }

            return Held(store, cli, new TurnPlan(going.Id, false, going.Title, null, events, cli.Flags.DryRun, isPrivate, null, once, research));
        }

        string said;
        if (fromStdin)
        {
            if (cli.Ask is not null)
            {
                return cli.Fail("a screen has no standard input to read; pass the words.", Exit.Usage);
            }

            var buffer = new char[MostSaid + 1];
            var read = Console.In.ReadBlock(buffer, 0, buffer.Length);
            if (read > MostSaid)
            {
                return cli.Fail("what was said on stdin is longer than 256 KiB.", Exit.Usage);
            }

            said = new string(buffer, 0, read).Trim();
        }
        else
        {
            said = string.Join(' ', text).Trim();
        }

        // Files alone are a message too: it says their names, so no line of the chat is empty.
        if (said.Length == 0 && files.Count > 0)
        {
            said = Models.Files.Attachments.Wordless(files.Select(f => f.Name));
        }

        if (said.Length == 0)
        {
            return cli.Fail(SayUsage, Exit.Usage);
        }

        if (said.Length > MostSaid)
        {
            return cli.Fail("what was said is longer than 256 KiB.", Exit.Usage);
        }

        var fresh = string.Equals(session, "new", StringComparison.OrdinalIgnoreCase);
        SessionInfo? found = null;
        if (!fresh)
        {
            found = store.Find(session);
            if (found is null)
            {
                return cli.Fail(NoSuch(session));
            }

            if (!found.Open)
            {
                return cli.Fail($"{found.Id} is closed; 'diptick chat say -- <words>' starts a new one.");
            }
        }

        // Who is addressed, read before anything is kept: an unknown name keeps nothing and sends nothing.
        var names = found is null ? [.. Table.Starting(cli.Config).Select(s => s.Name)] : Table.From(store.Table(found.Id)).Select(s => s.Name).ToList();
        Address? address = null;
        if (to is not null)
        {
            var named = to.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Addressing.Lower).ToList();
            if (named is ["team"] or ["all"])
            {
                address = new Address(AddressKind.Team, names, said);
            }
            else if (named.FirstOrDefault(n => !names.Contains(n)) is { } stranger)
            {
                return cli.Fail($"--to names {stranger}, who is not at this chat's table ({string.Join(", ", names)}).", Exit.Usage);
            }
            else
            {
                address = new Address(AddressKind.Seats, named.Distinct(StringComparer.Ordinal).ToList(), said);
            }
        }
        else if (!literal)
        {
            address = Addressing.Parse(said, names, out var addressProblem);
            if (address is null)
            {
                return cli.Fail($"{addressProblem}. Nothing was sent.", Exit.Usage);
            }

            if (address.Kind == AddressKind.None)
            {
                address = null;
            }
            else
            {
                said = address.Text;
            }
        }

        // A round nobody is planned to answer (pick mode, nobody addressed) keeps nothing, not even a new chat.
        var mode = once ?? ChatMode.Parse(found?.Mode ?? Starting(cli.Config), out _) ?? ChatMode.All;
        if (names.Count > 0 && mode.Check(names) is null && Planner.For(mode, address, names, cli.Config.Chat.Round.MaxAiTurns) is { Steps.Count: 0 } nobody)
        {
            return cli.Fail($"{nobody.Line}. Nothing was sent.", Exit.Usage);
        }

        if (SafeText.InvisibleIn(said) is > 0 and var invisible)
        {
            cli.Note(string.Create(CultureInfo.InvariantCulture, $"Removed {invisible} invisible characters from what you said before it went to the agents."));
        }

        var title = found?.Title is { Length: > 0 } kept ? kept : ChatStores.TitleFrom(said);
        var dry = cli.Flags.DryRun;
        var id = found?.Id;
        if (!dry && id is null)
        {
            id = store.Create(cli.Who, Starting(cli.Config), title, [.. Table.Starting(cli.Config).Select(s => Table.Record(cli.Config, s))]);
        }
        else if (!dry && found is { Title.Length: 0 })
        {
            // A chat made before anything was said in it (its table set first) is named by its first words.
            store.Title(found.Id, title);
        }

        return Held(store, cli, new TurnPlan(id ?? string.Empty, fresh, title, said, events, dry, isPrivate, address, once, research, files));
    }

    /// <summary>The mode a new chat starts in: <c>chat.mode</c>, unless it names an agent the new table does not seat.</summary>
    private static string Starting(MasterConfig config) =>
        ChatMode.Parse(config.Chat.Mode, out _) is { } mode && mode.Check([.. Table.Starting(config).Select(s => s.Name)]) is null ? mode.ToString() : ChatMode.All.ToString();

    /// <summary>The round with its chat held: the lease taken, renewed while it runs, and let go after, unless nothing is kept.</summary>
    private static int Held(IChatStore store, Cli cli, TurnPlan plan)
    {
        if (plan.Dry)
        {
            using var copy = DryCopy(store, cli, plan);
            return Round(copy, cli, plan with { Session = plan.Session.Length > 0 ? plan.Session : DryId });
        }

        if (store.Lease(plan.Session, Environment.ProcessId, LeaseLife) is { } holder)
        {
            return cli.Fail($"{plan.Session} is busy: a round is running in process {holder.ToString(CultureInfo.InvariantCulture)}. Wait for it to end.");
        }

        try
        {
            using var renew = new Timer(_ => Renew(store, plan.Session), null, LeaseLife / 2, LeaseLife / 2);
            return Round(store, cli, plan);
        }
        finally
        {
            store.Release(plan.Session, Environment.ProcessId);
        }
    }

    /// <summary>The id a dry run's copy of a new chat goes under; it is kept nowhere.</summary>
    private const string DryId = "dry-run";

    /// <summary>
    /// A dry run keeps nothing (<c>--dry-run</c>): the round runs on a copy of the chat in memory, or on
    /// a new chat that is never made, and every line it writes goes with the copy.
    /// </summary>
    private static MemoryChatStore DryCopy(IChatStore store, Cli cli, TurnPlan plan)
    {
        var copy = new MemoryChatStore();
        if (plan.Session.Length > 0 && store.Find(plan.Session) is { } found)
        {
            copy.Seed(found, store.Table(found.Id), store.Brief(found.Id), store.Messages(found.Id), store.Acts(found.Id));
            copy.SeedRules(found.Id, store.ChatRules(found.Id), store.ChatConnectors(found.Id), store.ChatSkills(found.Id));
        }
        else
        {
            var now = DateTimeOffset.UtcNow;
            copy.Seed(
                new SessionInfo(DryId, plan.Title, cli.Who, Starting(cli.Config), now, now, true, 0, null),
                [.. Table.Starting(cli.Config).Select(s => Table.Record(cli.Config, s))],
                string.Empty,
                [],
                []);
        }

        return copy;
    }

    private static int Round(IChatStore store, Cli cli, TurnPlan plan)
    {
        var (id, fresh, title, said, events, dry, isPrivate, address, once, research, files) = plan;
        if (events)
        {
            cli.WriteEvent(new { @event = "round", session = id, @new = fresh && !dry, title, kept = !dry });
        }
        else if (dry)
        {
            cli.Note("[dry run] the answers are shown; nothing is kept.");
        }
        else if (fresh && isPrivate)
        {
            cli.Note($"private chat {id}: kept in this process's memory only, and gone when it ends");
        }
        else if (fresh)
        {
            cli.Note($"session {id} · 'diptick chat say --session {id} -- …' goes on with it");
        }

        // A line still streaming when this round took the hold was left by a crash: it is cut, and said.
        foreach (var cut in store.Recover(id))
        {
            Tell(cli, events, string.Create(CultureInfo.InvariantCulture, $"#{cut} was cut off when DipTick last closed; it is kept as far as it got."));
        }

        // Ctrl+C on the command line and a screen's Esc both stop the answers through Cli.Stop; what
        // was said is kept, marked stopped.
        cli.StopsCleanly();

        var log = isPrivate ? AuditLog.ForPrivate(cli.Config.AuditPrivateDays) : AuditLog.ForThisUser();
        if (research is { } researchOn && !SetResearch(store, cli, log, id, researchOn, dry))
        {
            return Exit.Failed;
        }

        var watcher = events ? new EventWatcher(cli) : cli.Json || cli.Flags.Output == OutputFormat.None ? new Collector() : new TextWatcher(cli);
        using var prompts = new SemaphoreSlim(1, 1);

        // Each agent's own gate: the settings, the chat's own rules read at each request, and, where a
        // screen offers it, "Allow for this chat" beside Allow once and Deny.
        Gate GateFor(SeatConfig agent, Action<Decision> decided) =>
            new(cli.Config.Permissions, cli.GateFlags, log, cli.Who, cli.Err, question => OneAtATime(prompts, () => cli.Question(question)), agent, decided)
            {
                ChatRules = () => store.ChatRules(id),
                Approve = cli.Approve is { } screen ? question => OneAtATime(prompts, () => screen(question)) : null,
                AllowForChat = rule => KeepChatRule(store, cli, log, id, rule, Verdict.Allow),
            };

        var harness = new RoundHarness(
            cli.Config,
            store,
            cli.Who,
            GateFor,
            Engines.Create,
            log,
            watcher,
            cli.Stop,
            cli.Flags.MainTimeoutSeconds > 0 ? cli.Flags.MainTimeoutSeconds : null,
            Private: isPrivate);

        var result = new RoundRunner(harness).RunAsync(id, said, address, once, files).GetAwaiter().GetResult();
        if (result.Round == 0)
        {
            // Nobody was planned to answer (pick mode, and nobody addressed): nothing was kept or sent.
            return cli.Fail($"{result.EndReason}. Nothing was sent.", Exit.Usage);
        }

        if (!events)
        {
            var first = result.Turns.Count > 0 ? result.Turns[0] : null;
            cli.WriteJson(new
            {
                session = dry ? null : id,
                kept = !dry,
                round = result.Round,
                plan = result.Plan.Line,
                end = result.EndReason,
                turns = result.Turns.Select(t => new { t.Seat, t.Seq, status = t.Status, answer = t.Text, t.TokensIn, t.TokensOut, estimated = t.Estimated, cost = Cost(t.CostMicros), error = t.Why.Length > 0 ? t.Why : null }),

                // The first agent's answer again on its own, for a script that reads one answer.
                seq = first?.Seq,
                status = first?.Status,
                answer = first?.Text,
                engine = watcher.EngineOf(first?.Seat),
                error = first?.Why is { Length: > 0 } why ? why : null,
            });
        }

        var failed = result.Turns.Where(t => t.Status is MessageStatus.Failed or MessageStatus.Stopped or MessageStatus.Denied or MessageStatus.Refused).ToList();
        if (failed.Count == 0)
        {
            return Exit.Ok;
        }

        if (failed.Any(t => t.NeededPerson))
        {
            if (!events)
            {
                cli.Err.WriteLine("diptick: an agent's call needs a yes and there is no one to ask. Pass --yes, run it at a keyboard, or allow model.call in the permissions.");
            }

            return Exit.NoYes;
        }

        return Exit.Failed;
    }

    /// <summary>What a cost in millionths comes to, for the output; null when there is no price.</summary>
    private static decimal? Cost(long? micros) => micros is { } m ? m / 1_000_000m : null;

    /// <summary>A question to the person from several agents at once, asked one at a time, so two prompts never interleave.</summary>
    private static T OneAtATime<T>(SemaphoreSlim prompts, Func<T> ask)
    {
        prompts.Wait();
        try
        {
            return ask();
        }
        finally
        {
            prompts.Release();
        }
    }

    /// <summary>
    /// Keeps one of a chat's own rules, with its audit line first: no line, no rule, as every change
    /// is audited. Returns whether it was kept.
    /// </summary>
    internal static bool KeepChatRule(IChatStore store, Cli cli, AuditLog log, string session, string rule, Verdict verdict)
    {
        try
        {
            var hash = log.RecordPolicy(cli.Who, $"chat {session}", rule, verdict.ToString().ToUpperInvariant() switch { "ALLOW" => "allow in this chat", "ASK" => "ask in this chat", _ => "deny in this chat" }).Hash;
            store.SetChatRule(session, new ChatRule(rule, verdict), hash);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or Microsoft.Data.Sqlite.SqliteException)
        {
            cli.Err.WriteLine($"diptick: this chat's rule was not kept: {SafeText.Line(ex.Message)}");
            return false;
        }
    }

    /// <summary>A line from DipTick about this round: an event for a screen, a note on stderr for a person.</summary>
    private static void Tell(Cli cli, bool events, string text)
    {
        if (events)
        {
            cli.WriteEvent(new { @event = "notice", text });
        }
        else
        {
            cli.Note(text);
        }
    }

    /// <summary>Keeps the round's hold alive. A failure only lets the hold lapse; it never ends the round.</summary>
    private static void Renew(IChatStore store, string id)
    {
        try
        {
            store.Renew(id, Environment.ProcessId, LeaseLife);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidOperationException or ObjectDisposedException)
        {
            // The hold lapses at its time; the next round takes it again.
        }
    }

    /// <summary>What a round's watcher learns that the verb's ending needs, each agent's engine; the watchers that show a round build on it.</summary>
    private class Collector : IRoundWatcher
    {
        private readonly Dictionary<string, string> _engines = new(StringComparer.Ordinal);

        public string? EngineOf(string? seat)
        {
            lock (_engines)
            {
                return seat is not null && _engines.TryGetValue(seat, out var engine) ? engine : null;
            }
        }

        public virtual void Said(int round, int seq, Plan plan, ChatMode mode)
        {
        }

        public virtual void TurnStarted(string seat, SeatConfig agent, PlanStep step, int stepIndex)
        {
            lock (_engines)
            {
                _engines[seat] = $"{agent.Provider} {agent.Model}";
            }
        }

        public virtual void Delta(string seat, string text)
        {
        }

        public virtual void Tool(string seat, int seq, string tool, string text, string status)
        {
        }

        public virtual void TurnEnded(TurnResult result)
        {
        }

        public virtual void Act(string seat, int seq, ParsedAct act)
        {
        }

        public virtual void Notice(string text)
        {
        }

        public virtual void Ended(string reason)
        {
        }
    }

    /// <summary>A round as JSON Lines, for a screen: every event as it happens.</summary>
    private sealed class EventWatcher(Cli cli) : Collector
    {
        public override void Said(int round, int seq, Plan plan, ChatMode mode) =>
            cli.WriteEvent(new { @event = "said", round, seq, plan = plan.Line, steps = plan.Seats, mode = mode.ToString() });

        public override void TurnStarted(string seat, SeatConfig agent, PlanStep step, int stepIndex)
        {
            base.TurnStarted(seat, agent, step, stepIndex);
            cli.WriteEvent(new { @event = "turn", seat, agent = agent.Id, engine = $"{agent.Provider} {agent.Model}", provider = agent.Provider, model = agent.Model, color = agent.Color, step = stepIndex, role = step.Role.ToString(), state = MessageStatus.Streaming });
        }

        public override void Delta(string seat, string text) => cli.WriteEvent(new { @event = "delta", seat, text });

        public override void Tool(string seat, int seq, string tool, string text, string status) =>
            cli.WriteEvent(new { @event = "tool", seat, seq, tool, text, state = status });

        public override void TurnEnded(TurnResult result)
        {
            cli.WriteEvent(new
            {
                @event = "turn",
                seat = result.Seat,
                state = result.Status,
                seq = result.Seq,
                tokensIn = result.TokensIn,
                tokensOut = result.TokensOut,
                estimated = result.Estimated,
                cost = Cost(result.CostMicros),
                error = result.Why.Length > 0 ? result.Why : null,
            });
        }

        public override void Act(string seat, int seq, ParsedAct act) =>
            cli.WriteEvent(new { @event = "act", seat, seq, kind = act.Kind, to = (act.Act as AskAct)?.To, question = (act.Act as AskAct)?.Q, accepted = act.Accepted, code = act.Rejected, words = ChatActs.Words(act) });

        public override void Notice(string text) => cli.WriteEvent(new { @event = "notice", text });

        public override void Ended(string reason) => cli.WriteEvent(new { @event = "end", reason });
    }

    /// <summary>
    /// A round as text, for a person at a terminal: one agent's answer streamed as it comes; answers
    /// given at the same time each shown whole, under its name, as it ends, so none runs into another.
    /// What an agent says is shown, never obeyed: no escape sequence reaches the terminal.
    /// </summary>
    private sealed class TextWatcher(Cli cli) : Collector
    {
        private readonly Dictionary<string, StringBuilder> _held = new(StringComparer.Ordinal);
        private int _atOnce;
        private bool _several;

        public override void Said(int round, int seq, Plan plan, ChatMode mode)
        {
            _several = plan.Turns > 1;
            if (_several)
            {
                cli.Note($"round {round.ToString(CultureInfo.InvariantCulture)} · {plan.Line}");
            }
        }

        public override void TurnStarted(string seat, SeatConfig agent, PlanStep step, int stepIndex)
        {
            base.TurnStarted(seat, agent, step, stepIndex);
            _atOnce = step.Seats.Count;
            if (_several && _atOnce == 1)
            {
                cli.WriteText($"{SafeText.Line(seat)} · {SafeText.Line(agent.Model)}\n");
            }

            lock (_held)
            {
                _held[seat] = new StringBuilder();
            }
        }

        public override void Delta(string seat, string text)
        {
            if (_atOnce == 1)
            {
                cli.WriteText(SafeText.Block(text));
                return;
            }

            lock (_held)
            {
                _held[seat].Append(text);
            }
        }

        public override void TurnEnded(TurnResult result)
        {
            if (_atOnce > 1)
            {
                string held;
                lock (_held)
                {
                    held = _held[result.Seat].ToString();
                }

                // An answer that never came (denied, failed before a word) has only its reason, below.
                if (held.Length > 0)
                {
                    cli.WriteText($"{SafeText.Line(result.Seat)}\n{SafeText.Block(held)}\n\n");
                }
            }
            else if (result.Text.Length > 0)
            {
                cli.WriteText(_several ? "\n\n" : "\n");
            }

            if (result.Why.Length > 0 && result.Status is not (MessageStatus.Done or MessageStatus.Passed))
            {
                cli.Err.WriteLine($"diptick: {SafeText.Line(result.Seat)}: {SafeText.Line(result.Why)}");
            }
        }

        public override void Act(string seat, int seq, ParsedAct act) =>
            cli.Note($"{SafeText.Line(seat)} #{seq.ToString(CultureInfo.InvariantCulture)}: {SafeText.Line(ChatActs.Words(act))}");

        public override void Tool(string seat, int seq, string tool, string text, string status) =>
            cli.Note($"» {SafeText.Line(seat)} #{seq.ToString(CultureInfo.InvariantCulture)} {SafeText.Line(text.Split('\n')[0])}{(status == MessageStatus.Done ? string.Empty : $" [{status}]")}");

        public override void Notice(string text) => cli.Note(text);

        public override void Ended(string reason)
        {
            if (_several && reason != "plan-complete")
            {
                cli.Note($"round ended: {reason}");
            }
        }
    }

    /// <summary>
    /// A kept session written to a file the person names: text to read, or JSON Lines for a
    /// program. A person's act, not an agent's, so it goes through no policy; it honours a dry run
    /// and never overwrites without a yes.
    /// </summary>
    private static int Export(IChatStore store, Cli cli, VerbArgs words)
    {
        var session = words.Option("--session") ?? "last";
        var format = words.Option("--format") ?? "text";
        var rest = words.Words();
        if (words.Problem is not null || rest.Count != 1)
        {
            return cli.Fail(Usage, Exit.Usage);
        }

        if (format is not ("text" or "jsonl"))
        {
            return cli.Fail("--format is text or jsonl.", Exit.Usage);
        }

        if (store.Find(session) is not { } found)
        {
            return cli.Fail(NoSuch(session));
        }

        string path;
        try
        {
            path = Path.GetFullPath(rest[0]);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return cli.Fail($"not a path: {SafeText.Line(rest[0])}", Exit.Usage);
        }

        var lines = store.Messages(found.Id);
        if (cli.Flags.DryRun)
        {
            cli.Note(string.Create(CultureInfo.InvariantCulture, $"[dry run] would write {lines.Count} lines of {found.Id} to {path}; nothing written."));
            return Exit.Ok;
        }

        if (File.Exists(path))
        {
            var answer = cli.Confirm($"{path} exists. Overwrite it?");
            if (answer != Answer.Yes)
            {
                return cli.NotConfirmed(answer, $"overwriting {path}");
            }
        }

        var body = format == "jsonl" ? JsonLinesOf(lines) : TextOf(found, lines);
        try
        {
            File.WriteAllText(path, body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return cli.Fail($"could not write {path}: {ex.Message}");
        }

        if (!cli.WriteJson(new { session = found.Id, path, format, lines = lines.Count }))
        {
            cli.Note(string.Create(CultureInfo.InvariantCulture, $"Wrote {lines.Count} lines of {found.Id} to {path}."));
        }

        return Exit.Ok;
    }

    /// <summary>
    /// A chat as a Markdown file, for <c>diptick data export</c>: its title, then each line under
    /// its author and time, its text as it was written, and any end it did not reach said.
    /// </summary>
    internal static string MarkdownOf(SessionInfo session, IReadOnlyList<StoredMessage> lines)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(lines);
        var text = new StringBuilder();
        text.Append("# ").Append(session.Title.Length == 0 ? "(untitled)" : session.Title).Append("\n\n");
        text.Append(CultureInfo.InvariantCulture, $"Chat {session.Id}, begun {session.Created.ToLocalTime():yyyy-MM-dd HH:mm}, {lines.Count} lines.\n\n");
        foreach (var line in lines)
        {
            var who = line.Author switch { Author.Person => session.Person, Author.DipTick => "DipTick", _ => line.Author };
            var state = line.Status is MessageStatus.Done or MessageStatus.Passed ? string.Empty : $" ({line.Status})";
            text.Append(CultureInfo.InvariantCulture, $"## {who}, {line.Started.ToLocalTime():yyyy-MM-dd HH:mm:ss}{state}\n\n{line.Text}\n\n");
        }

        return text.ToString();
    }

    internal static string TextOf(SessionInfo session, IReadOnlyList<StoredMessage> lines)
    {
        var text = new StringBuilder();
        foreach (var line in lines)
        {
            var who = line.Author switch { Author.Person => session.Person, Author.DipTick => "diptick", _ => line.Author };
            var state = line.Status is MessageStatus.Done or MessageStatus.Passed ? string.Empty : $" [{line.Status}]";
            text.Append(CultureInfo.InvariantCulture, $"[{line.Started.ToLocalTime():yyyy-MM-dd HH:mm:ss}] {who}:\n{line.Text}{state}\n\n");
        }

        return text.ToString();
    }

    internal static string JsonLinesOf(IReadOnlyList<StoredMessage> lines)
    {
        var text = new StringBuilder();
        foreach (var line in lines)
        {
            text.Append(JsonSerializer.Serialize(new { line.Seq, line.Author, line.Kind, line.Status, line.Text, started = line.Started, ended = line.Ended }, JsonLines)).Append('\n');
        }

        return text.ToString();
    }
}
