using System.Diagnostics;
using System.Globalization;
using DipTick.Controllers;
using DipTick.Models;
using DipTick.Models.Chat;
using DipTick.Models.Harness;
using DipTick.Models.Keys;

namespace DipTick.ViewModels;

/// <summary>What a row in the settings screen does when changed.</summary>
internal enum RowAction
{
    Setting,
    Key,
    AskEachAnswer,
    SeatHeader,
    AddSeat,
    RemoveSeat,

    /// <summary>Setup's questions again (Settings, You): the one place setup opens after its first time.</summary>
    RunSetup,

    /// <summary>A place DipTick keeps something (Settings, Data): opens it in Explorer.</summary>
    OpenPath,

    /// <summary>One of DipTick's pages (Settings, Help): opens Help at it; the row's Raw is the page's id.</summary>
    OpenDoc,

    /// <summary>Settings, Data, Export all my data: every file DipTick keeps, in one zip (<c>diptick data export</c>).</summary>
    ExportData,

    /// <summary>Settings, Models, Add a model: the whole provider catalogue, then where the model goes.</summary>
    AddModel,

    /// <summary>Settings, Connectors: one connector added; Change opens its menu. Raw is its name.</summary>
    Connector,

    /// <summary>Settings, Connectors: a known connector to add (Raw is its id), or your own (Raw empty).</summary>
    AddConnector,

    /// <summary>Settings, Connectors: another app's list, imported entry by entry.</summary>
    ImportConnectors,

    /// <summary>Settings, Skills: one skill; Change opens its menu. Raw is its name.</summary>
    Skill,

    /// <summary>Settings, Skills: a skill added from a folder or a zip, shown whole and approved.</summary>
    AddSkill,
}

/// <summary>
/// One row of the settings screen: what it is called, its value as shown, and what it does.
/// <paramref name="Set"/> says the value is set in the file (or, for a key, saved), so Default has
/// something to put back; <paramref name="Raw"/> is the value itself, for a switch or a list to
/// start from.
/// </summary>
internal sealed record SettingRow(string Label, string Value, string Help, RowAction Action, SettingInfo? Info = null, string Section = "main", bool Set = false, string Raw = "");

/// <summary>
/// The settings screen with no screen code in it: the sections, each section's rows with their values
/// and help, and what Change and Default do. Every change runs the verb that makes it, in this
/// process, as the slash commands do; the key and the password use the same model code their verbs
/// use. Questions go through the chat's asker, so Change and Default run on a worker thread, never
/// the screen's.
/// </summary>
internal sealed partial class SettingsViewModel(ChatViewModel chat)
{
    /// <summary>
    /// The sections the screens show, in order. Database is not among them: it is a connection for
    /// a tool that does not exist yet, and it is easily taken for where the chats are kept. Its
    /// settings still work from <c>diptick config</c> and show in <c>diptick settings</c>; Help, Your
    /// data says so.
    /// </summary>
    public IReadOnlyList<string> Sections { get; } = [.. SettingsCatalogue.Sections.Select(s => s.Name).Where(s => s != SettingsCatalogue.HiddenSection)];

    /// <summary>Settings, Data, "What is this?": Help, open at the page about the files DipTick keeps.</summary>
    public void WhatIsThis() => chat.DocsHook?.Invoke(Controllers.Docs.YourData);

    /// <summary>A section's note when it does not act yet, else empty.</summary>
    public static string Note(string section) => SettingsCatalogue.Sections.FirstOrDefault(s => s.Name == section).Note ?? string.Empty;

