using System.Globalization;
using System.Text;
using System.Text.Json;
using DipTick.Models;
using DipTick.Models.Chat;
using DipTick.Models.Chat.Seam;
using DipTick.Models.Chat.Store;
using SeamMode = DipTick.Models.Chat.Seam.ChatMode;

namespace DipTick.ViewModels;

/// <summary>
/// One agent at the chat's table, as its chip shows it: its name in this chat, the set-up agent it is,
/// where it answers from, its model and colour, and whether it is still set up in Settings.
/// </summary>
internal sealed record TableChip(string Name, string Agent, string Provider, string Model, string Color, bool SetUp);

/// <summary>What one agent at the table is told, exactly as it is sent, and about how many tokens that is.</summary>
internal sealed record ToldText(string Seat, string Text, int Tokens);

/// <summary>
/// Agents at one table: the chat's table and mode, the plan line, rounds drawn answer by answer under
/// each agent's name, asking again, and what a chat has cost. Every change is a <c>diptick chat</c>
/// verb, run in this process; nothing here does more than the command line can.
/// </summary>
internal sealed partial class ChatViewModel
{
    /// <summary>The chat on screen's table, as last read; null until it is read again.</summary>
    private IReadOnlyList<TableChip>? _table;

    /// <summary>The chat on screen's mode, in its own grammar; empty until it is read again.</summary>
    private string _answerMode = string.Empty;

    /// <summary>What the chat on screen and today have cost, as last read; null where no price is set.</summary>
    private (decimal? Chat, decimal? Today, string Currency)? _costs;

    /// <summary>
    /// The agents at the chat on screen: a kept chat's own table (<c>chat seats</c>); a chat nobody has
    /// spoken in yet, the table it would start with (the model that answers you, then <c>chat.seats</c>).
    /// </summary>
    public IReadOnlyList<TableChip> Table
    {
        get
        {
            lock (_lock)
            {
                if (_table is { } known)
                {
                    return known;
                }
            }

            var (table, mode) = ReadTable();
            lock (_lock)
            {
                _table = table;
                _answerMode = mode;
            }

            return table;
        }
    }

    /// <summary>Who answers a message with no address, in <c>chat mode</c>'s grammar (<c>all</c>, <c>relay dip tick</c>…).</summary>
    public string AnswerMode
    {
        get
        {
            _ = Table;
            lock (_lock)
            {
                return _answerMode.Length == 0 ? SeamMode.All.ToString() : _answerMode;
            }
        }
    }

    /// <summary>The answer chip's words: "All answer", "dip, then tick", "Debate, 6 turns".</summary>
    public string AnswerModeWords => (SeamMode.Parse(AnswerMode, out _) ?? SeamMode.All).Words;

    /// <summary>
    /// The modes the answer chip offers for this table, each with its words: all, each agent alone,
    /// pick, and with two or more, a chain, a review and a debate in the table's order and, with two,
    /// the other way round too.
    /// </summary>
    public IReadOnlyList<(string Mode, string Words)> ModeChoices
    {
        get
        {
            var names = Table.Select(s => s.Name).ToList();
            var modes = new List<string> { "all" };
            modes.AddRange(names.Select(n => $"solo {n}"));
            modes.Add("pick");
            if (names.Count >= 2)
            {
                var orders = names.Count == 2 ? new[] { names, [names[1], names[0]] } : [names];
                foreach (var order in orders)
                {
                    modes.Add($"relay {string.Join(' ', order)}");
                }

                foreach (var order in orders)
                {
                    modes.Add($"review {order[0]} {order[1]}");
                }

                modes.Add($"stack {string.Join(' ', names)}");
            }

            return [.. modes.Select(m => SeamMode.Parse(m, out _)).OfType<SeamMode>().Select(m => (m.ToString(), m.Words))];
        }
    }

    /// <summary>The agents set up in Settings that can sit at a table: the model that answers you, then every seat section.</summary>
    public IReadOnlyList<TableChip> SetUpAgents
    {
        get
        {
            var agents = new List<TableChip>();
            if (Config.MainProvider.Length > 0)
            {
                agents.Add(Chip(new TableSeat(Models.Chat.Seam.Table.Main, Models.Chat.Seam.Table.Main, Config.MainModel)));
            }

            agents.AddRange(Config.Seats.Select(s => Chip(new TableSeat(s.Id, s.Id, s.Model))));
            return agents;
        }
    }

    /// <summary>What this chat has cost so far, where a price is known, for the chat's header: "0.42 USD this chat"; null with no price.</summary>
    public string? ChatCost => Costs() is { Chat: { } chat } costs && Config.Spending.ShowCost ? $"{Money(chat)} {costs.Currency} this chat" : null;

    /// <summary>What today has cost over every chat, for the status bar; null with no price.</summary>
    public string? TodayCost => Costs() is { Today: { } today } costs && Config.Spending.ShowCost ? $"today {Money(today)} {costs.Currency}" : null;

    /// <summary>
    /// The plan line: who will answer what is typed, in what order, before it
    /// is sent; or why it will not go. Null when nothing is typed, or it is a command.
    /// </summary>
    public (string Text, bool Problem)? PlanFor(string draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var text = draft.TrimStart();
        if (text.Length == 0 || (text.StartsWith('/') && !text.Contains('\n', StringComparison.Ordinal)) || IsExitWord(text))
        {
            return null;
        }

        var names = Table.Select(s => s.Name).ToList();
        if (names.Count == 0)
        {
            return (SetUpAgents.Count == 0 ? "No model is set up yet, so nobody can answer." : "Nobody sits at this chat's table. + Add agent seats one.", true);
        }

        if (Addressing.Parse(text, names, out var problem) is not { } address)
        {
            return ($"{problem}. Nothing will be sent.", true);
        }

        var mode = SeamMode.Parse(AnswerMode, out _) is { } m && m.Check(names) is null ? m : SeamMode.All;
        var plan = Planner.For(mode, address.Kind == AddressKind.None ? null : address, names, Config.Chat.Round.MaxAiTurns);
        return plan.Steps.Count == 0 ? ($"{plan.Line}. Nothing will be sent.", true) : ("→ " + plan.Line, false);
    }

