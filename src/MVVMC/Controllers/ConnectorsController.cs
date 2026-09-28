using System.Globalization;
using DipTick.Models;
using DipTick.Models.Connectors;
using DipTick.Models.Harness;
using DipTick.Models.Keys;

namespace DipTick.Controllers;

/// <summary>
/// <c>diptick connectors</c>: the connectors DipTick knows and those you added,
/// with each one's state; adding one (an address, a program on this PC, or a known one), importing a
/// Claude Code <c>.mcp.json</c>, switching one on (a local one after your yes to its exact command),
/// testing it, its tools, signing in and out, a header's secret value, and removing it.
/// </summary>
internal static class ConnectorsController
{
    private const string Usage = "usage: diptick connectors [list] | add <name> --url <address> [--header K=V]... [--client-id <id>] [--scopes <s>] [--callback-port <n>] [--sse] [--allow-private-network] | add <name> --command <program> [--arg <a>]... [--env K=V]... [--network none|internet|local|both] | add <name> --from atlassian|clickup|gmail|outlook [--tenant <id>] [--client-id <id>] | import <file> | enable <name> | disable <name> | test <name> | tools <name> | signin <name> | signout <name> | secret <name> <header>|client-secret | sandbox <name> [--network none|internet|local|both] [--read <folder>]... [--write <folder>]... [--no-folders] [--on|--off] | remove <name>";

