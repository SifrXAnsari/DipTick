using System.Globalization;
using DipTick.Models;
using DipTick.Models.Chat;
using DipTick.Models.Chat.Seam;
using DipTick.Models.Chat.Store;

namespace DipTick.Controllers;

/// <summary>
/// A chat's own table: who sits at it,
/// the mode it runs in, exactly what each agent is told, asking again, and what it has cost. Every
/// change is the person's; nothing an agent writes reaches these verbs.
/// </summary>
internal static partial class ChatController
{
    private const string SeatsUsage = "usage: diptick chat seats [--session <id|last>] [list] | add <agent> [--model <m>] [--as <name>] | remove <name> | model <name> <model>";

    /// <summary><c>chat seats</c>: the chat's table, and changes to it.</summary>
    private static int Seats(IChatStore store, Cli cli, VerbArgs words)
    {
        var session = words.Option("--session") ?? "last";
        var model = words.Option("--model");
        var name = words.Option("--as");
        var rest = words.Words();
        if (words.Problem is { } problem)
        {
            return cli.Fail($"{problem} {SeatsUsage}", Exit.Usage);
        }

        if (store.Find(session) is not { Open: true } found)
        {
            return cli.Fail(NoSuch(session));
        }

        var table = Table.From(store.Table(found.Id)).ToList();
        var last = store.Messages(found.Id).Select(m => m.Seq ?? 0).DefaultIfEmpty(0).Max();
        switch (rest)
        {
            case [] or ["list"]:
                return ListSeats(store, cli, found.Id, table);

            case ["add", var agentName]:
                var agent = Addressing.Lower(agentName);
                if (!Table.IsMain(agent) && cli.Config.Seats.All(s => s.Id != agent))
                {
                    return cli.Fail($"no agent '{SafeText.Line(agent)}' is set up. Settings, Models, Add a model sets one up; 'diptick seats' lists them.", Exit.Usage);
                }

                if (table.Count >= Table.Most)
                {
                    return cli.Fail(string.Create(CultureInfo.InvariantCulture, $"this chat's table is full: {Table.Most} agents at most."));
                }

                var seatName = name is null ? Table.NameFor(Table.IsMain(agent) ? Table.Main : agent, model ?? string.Empty, table) : Addressing.Lower(name);
                if (table.Any(s => s.Name == seatName) || Addressing.Reserved.Contains(seatName) || !System.Text.RegularExpressions.Regex.IsMatch(seatName, "^[a-z][a-z0-9-]{0,11}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                {
                    return cli.Fail($"'{SafeText.Line(seatName)}' cannot be this agent's name here: a name is 1 to 12 of a-z, 0-9 and -, starting with a letter, and not one already at the table.", Exit.Usage);
                }

                var sitting = new TableSeat(seatName, Table.IsMain(agent) ? Table.Main : agent, model ?? string.Empty);
                var record = Table.Record(cli.Config, sitting);

                // A new seat sends the chat somewhere new: said, and a yes asked, before it is.
                var where = record.Host.Length == 0 ? "no address yet" : record.Remote ? record.Host : $"{record.Host}, on this PC";
                if (cli.Flags.DryRun)
                {
                    cli.Note($"[dry run] would seat {seatName} ({record.Provider} {record.Model}) at {found.Id}; its answers would come from {where}.");
                    return Exit.Ok;
                }

                var yes = cli.Confirm($"Seat {seatName} ({record.Provider} {record.Model}) at this chat? It is sent the chat from now on, at {where}.");
                if (yes != Answer.Yes)
                {
                    return cli.NotConfirmed(yes, $"seating {seatName}");
                }

                store.RecordSeat(found.Id, last, record);
                store.Notice(found.Id, 0, $"{seatName} sat down at the table ({record.Provider} {record.Model}).");
                cli.WriteJson(new { session = found.Id, seat = seatName, agent = sitting.Profile, record.Provider, record.Model, record.Host });
                if (!cli.Json)
                {
                    cli.Note($"{seatName} sits at {found.Id}: {record.Provider} {record.Model}.");
                }

                return Exit.Ok;

            case ["remove", var leaving]:
                if (table.FirstOrDefault(s => s.Name == Addressing.Lower(leaving)) is not { } gone)
                {
                    return cli.Fail($"'{SafeText.Line(leaving)}' is not at this chat's table.", Exit.Usage);
                }

                if (cli.Flags.DryRun)
                {
                    cli.Note($"[dry run] would take {gone.Name} from {found.Id}'s table.");
                    return Exit.Ok;
                }

                store.RecordSeat(found.Id, last, Table.Record(cli.Config, gone, seated: false));
                store.Notice(found.Id, 0, $"{gone.Name} left the table.");
                cli.WriteJson(new { session = found.Id, removed = gone.Name });
                if (!cli.Json)
                {
                    cli.Note($"{gone.Name} left {found.Id}'s table. Its answers stay in the chat.");
                }

                return Exit.Ok;

            case ["model", var who, var newModel]:
                if (table.FirstOrDefault(s => s.Name == Addressing.Lower(who)) is not { } changing)
                {
                    return cli.Fail($"'{SafeText.Line(who)}' is not at this chat's table.", Exit.Usage);
                }

                if (!ModelLister.UsableId(newModel))
                {
                    return cli.Fail("a model's name has no spaces and only plain characters.", Exit.Usage);
                }

                if (cli.Flags.DryRun)
                {
                    cli.Note($"[dry run] would have {changing.Name} answer with {newModel} in {found.Id}.");
                    return Exit.Ok;
                }

                store.RecordSeat(found.Id, last, Table.Record(cli.Config, changing with { Model = newModel }));
                store.Notice(found.Id, 0, $"{changing.Name} now answers with {newModel} (was {changing.Model}).");
                cli.WriteJson(new { session = found.Id, seat = changing.Name, model = newModel, was = changing.Model });
                if (!cli.Json)
                {
                    cli.Note($"{changing.Name} answers with {newModel} in {found.Id}.");
                }

                return Exit.Ok;

            default:
                return cli.Fail(SeatsUsage, Exit.Usage);
        }
    }

    private static int ListSeats(IChatStore store, Cli cli, string session, List<TableSeat> table)
    {
        var rows = table.Select(s => (Seat: s, Agent: Table.Resolve(cli.Config, s), Record: store.Table(session).First(r => r.Seat == s.Name))).ToList();
        if (cli.WriteJson(new
        {
            session,
            mode = store.Find(session)?.Mode,
            seats = rows.Select(r => new
            {
                name = r.Seat.Name,
                agent = r.Seat.Profile,
                provider = r.Record.Provider,
                model = r.Record.Model,
                host = r.Record.Host,
                remote = r.Record.Remote,
                priceIn = r.Agent?.PriceIn,
                priceOut = r.Agent?.PriceOut,
                setUp = r.Agent is { Filled: true },
            }),
        }))
        {
            return Exit.Ok;
        }

        if (rows.Count == 0)
        {
            cli.Data("Nobody sits at this chat's table. 'diptick chat seats add <agent>' seats one.");
            return Exit.Ok;
        }

        foreach (var (seat, agent, record) in rows)
        {
            var where = record.Host.Length == 0 ? "no address" : record.Remote ? "↗ " + record.Host : record.Host + " (this PC)";
            var prices = agent is { PriceIn: { } pin, PriceOut: { } pout } ? string.Create(CultureInfo.InvariantCulture, $"{pin}/{pout} {cli.Config.Spending.Currency} a million tokens") : "no prices";
            cli.Data($"{seat.Name,-12}  {SafeText.Line(record.Provider)} {SafeText.Line(record.Model)}  {where}  {prices}{(agent is { Filled: true } ? string.Empty : "  (not set up)")}");
        }

        cli.Data($"mode: {store.Find(session)?.Mode}");
        return Exit.Ok;
    }

    /// <summary><c>chat mode</c>: shows the chat's mode, or sets it, checked against its table.</summary>
    private static int Mode(IChatStore store, Cli cli, VerbArgs words)
    {
        var session = words.Option("--session") ?? "last";
        var rest = words.Words();
        if (words.Problem is { } problem)
        {
            return cli.Fail($"{problem} usage: diptick chat mode [--session <id|last>] [<mode…>]", Exit.Usage);
        }

        if (store.Find(session) is not { Open: true } found)
        {
            return cli.Fail(NoSuch(session));
        }

        if (rest.Count == 0)
        {
            var now = ChatMode.Parse(found.Mode, out _) ?? ChatMode.All;
            if (!cli.WriteJson(new { session = found.Id, mode = now.ToString(), words = now.Words }))
            {
                cli.Data($"{now} ({now.Words})");
            }

            return Exit.Ok;
        }

        if (ChatMode.Parse(string.Join(' ', rest), out var why) is not { } mode)
        {
            return cli.Fail($"the mode {why}.", Exit.Usage);
        }

        if (mode.Check([.. Table.From(store.Table(found.Id)).Select(s => s.Name)]) is { } missing)
        {
            return cli.Fail($"the mode {missing}.", Exit.Usage);
        }

        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would set {found.Id}'s mode to {mode}.");
            return Exit.Ok;
        }

        store.SetMode(found.Id, mode.ToString());
        store.Notice(found.Id, 0, $"The mode is now: {mode.Words}.");
        if (!cli.WriteJson(new { session = found.Id, mode = mode.ToString(), words = mode.Words }))
        {
            cli.Note($"{found.Id}: {mode.Words}.");
        }

        return Exit.Ok;
    }