    /// <summary>
    /// Seats a set-up agent at this chat (<c>chat seats add</c>), with a model of its own when it already
    /// sits here; the verb asks the person's yes, since the chat goes somewhere new. A chat nobody has
    /// spoken in is kept from here on, so its table can be.
    /// </summary>
    public Task AddSeatAsync(string agent, string? model = null)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (EnsureKept() is not { } id)
        {
            return Task.CompletedTask;
        }

        var args = new List<string> { "chat", "seats" };
        if (_private)
        {
            args.Add("--private");
        }

        args.AddRange([$"--session={id}", "add", agent]);
        if (model is { Length: > 0 })
        {
            args.AddRange(["--model", model]);
        }

        return TableVerbAsync([.. args]);
    }

    /// <summary>
    /// A set-up agent sits here again with another model, which the person picks from its provider's
    /// list (free) or types; seated under that model's name. Runs on a worker thread: it asks.
    /// </summary>
    public async Task AddSeatWithModelAsync(string agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var current = Table.FirstOrDefault(s => s.Agent == agent)?.Model ?? string.Empty;
        if (AskModel(agent, current, $"{agent} again, with another model", "Which model should it answer with here?") is { } model)
        {
            await AddSeatAsync(agent, model).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Another model for an agent in this chat only (Settings keep its own), picked from its provider's
    /// list (free) or typed. Runs on a worker thread: it asks.
    /// </summary>
    public async Task PickSeatModelAsync(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (Table.FirstOrDefault(s => s.Name == name) is not { } chip)
        {
            return;
        }

        if (AskModel(chip.Agent, chip.Model, $"{name}'s model in this chat", "Which model should it answer with here? Only this chat changes.") is { } model && model != chip.Model)
        {
            await SetSeatModelAsync(name, model).ConfigureAwait(false);
        }
    }

    /// <summary>A model for a set-up agent: from its provider's list when there is one, else typed; null when the person left.</summary>
    private string? AskModel(string agent, string current, string title, string question)
    {
        if (Asker is not { } asker)
        {
            return null;
        }

        string? Typed(string why)
        {
            var typed = asker.Text(new Question(title, "The model's name, exactly as the provider writes it.", why), current, t => ModelLister.UsableId(t.Trim()) ? null : "a model's name has no spaces and only plain characters");
            return typed.Reply == Reply.Answered ? typed.Value.Trim() : null;
        }

        if (ModelLister.QueryFor(agent, Config, out var seat, out var none) is not { } query)
        {
            return Typed($"No list to pick from: {none}");
        }

        var gate = new Models.Harness.Gate(Config.Permissions, Flags, Models.Harness.AuditLog.ForThisUser(), Who, TextWriter.Null, AskPerson, seat);
        IReadOnlyList<ModelInfo> models;
        using (var timeout = Flags.NetTimeoutSeconds == 0 ? new CancellationTokenSource() : new CancellationTokenSource(TimeSpan.FromSeconds(Flags.NetTimeoutSeconds)))
        {
            try
            {
                models = asker.Busy($"Asking {Engines.DisplayName(seat?.Provider ?? Config.MainProvider, seat?.Endpoint ?? Config.MainEndpoint)} for its models…", () => ModelLister.Instance.ListAsync(query, gate, timeout.Token));
            }
            catch (Exception ex) when (ex is ChatException or OperationCanceledException)
            {
                return Typed($"The list could not be had: {SafeText.Line(ex.Message)}");
            }
        }

        var picked = asker.PickModel(new Question(title, question, $"{models.Count.ToString(CultureInfo.InvariantCulture)} offered."), models, current);
        return picked.Reply == Reply.Answered ? picked.Value : null;
    }

    /// <summary>Takes an agent from this chat's table (<c>chat seats remove</c>); its answers stay.</summary>
    public Task RemoveSeatAsync(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return EnsureKept() is { } id ? TableVerbAsync(Private("chat", "seats", $"--session={id}", "remove", name)) : Task.CompletedTask;
    }

    /// <summary>Has an agent answer with another model in this chat only (<c>chat seats model</c>).</summary>
    public Task SetSeatModelAsync(string name, string model)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(model);
        return EnsureKept() is { } id ? TableVerbAsync(Private("chat", "seats", $"--session={id}", "model", name, model)) : Task.CompletedTask;
    }

    /// <summary>Who answers a message with no address in this chat (<c>chat mode</c>).</summary>
    public Task SetAnswerModeAsync(string mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        if (EnsureKept() is not { } id)
        {
            return Task.CompletedTask;
        }

        var args = new List<string> { "chat", "mode" };
        if (_private)
        {
            args.Add("--private");
        }

        args.Add($"--session={id}");
        args.AddRange(mode.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return TableVerbAsync([.. args]);
    }

    /// <summary>A mode typed in DipTick's words, checked against this table as it is typed. Runs on a worker thread: it asks.</summary>
    public async Task AskAnswerModeAsync()
    {
        if (Asker is not { } asker)
        {
            return;
        }

        var names = Table.Select(s => s.Name).ToList();
        var typed = asker.Text(
            new Question("Who answers", "The mode, in DipTick's words.", "all · solo <name> · pick · relay <a> <b> · review <builder> <reviewer> [revise] · stack <a> <b> [turns]"),
            AnswerMode,
            t => SeamMode.Parse(t, out var why) is { } mode ? mode.Check(names) : $"the mode {why}");
        if (typed.Reply == Reply.Answered)
        {
            await SetAnswerModeAsync(typed.Value.Trim()).ConfigureAwait(false);
        }
    }

    /// <summary>This chat's own brief, told to every agent at its table from the next round (<c>chat brief --set</c>).</summary>
    public Task SetChatBriefAsync(string brief)
    {
        ArgumentNullException.ThrowIfNull(brief);
        return EnsureKept() is { } id ? TableVerbAsync(Private("chat", "brief", $"--session={id}", "--set", brief)) : Task.CompletedTask;
    }

    /// <summary>
    /// "What they're told": for each agent at the table, exactly the text it is sent
    /// before the chat, and this chat's own brief; <c>chat brief</c>, which calls nothing.
    /// </summary>
    public (IReadOnlyList<ToldText> Agents, string Brief, string? Problem) Told()
    {
        var (json, error, code) = Json(Private("chat", "brief", $"--session={_sessionId ?? "new"}"));
        if (code != Controllers.Exit.Ok)
        {
            return ([], string.Empty, Plain(error));
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var agents = root.GetProperty("agents").EnumerateArray()
                .Select(a => new ToldText(a.GetProperty("seat").GetString() ?? string.Empty, a.GetProperty("system").GetString() ?? string.Empty, a.GetProperty("tokens").GetInt32()))
                .ToList();
            return (agents, root.GetProperty("brief").GetString() ?? string.Empty, null);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return ([], string.Empty, $"'diptick chat brief' printed something this screen could not read: {ex.Message}");
        }
    }

    /// <summary>
    /// Asks the question behind an answer again (<c>chat retry</c>): of the agent that gave it, or of
    /// <paramref name="to"/>, another at the table. Its own round, drawn as it comes; the earlier answer stays.
    /// </summary>
    public async Task RetryAsync(int? seq = null, string? to = null)
    {
        if (Busy)
        {
            await Say(Role.System, "Still answering. Esc or /stop first.").ConfigureAwait(false);
            return;
        }

        if (_sessionId is not { } id)
        {
            await Say(Role.System, "There is no answer in this chat to ask again yet.").ConfigureAwait(false);
            return;
        }

        var args = new List<string>(Private("chat", "retry", $"--session={id}"));
        if (seq is { } n)
        {
            args.AddRange(["--seq", n.ToString(CultureInfo.InvariantCulture)]);
        }

        if (to is { Length: > 0 })
        {
            args.AddRange(["--to", Addressing.Lower(to.TrimStart('@'))]);
        }

        args.Add("--events");

        // The question goes again as the person's own line, as the chat keeps it.
        var transcript = Transcript;
        var answer = seq is { } wanted ? transcript.LastOrDefault(l => l.Role == Role.Agent && l.Seq == wanted) : transcript.LastOrDefault(l => l.Role == Role.Agent && l.Seq is not null);
        var asked = answer is null ? null : transcript.LastOrDefault(l => l.Role == Role.Person && l.Round == answer.Round && l.Round > 0)?.Text;
        await Say(Role.Person, asked ?? "(the same question, asked again)").ConfigureAwait(false);
        await RoundAsync([.. args]).ConfigureAwait(false);
    }

    /// <summary>
    /// F4 in the terminal screen: the table as one list of what can be done with it, asked in the
    /// prompt zone: another model for an agent here, taking one away, adding one, who answers, and
    /// what they are told. The window has its own menu for the same (F4 there). Runs on a worker thread.
    /// </summary>
    public async Task TableMenuAsync()
    {
        if (Asker is not { } asker)
        {
            await Say(Role.System, TableText()).ConfigureAwait(false);
            return;
        }

        var table = Table;
        var actions = new List<(Choice Choice, Func<Task> Run)>();
        foreach (var chip in table)
        {
            var name = chip.Name;
            actions.Add((new Choice($"{name}: another model here", $"now {chip.Model}; only this chat changes"), () => PickSeatModelAsync(name)));
            actions.Add((new Choice($"{name}: take it from this chat", "its answers stay"), () => RemoveSeatAsync(name)));
        }

        foreach (var agent in SetUpAgents)
        {
            var id = agent.Agent;
            actions.Add(table.Any(s => s.Agent == id)
                ? (new Choice($"Add {id} again", "with another model, under that model's name"), () => AddSeatWithModelAsync(id))
                : (new Choice($"Add {id}", $"{agent.Provider} {agent.Model}"), () => AddSeatAsync(id)));
        }

        actions.Add((new Choice($"Who answers: {AnswerModeWords}", "when you name nobody"), ChooseModeAsync));
        actions.Add((new Choice("What they're told", "exactly what each agent is sent"), () => BriefAsync(string.Empty)));
        var summary = table.Count == 0 ? "Nobody sits at this chat's table." : $"At this table: {string.Join(", ", table.Select(s => $"{s.Name} ({s.Model})"))}.";
        var picked = asker.Choose(new Question("The table", summary, "Esc leaves it as it is."), [.. actions.Select(a => a.Choice)], 0);
        if (picked.Reply == Reply.Answered)
        {
            await actions[picked.Value].Run().ConfigureAwait(false);
        }
    }

    /// <summary>Who answers when nobody is named, picked from the modes this table can run, or typed.</summary>
    private async Task ChooseModeAsync()
    {
        if (Asker is not { } asker)
        {
            return;
        }

        var modes = ModeChoices;
        var current = modes.ToList().FindIndex(m => m.Mode == AnswerMode);
        var picked = asker.Choose(
            new Question("Who answers", "When you name nobody, who answers?", "@name always asks that agent only."),
            [.. modes.Select(m => new Choice(m.Words, m.Mode)), new Choice("Another mode…", "typed in DipTick's words")],
            current < 0 ? 0 : current);
        if (picked.Reply != Reply.Answered)
        {
            return;
        }

        if (picked.Value < modes.Count)
        {
            await SetAnswerModeAsync(modes[picked.Value].Mode).ConfigureAwait(false);
        }
        else
        {
            await AskAnswerModeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>/seats: the table, or a change to it.</summary>
    private async Task SeatsAsync(string arg)
    {
        switch (arg.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            case []:
                await Say(Role.System, TableText()).ConfigureAwait(false);
                return;
            case ["add", var agent]:
                await AddSeatAsync(Addressing.Lower(agent)).ConfigureAwait(false);
                return;
            case ["add", var agent, var model]:
                await AddSeatAsync(Addressing.Lower(agent), model).ConfigureAwait(false);
                return;
            case ["remove", var name]:
                await RemoveSeatAsync(Addressing.Lower(name)).ConfigureAwait(false);
                return;
            case ["model", var name, var model]:
                await SetSeatModelAsync(Addressing.Lower(name), model).ConfigureAwait(false);
                return;
            default:
                await Say(Role.System, "/seats shows this chat's table; /seats add <agent> [<model>], /seats remove <name> and /seats model <name> <model> change it.").ConfigureAwait(false);
                return;
        }
    }

    /// <summary>The table in words: each agent, its model and where it answers from; the mode; who else is set up.</summary>
    public string TableText()
    {
        var table = Table;
        var text = new StringBuilder();
        if (table.Count == 0)
        {
            text.Append("Nobody sits at this chat's table.");
        }
        else
        {
            text.Append(table.Count == 1 ? "At this chat's table:" : $"At this chat's table, {table.Count.ToString(CultureInfo.InvariantCulture)} agents:");
            foreach (var chip in table)
            {
                text.Append(CultureInfo.InvariantCulture, $"\n  {chip.Name}  {chip.Provider} {chip.Model}{(chip.SetUp ? string.Empty : "  (not set up any more)")}");
            }
        }

        text.Append(CultureInfo.InvariantCulture, $"\nWho answers when you name nobody: {AnswerModeWords}.");
        var free = SetUpAgents.Where(a => table.All(s => s.Agent != a.Agent)).Select(a => a.Agent).ToList();
        if (free.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"\nSet up and free to sit here: {string.Join(", ", free)} (/seats add <agent>).");
        }

        return text.ToString();
    }

    /// <summary>/mode: who answers when nobody is named, or a change to it.</summary>
    private async Task ModeAsync(string arg)
    {
        if (arg.Trim().Length == 0)
        {
            var choices = string.Join("; ", ModeChoices.Select(c => $"{c.Mode} ({c.Words})"));
            await Say(Role.System, $"Who answers when you name nobody: {AnswerModeWords} ({AnswerMode}). /mode <mode> changes it: {choices}.").ConfigureAwait(false);
            return;
        }

        await SetAnswerModeAsync(arg.Trim()).ConfigureAwait(false);
    }

    /// <summary>/retry: the last question again, of the same agent, or of the one named.</summary>
    private Task RetryCommandAsync(string arg) => RetryAsync(null, arg.Trim().Length == 0 ? null : arg.Trim());

    /// <summary>/brief: what each agent is told, exactly; with words, this chat's own brief.</summary>
    private async Task BriefAsync(string arg)
    {
        if (arg.Trim().Length > 0)
        {
            await SetChatBriefAsync(arg.Trim()).ConfigureAwait(false);
            return;
        }

        var (agents, brief, problem) = Told();
        if (problem is not null)
        {
            await Say(Role.System, problem).ConfigureAwait(false);
            return;
        }

        var text = new StringBuilder(brief.Length == 0 ? "This chat has no brief of its own (/brief <words> gives it one)." : $"This chat's brief: {brief}");
        foreach (var agent in agents)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n\n── {agent.Seat} is told (about {agent.Tokens:N0} tokens) ──\n{agent.Text}");
        }

        await Say(Role.System, text.ToString()).ConfigureAwait(false);
    }

    /// <summary>/cost: what this chat has cost, agent by agent; /cost today, every chat's today.</summary>
    private async Task CostAsync(string arg)
    {
        if (arg.Trim().Equals("today", StringComparison.OrdinalIgnoreCase))
        {
            await Verb("chat", "cost", "--today").ConfigureAwait(false);
            return;
        }

        if (_sessionId is not { } id)
        {
            await Say(Role.System, "Nothing has been said in this chat yet, so it has cost nothing.").ConfigureAwait(false);
            return;
        }

        await Verb(Private("chat", "cost", $"--session={id}")).ConfigureAwait(false);
    }

    /// <summary>
    /// Lets the agents go on with no new words (<c>chat pass</c>): how a debate
    /// goes on past its turns; still the person's act.
    /// </summary>
    public async Task PassAsync()
    {
        if (Busy)
        {
            await Say(Role.System, "Still answering. Esc or /stop first.").ConfigureAwait(false);
            return;
        }

        if (_sessionId is not { } id)
        {
            await Say(Role.System, "Nothing has been said in this chat yet, so there is no floor to pass back.").ConfigureAwait(false);
            return;
        }

        await Say(Role.Person, "(passed the floor back to the agents)").ConfigureAwait(false);
        await RoundAsync(Private("chat", "pass", $"--session={id}", "--events")).ConfigureAwait(false);
    }

    /// <summary>The table and the mode as the chips need them: the chat on screen's, or a new chat's.</summary>
    private (IReadOnlyList<TableChip> Table, string Mode) ReadTable()
    {
        string? id;
        lock (_lock)
        {
            id = _sessionId;
        }

        if (id is null)
        {
            var starting = Models.Chat.Seam.Table.Starting(Config);
            var names = starting.Select(s => s.Name).ToList();
            var mode = SeamMode.Parse(Config.Chat.Mode, out _) is { } m && m.Check(names) is null ? m.ToString() : SeamMode.All.ToString();
            return ([.. starting.Select(Chip)], mode);
        }

        var (json, _, code) = Json(Private("chat", "seats", $"--session={id}"));
        if (code != Controllers.Exit.Ok)
        {
            return ([], SeamMode.All.ToString());
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var chips = root.GetProperty("seats").EnumerateArray().Select(s => new TableChip(
                s.GetProperty("name").GetString() ?? string.Empty,
                s.GetProperty("agent").GetString() ?? string.Empty,
                s.GetProperty("provider").GetString() ?? string.Empty,
                s.GetProperty("model").GetString() ?? string.Empty,
                s.TryGetProperty("color", out var color) && color.ValueKind == JsonValueKind.String ? color.GetString()! : "white",
                s.GetProperty("setUp").GetBoolean())).ToList();
            return (chips, root.GetProperty("mode").GetString() ?? SeamMode.All.ToString());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return ([], SeamMode.All.ToString());
        }
    }

    /// <summary>A seat as its chip shows it, from the settings.</summary>
    private TableChip Chip(TableSeat seat)
    {
        var agent = Models.Chat.Seam.Table.Resolve(Config, seat);
        return new TableChip(
            seat.Name,
            seat.Profile,
            agent is null ? string.Empty : Engines.DisplayName(agent.Provider, agent.Endpoint),
            seat.Model.Length > 0 ? seat.Model : agent?.Model ?? string.Empty,
            agent?.Color ?? "white",
            agent is { Filled: true });
    }

    /// <summary>Whether each agent's model takes tools, by set-up agent and model, as found for this run.</summary>
    private readonly Dictionary<string, bool> _takesTools = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether an agent at the table can be offered tools: an agent that cannot is told of no tools,
    /// and its chip says so. Null while it is being found out, which for Ollama means asking it once,
    /// in the background.
    /// </summary>
    public bool? TakesTools(TableChip chip)
    {
        ArgumentNullException.ThrowIfNull(chip);
        var key = $"{chip.Agent}|{chip.Model}";
        lock (_lock)
        {
            if (_takesTools.TryGetValue(key, out var known))
            {
                return known;
            }
        }

        if (Models.Chat.Seam.Table.Resolve(Config, new TableSeat(chip.Name, chip.Agent, chip.Model)) is not { } agent)
        {
            return false;
        }

        _ = Task.Run(async () =>
        {
            var can = await Models.Tools.ToolSupport.CanCallToolsAsync(agent, CancellationToken.None).ConfigureAwait(false);
            lock (_lock)
            {
                _takesTools[key] = can;
            }

            Changed?.Invoke();
        });
        return null;
    }

    /// <summary>The chip's colour of an agent at the table, by its name; the ink when it is not there.</summary>
    private string ColorOf(string seat) => Table.FirstOrDefault(s => s.Name == seat)?.Color ?? "white";

    /// <summary>
    /// The chat on screen's id, making it first when nobody has spoken in it yet (<c>chat new</c>), so its
    /// table and mode can be kept; null when it could not be made.
    /// </summary>
    private string? EnsureKept()
    {
        lock (_lock)
        {
            if (_sessionId is { } known)
            {
                return known;
            }
        }

        var (json, error, code) = Json(Private("chat", "new"));
        if (code != Controllers.Exit.Ok)
        {
            _ = Say(Role.System, Plain(error) is { Length: > 0 } said ? said : "The chat could not be made.");
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var id = doc.RootElement.GetProperty("session").GetString();
            if (id is not { Length: > 0 })
            {
                return null;
            }

            lock (_lock)
            {
                // What is typed for the new chat goes with it; a private chat's stays its own.
                if (!_private && _ownDraft.Length > 0)
                {
                    _book.Set(id, _ownDraft);
                    _ownDraft = string.Empty;
                }

                _sessionId = id;
                if (_private)
                {
                    _privateIds.Add(id);
                }

                _sessions = null;
            }

            return id;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _ = Say(Role.System, $"'diptick chat new' printed something this screen could not read: {ex.Message}");
            return null;
        }
    }

    /// <summary>A verb that changes the table, the mode or the brief: what it says goes in the chat, and the chips are read again.</summary>
    private Task TableVerbAsync(string[] args)
    {
        Run(yes: false, args);
        lock (_lock)
        {
            _table = null;
            _answerMode = string.Empty;
            _sessions = null;
        }

        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>A chat verb's words, with <c>--private</c> after the subcommand when the chat on screen is private.</summary>
    private string[] Private(params string[] args) =>
        _private && args.Length >= 2 ? [args[0], args[1], "--private", .. args[2..]] : args;

    /// <summary>What the chat on screen and today cost, read once after each round (<c>chat cost</c>).</summary>
    private (decimal? Chat, decimal? Today, string Currency)? Costs()
    {
        lock (_lock)
        {
            if (_costs is { } known)
            {
                return known;
            }
        }

        decimal? chat = null;
        decimal? today = null;
        if (_sessionId is { } id && !_private)
        {
            var (json, _, code) = Json("chat", "cost", $"--session={id}");
            chat = code == Controllers.Exit.Ok ? CostIn(json, "cost") : null;
        }

        var (todayJson, _, todayCode) = Json("chat", "cost", "--today");
        today = todayCode == Controllers.Exit.Ok ? CostIn(todayJson, "cost") : null;
        var costs = (chat, today is 0 ? null : today, Config.Spending.Currency);
        lock (_lock)
        {
            _costs = costs;
        }

        return costs;
    }

    private static decimal? CostIn(string json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(name, out var cost) && cost.ValueKind == JsonValueKind.Number ? cost.GetDecimal() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>An amount as the chips show it: to the cent from a cent up, else to four places.</summary>
    internal static string Money(decimal amount) =>
        amount.ToString(amount >= 0.01m ? "0.00" : "0.####", CultureInfo.InvariantCulture);

    /// <summary>
    /// One round, drawn as it comes (<c>chat say</c>, <c>pass</c> or <c>retry</c>, with <c>--events</c>):
    /// each agent's answer on its own line under its name as it starts, growing as it streams, and ended
    /// with its tokens and cost; a question one puts to another under the answer that asks it; why a
    /// round ended early, said. The person's questions come to the screen's prompt; Esc stops it all.
    /// Returns whether what was said was kept (its "said" event came).
    /// </summary>
    private async Task<bool> RoundAsync(string[] args)
    {
        using var turn = new CancellationTokenSource();
        _turn = turn;

        // A mark where the answers will come, until the first one starts.
        var drawn = new RoundDrawn(new ChatLine(Role.Agent, string.Empty, DateTimeOffset.Now) { Status = MessageStatus.Streaming });
        Add(drawn.Placeholder!);
        Changed?.Invoke();

        using var events = new EventLines(line => DrawRound(line, drawn));
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            var running = Task.Run(() => RunVerb(args, events, errors, yes: false, "text", turn.Token));
            _running = running;
            await running.ConfigureAwait(false);
            events.Finish();
        }
        finally
        {
            _turn = null;
        }

        lock (_lines)
        {
            // No answer started: the mark goes.
            if (drawn.Placeholder is { } mark && Find(mark) is var at and >= 0)
            {
                _transcript.RemoveAt(at);
            }

            // An answer cut off in the middle by Esc or a crash of the verb is kept as far as it got.
            foreach (var (seat, line) in drawn.Streaming)
            {
                if (Find(line) is var index and >= 0)
                {
                    _transcript[index] = line.Text.Length == 0 ? line with { Status = MessageStatus.Stopped, Text = "(stopped before a word)" } : line with { Status = MessageStatus.Stopped };
                }
            }
        }

        if (Plain(errors.ToString()) is { Length: > 0 } complaint)
        {
            Add(new ChatLine(Role.System, complaint, DateTimeOffset.Now));
        }

        lock (_lock)
        {
            _sessions = null;
            _costs = null;
            _sources = null;
        }

        // The files the round brought, attached or made, under the lines they came with.
        ReadFiles();
        Changed?.Invoke();
        return drawn.Said;
    }

    /// <summary>One event of a round, drawn (<see cref="Controllers.ChatController"/>'s events: round, said, turn, delta, act, notice, end).</summary>
    private void DrawRound(string line, RoundDrawn drawn)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            switch (root.GetProperty("event").GetString())
            {
                case "round":
                    Began(root);
                    break;
                case "said":
                    drawn.Said = true;
                    drawn.Round = root.GetProperty("round").GetInt32();
                    var seq = root.GetProperty("seq").GetInt32();
                    lock (_lines)
                    {
                        // The person's line just said takes its number, so Retry can name it.
                        var said = _transcript.FindLastIndex(l => l.Role == Role.Person && l.Seq is null);
                        if (said >= 0)
                        {
                            _transcript[said] = _transcript[said] with { Seq = seq, Round = drawn.Round };
                        }
                    }

                    break;
                case "turn":
                    var seat = root.GetProperty("seat").GetString() ?? string.Empty;
                    var state = root.GetProperty("state").GetString() ?? string.Empty;
                    if (state == MessageStatus.Streaming)
                    {
                        TurnBegan(root, seat, drawn);
                    }
                    else
                    {
                        TurnEnded(root, seat, state, drawn);
                    }

                    break;
                case "delta":
                    var who = root.GetProperty("seat").GetString() ?? string.Empty;
                    if (drawn.Streaming.TryGetValue(who, out var growing))
                    {
                        var now = growing with { Text = growing.Text + root.GetProperty("text").GetString() };
                        Replace(growing, now);
                        drawn.Streaming[who] = now;
                    }

                    break;
                case "act":
                    Asked(root);
                    break;
                case "tool":
                    // A tool call an agent made, folded under the answer that made it: the agent, then what it did,
                    // then what it was asked and gave. With several answering at once, under its own agent's answer.
                    var caller = root.GetProperty("seat").GetString() ?? string.Empty;
                    var called = new ChatLine(Role.Tool, root.GetProperty("text").GetString() ?? string.Empty, DateTimeOffset.Now)
                    {
                        Seat = caller,
                        Color = ColorOf(caller),
                        Seq = root.TryGetProperty("seq", out var toolSeq) && toolSeq.ValueKind == JsonValueKind.Number ? toolSeq.GetInt32() : null,
                        Round = drawn.Round,
                        Status = root.TryGetProperty("state", out var toolState) ? toolState.GetString() ?? MessageStatus.Done : MessageStatus.Done,
                    };
                    lock (_lines)
                    {
                        var at = drawn.Streaming.TryGetValue(caller, out var answering) ? Find(answering) : -1;
                        _transcript.Insert(at >= 0 ? After(at, caller) : _transcript.Count, called);
                    }

                    break;
                case "notice":
                    var notice = new ChatLine(Role.System, root.GetProperty("text").GetString() ?? string.Empty, DateTimeOffset.Now);
                    lock (_lines)
                    {
                        // Above the mark while no answer has started, so the answers still come last.
                        var place = drawn.Placeholder is { } mark ? Find(mark) : -1;
                        _transcript.Insert(place >= 0 ? place : _transcript.Count, notice);
                    }

                    break;
                case "end":
                    if (EndWords(root.GetProperty("reason").GetString() ?? string.Empty) is { } words)
                    {
                        Add(new ChatLine(Role.System, words, DateTimeOffset.Now));
                    }

                    break;
                default:
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // A line the screen cannot read is shown as it came, as DipTick's: nothing is lost.
            Add(new ChatLine(Role.System, line, DateTimeOffset.Now));
        }

        Changed?.Invoke();
    }

    /// <summary>The round's first event: the chat's id, which a new chat takes here, and its title.</summary>
    private void Began(JsonElement root)
    {
        if (!root.TryGetProperty("session", out var session) || session.ValueKind != JsonValueKind.String)
        {
            return;
        }

        lock (_lock)
        {
            // A new chat's first answer gives it its id, and what is typed for it goes along; a private chat's stays its own.
            if (_sessionId is null && !_private && session.GetString() is { Length: > 0 } given && _ownDraft.Length > 0)
            {
                _book.Set(given, _ownDraft);
                _ownDraft = string.Empty;
            }

            if (_sessionId is null)
            {
                // The table a new chat started with is the one it now has: read again from the store.
                _table = null;
            }

            _sessionId = session.GetString();
            if (_private && _sessionId is { } made)
            {
                _privateIds.Add(made);
            }

            if (root.TryGetProperty("title", out var title) && _title.Length == 0)
            {
                _title = title.GetString() ?? string.Empty;
            }

            _sessions = null;
        }
    }

    /// <summary>An agent's turn starts: its line takes the mark's place, or comes after the last.</summary>
    private void TurnBegan(JsonElement root, string seat, RoundDrawn drawn)
    {
        var model = root.TryGetProperty("model", out var m) ? m.GetString() ?? string.Empty : string.Empty;
        var color = root.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : ColorOf(seat);
        var step = root.TryGetProperty("step", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0;
        var line = new ChatLine(Role.Agent, string.Empty, DateTimeOffset.Now) { Seat = seat, Model = model, Color = color, Round = drawn.Round, Step = step, Status = MessageStatus.Streaming };
        lock (_lines)
        {
            if (drawn.Placeholder is { } mark && Find(mark) is var at and >= 0)
            {
                _transcript[at] = line;
            }
            else
            {
                _transcript.Add(line);
            }
        }

        drawn.Placeholder = null;
        drawn.Streaming[seat] = line;

        // What answered is let go of at quit, whatever the chat on screen by then.
        if (root.TryGetProperty("agent", out var agent) && agent.GetString() is { Length: > 0 } section && model.Length > 0)
        {
            lock (_lock)
            {
                _answeredBy.Add((section, model));
            }
        }
    }

    /// <summary>An agent's turn ends: its number, what it used and cost, and how it ended; an answer that never came leaves only why.</summary>
    private void TurnEnded(JsonElement root, string seat, string state, RoundDrawn drawn)
    {
        var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        if (!drawn.Streaming.Remove(seat, out var line))
        {
            if (error is not null)
            {
                Add(new ChatLine(Role.System, $"{seat}: {error}", DateTimeOffset.Now));
            }

            return;
        }

        var ended = line with
        {
            Status = state,
            Seq = root.TryGetProperty("seq", out var seq) && seq.ValueKind == JsonValueKind.Number ? seq.GetInt32() : null,
            TokensIn = root.TryGetProperty("tokensIn", out var tin) && tin.ValueKind == JsonValueKind.Number ? tin.GetInt32() : null,
            TokensOut = root.TryGetProperty("tokensOut", out var tout) && tout.ValueKind == JsonValueKind.Number ? tout.GetInt32() : null,
            Estimated = root.TryGetProperty("estimated", out var est) && est.ValueKind == JsonValueKind.True,
            Cost = root.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Number ? cost.GetDecimal() : null,
        };

        lock (_lines)
        {
            var at = Find(line);
            if (at >= 0 && ended.Text.Length == 0 && state is not (MessageStatus.Done or MessageStatus.Passed))
            {
                // Not the agent's line: nothing of it came. DipTick says why, where it would have been.
                _transcript[at] = new ChatLine(Role.System, $"{seat}: {error ?? state}", DateTimeOffset.Now);
                return;
            }

            if (at >= 0)
            {
                _transcript[at] = ended;
                if (error is not null && state is not (MessageStatus.Done or MessageStatus.Passed))
                {
                    // Why it ended so, after the answer and the tool rows under it, which stay with their answer.
                    _transcript.Insert(After(at, seat), new ChatLine(Role.System, $"{seat}: {error}", DateTimeOffset.Now));
                }
            }
        }
    }

    /// <summary>A question an agent put to another (or what DipTick would not count), under the answer that asked it.</summary>
    private void Asked(JsonElement root)
    {
        var seq = root.GetProperty("seq").GetInt32();
        var accepted = root.GetProperty("accepted").GetBoolean();
        var to = root.TryGetProperty("to", out var list) && list.ValueKind == JsonValueKind.Array ? string.Join(", ", list.EnumerateArray().Select(t => t.GetString() == "person" ? Who : t.GetString())) : string.Empty;
        var question = root.TryGetProperty("question", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString() : null;
        var words = accepted && question is not null
            ? $"asks {to}: “{question}”"
            : $"{root.GetProperty("words").GetString()} (not passed on)";
        lock (_lines)
        {
            var at = _transcript.FindLastIndex(l => l.Role == Role.Agent && l.Seq == seq);
            if (at >= 0)
            {
                _transcript[at] = _transcript[at] with { Acts = [.. _transcript[at].Acts, words] };
            }
        }
    }

    /// <summary>Why a round ended before its plan did, in words; null when it ran whole or said so itself.</summary>
    private string? EndWords(string reason) => reason switch
    {
        "hand-back" => $"The floor is back with you, {Who}.",
        "passed" => "The agents had nothing more to add, so the round ended.",
        MessageStatus.Repeat => "An answer repeated one given earlier, so the round ended there.",
        "dependency" => "An answer did not finish, so the next agent had nothing to follow; the round ended there.",
        "budget:seconds" => "The round ran out of time (chat.maxRoundSeconds in Settings, Chat).",
        "budget:tokens" => "The round used all its tokens (chat.maxRoundTokens in Settings, Chat).",
        _ => null,
    };

    /// <summary>A line swapped for its newer self, by reference; nothing when it is gone (a new chat, another opened).</summary>
    private void Replace(ChatLine was, ChatLine now)
    {
        lock (_lines)
        {
            if (Find(was) is var at and >= 0)
            {
                _transcript[at] = now;
            }
        }
    }

    /// <summary>A round on screen as its events draw it.</summary>
    private sealed class RoundDrawn(ChatLine placeholder)
    {
        /// <summary>The mark where answers will come, until the first starts.</summary>
        public ChatLine? Placeholder { get; set; } = placeholder;

        /// <summary>The round's number, from its "said" event.</summary>
        public int Round { get; set; }

        /// <summary>Whether what the person said was kept: its "said" event came.</summary>
        public bool Said { get; set; }

        /// <summary>Each answer still coming, by its agent's name.</summary>
        public Dictionary<string, ChatLine> Streaming { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// A kept chat's lines as the screen draws them, from <c>chat show</c>'s JSON: each answer under its
    /// agent's name and colour, with its number, round, tokens and cost, and the questions it put to others.
    /// </summary>
    private List<ChatLine> LinesOf(JsonElement root)
    {
        var colors = root.TryGetProperty("seats", out var seats) && seats.ValueKind == JsonValueKind.Array
            ? seats.EnumerateArray().ToDictionary(s => s.GetProperty("name").GetString() ?? string.Empty, s => s.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "white", StringComparer.Ordinal)
            : [];
        var models = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("seats", out var table) && table.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in table.EnumerateArray())
            {
                models[s.GetProperty("name").GetString() ?? string.Empty] = s.GetProperty("model").GetString() ?? string.Empty;
            }
        }

        var asks = new Dictionary<int, List<string>>();
        if (root.TryGetProperty("acts", out var acts) && acts.ValueKind == JsonValueKind.Array)
        {
            foreach (var act in acts.EnumerateArray())
            {
                var to = string.Join(", ", act.GetProperty("to").EnumerateArray().Select(t => t.GetString() == "person" ? Who : t.GetString()));
                var words = act.GetProperty("accepted").GetBoolean()
                    ? $"asks {to}: “{act.GetProperty("question").GetString()}”"
                    : $"{act.GetProperty("kind").GetString()?.ToUpperInvariant()} not accepted: {act.GetProperty("code").GetString()} (not passed on)";
                var seq = act.GetProperty("seq").GetInt32();
                if (!asks.TryGetValue(seq, out var list))
                {
                    asks[seq] = list = [];
                }

                list.Add(words);
            }
        }

        var lines = new List<ChatLine>();
        var toolsUnder = new List<(ChatLine Tool, int Under)>();
        foreach (var line in root.GetProperty("lines").EnumerateArray())
        {
            var author = line.GetProperty("author").GetString() ?? string.Empty;
            var status = line.GetProperty("status").GetString() ?? MessageStatus.Done;
            var text = line.GetProperty("text").GetString() ?? string.Empty;
            var kind = line.TryGetProperty("kind", out var k) ? k.GetString() : null;
            var role = kind == MessageKind.Tool ? Role.Tool : author switch { Author.Person => Role.Person, Author.DipTick => Role.System, _ => Role.Agent };
            int? seq = line.TryGetProperty("seq", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : null;
            if (role == Role.Person && kind == MessageKind.Pass)
            {
                text = "(passed the floor back to the agents)";
            }

            var agentLine = role is Role.Agent or Role.Tool;
            lines.Add(new ChatLine(role, text, line.GetProperty("started").GetDateTimeOffset().ToLocalTime())
            {
                Seat = agentLine ? author : string.Empty,
                Model = role == Role.Agent && models.TryGetValue(author, out var model) ? model : string.Empty,
                Color = agentLine && colors.TryGetValue(author, out var color) ? color : string.Empty,
                Seq = seq,
                Round = line.TryGetProperty("round", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : 0,
                Step = line.TryGetProperty("step", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetInt32() : 0,
                Status = status,
                TokensIn = line.TryGetProperty("tokensIn", out var tin) && tin.ValueKind == JsonValueKind.Number ? tin.GetInt32() : null,
                TokensOut = line.TryGetProperty("tokensOut", out var tout) && tout.ValueKind == JsonValueKind.Number ? tout.GetInt32() : null,
                Estimated = line.TryGetProperty("estimated", out var est) && est.ValueKind == JsonValueKind.True,
                Cost = line.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Number ? cost.GetDecimal() : null,
                Acts = seq is { } n && asks.TryGetValue(n, out var said) ? said : [],
            });
            if (role == Role.Tool && line.TryGetProperty("under", out var under) && under.ValueKind == JsonValueKind.Number)
            {
                toolsUnder.Add((lines[^1], under.GetInt32()));
            }
        }

        // Each tool line under the answer it was called for, after the rows already put there, in the order they were called.
        var moved = new HashSet<ChatLine>(ReferenceEqualityComparer.Instance);
        foreach (var (tool, under) in toolsUnder)
        {
            if (!lines.Exists(l => l.Role == Role.Agent && l.Seq == under))
            {
                continue;
            }

            lines.RemoveAt(lines.FindIndex(l => ReferenceEquals(l, tool)));
            var place = lines.FindIndex(l => l.Role == Role.Agent && l.Seq == under) + 1;
            while (place < lines.Count && moved.Contains(lines[place]))
            {
                place++;
            }

            lines.Insert(place, tool);
            moved.Add(tool);
        }

        return lines;
    }

    /// <summary>Where a tool row goes under the answer at <paramref name="at"/>: after it, and after that agent's rows already under it.</summary>
    private int After(int at, string seat)
    {
        var after = at + 1;
        while (after < _transcript.Count && _transcript[after].Role == Role.Tool && _transcript[after].Seat == seat)
        {
            after++;
        }

        return after;
    }

    /// <summary>What an answer used, for under it: tokens in and out (~ where DipTick counted them), and its cost where a price is known.</summary>
    public string UsageOf(ChatLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Role != Role.Agent || line.Status == MessageStatus.Streaming || (line.TokensIn is null && line.TokensOut is null))
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        var about = line.Estimated ? "~" : string.Empty;
        text.Append(CultureInfo.InvariantCulture, $"{about}{line.TokensIn ?? 0:N0} in · {about}{line.TokensOut ?? 0:N0} out");
        if (line.Cost is { } cost && Config.Spending.ShowCost)
        {
            text.Append(CultureInfo.InvariantCulture, $" · {Money(cost)} {Config.Spending.Currency}");
        }

        return text.ToString();
    }

    /// <summary>How an answer ended, in words, when it did not end whole; empty when it did.</summary>
    public static string EndOf(ChatLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return line.Status switch
        {
            MessageStatus.Streaming => "answering…",
            MessageStatus.Cut => "cut short",
            MessageStatus.Stopped => "stopped",
            MessageStatus.Passed => "passed",
            MessageStatus.Repeat => "repeated an earlier answer; not passed on",
            MessageStatus.Refused => "refused",
            MessageStatus.Failed => "failed",
            MessageStatus.Denied => "not asked",
            _ => string.Empty,
        };
    }
}