    /// <summary>Whether a row answers a search: its label, what it does, or its setting's name holds the words.</summary>
    public static bool Matches(SettingRow row, string words)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(words);
        return row.Label.Contains(words, StringComparison.OrdinalIgnoreCase)
            || row.Help.Contains(words, StringComparison.OrdinalIgnoreCase)
            || (row.Info?.Name.Contains(words, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>
    /// The command that does what a row does, from a terminal (CLI first): shown with "all about it"
    /// (F1) on the setting you are on, in the window and in the terminal alike. Empty where there is none.
    /// </summary>
    public static string CommandFor(SettingRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Action switch
        {
            RowAction.Setting when row.Info is { Kind: SettingKind.Password } => "diptick config password",
            RowAction.Setting when row.Info is { } info => $"diptick config set {info.Name} <value>  ·  diptick settings {info.Name}",
            RowAction.Key => $"diptick key set {row.Section}  ·  diptick key clear {row.Section}",
            RowAction.RunSetup => "diptick setup (--full for every question)",
            RowAction.AddSeat => "diptick seats add <id> --provider <p> --model <m>",
            RowAction.RemoveSeat => "diptick seats remove <id>",
            RowAction.OpenPath => "diptick data",
            RowAction.OpenDoc => $"diptick docs {row.Raw}",
            RowAction.ExportData => "diptick data export <file.zip>",
            RowAction.AddModel => "diptick providers  ·  diptick setup",
            RowAction.Connector => $"diptick connectors test {row.Raw}  ·  diptick connectors tools {row.Raw}  ·  diptick chat connectors add {row.Raw}",
            RowAction.AddConnector => row.Raw.Length > 0 ? $"diptick connectors add {row.Raw} --from {row.Raw}" : "diptick connectors add <name> --url <address>  ·  --command <program>",
            RowAction.ImportConnectors => "diptick connectors import <file>",
            RowAction.Skill => $"diptick skills show {row.Raw}  ·  diptick chat skills add {row.Raw}",
            RowAction.AddSkill => "diptick skills add <folder|zip>",
            _ => string.Empty,
        };
    }

    /// <summary>Every row in every section that answers a search, with the section it lives in.</summary>
    public IReadOnlyList<(string Section, SettingRow Row)> Find(string words)
    {
        ArgumentNullException.ThrowIfNull(words);
        return [.. Sections.SelectMany(section => Rows(section).Select(row => (Section: section, Row: row))).Where(x => Matches(x.Row, words.Trim()))];
    }

    /// <summary>A section's rows, with the values as they stand now.</summary>
    public IReadOnlyList<SettingRow> Rows(string section)
    {
        var config = chat.Config;
        if (section == "Data")
        {
            return DataRows();
        }

        if (section == "Connectors")
        {
            return ConnectorRows();
        }

        if (section == "Skills")
        {
            return SkillRows();
        }

        if (section == SettingsCatalogue.HelpSection)
        {
            return [.. Controllers.Docs.Pages.Select(p => new SettingRow(p.Title, string.Empty, Summary(p), RowAction.OpenDoc, Raw: p.Id))];
        }

        var values = ConfigController.Rows(new Cli(chat.Flags, config, TextWriter.Null, TextWriter.Null));
        var rows = new List<SettingRow>();
        if (section == "Models")
        {
            rows.Add(new SettingRow("Add a model…", string.Empty, $"Every provider DipTick knows ({Models.Providers.ProviderCatalogue.All.Count(p => p.WorksNow)} work now): runtimes on this PC, model makers, hosts, clouds and gateways, searchable. Picking one asks only for what it needs, lists its models (it costs nothing), then asks where the model goes: to answer you, or a new seat at the table.", RowAction.AddModel));
        }

        foreach (var info in SettingsCatalogue.All.Where(s => s.Section == section))
        {
            if (section == "Permissions" && rows.Count == 0)
            {
                rows.Add(new SettingRow("Ask before each answer", AskEachWords(config.Permissions, config.Problems), "Whether each answer waits for your yes. Most people say no; say yes on a paid provider to approve every call.", RowAction.AskEachAnswer));
            }

            rows.Add(Row(info, values, "main"));

            // Brave takes your own key: saved under Brave's host, like a model's.
            if (info.Name == "research.search" && config.ResearchSearch == "brave")
            {
                var brave = Models.Research.SearchService.Keys(config);
                var foundBrave = brave.Describe();
                rows.Add(new SettingRow("  Brave's key", foundBrave.Words, $"Your Brave Search API key, for {brave.Host}: saved in Windows Credential Manager, or read from the variable research.keyVariable names. Brave charges $5 per 1,000 searches, with $5 of credit each month.", RowAction.Key, Section: "research", Set: foundBrave.Source == KeySource.Stored));
            }

            if (info.Name == "main.model" && Engines.KeysFor(config) is { } keys)
            {
                var found = keys.Describe();
                rows.Add(new SettingRow("Key", found.Words, $"The key for {keys.Host}: saved in Windows Credential Manager, or read from the variable main.keyVariable names. Change saves a new one; Remove takes the saved one away.", RowAction.Key, Set: found.Source == KeySource.Stored));
            }
        }

        if (section == "You")
        {
            rows.Add(new SettingRow("Run setup again", string.Empty, "Setup's questions again: your name, your provider, its key and its model; Full adds a password and approvals. Nothing changes until you save, and Esc leaves at any step.", RowAction.RunSetup));
        }

        if (section == "Models")
        {
            foreach (var seat in config.Seats)
            {
                rows.Add(new SettingRow($"Seat {seat.Id}", $"{Engines.DisplayName(seat.Provider, seat.Endpoint)} {seat.Model}", "One agent's seat at the table, with its own provider, model, key and lane. Change it to run setup's model steps for it.", RowAction.SeatHeader, Section: seat.Id));
                foreach (var template in SettingsCatalogue.SeatTemplate)
                {
                    var info = SettingsCatalogue.For(template, seat.Id);
                    rows.Add(Row(info, values, seat.Id) with { Label = "  " + info.Label });
                    if (info.Name.EndsWith(".model", StringComparison.Ordinal) && Engines.KeysFor(seat) is { } seatKeys)
                    {
                        var found = seatKeys.Describe();
                        rows.Add(new SettingRow("  Key", found.Words, $"The key for {seatKeys.Host}.", RowAction.Key, Section: seat.Id, Set: found.Source == KeySource.Stored));
                    }
                }
            }

            rows.Add(new SettingRow("Add a seat…", string.Empty, $"Seats another model at the table (at most {MasterConfig.MaxSeats}). 'diptick ask --to team' asks every seat at once today.", RowAction.AddSeat));
            if (config.Seats.Count > 0)
            {
                rows.Add(new SettingRow("Remove a seat…", string.Empty, "Takes a model away from the table. Its section stays in the file, unused.", RowAction.RemoveSeat));
            }
        }

        return rows;
    }

    /// <summary>Change: the editor for the row's kind, then the verb that makes the change. Runs on a worker thread.</summary>
    public async Task ChangeAsync(SettingRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        // Neither asks in the prompt zone: a page opens in Help, and the export asks where in its own way.
        if (row.Action == RowAction.OpenDoc)
        {
            chat.DocsHook?.Invoke(row.Raw);
            return;
        }

        if (row.Action == RowAction.ExportData)
        {
            await chat.ExportAllDataAsync().ConfigureAwait(false);
            return;
        }

        if (chat.Asker is not { } asker)
        {
            return;
        }

        switch (row.Action)
        {
            case RowAction.RunSetup:
                await chat.RunSetupAsync(full: false).ConfigureAwait(false);
                return;
            case RowAction.OpenPath:
                if (Reveal(row.Value) is { } why)
                {
                    asker.Tell("Settings · Data", why);
                }

                return;
            case RowAction.Key:
                await KeyAsync(asker, row.Section).ConfigureAwait(false);
                return;
            case RowAction.AskEachAnswer:
                await AskEachAsync(asker).ConfigureAwait(false);
                return;
            case RowAction.SeatHeader:
                await ModelStepsAsync(asker, row.Section, newSeat: false).ConfigureAwait(false);
                return;
            case RowAction.AddSeat:
                await AddSeatAsync(asker).ConfigureAwait(false);
                return;
            case RowAction.AddModel:
                await AddModelAsync(asker).ConfigureAwait(false);
                return;
            case RowAction.RemoveSeat:
                await RemoveSeatAsync(asker).ConfigureAwait(false);
                return;
            case RowAction.Connector:
                await ConnectorMenuAsync(asker, row.Raw).ConfigureAwait(false);
                return;
            case RowAction.AddConnector:
                await AddConnectorAsync(asker, row.Raw).ConfigureAwait(false);
                return;
            case RowAction.ImportConnectors:
                ImportConnectors(asker);
                return;
            case RowAction.Skill:
                await SkillMenuAsync(asker, row.Raw).ConfigureAwait(false);
                return;
            case RowAction.AddSkill:
                AddSkill(asker);
                return;
        }

        var info = row.Info!;
        var title = $"Settings · {info.Label.Trim()}";
        var current = CurrentValue(info);
        string? value = null;
        switch (info.Kind)
        {
            case SettingKind.Provider:
                await ModelStepsAsync(asker, row.Section, newSeat: false).ConfigureAwait(false);
                return;
            case SettingKind.Model:
                value = PickModel(asker, row.Section, current);
                break;
            case SettingKind.Password:
                await PasswordAsync(asker).ConfigureAwait(false);
                return;
            case SettingKind.Choice:
                var choices = info.Choices ?? [];
                var chosen = asker.Choose(new Question(title, info.Label.Trim(), info.Help), [.. choices.Select(c => new Choice(c.Length == 0 ? "(the default)" : c))], Math.Max(0, choices.ToList().IndexOf(current)));
                value = chosen.Reply == Reply.Answered ? choices[chosen.Value] : null;
                break;
            case SettingKind.YesNo:
                var yes = asker.YesNo(new Question(title, $"{info.Label.Trim()}?", info.Help), current == "true");
                value = yes.Reply == Reply.Answered ? (yes.Value ? "true" : "false") : null;
                break;
            default:
                var typed = asker.Text(new Question(title, $"{info.Label.Trim()}: {info.Takes}.", info.Help), current == "(none)" ? string.Empty : current, text => Check(info, text));
                value = typed.Reply == Reply.Answered ? typed.Value : null;
                break;
        }

        if (value is null || value == current)
        {
            return;
        }

        await SetAsync(asker, info, current, value).ConfigureAwait(false);
    }

    /// <summary>
    /// A value picked in place (a switch, a list): the same verb Change runs, and the same yes first
    /// where the setting needs one. Runs on a worker thread.
    /// </summary>
    public async Task SetValueAsync(SettingRow row, string value)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(value);
        if (chat.Asker is not { } asker || row.Info is not { } info)
        {
            return;
        }

        var current = CurrentValue(info);
        if (value == current)
        {
            return;
        }

        await SetAsync(asker, info, current, value).ConfigureAwait(false);
    }

    /// <summary>Default: the value leaves the file so its default applies; for a key, the saved key is removed. Runs on a worker thread.</summary>
    public async Task DefaultAsync(SettingRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (chat.Asker is not { } asker)
        {
            return;
        }

        if (row.Action == RowAction.Key)
        {
            var keys = KeysOf(row.Section);
            if (keys is null)
            {
                return;
            }

            var remove = asker.YesNo(new Question("Settings · Key", $"Remove the key saved for {keys.Host}?", "Calls to it have no key until one is saved again."), false);
            if (remove.Reply != Reply.Answered || !remove.Value)
            {
                return;
            }

            if (chat.Flags.DryRun)
            {
                await chat.Say(Role.System, $"[dry run] would remove the key saved for {keys.Host}; nothing removed.").ConfigureAwait(false);
                return;
            }

            try
            {
                await chat.Say(Role.System, WindowsKeyStore.Instance.Delete(keys.Host) ? $"Removed the key for {keys.Host}." : $"No key was saved for {keys.Host}.").ConfigureAwait(false);
            }
            catch (KeyStoreException ex)
            {
                await chat.Say(Role.System, ex.Message).ConfigureAwait(false);
            }

            return;
        }

        if (row.Action != RowAction.Setting || row.Info is not { Kind: not SettingKind.Password } info)
        {
            return;
        }

        var yes = SettingsCatalogue.NeedsYes(info.Name);
        if (yes)
        {
            var sure = asker.YesNo(new Question($"Settings · {info.Label.Trim()}", $"Put {info.Name} back to its default ({(info.Default.Length == 0 ? "empty" : info.Default)})?", "It changes what agents may do or where the conversation goes."), false);
            if (sure.Reply != Reply.Answered || !sure.Value)
            {
                return;
            }
        }

        if (chat.Run(yes, "config", "unset", info.Name) == Exit.Ok)
        {
            chat.Reload();
        }
    }

    private static SettingRow Row(SettingInfo info, IReadOnlyList<ConfigController.Row> values, string section)
    {
        var help = info.Help + $"\nDefault: {(info.Default.Length == 0 ? "empty" : info.Default)}. Takes {info.Takes}."
            + (SettingsCatalogue.NeedsYes(info.Name) ? " Changing it needs your yes." : string.Empty);
        // Default is offered only where it would change something: the file sets a value that is not the default.
        var value = values.FirstOrDefault(v => string.Equals(v.Name, info.Name, StringComparison.OrdinalIgnoreCase));
        var raw = value?.Value ?? string.Empty;
        var set = value?.Layer == "file" && !string.Equals(raw, info.Default, StringComparison.Ordinal);
        return new SettingRow(info.Label, SettingsController.Value(info, values), help, RowAction.Setting, info, section, set, raw);
    }

    /// <summary>The settings file, selected in Explorer (F7 in Settings). Returns why it could not be shown, or null.</summary>
    public string? RevealFile() => (chat.Config.Path ?? chat.SettingsPath) is { } path
        ? Reveal(path)
        : "No settings file is in use (--config none), so there is none to show.";

    /// <summary>
    /// Settings, Data: where DipTick keeps each thing, as paths (the same list <c>diptick data</c>
    /// prints), a click opening the folder; then Export all my data.
    /// </summary>
    private List<SettingRow> DataRows() =>
    [
        .. DataPlaces.For(chat.Config, chat.Config.Path ?? chat.SettingsPath).Select(p => new SettingRow(p.Label, p.Path, p.Help, RowAction.OpenPath)),
        new SettingRow("Export all my data…", string.Empty, "Everything DipTick keeps, in one zip file: your settings (no key, and no password), every kept chat as text and as JSON Lines, the audit log and whether it verified. It holds your chats as plain text: keep it where you would keep them.", RowAction.ExportData),
    ];

    /// <summary>A page's first paragraph, as plain words, a link as its words alone: what its row in Settings, Help says of it.</summary>
    internal static string Summary(Controllers.DocPage page) =>
        MarkdownText.Parse(page.Text).FirstOrDefault(b => b.Kind == MdKind.Paragraph && !b.Text.StartsWith('*')) is { } first
            ? string.Concat(MarkdownText.Spans(first.Text).Select(s => s.Text))
            : page.Title;

    /// <summary>Opens Explorer at <paramref name="path"/>: the file selected, or the folder. Returns why it could not, or null.</summary>
    internal static string? Reveal(string path)
    {
        string arguments;
        if (File.Exists(path))
        {
            arguments = $"/select,\"{path}\"";
        }
        else if (Directory.Exists(path))
        {
            arguments = $"\"{path}\"";
        }
        else if (Path.GetDirectoryName(path) is { Length: > 0 } parent && Directory.Exists(parent))
        {
            arguments = $"\"{parent}\"";
        }
        else
        {
            return $"{path} does not exist yet.";
        }

        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(explorer, arguments) { UseShellExecute = false });
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"Explorer could not be opened: {ex.Message}";
        }
    }

    /// <summary>A quick check before the verb's own: numbers in range, names without spaces. The verb proves the rest.</summary>
    private static string? Check(SettingInfo info, string text) => info.Kind switch
    {
        SettingKind.Number => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= info.Min && n <= info.Max ? null : $"a whole number from {info.Min} to {info.Max}",
        SettingKind.Money => text is "unknown" or "null" || decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var m) && m >= 0 ? null : "an amount like 0.25",
        SettingKind.Address => text.Length == 0 ? null : MasterConfig.EndpointProblem(text),
        SettingKind.Variable => text.Length == 0 || (text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && !char.IsAsciiDigit(text[0])) ? null : "a variable's name: letters, digits and _, not its value",
        _ => SafeText.HasControls(text) ? "no control characters" : null,
    };

    private static string AskEachWords(Permissions p, IReadOnlyList<string> problems)
    {
        if (problems.Any(x => x.Contains("permissions section cannot be trusted", StringComparison.Ordinal))
            || !SettingsCatalogue.NamesOnlyExactly(p.Allow, Capability.ModelCall) || !SettingsCatalogue.NamesOnlyExactly(p.Ask, Capability.ModelCall)
            || p.Deny.Any(r => Capabilities.Matches(r, Capability.ModelCall)))
        {
            return "custom (see the lists)";
        }

        return p.Ask.Any(r => Capabilities.IsExactly(r, Capability.ModelCall)) ? "yes" : p.Allow.Any(r => Capabilities.IsExactly(r, Capability.ModelCall)) ? "no" : "no answers allowed";
    }

    private string CurrentValue(SettingInfo info)
    {
        var row = ConfigController.Rows(new Cli(chat.Flags, chat.Config, TextWriter.Null, TextWriter.Null))
            .FirstOrDefault(r => string.Equals(r.Name, info.Name, StringComparison.OrdinalIgnoreCase));
        return row?.Value ?? string.Empty;
    }

    private async Task SetAsync(IAsker asker, SettingInfo info, string current, string value)
    {
        var yes = SettingsCatalogue.NeedsYes(info.Name);
        if (yes)
        {
            var sure = asker.YesNo(
                new Question($"Settings · {info.Label.Trim()}", $"Change {info.Name} from {Shown(current)} to {Shown(value)}?", "It changes what agents may do or where the conversation goes."),
                false);
            if (sure.Reply != Reply.Answered || !sure.Value)
            {
                return;
            }
        }

        if (chat.Run(yes, "config", "set", info.Name, value) == Exit.Ok)
        {
            chat.Reload();
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static string Shown(string value) => value.Length == 0 ? "(empty)" : value;

    private string? PickModel(IAsker asker, string section, string current)
    {
        if (ModelLister.QueryFor(section, chat.Config, out var seat, out var why) is not { } query)
        {
            var typed = asker.Text(new Question("Settings · Model", "The model's name, exactly as the provider writes it.", $"No list to pick from: {why}"), current, t => ModelLister.UsableId(t) ? null : "a model's name has no spaces and only plain characters");
            return typed.Reply == Reply.Answered ? typed.Value : null;
        }

        var gate = new Gate(chat.Config.Permissions, chat.Flags, AuditLog.ForThisUser(), chat.Who, TextWriter.Null, question => chat.ConfirmHook?.Invoke(question), seat);
        IReadOnlyList<ModelInfo> models;
        using (var timeout = chat.Flags.NetTimeoutSeconds == 0 ? new CancellationTokenSource() : new CancellationTokenSource(TimeSpan.FromSeconds(chat.Flags.NetTimeoutSeconds)))
        {
            try
            {
                models = asker.Busy("Asking the provider for its models…", () => ModelLister.Instance.ListAsync(query, gate, timeout.Token));
            }
            catch (Exception ex) when (ex is ChatException or OperationCanceledException)
            {
                var typed = asker.Text(new Question("Settings · Model", "The model's name, exactly as the provider writes it.", $"The list could not be had: {SafeText.Line(ex.Message)}"), current, t => ModelLister.UsableId(t) ? null : "a model's name has no spaces and only plain characters");
                return typed.Reply == Reply.Answered ? typed.Value : null;
            }
        }

        var picked = asker.PickModel(new Question("Settings · Model", "Which model?", $"{models.Count} offered."), models, current);
        return picked.Reply == Reply.Answered ? picked.Value : null;
    }

    /// <summary>Where a Key row's key comes from: the model that answers you, a seat, or the search service (Brave's).</summary>
    private KeyFinder? KeysOf(string section) => section switch
    {
        "main" => Engines.KeysFor(chat.Config),
        "research" => Models.Research.SearchService.Keys(chat.Config),
        _ => chat.Config.Seat(section) is { } seat ? Engines.KeysFor(seat) : null,
    };
    private async Task KeyAsync(IAsker asker, string section)
    {
        var keys = KeysOf(section);
        if (keys is null)
        {
            await chat.Say(Role.System, "This model has no provider yet, so there is no host to save a key for.").ConfigureAwait(false);
            return;
        }

        var typed = asker.Secret(
            new Question("Settings · Key", $"Paste the key for {keys.Host}.", $"It is not shown as you type. DipTick keeps it in Windows Credential Manager, for this Windows account on this PC, and sends it only to {keys.Host}."),
            k => ApiKeys.Check(k.Trim()));
        if (typed.Reply != Reply.Answered)
        {
            return;
        }

        if (chat.Flags.DryRun)
        {
            await chat.Say(Role.System, $"[dry run] would save a key for {keys.Host}; nothing saved.").ConfigureAwait(false);
            return;
        }

        try
        {
            WindowsKeyStore.Instance.Write(keys.Host, typed.Value.Trim());
            await chat.Say(Role.System, keys.KeyVariable.Length > 0
                ? $"Saved the key for {keys.Host}. {section}.keyVariable names {keys.KeyVariable}, so that variable is still what is used; clear it to use the saved key."
                : $"Saved the key for {keys.Host} in Windows Credential Manager.").ConfigureAwait(false);
        }
        catch (KeyStoreException ex)
        {
            await chat.Say(Role.System, ex.Message).ConfigureAwait(false);
        }
    }

    private async Task AskEachAsync(IAsker asker)
    {
        var p = chat.Config.Permissions;
        var words = AskEachWords(p, chat.Config.Problems);
        if (words.StartsWith("custom", StringComparison.Ordinal))
        {
            asker.Tell("Settings · Ask before each answer", "Your permission lists name model.call through a group or *, or deny it, so this cannot be switched on its own. Change the lists below instead.");
            return;
        }

        var now = words == "yes";
        var answer = asker.YesNo(new Question("Settings · Ask before each answer", "Should each answer wait for your yes?", "Most people say no. Say yes on a paid provider to approve every call."), now);
        if (answer.Reply != Reply.Answered || answer.Value == now)
        {
            return;
        }

        chat.SetAskEachAnswer(answer.Value);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task ModelStepsAsync(IAsker asker, string section, bool newSeat)
    {
        var result = new SetupFlow(chat.SetupContext(), asker, section, newSeat).RunModel();
        await chat.Say(Role.System, result.Message).ConfigureAwait(false);
        if (result.Outcome == SetupOutcome.Saved)
        {
            chat.Reload();
        }
    }

    private async Task AddSeatAsync(IAsker asker)
    {
        if (NewSeatId(asker, "Settings · Add a seat") is { } id)
        {
            await ModelStepsAsync(asker, id, newSeat: true).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A new seat's id, asked; null when the table is full (said so) or the person left. The same
    /// question for Add a seat and for Add a model's "at the table".
    /// </summary>
    private string? NewSeatId(IAsker asker, string title)
    {
        var config = chat.Config;
        if (config.Seats.Count >= MasterConfig.MaxSeats)
        {
            asker.Tell(title, $"At most {MasterConfig.MaxSeats} agents sit at the table. Remove one first.");
            return null;
        }

        return SetupFlow.AskSeatId(asker, config, title);
    }

    /// <summary>
    /// Settings, Models, Add a model: the whole catalogue, searchable and
    /// grouped by kind, those that work now first; then where the model goes (to answer you, or a new
    /// seat at the table); then only what that provider needs, its models, and Save.
    /// </summary>
    private async Task AddModelAsync(IAsker asker)
    {
        const string Title = "Settings · Add a model";
        var picked = asker.Pick(
            new Question(Title, "Which provider?", $"Every provider DipTick knows, grouped by kind, those that work now first; checked {Models.Providers.ProviderCatalogue.Checked:yyyy-MM-dd}."),
            SetupFlow.ProviderItems(),
            string.Empty);
        if (picked.Reply != Reply.Answered)
        {
            return;
        }

        var config = chat.Config;
        var section = "main";
        var newSeat = false;
        if (config.MainProvider.Length > 0)
        {
            var where = asker.Choose(
                new Question(Title, "Where does it go?", $"{Engines.DisplayName(config.MainProvider, config.MainEndpoint)} {config.MainModel} answers you now."),
                [new Choice("Answer you", "in place of the model that answers you now"), new Choice("Sit at the table", "a new seat, beside the model that answers you")],
                1);
            if (where.Reply != Reply.Answered)
            {
                return;
            }

            if (where.Value == 1)
            {
                if (NewSeatId(asker, Title) is not { } id)
                {
                    return;
                }

                (section, newSeat) = (id, true);
            }
        }

        var result = new SetupFlow(chat.SetupContext(), asker, section, newSeat).RunModel(picked.Value);
        await chat.Say(Role.System, result.Message).ConfigureAwait(false);
        if (result.Outcome == SetupOutcome.Saved)
        {
            chat.Reload();
        }
    }

    private async Task RemoveSeatAsync(IAsker asker)
    {
        var seats = chat.Config.Chat.Seats;
        if (seats.Count == 0)
        {
            return;
        }

        var which = asker.Choose(new Question("Settings · Remove a seat", "Which seat leaves the table?", "Its section stays in the file, unused."), [.. seats.Select(s => new Choice(s))], null);
        if (which.Reply != Reply.Answered)
        {
            return;
        }

        var sure = asker.YesNo(new Question("Settings · Remove a seat", $"Unseat '{seats[which.Value]}'?", "The conversation stops going to it."), false);
        if (sure.Reply == Reply.Answered && sure.Value && chat.Run(true, "seats", "remove", seats[which.Value]) == Exit.Ok)
        {
            chat.Reload();
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task PasswordAsync(IAsker asker)
    {
        if ((chat.Config.Path ?? chat.SettingsPath) is not { } path || !File.Exists(path))
        {
            await chat.Say(Role.System, "There is no settings file to keep a password in yet. Run setup again, just above, makes one.").ConfigureAwait(false);
            return;
        }

        var first = asker.Secret(new Question("Settings · Password", "A new password, 15 or more characters.", "Only a hash of it is kept (Argon2id), never the password. It is asked before the chat screen opens."), PasswordHash.Refuse);
        if (first.Reply != Reply.Answered)
        {
            return;
        }

        var again = asker.Secret(new Question("Settings · Password", "The same password again."), p => p == first.Value ? null : "the two do not match");
        if (again.Reply != Reply.Answered)
        {
            return;
        }

        if (chat.Flags.DryRun)
        {
            await chat.Say(Role.System, "[dry run] the password is acceptable; nothing written.").ConfigureAwait(false);
            return;
        }

        await chat.Say(Role.System, MasterConfigWriter.SetPasswordHash(path, PasswordHash.Hash(first.Value)) is { } failed ? failed : "Password set.").ConfigureAwait(false);
        chat.Reload();
    }
}