    /// <summary>
    /// <c>chat brief</c>: exactly what each agent at the table is told for a plain round now, and its
    /// size; <c>--set</c> changes the chat's own brief. It calls nothing.
    /// </summary>
    private static int Brief(IChatStore store, Cli cli, VerbArgs words)
    {
        var session = words.Option("--session") ?? "last";
        var only = words.Option("--seat");
        var set = words.Option("--set");
        var problem = words.Problem;
        if (problem is not null || words.Words().Count > 0)
        {
            return cli.Fail($"{problem ?? string.Empty} usage: diptick chat brief [--session <id|last>] [--seat <name>] [--set <text>]".Trim(), Exit.Usage);
        }

        // A chat nobody has spoken in yet: what its agents would be told, from the table it would start with.
        if (string.Equals(session, "new", StringComparison.OrdinalIgnoreCase))
        {
            if (set is not null)
            {
                return cli.Fail("a chat not yet begun has no brief to set: say something first, then set it.", Exit.Usage);
            }

            return ShowTold(cli, null, string.Empty, Told(cli.Config, Table.Starting(cli.Config), Starting(cli.Config), string.Empty), only);
        }

        if (store.Find(session) is not { Open: true } found)
        {
            return cli.Fail(NoSuch(session));
        }

        if (set is not null)
        {
            if (set.Length > 2000)
            {
                return cli.Fail("a chat's brief is at most 2000 characters.", Exit.Usage);
            }

            if (cli.Flags.DryRun)
            {
                cli.Note($"[dry run] would set {found.Id}'s brief.");
                return Exit.Ok;
            }

            store.SetBrief(found.Id, set.Trim());
            store.Notice(found.Id, 0, set.Trim().Length == 0 ? "This chat's brief was cleared." : "This chat's brief changed; every agent is told it from the next round.");
        }

        var brief = store.Brief(found.Id);
        return ShowTold(cli, found.Id, brief, Told(cli.Config, Table.From(store.Table(found.Id)), found.Mode, brief, store.ChatRules(found.Id), store.ChatSkills(found.Id)), only);
    }

