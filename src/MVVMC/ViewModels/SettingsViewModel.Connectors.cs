using System.Text;
using DipTick.Controllers;
using DipTick.Models;
using DipTick.Models.Chat;
using DipTick.Models.Connectors;
using DipTick.Models.Harness;
using DipTick.Models.Keys;

namespace DipTick.ViewModels;

/// <summary>
/// Settings, Connectors: the connectors added, each with its state and one
/// menu (Connect, Tools, On in this chat, Switch on or off, a header's value, your client's secret, Sign
/// out, Remove); the known ones not added yet, each with what it needs and Add; your own; and importing
/// another app's list. Each change is a <c>diptick connectors</c> verb, run in this process; a secret is
/// asked hidden and kept by the same code the verb uses.
/// </summary>
internal sealed partial class SettingsViewModel
{
    private List<SettingRow> ConnectorRows()
    {
        var list = ConnectorFile.Load(chat.Config);
        var rows = new List<SettingRow>();
        foreach (var connector in list.Connectors)
        {
            rows.Add(new SettingRow(connector.Name, ConnectorsController.State(connector), $"{About(connector)} Change opens its menu: Connect, Tools, On in this chat, and the rest.", RowAction.Connector, Raw: connector.Name));
        }

        foreach (var known in ConnectorCatalogue.All.Where(k => !list.Connectors.Any(c => c.Catalogue == k.Id)))
        {
            rows.Add(new SettingRow(known.Title, "not added", $"Signs in with {known.SignIn}. {known.Needs} (Read {known.Read}: {string.Join(", ", known.Sources)}.)", RowAction.AddConnector, Raw: known.Id));
        }

        rows.Add(new SettingRow("Add your own…", string.Empty, "A service's MCP address (https), or a program on this PC that speaks MCP. A program runs only after your yes to exactly what it runs.", RowAction.AddConnector));
        rows.Add(new SettingRow("Import a list…", string.Empty, "Connectors from another app's list, such as a Claude Code .mcp.json: each shown and imported only with your yes; any secret in it goes to Credential Manager, never into DipTick's file.", RowAction.ImportConnectors));
        if (list.Path is { } path)
        {
            foreach (var problem in list.Problems)
            {
                rows.Add(new SettingRow("A problem in connectors.json", path, problem, RowAction.OpenPath));
            }
        }

        return rows;
    }

    private static string About(Connector connector)
    {
        if (connector.Local)
        {
            var where = connector.Box.On ? $", {connector.Box.Words}" : " as you, with your files, your network and your rights";
            return $"Runs {connector.Command} {string.Join(' ', connector.Args)}".TrimEnd() + $" on this PC{where}.";
        }

        var known = connector.Catalogue is { } id ? ConnectorCatalogue.Find(id) : null;
        return $"{connector.Url}{(known is null ? string.Empty : $" ({known.Title})")}.";
    }

