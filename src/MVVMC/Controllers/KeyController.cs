using DipTick.Models;
using DipTick.Models.Chat;
using DipTick.Models.Chat.Store;
using DipTick.Models.Keys;

namespace DipTick.Controllers;

/// <summary>
/// <c>diptick key [status|set|clear]</c>: the API keys DipTick saves in Windows Credential Manager,
/// one per host.
/// </summary>
/// <remarks>
/// A key is never an argument, so it never lands in shell history or a process list: it is typed
/// hidden at the keyboard, or read as one line from stdin. It is never printed, by any verb.
/// </remarks>
internal static class KeyController
{
    public static int Run(IReadOnlyList<string> args, Cli cli)
    {
        var sub = args.Count > 1 ? args[1] : "status";
        return sub switch
        {
            "status" or "list" when args.Count <= 2 => Status(cli),
            "set" when args.Count == 3 => Set(args[2], cli),
            "clear" or "remove" when args.Count == 3 => Clear(args[2], cli),
            _ => cli.Fail("usage: diptick key [status] | key set <where> | key clear <where>   (<where>: main, a seat, research, a provider, or a host)", Exit.Usage),
        };
    }

    /// <summary>Every place the settings send a conversation: the <c>main</c> section when it has a provider, and each seat.</summary>
    internal static List<(string Section, string Provider, KeyFinder Keys)> InUse(MasterConfig config)
    {
        var list = new List<(string, string, KeyFinder)>();
        if (Engines.KeysFor(config) is { } mainKeys)
        {
            list.Add(("main", config.MainProvider, mainKeys));
        }

        foreach (var seat in config.Seats)
        {
            if (Engines.KeysFor(seat) is { } keys)
            {
                list.Add((seat.Id, seat.Provider, keys));
            }
        }

        // The search service agents use: Brave takes the person's own key.
        if (config.ResearchSearch == "brave")
        {
            list.Add(("research", "brave", Models.Research.SearchService.Keys(config)));
        }

        return list;
    }

    /// <summary>
    /// The host a key for <paramref name="where"/> belongs to: <c>main</c>, a seat id, a provider DipTick
    /// knows, or a host (with a port when it is not 443).
    /// </summary>
    internal static string? HostFor(string where, MasterConfig config, out string why)
    {
        why = string.Empty;
        // "ai" is an older name for the main model; a script that still says so means main.
        if (where is ChatStores.MainSeat or ChatStores.FormerMainSeat)
        {
            if (Engines.KeysFor(config) is { } mainKeys)
            {
                return mainKeys.Host;
            }

            why = "no model is set up yet; run 'diptick setup' first.";
            return null;
        }

        if (config.Seat(where) is { } seat)
        {
            if (Engines.KeysFor(seat) is { } keys)
            {
                return keys.Host;
            }

            why = $"seat '{where}' has no provider or endpoint yet.";
            return null;
        }

        // The search service's key: Brave's, saved under Brave's host.
        if (where == "research")
        {
            return Models.Research.SearchService.Keys(config).Host;
        }

        if (Engines.Known.TryGetValue(where, out var known))
        {
            if (known is not null)
            {
                return ApiKeys.HostOf(known);
            }

            why = $"'{where}' has no address of its own; name its host instead (e.g. api.example.com).";
            return null;
        }

        // A host, and nothing else: no scheme, path, user name or query.
        if (where.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '[' or ']')
            && Uri.TryCreate($"https://{where}/", UriKind.Absolute, out var uri)
            && uri.PathAndQuery == "/" && uri.UserInfo.Length == 0 && uri.HostNameType != UriHostNameType.Unknown)
        {
            return ApiKeys.HostOf(uri);
        }