    /// <summary>What each agent is told, or the one <paramref name="only"/> names: JSON, or each under its name with its size.</summary>
    private static int ShowTold(Cli cli, string? session, string brief, List<(string Seat, string Text)> told, string? only)
    {
        if (only is not null && told.All(t => t.Seat != Addressing.Lower(only)))
        {
            return cli.Fail($"'{SafeText.Line(only)}' is not at this chat's table.", Exit.Usage);
        }

        var shown = only is null ? told : [.. told.Where(t => t.Seat == Addressing.Lower(only))];
        if (cli.WriteJson(new { session, brief, agents = shown.Select(t => new { seat = t.Seat, system = t.Text, tokens = (t.Text.Length + 3) / 4 }) }))
        {
            return Exit.Ok;
        }

        if (shown.Count == 0)
        {
            cli.Data("Nobody sits at this chat's table, so nobody is told anything.");
        }

        foreach (var (seat, text) in shown)
        {
            cli.Data(string.Create(CultureInfo.InvariantCulture, $"── {seat} is told (about {(text.Length + 3) / 4} tokens) ──"));
            cli.Data(SafeText.Block(text));
            cli.Data(string.Empty);
        }

        return Exit.Ok;
    }

    /// <summary>
    /// What each agent at a table is told for the next plain round, in the chat's mode, as it would be
    /// sent: today's date, and the tool and research lines as its turn would give them under
    /// <paramref name="rules"/>, the chat's own (none for a chat not started).
    /// </summary>
    internal static List<(string Seat, string Text)> Told(MasterConfig config, IReadOnlyList<TableSeat> table, string modeText, string chatBrief, IReadOnlyList<Models.Harness.ChatRule>? rules = null, IReadOnlyList<ChatSkill>? skills = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(table);
        var names = table.Select(s => s.Name).ToList();
        var mode = ChatMode.Parse(modeText, out _) is { } m && m.Check(names) is null ? m : ChatMode.All;
        var plan = Planner.For(mode, null, names, config.Chat.Round.MaxAiTurns);
        var today = DateTime.Now.ToString("dddd, yyyy-MM-dd", CultureInfo.InvariantCulture);
        var told = new List<(string, string)>();
        foreach (var seat in table)
        {
            var agent = Table.Resolve(config, seat);
            var step = plan.Steps.FirstOrDefault(s => s.Seats.Contains(seat.Name)) ?? new PlanStep([seat.Name], StepRole.Answer);
            var peers = table.Where(s => s.Name != seat.Name).Select(s => Table.Resolve(config, s) is { } a ? new Peer(s.Name, Engines.DisplayName(a.Provider, a.Endpoint), a.Model) : new Peer(s.Name, "not set up", s.Model)).ToList();
            var (tools, research, byProvider) = agent is { Filled: true } ? Models.Tools.AgentTools.Preview(config, agent, rules ?? []) : (false, false, false);
            var usable = Models.Skills.SkillChat.Usable(config, skills ?? []);
            var facts = new BriefingFacts(seat.Name, RoundRunner.Person(config), peers, step, plan, agent?.MaxAnswerTokens ?? 1500, config.UserBrief, agent?.Brief ?? string.Empty, chatBrief)
            {
                Today = today,
                Tools = tools,
                Research = research,
                ResearchByProvider = byProvider,
                Skills = tools ? [.. usable.Select(s => (s.Name, s.Description))] : [],
                SkillTexts = tools ? [] : [.. usable.Select(Models.Skills.SkillChat.Wrapped)],
            };
            told.Add((seat.Name, Briefing.For(facts)));
        }

        return told;
    }