    public static int Run(IReadOnlyList<string> args, Cli cli)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(cli);
        var sub = args.Count > 1 ? args[1] : "list";
        var words = new VerbArgs(args, 2, cli.LiteralFrom);
        return sub switch
        {
            "list" => List(cli, words),
            "add" => Add(cli, words),
            "import" => Import(cli, words),
            "enable" => Enable(cli, words),
            "disable" => Disable(cli, words),
            "test" => Test(cli, words, signIn: false),
            "signin" => Test(cli, words, signIn: true),
            "tools" => Tools(cli, words),
            "signout" => SignOut(cli, words),
            "secret" => Secret(cli, words),
            "sandbox" => Sandbox(cli, words),
            "remove" => Remove(cli, words),
            _ => cli.Fail(Usage, Exit.Usage),
        };
    }

    /// <summary>A connector's state in words: off, waiting for a yes, needing a sign-in, or ready; with what is open now.</summary>
    public static string State(Connector connector)
    {
        ArgumentNullException.ThrowIfNull(connector);
        if (!connector.Enabled)
        {
            return connector.Local && connector.Consent is null ? "off: runs a program on this PC and waits for your yes" : "off";
        }

        if (ConnectorHub.LocalProblem(connector) is { } problem)
        {
            return problem;
        }

        if (!connector.Local && !connector.Headers.ContainsKey("Authorization") && !connector.SecretHeaders.Contains("Authorization", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (ConnectorSecrets.Tokens(connector.Name) is null)
                {
                    return ConnectorHub.Describe(connector.Name) is { } open ? $"connected: {open}" : "on; signs in when it asks (Connect)";
                }
            }
            catch (KeyStoreException ex)
            {
                return ex.Message;
            }
        }

        return ConnectorHub.Describe(connector.Name) is { } live ? $"connected: {live}" : "on";
    }

    private static int List(Cli cli, VerbArgs words)
    {
        if (words.Problem is not null || words.Words().Count > 0)
        {
            return cli.Fail(Usage, Exit.Usage);
        }

        var list = ConnectorFile.Load(cli.Config);
        var known = ConnectorCatalogue.All.Where(k => !list.Connectors.Any(c => c.Catalogue == k.Id)).ToList();
        if (cli.WriteJson(new
        {
            file = list.Path,
            connectors = list.Connectors.Select(c => new
            {
                name = c.Name,
                type = c.Type,
                url = c.Url,
                command = c.Command,
                args = c.Args,
                enabled = c.Enabled,
                state = State(c),
                catalogue = c.Catalogue,
                sandbox = c.Local ? new { on = c.Box.On, network = c.Box.Network, read = c.Box.Read, write = c.Box.Write } : null,
            }),
            known = known.Select(k => new { id = k.Id, title = k.Title, url = k.Url, signIn = k.SignIn, needs = k.Needs, sources = k.Sources, read = k.Read }),
            problems = list.Problems,
        }))
        {
            return Exit.Ok;
        }

        if (list.Connectors.Count == 0)
        {
            cli.Note("No connectors added yet.");
        }

        foreach (var c in list.Connectors)
        {
            var where = c.Local ? $"{c.Command} {string.Join(' ', c.Args)}".Trim() : c.Url ?? string.Empty;
            var box = c.Local ? c.Box.On ? $"  · sandboxed, {c.Box.NetworkWords}" : "  · runs as you" : string.Empty;
            cli.Data(SafeText.Line($"  {c.Name,-16} {(c.Local ? "local " : c.Type == ConnectorFile.Sse ? "sse   " : "remote")}  {where}  · {State(c)}{box}"));
        }

        if (known.Count > 0)
        {
            cli.Data(string.Empty);
            cli.Data("Known, not added ('diptick connectors add <name> --from <id>'):");
            foreach (var k in known)
            {
                cli.Data(SafeText.Line($"  {k.Id,-10} {k.Title}: {k.SignIn}. {k.Needs}"));
            }
        }

        foreach (var problem in list.Problems)
        {
            cli.Note(problem);
        }

        return Exit.Ok;
    }

    private static int Add(Cli cli, VerbArgs words)
    {
        var url = words.Option("--url");
        var command = words.Option("--command");
        var from = words.Option("--from");
        var tenant = words.Option("--tenant");
        var clientId = words.Option("--client-id");
        var scopes = words.Option("--scopes");
        var portText = words.Option("--callback-port");
        var sse = words.Switch("--sse");
        var allowPrivate = words.Switch("--allow-private-network");
        var network = words.Option("--network");
        var headerTexts = words.All("--header");
        var argTexts = words.All("--arg");
        var envTexts = words.All("--env");
        var rest = words.Words();
        if (words.Problem is not null || rest.Count != 1 || new[] { url, command, from }.Count(x => x is not null) != 1 || (network is not null && command is null))
        {
            return cli.Fail(Usage, Exit.Usage);
        }

        if (network is not null && !ConnectorSandbox.Networks.Contains(network, StringComparer.Ordinal))
        {
            return cli.Fail($"--network takes {string.Join(", ", ConnectorSandbox.Networks)}.", Exit.Usage);
        }

        var name = rest[0];
        if (ConnectorFile.NameProblem(name) is { } bad)
        {
            return cli.Fail($"{bad}.", Exit.Usage);
        }

        if (NoFile(cli, out var list) is { } noFile)
        {
            return noFile;
        }

        if (list.Find(name) is not null)
        {
            return cli.Fail($"there is already a connector called {name}; 'diptick connectors remove {name}' first, or choose another name.", Exit.Usage);
        }

        int? port = null;
        if (portText is not null)
        {
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p is < 1024 or > 65535)
            {
                return cli.Fail("--callback-port takes a port from 1024 to 65535.", Exit.Usage);
            }

            port = p;
        }

        Connector connector;
        if (command is not null)
        {
            if (Pairs(envTexts) is not { } env)
            {
                return cli.Fail("--env takes NAME=value.", Exit.Usage);
            }

            connector = new Connector(name, ConnectorFile.Stdio, command, argTexts, env, null, new Dictionary<string, string>(), [], null, null, Enabled: false, Consent: null, AllowPrivateNetwork: false, Catalogue: null, ConnectorSandbox.Default with { Network = network ?? ConnectorSandbox.None });
        }
        else
        {
            string? catalogue = null;
            if (from is not null)
            {
                if (ConnectorCatalogue.Find(from) is not { } known)
                {
                    return cli.Fail($"DipTick knows {string.Join(", ", ConnectorCatalogue.All.Select(k => k.Id))}; not {SafeText.Line(from)}.", Exit.Usage);
                }

                var parts = new Dictionary<string, string>(StringComparer.Ordinal);
                if (tenant is not null)
                {
                    parts["tenant"] = tenant;
                }

                var (address, problem) = ConnectorCatalogue.Address(known, parts);
                if (address is null)
                {
                    return cli.Fail($"{problem}.", Exit.Usage);
                }

                if (known.NeedsClientId && clientId is null)
                {
                    return cli.Fail($"{known.Title} signs in only through your own app registration: give its --client-id. {known.Needs}", Exit.Usage);
                }

                url = address;
                catalogue = known.Id;
            }

            // An address that names a variable is checked when it is used, once the variable is read.
            if (!url!.Contains("${", StringComparison.Ordinal) && (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || ConnectorHub.RemoteProblem(endpoint) is not null))
            {
                return cli.Fail($"--url {SafeText.Line(url)}: an https address (http only to this PC).", Exit.Usage);
            }

            if (Pairs(headerTexts) is not { } headers)
            {
                return cli.Fail("--header takes Name=value.", Exit.Usage);
            }

            if (headers.FirstOrDefault(h => ConnectorFile.LooksSecret(h.Key, h.Value)) is { Key: { } secretHeader })
            {
                return cli.Fail($"--header {secretHeader} holds a secret, which would sit in connectors.json. Name a variable (\"Bearer ${{MY_TOKEN}}\"), or add it without the header and run 'diptick connectors secret {name} {secretHeader}'.", Exit.Usage);
            }

            var oauth = clientId is not null || port is not null || scopes is not null ? new ConnectorOAuth(clientId, port, scopes) : null;
            connector = new Connector(name, sse ? ConnectorFile.Sse : ConnectorFile.Http, null, [], new Dictionary<string, string>(), url, headers, [], oauth, null, Enabled: true, Consent: null, AllowPrivateNetwork: allowPrivate, Catalogue: catalogue);
        }

        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would add the connector {name}; nothing written.");
            return Exit.Ok;
        }

        if (Save(cli, list, [.. list.Connectors, connector]) is { } failed)
        {
            return failed;
        }

        if (!cli.WriteJson(new { added = name, enabled = connector.Enabled }))
        {
            cli.Note(connector.Local
                ? $"Added {name}, {connector.Box.Words}. It runs a program on this PC, so it is off until 'diptick connectors enable {name}' shows you what runs and you say yes."
                : $"Added {name}. 'diptick connectors signin {name}' signs in; 'diptick chat connectors add {name}' puts it in a chat.");
        }

        return Exit.Ok;
    }

    private static int Import(Cli cli, VerbArgs words)
    {
        var rest = words.Words();
        if (words.Problem is not null || rest.Count != 1)
        {
            return cli.Fail(Usage, Exit.Usage);
        }

        if (NoFile(cli, out var list) is { } noFile)
        {
            return noFile;
        }

        string text;
        try
        {
            text = File.ReadAllText(rest[0]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return cli.Fail($"{SafeText.Line(rest[0])} could not be read: {SafeText.Line(ex.Message)}");
        }

        var incoming = ConnectorFile.Imported(text, SafeText.Line(rest[0]));
        foreach (var problem in incoming.Problems)
        {
            cli.Note(problem);
        }

        var kept = new List<Connector>(list.Connectors);
        var added = new List<string>();
        foreach (var entry in incoming.Entries)
        {
            var c = entry.Connector;
            if (kept.Any(k => k.Name == c.Name))
            {
                cli.Note($"{c.Name}: there is already a connector of that name; not imported.");
                continue;
            }

            var shown = c.Local ? $"{c.Command} {string.Join(' ', c.Args)}".Trim() : c.Url;
            var secrets = entry.Secrets.Count == 0 && entry.ClientSecret is null ? string.Empty : $" Its {string.Join(", ", entry.Secrets.Keys.Concat(entry.ClientSecret is null ? [] : ["client secret"]))} go to Windows Credential Manager, not the file.";
            var answer = cli.Confirm($"Import {c.Name}{(entry.Renamed ? $" (named {SafeText.Line(entry.Original)} there)" : string.Empty)}: {SafeText.Line(shown ?? string.Empty)}?{secrets}{(c.Local ? " It stays off until you enable it." : string.Empty)}");
            if (answer != Answer.Yes)
            {
                continue;
            }

            if (!cli.Flags.DryRun)
            {
                try
                {
                    foreach (var (header, value) in entry.Secrets)
                    {
                        ConnectorSecrets.SetHeader(c.Name, header, value);
                    }

                    if (entry.ClientSecret is { } secret)
                    {
                        ConnectorSecrets.SetClientSecret(c.Name, secret);
                    }
                }
                catch (KeyStoreException ex)
                {
                    cli.Note($"{c.Name}: {ex.Message} Not imported.");
                    continue;
                }
            }

            kept.Add(c);
            added.Add(c.Name);
        }

        if (added.Count == 0)
        {
            cli.Note("Nothing imported.");
            return Exit.Ok;
        }

        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would import {string.Join(", ", added)}; nothing written.");
            return Exit.Ok;
        }

        if (Save(cli, list, kept) is { } failed)
        {
            return failed;
        }

        if (!cli.WriteJson(new { imported = added }))
        {
            cli.Note($"Imported {string.Join(", ", added)}.");
        }

        return Exit.Ok;
    }

    /// <summary>
    /// <c>enable</c>: a remote connector on; a local one on only after you are shown exactly what runs (the
    /// program as found, every argument, each variable's name) and say yes, which is kept as its hash.
    /// </summary>
    private static int Enable(Cli cli, VerbArgs words)
    {
        if (One(cli, words) is not { } found)
        {
            return Exit.Usage;
        }

        var (list, connector) = found;

        string? consent = connector.Consent;
        if (connector.Local)
        {
            var (launch, problem) = Launch.For(connector, Environment.GetEnvironmentVariable);
            if (launch is null)
            {
                return cli.Fail($"{connector.Name} cannot run: {problem}.");
            }

            if (ConnectorHub.SandboxProblem(connector, launch.Program, SettingsFolder(cli)) is { } boxed)
            {
                return cli.Fail($"{connector.Name} cannot run: {boxed}.");
            }

            var shownArgs = connector.Args.Count == 0 ? "no arguments" : string.Join(" ", connector.Args.Select(a => $"[{a}]"));
            var shownEnv = connector.Env.Count == 0 ? "no variables of its own" : "its variables " + string.Join(", ", connector.Env.Keys);
            cli.Note($"{connector.Name} runs {launch.Program} with {shownArgs}, and {shownEnv}.");
            cli.Note(connector.Box.On
                ? $"It runs {connector.Box.Words}; nothing else of yours. DipTick ends it, and whatever it starts, when it quits."
                : $"It {connector.Box.Words} ('diptick connectors sandbox {connector.Name} --on' gives it a sandbox of its own). DipTick ends it, and whatever it starts, when it quits.");
            var answer = cli.Confirm($"Let {connector.Name} run on this PC?");
            if (answer != Answer.Yes)
            {
                return cli.NotConfirmed(answer, $"enabling {connector.Name}");
            }

            consent = ConnectorFile.ConsentHash(launch.Program, connector);
        }

        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would switch {connector.Name} on; nothing written.");
            return Exit.Ok;
        }

        if (Save(cli, list, Replaced(list, connector with { Enabled = true, Consent = consent })) is { } failed)
        {
            return failed;
        }

        cli.Note($"{connector.Name} is on.");
        return Exit.Ok;
    }

    private static int Disable(Cli cli, VerbArgs words)
    {
        if (One(cli, words) is not { } found)
        {
            return Exit.Usage;
        }

        var (list, connector) = found;

        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would switch {connector.Name} off; nothing written.");
            return Exit.Ok;
        }

        ConnectorHub.CloseAsync(connector.Name).GetAwaiter().GetResult();
        if (Save(cli, list, Replaced(list, connector with { Enabled = false })) is { } failed)
        {
            return failed;
        }

        cli.Note($"{connector.Name} is off; its sign-in is kept.");
        return Exit.Ok;
    }

    /// <summary><c>test</c>: connects and lists its tools; <c>signin</c> does the same, signing in in your browser when it asks.</summary>
    private static int Test(Cli cli, VerbArgs words, bool signIn)
    {
        if (One(cli, words) is not { } found)
        {
            return Exit.Usage;
        }

        var (_, connector) = found;

        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would connect to {connector.Name}; nothing sent.");
            return Exit.Ok;
        }

        cli.StopsCleanly();
        try
        {
            var tools = ConnectorHub.ToolsAsync(cli.Config, connector, interactive: signIn, say: cli.Note, cli.Stop).GetAwaiter().GetResult();
            var dropped = ConnectorHub.Dropped(connector.Name);
            if (!cli.WriteJson(new { connector = connector.Name, server = ConnectorHub.Describe(connector.Name), tools = tools.Select(t => t.Name), dropped }))
            {
                cli.Note(string.Create(CultureInfo.InvariantCulture, $"{(signIn ? "Signed in to" : "Connected to")} {connector.Name} ({ConnectorHub.Describe(connector.Name)}): {tools.Count} tools{(dropped > 0 ? $", {dropped} more left out" : string.Empty)}."));
            }

            return Exit.Ok;
        }
        catch (ConnectorException ex)
        {
            return cli.Fail($"{ex.Message}.");
        }
        catch (OperationCanceledException)
        {
            return cli.Fail("stopped.");
        }
    }

    /// <summary><c>tools</c>: each tool with what the rules say of it for everyone: allowed, ask first, or never.</summary>
    private static int Tools(Cli cli, VerbArgs words)
    {
        if (One(cli, words) is not { } found)
        {
            return Exit.Usage;
        }

        var (_, connector) = found;

        try
        {
            var tools = ConnectorHub.ToolsAsync(cli.Config, connector, interactive: false, say: null, cli.Stop).GetAwaiter().GetResult();
            var rows = tools.Select(t => (Tool: t, Rule: Policy.Decide(Capability.McpCall, ConnectorTools.Target(connector.Name, t.Name), cli.Config.Permissions, cli.Flags with { DryRun = false }).Verdict)).ToList();
            if (cli.WriteJson(new { connector = connector.Name, tools = rows.Select(r => new { name = r.Tool.Name, title = r.Tool.Title, rule = Words(r.Rule), description = SafeText.Line(r.Tool.Description) }) }))
            {
                return Exit.Ok;
            }

            foreach (var (tool, rule) in rows)
            {
                var about = SafeText.Line(tool.Description);
                cli.Data(SafeText.Line($"  {tool.Name,-32} {Words(rule),-14} {(about.Length > 90 ? about[..90] + "…" : about)}"));
            }

            cli.Note($"'diptick policy set mcp.call:{connector.Name}/<tool> allow|ask|deny' sets one; mcp.call:{connector.Name}/* all of them.");
            return Exit.Ok;
        }
        catch (ConnectorException ex)
        {
            return cli.Fail($"{ex.Message}.");
        }
    }

    private static int SignOut(Cli cli, VerbArgs words)
    {
        if (One(cli, words) is not { } found)
        {
            return Exit.Usage;
        }

        var (_, connector) = found;

        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would sign out of {connector.Name}; nothing removed.");
            return Exit.Ok;
        }

        ConnectorHub.CloseAsync(connector.Name).GetAwaiter().GetResult();
        try
        {
            cli.Note(ConnectorSecrets.ClearTokens(connector.Name) ? $"Signed out of {connector.Name}: its tokens are gone from Credential Manager." : $"{connector.Name} was not signed in.");
            return Exit.Ok;
        }
        catch (KeyStoreException ex)
        {
            return cli.Fail(ex.Message);
        }
    }

    /// <summary><c>secret</c>: a header's value, or your client's secret, kept in Credential Manager: typed hidden, or one line of stdin.</summary>
    private static int Secret(Cli cli, VerbArgs words)
    {
        var rest = words.Words();
        if (words.Problem is not null || rest.Count != 2)
        {
            return cli.Fail(Usage, Exit.Usage);
        }

        if (NoFile(cli, out var list) is { } noFile)
        {
            return noFile;
        }

        if (list.Find(rest[0]) is not { } connector)
        {
            return cli.Fail($"no connector {SafeText.Line(rest[0])}; 'diptick connectors' lists them.", Exit.Usage);
        }

        var what = rest[1];
        var clientSecret = what.Equals("client-secret", StringComparison.OrdinalIgnoreCase);
        if (!clientSecret && (what.Length is 0 or > 64 || !what.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
        {
            return cli.Fail("a header's name is letters, digits and -.", Exit.Usage);
        }

        if (connector.Local)
        {
            return cli.Fail($"{connector.Name} runs on this PC and takes no headers; its variables are in --env.", Exit.Usage);
        }

        if (!cli.Flags.Interactive && !Console.IsInputRedirected)
        {
            return cli.Fail("saving a secret means typing it, and --no-interactive forbids asking. Pipe it on stdin instead.", Exit.NoYes);
        }

        var value = (Console.IsInputRedirected ? Console.In.ReadLine() : ConsoleInput.ReadSecret($"{(clientSecret ? "Client secret" : what)} for {connector.Name}, not shown as you type: ", cli.Err))?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return cli.Fail("nothing given; nothing saved.");
        }

        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would save {connector.Name}'s {(clientSecret ? "client secret" : what)}; nothing saved.");
            return Exit.Ok;
        }

        try
        {
            if (clientSecret)
            {
                ConnectorSecrets.SetClientSecret(connector.Name, value);
            }
            else
            {
                ConnectorSecrets.SetHeader(connector.Name, what, value);
                if (!connector.SecretHeaders.Contains(what, StringComparer.OrdinalIgnoreCase))
                {
                    var headers = connector.Headers.Where(h => !h.Key.Equals(what, StringComparison.OrdinalIgnoreCase)).ToDictionary(h => h.Key, h => h.Value, StringComparer.Ordinal);
                    if (Save(cli, list, Replaced(list, connector with { SecretHeaders = [.. connector.SecretHeaders, what], Headers = headers })) is { } failed)
                    {
                        return failed;
                    }
                }
            }
        }
        catch (KeyStoreException ex)
        {
            return cli.Fail(ex.Message);
        }

        ConnectorHub.CloseAsync(connector.Name).GetAwaiter().GetResult();
        cli.Note($"Saved in Windows Credential Manager; it is only ever sent to {connector.Name}.");
        return Exit.Ok;
    }

    private static int Remove(Cli cli, VerbArgs words)
    {
        if (One(cli, words) is not { } found)
        {
            return Exit.Usage;
        }

        var (list, connector) = found;

        var answer = cli.Confirm(connector.Local
            ? $"Remove {connector.Name}, with its folder in DipTick's data, its sandbox, and what its sandbox was given of your folders?"
            : $"Remove {connector.Name}, with its sign-in and saved values?");
        if (answer != Answer.Yes)
        {
            return cli.NotConfirmed(answer, $"removing {connector.Name}");
        }

        ConnectorHub.CloseAsync(connector.Name).GetAwaiter().GetResult();
        try
        {
            ConnectorSecrets.ClearAll(connector.Name, connector.SecretHeaders);
        }
        catch (KeyStoreException ex)
        {
            cli.Note(ex.Message);
        }

        if (connector.Local)
        {
            foreach (var left in ConnectorPlace.Remove(connector.Name))
            {
                cli.Note(left);
            }
        }

        if (Save(cli, list, [.. list.Connectors.Where(c => c.Name != connector.Name)]) is { } failed)
        {
            return failed;
        }

        cli.Note($"Removed {connector.Name}.");
        return Exit.Ok;
    }

    /// <summary>
    /// <c>sandbox</c>: where a local connector runs. With nothing more it
    /// says so; with options it changes it, which needs your yes again (enable shows it), and takes back at
    /// once any folder it no longer names.
    /// </summary>
    private static int Sandbox(Cli cli, VerbArgs words)
    {
        var network = words.Option("--network");
        var read = words.All("--read");
        var write = words.All("--write");
        var noFolders = words.Switch("--no-folders");
        var on = words.Switch("--on");
        var off = words.Switch("--off");
        if (on && off)
        {
            return cli.Fail("--on or --off, not both.", Exit.Usage);
        }

        if (One(cli, words) is not { } found)
        {
            return Exit.Usage;
        }

        var (list, connector) = found;
        if (!connector.Local)
        {
            return cli.Fail($"{connector.Name} is a service, reached over the network; only a program on this PC runs in a sandbox.", Exit.Usage);
        }

        var box = connector.Box;
        var changing = network is not null || read.Count > 0 || write.Count > 0 || noFolders || on || off;
        if (!changing)
        {
            if (!cli.WriteJson(new { connector = connector.Name, sandbox = new { on = box.On, network = box.Network, read = box.Read, write = box.Write }, words = box.Words }))
            {
                cli.Note($"{connector.Name} {(box.On ? $"runs {box.Words}" : box.Words)}.");
            }

            return Exit.Ok;
        }

        if (network is not null && !ConnectorSandbox.Networks.Contains(network, StringComparer.Ordinal))
        {
            return cli.Fail($"--network takes {string.Join(", ", ConnectorSandbox.Networks)}.", Exit.Usage);
        }

        var settings = SettingsFolder(cli);
        var readFolders = noFolders ? new List<string>() : [.. box.Read];
        var writeFolders = noFolders ? new List<string>() : [.. box.Write];
        foreach (var (typed, writes) in read.Select(f => (f, false)).Concat(write.Select(f => (f, true))))
        {
            string folder;
            try
            {
                folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(typed.Trim().Trim('"')));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return cli.Fail($"{SafeText.Line(typed)} is not a folder's path: {SafeText.Line(ex.Message)}", Exit.Usage);
            }

            if (ConnectorPlace.FolderProblem(folder, settings) is { } problem)
            {
                return cli.Fail($"{problem}.", Exit.Usage);
            }

            // A folder is named once: read and write, if either list asks for writing.
            readFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
            if (writes || writeFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                writeFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
                writeFolders.Add(folder);
            }
            else
            {
                readFolders.Add(folder);
            }
        }

        if (readFolders.Count + writeFolders.Count > ConnectorSandbox.MostFolders)
        {
            return cli.Fail(string.Create(CultureInfo.InvariantCulture, $"a connector's sandbox names at most {ConnectorSandbox.MostFolders} folders."), Exit.Usage);
        }

        var changed = new ConnectorSandbox(!off && (on || box.On), network ?? box.Network, readFolders, writeFolders);
        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] {connector.Name} would run {(changed.On ? changed.Words : "as you")}; nothing written.");
            return Exit.Ok;
        }

        ConnectorHub.CloseAsync(connector.Name).GetAwaiter().GetResult();
        if (Save(cli, list, Replaced(list, connector with { Sandbox = changed })) is { } failed)
        {
            return failed;
        }

        foreach (var left in ConnectorPlace.Narrow(connector.Name, changed))
        {
            cli.Note(left);
        }

        if (!cli.WriteJson(new { connector = connector.Name, sandbox = new { on = changed.On, network = changed.Network, read = changed.Read, write = changed.Write }, words = changed.Words }))
        {
            cli.Note($"{connector.Name} now {(changed.On ? $"runs {changed.Words}" : changed.Words)}. Your yes covered where it ran: 'diptick connectors enable {connector.Name}' shows it and asks again.");
        }

        return Exit.Ok;
    }

    /// <summary>The folder of the settings in use, which no connector is given; null with none.</summary>
    private static string? SettingsFolder(Cli cli) => cli.Config.Path is { } settings ? Path.GetDirectoryName(Path.GetFullPath(settings)) : null;

    private static string Words(Verdict verdict) => verdict switch
    {
        Verdict.Allow => "allowed",
        Verdict.Ask => "asks first",
        Verdict.DryRun => "dry run",
        _ => "never",
    };

    /// <summary>The list of the settings in use; a failure's exit, said, when there is no settings file to keep it beside.</summary>
    private static int? NoFile(Cli cli, out ConnectorList list)
    {
        list = ConnectorFile.Load(cli.Config);
        return list.Path is null ? cli.Fail("no settings file is in use (--config none), so there is nowhere to keep connectors.", Exit.Usage) : null;
    }

    /// <summary>The connector named on the line, with the list it is in; an exit when there is none.</summary>
    private static (ConnectorList List, Connector Connector)? One(Cli cli, VerbArgs words)
    {
        var rest = words.Words();
        if (words.Problem is not null || rest.Count != 1)
        {
            cli.Fail(Usage, Exit.Usage);
            return null;
        }

        var list = ConnectorFile.Load(cli.Config);
        if (list.Find(rest[0]) is { } connector)
        {
            return (list, connector);
        }

        cli.Fail($"no connector {SafeText.Line(rest[0])}; 'diptick connectors' lists them.", Exit.Usage);
        return null;
    }

    private static List<Connector> Replaced(ConnectorList list, Connector changed) => [.. list.Connectors.Select(c => c.Name == changed.Name ? changed : c)];

    private static int? Save(Cli cli, ConnectorList list, IReadOnlyList<Connector> connectors)
    {
        if (list.Path is not { } path)
        {
            return cli.Fail("no settings file is in use (--config none), so there is nowhere to keep connectors.", Exit.Usage);
        }

        try
        {
            ConnectorFile.Write(path, connectors);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return cli.Fail($"{path} could not be written: {SafeText.Line(ex.Message)}");
        }
    }

    /// <summary>NAME=value pairs; null when one is not.</summary>
    private static Dictionary<string, string>? Pairs(IReadOnlyList<string> texts)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var text in texts)
        {
            var at = text.IndexOf('=', StringComparison.Ordinal);
            if (at <= 0)
            {
                return null;
            }

            pairs[text[..at].Trim()] = text[(at + 1)..];
        }

        return pairs;
    }
}