        why = $"'{where}' is not main, a seat, a provider ({string.Join(", ", Engines.Known.Where(k => k.Value is not null).Select(k => k.Key))}) or a host.";
        return null;
    }

    private static int Status(Cli cli)
    {
        var rows = InUse(cli.Config).Select(u => (u.Section, u.Provider, Finding: u.Keys.Describe())).ToList();
        if (cli.WriteJson(new
        {
            keys = rows.Select(r => new
            {
                section = r.Section,
                provider = r.Provider,
                host = r.Finding.Host,
                source = r.Finding.Source switch
                {
                    KeySource.Variable => "variable",
                    KeySource.VariableEmpty => "variable-empty",
                    KeySource.Stored => "stored",
                    KeySource.NotNeeded => "not-needed",
                    KeySource.StoreFailed => "store-failed",
                    _ => "missing",
                },
                variable = r.Finding.Variable,
            }),
        }))
        {
            return Exit.Ok;
        }

        if (rows.Count == 0)
        {
            cli.Note("No model is set up yet; 'diptick setup' starts.");
            return Exit.Ok;
        }

        var width = rows.Max(r => r.Section.Length);
        var hostWidth = rows.Max(r => r.Finding.Host.Length);
        foreach (var (section, _, finding) in rows)
        {
            cli.Data(SafeText.Line($"  {section.PadRight(width)}  {finding.Host.PadRight(hostWidth)}  {finding.Words}"));
        }

        return Exit.Ok;
    }

    private static int Set(string where, Cli cli)
    {
        if (HostFor(where, cli.Config, out var why) is not { } host)
        {
            return cli.Fail(why, Exit.Usage);
        }

        if (!cli.Flags.Interactive && !Console.IsInputRedirected)
        {
            return cli.Fail("saving a key means typing it, and --no-interactive forbids asking. Pipe it on stdin instead.", Exit.NoYes);
        }

        var users = InUse(cli.Config).Where(u => u.Keys.Host == host).ToList();
        var usedBy = users.Count == 0 ? string.Empty : $" (used by {string.Join(", ", users.Select(u => u.Section))})";
        var key = ConsoleInput.ReadSecret($"Key for {host}{usedBy}, not shown as you type: ", cli.Err)?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return cli.Fail("no key given; nothing saved.");
        }

        if (ApiKeys.Check(key) is { } bad)
        {
            return cli.Fail($"not saved: {bad}.", Exit.Usage);
        }

        string? existing;
        try
        {
            existing = WindowsKeyStore.Instance.Read(host);
        }
        catch (KeyStoreException ex)
        {
            return cli.Fail(ex.Message);
        }

        if (existing == key)
        {
            cli.Note($"That key is already saved for {host}.");
            return Exit.Ok;
        }

        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would save a key for {host} in Windows Credential Manager; nothing saved.");
            return Exit.Ok;
        }

        if (existing is not null)
        {
            var answer = cli.Confirm($"Replace the key saved for {host}?");
            if (answer != Answer.Yes)
            {
                return cli.NotConfirmed(answer, $"replacing the key for {host}");
            }
        }

        try
        {
            WindowsKeyStore.Instance.Write(host, key);
        }
        catch (KeyStoreException ex)
        {
            return cli.Fail(ex.Message);
        }

        if (!cli.WriteJson(new { saved = true, host, target = WindowsKeyStore.Target(host) }))
        {
            cli.Note($"Saved the key for {host} in Windows Credential Manager ({WindowsKeyStore.Target(host)}).");
        }

        foreach (var (section, _, keys) in users.Where(u => u.Keys.KeyVariable.Length > 0))
        {
            cli.Note($"Note: {section}.keyVariable names {keys.KeyVariable}, so {section} uses that variable, not this key.");
        }

        return Exit.Ok;
    }

    private static int Clear(string where, Cli cli)
    {
        if (HostFor(where, cli.Config, out var why) is not { } host)
        {
            return cli.Fail(why, Exit.Usage);
        }

        string? existing;
        try
        {
            existing = WindowsKeyStore.Instance.Read(host);
        }
        catch (KeyStoreException ex)
        {
            return cli.Fail(ex.Message);
        }

        if (existing is null)
        {
            cli.Note($"No key is saved for {host}.");
            return Exit.Ok;
        }

        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would remove the key saved for {host}; nothing removed.");
            return Exit.Ok;
        }

        var answer = cli.Confirm($"Remove the key saved for {host}? Calls to it then have no key until one is saved again.");
        if (answer != Answer.Yes)
        {
            return cli.NotConfirmed(answer, $"removing the key for {host}");
        }

        try
        {
            WindowsKeyStore.Instance.Delete(host);
        }
        catch (KeyStoreException ex)
        {
            return cli.Fail(ex.Message);
        }

        if (!cli.WriteJson(new { removed = true, host }))
        {
            cli.Note($"Removed the key for {host}.");
        }

        return Exit.Ok;
    }
}