    /// <summary>
    /// <c>chat retry</c>: the question behind an answer asked again (the last answer's, or <c>--seq</c>'s),
    /// of the same agent or <c>--to</c> another. Its own round; the earlier answer stays.
    /// </summary>
    private static int Retry(IChatStore store, Cli cli, VerbArgs words, bool isPrivate)
    {
        var session = words.Option("--session") ?? "last";
        var seqText = words.Option("--seq");
        var to = words.Option("--to");
        var events = words.Switch("--events");
        var problem = words.Problem;
        if (problem is not null || words.Words().Count > 0)
        {
            return cli.Fail($"{problem ?? string.Empty} usage: diptick chat retry [--session <id|last>] [--seq <n>] [--to <name>] [--events]".Trim(), Exit.Usage);
        }

        if (store.Find(session) is not { Open: true } found)
        {
            return cli.Fail(NoSuch(session));
        }

        var lines = store.Messages(found.Id);
        var answer = seqText is null
            ? lines.LastOrDefault(l => l.Kind == MessageKind.Answer && l.Seq is not null)
            : int.TryParse(seqText, NumberStyles.None, CultureInfo.InvariantCulture, out var seq) ? lines.FirstOrDefault(l => l.Seq == seq && l.Kind == MessageKind.Answer) : null;
        if (answer is null)
        {
            return cli.Fail(seqText is null ? "this chat has no answer to ask again." : $"#{SafeText.Line(seqText)} is not an answer in this chat.", Exit.Usage);
        }

        var asked = lines.LastOrDefault(l => l.Round == answer.Round && l.Author == Author.Person && l.Kind == MessageKind.Say);
        if (asked is null)
        {
            return cli.Fail($"#{answer.Seq} answered no words of yours to ask again.", Exit.Usage);
        }

        var names = Table.From(store.Table(found.Id)).Select(s => s.Name).ToList();
        var whom = to is null ? answer.Author : Addressing.Lower(to);
        if (!names.Contains(whom))
        {
            return cli.Fail($"{whom} is not at this chat's table ({string.Join(", ", names)}).", Exit.Usage);
        }

        return Held(store, cli, new TurnPlan(found.Id, false, found.Title, asked.Text, events, cli.Flags.DryRun, isPrivate, new Address(AddressKind.Seats, [whom], asked.Text), null));
    }

