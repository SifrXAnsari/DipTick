using System.Text.Json;
using DipTick.Models;

namespace DipTick.Controllers;

/// <summary>
/// <c>diptick config [list|get|set|unset|check|password]</c>, <c>login</c> and <c>whoami</c>.
/// </summary>
internal static class ConfigController
{
    private static readonly HashSet<string> ListKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "permissions.allow", "permissions.ask", "permissions.deny", "permissions.roots", "permissions.hosts",
    };

    private static readonly HashSet<string> BoolKeys = new(StringComparer.OrdinalIgnoreCase) { "cli.dryRun", "cli.yes", "chat.save", "quit.unloadModels", "money.showCost" };

    private static readonly HashSet<string> NumberKeys = new(StringComparer.OrdinalIgnoreCase) { "main.timeoutSeconds", "cli.timeoutSeconds" };

    public static int Run(IReadOnlyList<string> args, Cli cli)
    {
        var sub = args.Count > 1 ? args[1] : "list";
        return sub switch
        {
            "list" or "show" when args.Count <= 2 => List(cli),
            "get" when args.Count == 3 => Get(Current(args[2], cli), cli),
            "set" when args.Count == 4 => Set(Current(args[2], cli), args[3], cli),
            "unset" when args.Count == 3 => Unset(Current(args[2], cli), cli),
            "check" when args.Count == 2 => Check(cli),
            "password" when args.Count == 2 => Password(cli),
            _ => cli.Fail("usage: diptick config [list|get <key>|set <key> <value>|unset <key>|check|password]", Exit.Usage),
        };
    }

    public static int Login(Cli cli)
    {
        if (cli.Config.UserPasswordHash.Length == 0)
        {
            return cli.Fail("no password is set. Run 'diptick config password' first.");
        }

        if (!cli.Flags.Interactive && !Console.IsInputRedirected)
        {
            return cli.Fail("login needs the password and --no-interactive forbids asking. Pipe it on stdin instead.", Exit.NoYes);
        }

        var password = ConsoleInput.ReadSecret("Password: ", cli.Err);
        if (password is null)
        {
            return cli.Fail("no password given.");
        }

        if (!PasswordHash.Verify(password, cli.Config.UserPasswordHash))
        {
            return cli.Fail("wrong password.");
        }

        if (!cli.WriteJson(new { signedIn = true, user = cli.Who }))
        {
            cli.Note($"Signed in as {cli.Who}.");
        }

        return Exit.Ok;
    }

    public static int WhoAmI(Cli cli)
    {
        var config = cli.Config;
        if (cli.WriteJson(new { user = cli.Who, named = config.UserName.Length > 0, passwordSet = config.UserPasswordHash.Length > 0, config = config.Path }))
        {
            return Exit.Ok;
        }

        cli.Data(cli.Who + (config.UserName.Length > 0 ? string.Empty : " (user.name is empty; this is the Windows account)"));
        cli.Data(config.UserPasswordHash.Length > 0 ? "password: set" : "password: not set");
        cli.Data($"config: {config.Path ?? "(none; every value is a default)"}");
        return Exit.Ok;
    }

    /// <summary>Every setting, its value, and the layer that set it: flag, variable, file or default.</summary>
    private static int List(Cli cli)
    {
        var rows = Rows(cli);
        if (cli.WriteJson(new { config = cli.Config.Path, settings = rows.Select(r => new { name = r.Name, value = r.Value, layer = r.Layer }), problems = cli.Config.Problems }))
        {
            return Exit.Ok;
        }

        cli.Note(cli.Config.Path is null ? $"No {MasterConfig.FileName} in use; every file value is a default." : $"From {cli.Config.Path}");
        var width = rows.Max(r => r.Name.Length);
        foreach (var r in rows)
        {
            cli.Data($"  {r.Name.PadRight(width)}  {(r.Value.Length == 0 ? "(empty)" : r.Value)}  [{r.Layer}]");
        }

        if (cli.Config.Problems.Count > 0)
        {
            cli.Note($"{cli.Config.Problems.Count} problem(s); run 'diptick config check'.");
        }

        return Exit.Ok;
    }

    private static int Get(string key, Cli cli)
    {
        var row = Rows(cli).FirstOrDefault(r => string.Equals(r.Name, key, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            return cli.Fail($"no setting '{key}'. 'diptick config list' shows them.", Exit.Usage);
        }

        if (!cli.WriteJson(new { name = row.Name, value = row.Value, layer = row.Layer }))
        {
            cli.Data(row.Value);
        }

        return Exit.Ok;
    }

    private static int Set(string key, string value, Cli cli)
    {
        if (Target(key, cli, out var path, out var section, out var name) is { } refused)
        {
            return refused;
        }

        var literal = Literal(key, value, out var expect);
        if (literal is null)
        {
            return cli.Fail($"'{value}' is not a value for {key}.", Exit.Usage);
        }

        var old = Rows(cli).First(r => string.Equals(r.Name, key, StringComparison.OrdinalIgnoreCase)).Value;
        if (MasterConfigWriter.Set(path, section, name, literal, expect, write: false) is { } why)
        {
            return cli.Fail(why);
        }

        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] {key}: {Shown(old)} -> {Shown(expect)}; nothing written.");
            return Exit.Ok;
        }

        // What agents may do, or where the chat goes, changes here: it needs a yes like any moderate change.
        if (NeedsYes(key))
        {
            var answer = cli.Confirm($"Change what agents may do or where the chat goes: {key} {Shown(old)} -> {Shown(expect)}?");
            if (answer != Answer.Yes)
            {
                return cli.NotConfirmed(answer, $"changing {key}");
            }
        }

        if (MasterConfigWriter.Set(path, section, name, literal, expect, write: true) is { } failed)
        {
            return cli.Fail(failed);
        }

        cli.Note($"{key}: {Shown(old)} -> {Shown(expect)}");
        return Exit.Ok;
    }

    private static int Unset(string key, Cli cli)
    {
        if (Target(key, cli, out var path, out var section, out var name) is { } refused)
        {
            return refused;
        }

        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would remove {key}, so its default applies; nothing written.");
            return Exit.Ok;
        }

        if (NeedsYes(key))
        {
            var answer = cli.Confirm($"Remove {key}, changing what agents may do or where the chat goes?");
            if (answer != Answer.Yes)
            {
                return cli.NotConfirmed(answer, $"removing {key}");
            }
        }

        if (MasterConfigWriter.Unset(path, section, name, write: true) is { } failed)
        {
            return cli.Fail(failed);
        }

        cli.Note($"{key} removed; its default applies.");
        return Exit.Ok;
    }

    private static int Check(Cli cli)
    {
        var problems = cli.Config.Problems;
        if (cli.WriteJson(new { config = cli.Config.Path, ok = problems.Count == 0, problems }))
        {
            return problems.Count == 0 ? Exit.Ok : Exit.Failed;
        }

        if (problems.Count == 0)
        {
            cli.Note(cli.Config.Path is null ? "No config file; defaults are fine." : $"{cli.Config.Path}: no problems.");
            return Exit.Ok;
        }

        foreach (var problem in problems)
        {
            cli.Err.WriteLine($"  {problem}");
        }

        return Exit.Failed;
    }

    private static int Password(Cli cli)
    {
        var path = cli.Config.Path;
        if (path is null)
        {
            return cli.Fail($"no {MasterConfig.FileName} in use. Run 'diptick init', or pass --config.");
        }

        if (!cli.Flags.Interactive && !Console.IsInputRedirected)
        {
            return cli.Fail("setting a password means typing it, and --no-interactive forbids asking. Pipe it on stdin instead.", Exit.NoYes);
        }

        var first = ConsoleInput.ReadSecret("New password: ", cli.Err);
        if (first is null)
        {
            return cli.Fail("no password given.");
        }

        if (PasswordHash.Refuse(first) is { } why)
        {
            return cli.Fail(why);
        }

        // Typed at a keyboard, ask twice; piped in, the pipe is the confirmation.
        if (!Console.IsInputRedirected && ConsoleInput.ReadSecret("Again: ", cli.Err) != first)
        {
            return cli.Fail("the two did not match; nothing changed.");
        }

        if (cli.Flags.DryRun)
        {
            cli.Note("[dry run] the password is acceptable; nothing written.");
            return Exit.Ok;
        }

        if (MasterConfigWriter.SetPasswordHash(path, PasswordHash.Hash(first)) is { } failed)
        {
            return cli.Fail(failed);
        }

        cli.Note($"Password set in {path}.");
        return Exit.Ok;
    }

    internal sealed record Row(string Name, string Value, string Layer);

    /// <summary>Every setting's value as it applies to this run, and the layer that set it.</summary>
    internal static List<Row> Rows(Cli cli)
    {
        var rows = new List<Row>();
        foreach (var s in cli.Config.Settings)
        {
            var layer = s.FromDefault ? "default" : "file";
            var value = s.Value;
            if (cli.Flags.Layers.TryGetValue(s.Name, out var resolved) && resolved is Layer.Flag or Layer.Variable)
            {
                layer = resolved == Layer.Flag ? "flag" : "variable";
                value = Effective(s.Name, cli.Flags) ?? value;
            }

            rows.Add(new Row(s.Name, value, layer));
        }

        return rows;
    }

    private static string? Effective(string name, RunFlags f) => name switch
    {
        "cli.dryRun" => f.DryRun ? "true" : "false",
        "cli.yes" => f.Yes ? "true" : "false",
        "cli.interactive" => f.Interactive ? "true" : "false",
        "cli.verbosity" => f.Verbosity switch { Verbosity.Quiet => "quiet", Verbosity.Verbose => "verbose", Verbosity.Debug => "debug", _ => "normal" },
        "cli.color" => f.Color switch { ColorMode.Always => "always", ColorMode.Never => "never", _ => "auto" },
        "cli.timeoutSeconds" => f.NetTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "output.format" => f.Output switch { OutputFormat.Json => "json", OutputFormat.None => "none", _ => "text" },
        _ => null,
    };

    private static int? Target(string key, Cli cli, out string path, out string section, out string name)
    {
        path = cli.Config.Path ?? string.Empty;
        section = name = string.Empty;
        if (string.Equals(key, "user.passwordHash", StringComparison.OrdinalIgnoreCase))
        {
            return cli.Fail("user.passwordHash is written only by 'diptick config password'.", Exit.Usage);
        }

        if (!cli.Config.Settings.Any(s => string.Equals(s.Name, key, StringComparison.OrdinalIgnoreCase)))
        {
            return cli.Fail($"no setting '{key}'. 'diptick config list' shows them.", Exit.Usage);
        }

        if (cli.Config.Path is null)
        {
            return cli.Fail($"no {MasterConfig.FileName} in use to change. Run 'diptick init', or pass --config.", Exit.Usage);
        }

        var dot = key.IndexOf('.', StringComparison.Ordinal);
        section = key[..dot];
        name = cli.Config.Settings.First(s => string.Equals(s.Name, key, StringComparison.OrdinalIgnoreCase)).Name[(dot + 1)..];
        return null;
    }

    /// <summary>The JSON literal for a value typed on the command line, and how it will read back.</summary>
    private static string? Literal(string key, string value, out string expect)
    {
        expect = value.Trim();
        if (IsList(key))
        {
            var items = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            expect = items.Length == 0 ? "(none)" : string.Join(", ", items);
            return "[" + string.Join(", ", items.Select(i => JsonSerializer.Serialize(i))) + "]";
        }

        if (BoolKeys.Contains(key) || (string.Equals(key, "cli.interactive", StringComparison.OrdinalIgnoreCase) && !string.Equals(expect, "auto", StringComparison.OrdinalIgnoreCase)))
        {
            var b = RunFlags.ParseBool(value);
            expect = b switch { true => "true", false => "false", null => expect };
            return b switch { true => "true", false => "false", null => null };
        }

        if (NumberKeys.Contains(key))
        {
            var n = RunFlags.ParseTimeout(value, allowZero: false);
            expect = n?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? expect;
            return n is null ? null : expect;
        }

        if (IsWhole(key))
        {
            // The range is the reader's to judge: a value outside it is refused on read-back.
            if (!int.TryParse(value.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var whole))
            {
                return null;
            }

            expect = whole.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return expect;
        }

        if (IsMoney(key))
        {
            // A cap is off when not set; a price is unknown.
            var cap = key.StartsWith("money.cap", StringComparison.OrdinalIgnoreCase);
            if (value.Trim() is "null" or "unknown" or "off")
            {
                expect = cap ? "off" : "unknown";
                return "null";
            }

            if (!decimal.TryParse(value.Trim(), System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var amount))
            {
                return null;
            }

            expect = amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return expect;
        }

        if (string.Equals(key, "cli.interactive", StringComparison.OrdinalIgnoreCase))
        {
            expect = "auto";
        }

        return JsonSerializer.Serialize(expect);
    }

    private static string Shown(string value) => value.Length == 0 ? "(empty)" : value;

    /// <summary>
    /// A key as it is spelled now: <c>ai.*</c> is an older name for the main model's keys, and a
    /// script that still says so is answered under the new name, with a note saying which.
    /// </summary>
    internal static string Current(string key, Cli cli)
    {
        if (!key.StartsWith("ai.", StringComparison.OrdinalIgnoreCase))
        {
            return key;
        }

        var now = "main." + key[3..];
        cli.Note($"'{key}' is now '{now}'.");
        return now;
    }

    private static bool IsList(string key) =>
        ListKeys.Contains(key) || string.Equals(key, "chat.seats", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith(".lane", StringComparison.OrdinalIgnoreCase) || key.EndsWith(".ask", StringComparison.OrdinalIgnoreCase);

    private static bool IsWhole(string key) =>
        key.EndsWith(".timeoutSeconds", StringComparison.OrdinalIgnoreCase) || key.EndsWith(".maxAnswerTokens", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "chat.pasteLines", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "audit.privateDays", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith(".contextTokens", StringComparison.OrdinalIgnoreCase) || key.StartsWith("chat.max", StringComparison.OrdinalIgnoreCase);

    private static bool IsMoney(string key) =>
        key.EndsWith(".priceIn", StringComparison.OrdinalIgnoreCase) || key.EndsWith(".priceOut", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("money.cap", StringComparison.OrdinalIgnoreCase);

    /// <summary>The rule lives with the settings' descriptions, so the screen and this verb ask for the same yes.</summary>
    private static bool NeedsYes(string key) => SettingsCatalogue.NeedsYes(key);
}