    /// <summary>One connector's menu. Runs on a worker thread.</summary>
    private async Task ConnectorMenuAsync(IAsker asker, string name)
    {
        if (ConnectorFile.Load(chat.Config).Find(name) is not { } connector)
        {
            return;
        }

        var on = chat.ConnectorsOn.Contains(name, StringComparer.Ordinal);
        var actions = new List<(Choice Choice, Func<Task> Run)>();
        if (!connector.Enabled)
        {
            actions.Add((new Choice("Switch on", connector.Local ? "shows exactly what it runs, and asks your yes" : "its tools can be offered again"), () => Verb("connectors", "enable", name)));
        }
        else
        {
            actions.Add((new Choice("Connect", connector.Local ? "starts it and lists its tools" : "signs in in your browser when it asks, and lists its tools"), () => LongVerbAsync($"connecting to {name}…", "connectors", "signin", name)));
            actions.Add((new Choice("Tools…", "each of its tools: Allowed, Ask me first or Never"), () => ToolsAsync(asker, connector)));
            actions.Add((new Choice(on ? "Off in this chat" : "On in this chat", on ? "its tools are no longer offered here" : "agents here may call its tools, each through your permissions"), () => chat.SetConnectorAsync(name, !on)));
            actions.Add((new Choice("Switch off", "no chat is offered its tools; its sign-in is kept"), () => Verb("connectors", "disable", name)));
        }

        if (connector.Local)
        {
            actions.Add((new Choice("Sandbox…", $"now {connector.Box.Brief}"), () => SandboxMenuAsync(asker, connector)));
        }

        if (!connector.Local)
        {
            actions.Add((new Choice("Save a header's value…", "an API token, say: kept in Credential Manager, never in the file"), () => SaveSecretAsync(asker, connector, header: true)));
            if (connector.OAuth?.ClientId is not null)
            {
                actions.Add((new Choice("Save your client's secret…", "for an app registration that has one"), () => SaveSecretAsync(asker, connector, header: false)));
            }

            actions.Add((new Choice("Sign out", "its tokens leave Credential Manager"), () => Verb("connectors", "signout", name)));
        }

        actions.Add((new Choice("Remove", connector.Local ? "its sandbox goes, and your folders it was given are taken back" : "it leaves the list, with its sign-in and saved values"), () => Verb("connectors", "remove", name)));
        var picked = asker.Choose(new Question($"Settings · Connectors · {name}", ConnectorsController.State(connector), About(connector)), [.. actions.Select(a => a.Choice)], 0);
        if (picked.Reply == Reply.Answered)
        {
            await actions[picked.Value].Run().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Where a local connector runs (<c>connectors sandbox</c>): its network, a folder it may read or read and
    /// write, no folders, or as you. A change needs its yes again, so the yes is asked at once.
    /// </summary>
    private async Task SandboxMenuAsync(IAsker asker, Connector connector)
    {
        var title = $"Settings · Connectors · {connector.Name} · Sandbox";
        var box = connector.Box;
        var choices = new List<(Choice Choice, string[]? Args)>
        {
            (new Choice("Network…", $"now {box.NetworkWords}"), null),
            (new Choice("Add a folder it may read…", "access to that folder alone"), ["--read"]),
            (new Choice("Add a folder it may read and write…", "access to that folder alone"), ["--write"]),
        };
        if (box.Read.Count + box.Write.Count > 0)
        {
            choices.Add((new Choice("No folders", "takes back each folder it was given"), ["--no-folders"]));
        }

        choices.Add(box.On
            ? (new Choice("Run it as you", "no sandbox: your files, your network and your rights"), ["--off"])
            : (new Choice("Give it a sandbox of its own", (box with { On = true }).Words), ["--on"]));
        var picked = asker.Choose(new Question(title, $"It {(box.On ? $"runs {box.Words}" : box.Words)}.", "A connector's sandbox is an AppContainer of its own. Windows gives it a network by kind, not by address, and a folder by that folder's own access list."), [.. choices.Select(c => c.Choice)], 0);
        if (picked.Reply != Reply.Answered)
        {
            return;
        }

        var args = new List<string> { "connectors", "sandbox", connector.Name };
        var (_, extra) = choices[picked.Value];
        if (extra is null)
        {
            var networks = new[]
            {
                (ConnectorSandbox.None, "No network", "it reaches nothing"),
                (ConnectorSandbox.Internet, "The internet", "not this PC's own networks: a program that fetches packages, or calls a service"),
                (ConnectorSandbox.Local, "Home and work networks", "not the internet: a server on your network"),
                (ConnectorSandbox.Both, "Both", "the internet and your home and work networks"),
            };
            var kind = asker.Choose(new Question(title, "What may it reach?", "Windows never lets a sandbox reach programs on this PC itself."), [.. networks.Select(n => new Choice(n.Item2, n.Item3))], Math.Max(0, Array.FindIndex(networks, n => n.Item1 == box.Network)));
            if (kind.Reply != Reply.Answered)
            {
                return;
            }

            args.AddRange(["--network", networks[kind.Value].Item1]);
        }
        else if (extra[0] is "--read" or "--write")
        {
            var folder = asker.Text(new Question(title, "Which folder?", "Its full path. Never DipTick's own settings or data."), string.Empty, text => ConnectorPlace.FolderProblem(text.Trim().Trim('"'), chat.Config.Path is { } settings ? Path.GetDirectoryName(Path.GetFullPath(settings)) : null));
            if (folder.Reply != Reply.Answered)
            {
                return;
            }

            args.AddRange([extra[0], folder.Value.Trim().Trim('"')]);
        }
        else
        {
            args.AddRange(extra);
        }

        if (chat.Run(false, [.. args]) == Exit.Ok)
        {
            // Its yes covered where it ran: shown again, and asked, now.
            await Verb("connectors", "enable", connector.Name).ConfigureAwait(false);
        }
    }

    /// <summary>A connector's tools, and a rule for one: Allowed, Ask me first or Never (<c>policy set mcp.call:&lt;c&gt;/&lt;t&gt;</c>).</summary>
    private async Task ToolsAsync(IAsker asker, Connector connector)
    {
        IReadOnlyList<ModelContextProtocol.Client.McpClientTool> tools;
        try
        {
            tools = await ConnectorHub.ToolsAsync(chat.Config, connector, interactive: false, say: null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ConnectorException ex)
        {
            asker.Tell($"Settings · Connectors · {connector.Name}", $"{ex.Message}.");
            return;
        }

        if (tools.Count == 0)
        {
            asker.Tell($"Settings · Connectors · {connector.Name}", "It offers no tools.");
            return;
        }

        var rows = tools.Select(t => (Tool: t, Rule: Policy.Decide(Capability.McpCall, ConnectorTools.Target(connector.Name, t.Name), chat.Config.Permissions, chat.Flags with { DryRun = false }).Verdict)).ToList();
        var pickedTool = asker.Choose(
            new Question($"Settings · Connectors · {connector.Name} · Tools", "Which tool's rule?", "Each call of a tool is mcp.call:<connector>/<tool>; the rule decides whether it goes, asks you first, or never goes."),
            [.. rows.Select(r => new Choice($"{r.Tool.Name} · {RuleWords(r.Rule)}", Clip(SafeText.Line(r.Tool.Description))))],
            0);
        if (pickedTool.Reply != Reply.Answered)
        {
            return;
        }

        var (tool, _) = rows[pickedTool.Value];
        var verdicts = new[] { ("allow", "Allowed", "it goes without asking"), ("ask", "Ask me first", "you are asked each time"), ("deny", "Never", "it never goes") };
        var pickedRule = asker.Choose(new Question($"Settings · Connectors · {connector.Name} · {tool.Name}", "What should happen when an agent calls it?", Clip(SafeText.Line(tool.Description))), [.. verdicts.Select(v => new Choice(v.Item2, v.Item3))], 1);
        if (pickedRule.Reply == Reply.Answered)
        {
            chat.Run(false, "policy", "set", $"mcp.call:{ConnectorTools.Target(connector.Name, tool.Name)}", verdicts[pickedRule.Value].Item1);
            chat.Reload();
        }
    }

    /// <summary>Adds a known connector, asking only what it needs, or one of your own; then offers to connect it.</summary>
    private async Task AddConnectorAsync(IAsker asker, string id)
    {
        if (id.Length == 0)
        {
            await AddOwnConnectorAsync(asker).ConfigureAwait(false);
            return;
        }

        if (ConnectorCatalogue.Find(id) is not { } known)
        {
            return;
        }

        var title = $"Settings · Connectors · {known.Title}";
        var args = new List<string> { "connectors", "add", id, "--from", id };
        foreach (var part in known.Parts)
        {
            var value = asker.Text(new Question(title, $"Its {part}?", known.Needs), string.Empty, text => text.Trim().Length == 0 ? $"the {part}, as your organisation's admin page shows it" : null);
            if (value.Reply != Reply.Answered)
            {
                return;
            }

            args.AddRange([$"--{part}", value.Value.Trim()]);
        }

        string? secret = null;
        if (known.NeedsClientId)
        {
            var clientId = asker.Text(new Question(title, "Your app registration's client ID?", $"{known.SignIn}. {known.Needs}"), string.Empty, text => text.Trim().Length == 0 ? "the client ID your app registration shows" : null);
            if (clientId.Reply != Reply.Answered)
            {
                return;
            }

            args.AddRange(["--client-id", clientId.Value.Trim()]);
            var typed = asker.Secret(new Question(title, "Its client secret, if it has one (empty for none)?", "Kept in Windows Credential Manager, and only ever sent to the service's sign-in."), _ => null);
            if (typed.Reply != Reply.Answered)
            {
                return;
            }

            secret = typed.Value.Trim().Length > 0 ? typed.Value.Trim() : null;
        }

        if (chat.Run(false, [.. args]) != Exit.Ok)
        {
            return;
        }

        if (secret is not null && !chat.Flags.DryRun)
        {
            try
            {
                ConnectorSecrets.SetClientSecret(id, secret);
            }
            catch (KeyStoreException ex)
            {
                await chat.Say(Role.System, ex.Message).ConfigureAwait(false);
            }
        }

        await OfferConnectAsync(asker, id, title).ConfigureAwait(false);
    }

    private async Task AddOwnConnectorAsync(IAsker asker)
    {
        const string Title = "Settings · Connectors · Add your own";
        var kind = asker.Choose(new Question(Title, "What is it?"), [new Choice("A service's address", "its MCP address, https"), new Choice("A program on this PC", "that speaks MCP over its standard input and output")], 0);
        if (kind.Reply != Reply.Answered)
        {
            return;
        }

        List<string> args;
        string name;
        if (kind.Value == 0)
        {
            var address = asker.Text(new Question(Title, "Its address?", "https only; http only to this PC."), "https://", text => Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && ConnectorHub.RemoteProblem(uri) is null ? null : "an https address");
            if (address.Reply != Reply.Answered)
            {
                return;
            }

            var host = new Uri(address.Value.Trim()).Host.Split('.').FirstOrDefault(p => p is not ("mcp" or "www" or "api")) ?? "connector";
            if (AskName(asker, Title, ConnectorFile.NameFrom(host)) is not { } chosen)
            {
                return;
            }

            name = chosen;
            args = ["connectors", "add", name, "--url", address.Value.Trim()];
        }
        else
        {
            var program = asker.Text(new Question(Title, "The program?", "Its full path, or a name on PATH, like npx or uvx."), string.Empty, text => text.Trim().Length == 0 ? "the program to run" : null);
            if (program.Reply != Reply.Answered)
            {
                return;
            }

            var arguments = asker.Text(new Question(Title, "Its arguments, as you would type them (empty for none)?", "A word in quotes stays one argument."), string.Empty, _ => null);
            if (arguments.Reply != Reply.Answered)
            {
                return;
            }

            if (AskName(asker, Title, ConnectorFile.NameFrom(Path.GetFileNameWithoutExtension(program.Value.Trim()))) is not { } chosen)
            {
                return;
            }

            // It runs in a sandbox of its own; what it may reach is asked now, its folders later (Sandbox…).
            var networks = new[]
            {
                (ConnectorSandbox.None, "No network", "it reaches nothing: most programs that work on files"),
                (ConnectorSandbox.Internet, "The internet", "npx, uvx and the like fetch their package; a program that calls a service"),
                (ConnectorSandbox.Local, "Home and work networks", "a server on your own network, not the internet"),
                (ConnectorSandbox.Both, "Both", "the internet and your home and work networks"),
            };
            var reach = asker.Choose(new Question(Title, "What may it reach?", "It runs in a sandbox of its own, with none of your folders until you give it one (Sandbox… in its menu)."), [.. networks.Select(n => new Choice(n.Item2, n.Item3))], 0);
            if (reach.Reply != Reply.Answered)
            {
                return;
            }

            name = chosen;
            args = ["connectors", "add", name, "--command", program.Value.Trim(), "--network", networks[reach.Value].Item1];
            foreach (var argument in Split(arguments.Value))
            {
                args.AddRange(["--arg", argument]);
            }
        }

        if (chat.Run(false, [.. args]) != Exit.Ok)
        {
            return;
        }

        if (kind.Value == 1)
        {
            // A program runs only after the yes that shows exactly what runs.
            chat.Run(false, "connectors", "enable", name);
            return;
        }

        await OfferConnectAsync(asker, name, Title).ConfigureAwait(false);
    }

    private static string? AskName(IAsker asker, string title, string preset)
    {
        var typed = asker.Text(new Question(title, "What should it be called?", "1 to 32 of a-z, 0-9 and -: it names its tools and its rules."), preset, text => ConnectorFile.NameProblem(text.Trim()));
        return typed.Reply == Reply.Answered ? typed.Value.Trim() : null;
    }

    private async Task OfferConnectAsync(IAsker asker, string name, string title)
    {
        var now = asker.YesNo(new Question(title, "Connect it now?", "A service opens its sign-in in your browser when it asks for one."), true);
        if (now.Reply == Reply.Answered && now.Value)
        {
            await LongVerbAsync($"connecting to {name}…", "connectors", "signin", name).ConfigureAwait(false);
        }
    }

    /// <summary>Another app's list, imported entry by entry: <c>connectors import</c>, which asks for each.</summary>
    private void ImportConnectors(IAsker asker)
    {
        var path = asker.Text(new Question("Settings · Connectors · Import a list", "Which file?", "A Claude Code .mcp.json, or any file with an \"mcpServers\" object. Each connector is shown and asked about."), string.Empty, text => File.Exists(text.Trim().Trim('"')) ? null : "a file that is there");
        if (path.Reply == Reply.Answered)
        {
            chat.Run(false, "connectors", "import", path.Value.Trim().Trim('"'));
        }
    }

    /// <summary>A header's value or your client's secret, asked hidden and kept in Credential Manager by the code <c>connectors secret</c> uses.</summary>
    private Task SaveSecretAsync(IAsker asker, Connector connector, bool header)
    {
        SaveSecret(asker, connector, header);
        return Task.CompletedTask;
    }

    private void SaveSecret(IAsker asker, Connector connector, bool header)
    {
        var title = $"Settings · Connectors · {connector.Name}";
        var headerName = "client-secret";
        if (header)
        {
            var named = asker.Text(new Question(title, "Which header?", "Its name, like Authorization or X-Api-Key."), "Authorization", text => text.Trim().Length is > 0 and <= 64 && text.Trim().All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? null : "letters, digits and -");
            if (named.Reply != Reply.Answered)
            {
                return;
            }

            headerName = named.Value.Trim();
        }

        var value = asker.Secret(new Question(title, header ? $"Its {headerName} value?" : "Your client's secret?", "Not shown as you type; kept in Windows Credential Manager and only ever sent to this connector."), text => text.Trim().Length == 0 ? "something to save" : null);
        if (value.Reply != Reply.Answered || chat.Flags.DryRun)
        {
            return;
        }

        try
        {
            if (header)
            {
                ConnectorSecrets.SetHeader(connector.Name, headerName, value.Value.Trim());
                if (!connector.SecretHeaders.Contains(headerName, StringComparer.OrdinalIgnoreCase) && ConnectorFile.Load(chat.Config) is { Path: { } path } list)
                {
                    var changed = list.Connectors.Select(c => c.Name == connector.Name
                        ? c with { SecretHeaders = [.. c.SecretHeaders, headerName], Headers = c.Headers.Where(h => !h.Key.Equals(headerName, StringComparison.OrdinalIgnoreCase)).ToDictionary(h => h.Key, h => h.Value, StringComparer.Ordinal) }
                        : c).ToList();
                    ConnectorFile.Write(path, changed);
                }
            }
            else
            {
                ConnectorSecrets.SetClientSecret(connector.Name, value.Value.Trim());
            }

            asker.Tell(title, "Saved in Windows Credential Manager.");
        }
        catch (Exception ex) when (ex is KeyStoreException or IOException or UnauthorizedAccessException)
        {
            asker.Tell(title, ex.Message);
        }
    }

    private Task Verb(params string[] args)
    {
        chat.Run(false, args);
        return Task.CompletedTask;
    }

    /// <summary>A verb that may wait on your browser: the status line says so while it runs.</summary>
    private async Task LongVerbAsync(string working, params string[] args)
    {
        chat.Working = working;
        try
        {
            await Task.Run(() => chat.Run(false, args)).ConfigureAwait(false);
        }
        finally
        {
            chat.Working = null;
        }
    }

    private static string RuleWords(Verdict verdict) => verdict switch
    {
        Verdict.Allow => "Allowed",
        Verdict.Ask => "Ask me first",
        _ => "Never",
    };

    private static string Clip(string text) => text.Length <= 200 ? text : text[..200] + "…";

    /// <summary>Words as a person types them: split at spaces, a quoted part kept whole.</summary>
    private static List<string> Split(string text)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var quoted = false;
        var any = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
                continue;
            }

            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any)
                {
                    words.Add(word.ToString());
                    word.Clear();
                    any = false;
                }

                continue;
            }

            word.Append(c);
            any = true;
        }

        if (any)
        {
            words.Add(word.ToString());
        }

        return words;
    }
}