    /// <summary>
    /// <c>chat cost</c>: what a chat's answers used and cost, each agent's and the whole, or with
    /// <c>--today</c> every chat's today. Cost only where an agent's prices are set; tokens always,
    /// marked where estimated.
    /// </summary>
    private static int Cost(IChatStore store, Cli cli, VerbArgs words)
    {
        var session = words.Option("--session") ?? "last";
        var today = words.Switch("--today");
        var problem = words.Problem;
        if (problem is not null || words.Words().Count > 0)
        {
            return cli.Fail($"{problem ?? string.Empty} usage: diptick chat cost [--session <id|last>] [--today]".Trim(), Exit.Usage);
        }

        var currency = cli.Config.Spending.Currency;
        if (today)
        {
            var now = DateTimeOffset.Now;
            var spent = store.Spent(null, new DateTimeOffset(now.Date, now.Offset));
            if (!cli.WriteJson(new { today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), cost = spent / 1_000_000m, currency }))
            {
                cli.Data(string.Create(CultureInfo.InvariantCulture, $"today: {spent / 1_000_000m:0.####} {currency}, over every chat with prices set"));
            }

            return Exit.Ok;
        }

        if (store.Find(session) is not { } found)
        {
            return cli.Fail(NoSuch(session));
        }

        var answers = store.Messages(found.Id).Where(l => l.Kind == MessageKind.Answer && l.Seq is not null).ToList();
        var bySeat = answers.GroupBy(a => a.Author).Select(g => new
        {
            seat = g.Key,
            answers = g.Count(),
            tokensIn = g.Sum(a => a.TokensIn ?? 0),
            tokensOut = g.Sum(a => a.TokensOut ?? 0),
            estimated = g.Any(a => a.TokensEstimated),
            cost = g.Any(a => a.CostMicros is not null) ? g.Sum(a => a.CostMicros ?? 0) / 1_000_000m : (decimal?)null,
        }).ToList();
        var total = answers.Any(a => a.CostMicros is not null) ? answers.Sum(a => a.CostMicros ?? 0) / 1_000_000m : (decimal?)null;
        if (cli.WriteJson(new { session = found.Id, currency, seats = bySeat, cost = total }))
        {
            return Exit.Ok;
        }

        foreach (var row in bySeat)
        {
            var tokens = string.Create(CultureInfo.InvariantCulture, $"{(row.estimated ? "~" : string.Empty)}{row.tokensIn:N0} in, {row.tokensOut:N0} out");
            var cost = row.cost is { } c ? string.Create(CultureInfo.InvariantCulture, $"{c:0.####} {currency}") : "no prices";
            cli.Data(string.Create(CultureInfo.InvariantCulture, $"{row.seat,-12}  {row.answers} answers  {tokens}  {cost}"));
        }

        cli.Data(total is { } all ? string.Create(CultureInfo.InvariantCulture, $"this chat: {all:0.####} {currency}") : "this chat: no prices set, so no cost to show");
        return Exit.Ok;
    }
}
