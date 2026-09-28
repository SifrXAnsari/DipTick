using System.Globalization;
using System.Text.Json;
using DipTick.Models;
using DipTick.Models.Chat.Store;
using DipTick.Models.Harness;

namespace DipTick.Controllers;

/// <summary>
/// The permission controls as verbs: a rule set in the
/// settings, for one agent, or for one chat; folders and hosts added and removed; a preset. Loosening
/// anything asks for a yes; every change is a line in the audit log; a dry run writes nothing.
/// </summary>
internal static partial class PolicyController
{
    private static readonly string[] Lists = ["allow", "ask", "deny"];

    /// <summary><c>policy set &lt;rule&gt; allow|ask|deny [--ai &lt;id&gt;] [--session &lt;id&gt;]</c>.</summary>
    private static int SetRule(VerbArgs words, Cli cli)
    {
        var agent = words.Option("--ai");
        var session = words.Option("--session");
        var isPrivate = words.Switch("--private");
        var rest = words.Words();
        if (words.Problem is not null || rest.Count != 2 || !Lists.Contains(rest[1]) || (agent is not null && session is not null))
        {
            return cli.Fail(PolicyUsage, Exit.Usage);
        }

        var rule = Capabilities.Canonical(rest[0].Trim());
        if (!Capabilities.IsRule(rule))
        {
            return cli.Fail($"'{SafeText.Line(rule)}' is not a rule: a capability ({string.Join(", ", Capabilities.Names)}), a group like fs.*, *, or one narrowed to a target, like mcp.call:clickup/*.", Exit.Usage);
        }

        var verdict = rest[1];
        if (session is not null)
        {
            return SetChatRule(cli, session, isPrivate, rule, verdict);
        }

        return agent is not null ? SetAgentRule(cli, agent, rule, verdict) : SetSettingsRule(cli, rule, verdict);
    }

    /// <summary>A rule in the settings' lists: out of the other two, into the one named.</summary>
    private static int SetSettingsRule(Cli cli, string rule, string verdict)
    {
        if (cli.Config.Path is not { } path)
        {
            return cli.Fail($"no {MasterConfig.FileName} in use to change. Run 'diptick init', or pass --config.", Exit.Usage);
        }

        var p = cli.Config.Permissions;
        var lists = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["allow"] = [.. p.Allow.Where(r => !Same(r, rule))],
            ["ask"] = [.. p.Ask.Where(r => !Same(r, rule))],
            ["deny"] = [.. p.Deny.Where(r => !Same(r, rule))],
        };
        var was = p.Allow.Any(r => Same(r, rule)) ? "allow" : p.Ask.Any(r => Same(r, rule)) ? "ask" : p.Deny.Any(r => Same(r, rule)) ? "deny" : "none";
        if (was == verdict)
        {
            cli.Note($"{rule} is already {Word(verdict)}; nothing changed.");
            return Exit.Ok;
        }

        lists[verdict].Add(rule);
        return WritePermissions(cli, path, lists, $"{rule}: {Word(was)} -> {Word(verdict)}", Presets.Strictness(verdict) < Presets.Strictness(was == "none" ? "deny" : was), "settings", rule);
    }

    /// <summary>
    /// A rule for one agent (its lane and its ask list, which only ever narrow the permissions): deny
    /// takes it out of the lane, ask adds it to the agent's ask list, allow takes it off that list.
    /// </summary>
    private static int SetAgentRule(Cli cli, string id, string rule, string verdict)
    {
        if (cli.Config.Path is not { } path)
        {
            return cli.Fail($"no {MasterConfig.FileName} in use to change. Run 'diptick init', or pass --config.", Exit.Usage);
        }

        if (cli.Config.Seat(id) is not { } seat)
        {
            return cli.Fail(Models.Chat.Seam.Table.IsMain(id)
                ? "the model that answers you follows the permissions themselves: change them without --ai, or narrow a seat."
                : $"no agent '{SafeText.Line(id)}' is set up; 'diptick seats' lists them.", Exit.Usage);
        }

        var lane = seat.Lane.ToList();
        var ask = seat.Ask.ToList();
        switch (verdict)
        {
            case "deny":
                // A lane of everything becomes every capability but the ones the rule names.
                lane = lane.Contains("*")
                    ? [.. Capabilities.All.Where(c => !Capabilities.Matches(rule, c)).Select(Capabilities.Name)]
                    : [.. lane.Where(r => !Same(r, rule) && !(Capabilities.Parse(r) is { } cap && Capabilities.Matches(rule, cap)))];
                ask = [.. ask.Where(r => !Same(r, rule))];
                break;
            case "ask":
                if (!ask.Any(r => Same(r, rule)))
                {
                    ask.Add(rule);
                }

                if (!lane.Contains("*") && !lane.Any(r => Same(r, rule)))
                {
                    lane.Add(rule);
                }

                break;
            default:
                ask = [.. ask.Where(r => !Same(r, rule))];
                if (!lane.Contains("*") && !lane.Any(r => Same(r, rule)))
                {
                    lane.Add(rule);
                }

                break;
        }

        var edits = new List<MasterConfigWriter.Edit>
        {
            new(id, "lane", ListLiteral(lane), Shown(lane)),
            new(id, "ask", ListLiteral(ask), Shown(ask)),
        };
        var change = $"{id}: {rule} {Word(verdict)} (lane {Shown(lane)}; always ask {Shown(ask)})";
        return Write(cli, path, edits, change, loosens: verdict != "deny", $"agent {id}", rule);
    }

    /// <summary>A chat's own rule: it tightens anything, turns an ask into an allow for itself, and never loosens a never.</summary>
    private static int SetChatRule(Cli cli, string session, bool isPrivate, string rule, string verdict)
    {
        using var store = ChatStores.Open(cli.Config, isPrivate);
        if (store.Find(session) is not { Open: true } found)
        {
            return cli.Fail($"no open chat '{SafeText.Line(session)}'.");
        }

        var v = verdict switch { "allow" => Verdict.Allow, "ask" => Verdict.Ask, _ => Verdict.Deny };
        if (v == Verdict.Allow && Capabilities.Parse(rule.Split(':')[0]) is { } cap && cli.Config.Permissions.Deny.Any(r => Capabilities.SpeaksOf(r, cap)))
        {
            cli.Note($"The settings say never to {rule}; a chat's allow never loosens a never, so this rule only turns an ask into an allow here.");
        }

        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would set {rule} to {Word(verdict)} in {found.Id}; nothing written.");
            return Exit.Ok;
        }

        if (v == Verdict.Allow)
        {
            var answer = cli.Confirm($"Let agents {rule} without asking, in this chat only?");
            if (answer != Answer.Yes)
            {
                return cli.NotConfirmed(answer, $"allowing {rule} in this chat");
            }
        }

        var log = isPrivate ? AuditLog.ForPrivate(cli.Config.AuditPrivateDays) : AuditLog.ForThisUser();
        if (!ChatController.KeepChatRule(store, cli, log, found.Id, rule, v))
        {
            return Exit.Failed;
        }

        if (!cli.WriteJson(new { session = found.Id, rule, verdict }))
        {
            cli.Note($"{found.Id}: {rule} is {Word(verdict)} in this chat.");
        }

        return Exit.Ok;
    }

    /// <summary><c>policy unset &lt;rule&gt; --session &lt;id&gt;</c>: a chat's own rule taken away; the settings decide again.</summary>
    private static int UnsetRule(VerbArgs words, Cli cli)
    {
        var session = words.Option("--session");
        var isPrivate = words.Switch("--private");
        var rest = words.Words();
        if (words.Problem is not null || rest.Count != 1 || session is null)
        {
            return cli.Fail(PolicyUsage, Exit.Usage);
        }

        using var store = ChatStores.Open(cli.Config, isPrivate);
        if (store.Find(session) is not { } found)
        {
            return cli.Fail($"no chat '{SafeText.Line(session)}'.");
        }

        var rule = Capabilities.Canonical(rest[0].Trim());
        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would take {rule} out of {found.Id}'s own rules; nothing written.");
            return Exit.Ok;
        }

        var log = isPrivate ? AuditLog.ForPrivate(cli.Config.AuditPrivateDays) : AuditLog.ForThisUser();
        try
        {
            log.RecordPolicy(cli.Who, $"chat {found.Id}", rule, "taken away; the settings decide");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            return cli.Fail($"the audit log cannot be written ({SafeText.Line(ex.Message)}); nothing changed.");
        }

        var gone = store.ClearChatRule(found.Id, rule);
        cli.Note(gone ? $"{found.Id}: {rule} follows the settings again." : $"{found.Id} had no rule of its own for {rule}.");
        return Exit.Ok;
    }

    /// <summary><c>policy chat [--session &lt;id&gt;]</c>: a chat's own rules, and what the settings say beside each.</summary>
    private static int ChatRules(VerbArgs words, Cli cli)
    {
        var session = words.Option("--session") ?? "last";
        var isPrivate = words.Switch("--private");
        if (words.Problem is not null || words.Words().Count > 0)
        {
            return cli.Fail(PolicyUsage, Exit.Usage);
        }

        using var store = ChatStores.Open(cli.Config, isPrivate);
        if (store.Find(session) is not { } found)
        {
            return cli.Fail($"no chat '{SafeText.Line(session)}'.");
        }

        var rules = store.ChatRules(found.Id);
        if (cli.WriteJson(new { session = found.Id, rules = rules.Select(r => new { rule = r.Rule, verdict = r.Verdict.ToString().ToUpperInvariant() switch { "ALLOW" => "allow", "ASK" => "ask", _ => "deny" } }) }))
        {
            return Exit.Ok;
        }

        if (rules.Count == 0)
        {
            cli.Data($"{found.Id} has no rules of its own: the settings decide (diptick policy show).");
            return Exit.Ok;
        }

        foreach (var rule in rules)
        {
            cli.Data($"  {rule.Rule,-28} {Word(rule.Verdict switch { Verdict.Allow => "allow", Verdict.Ask => "ask", _ => "deny" })} in this chat");
        }

        return Exit.Ok;
    }

    /// <summary><c>policy folders|hosts [add|remove &lt;item&gt;]</c>: the lists the file and web capabilities are held to.</summary>
    private static int EditList(VerbArgs words, Cli cli, string key, string noun)
    {
        var rest = words.Words();
        // The file's own list: the chats' folder DipTick adds to the folders is never written back.
        var current = key == "roots" ? cli.Config.FileRoots : cli.Config.Permissions.Hosts;
        if (words.Problem is not null || rest.Count is 1 or > 2 || (rest.Count == 2 && rest[0] is not ("add" or "remove")))
        {
            return cli.Fail(PolicyUsage, Exit.Usage);
        }

        if (rest.Count == 0)
        {
            if (!cli.WriteJson(new { list = key, items = current }))
            {
                cli.Data(current.Count == 0 ? $"No {noun} is open to agents." : string.Join('\n', current));
            }

            return Exit.Ok;
        }

        if (cli.Config.Path is not { } path)
        {
            return cli.Fail($"no {MasterConfig.FileName} in use to change. Run 'diptick init', or pass --config.", Exit.Usage);
        }

        var item = rest[1].Trim();
        if (key == "roots")
        {
            try
            {
                item = Path.GetFullPath(item);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return cli.Fail($"not a folder's path: {SafeText.Line(item)}", Exit.Usage);
            }
        }

        var items = current.ToList();
        var adding = rest[0] == "add";
        if (adding == items.Contains(item, StringComparer.OrdinalIgnoreCase))
        {
            cli.Note(adding ? $"{item} is already in the list." : $"{item} is not in the list.");
            return Exit.Ok;
        }

        if (adding)
        {
            items.Add(item);
        }
        else
        {
            items.RemoveAll(i => string.Equals(i, item, StringComparison.OrdinalIgnoreCase));
        }

        var edits = new List<MasterConfigWriter.Edit> { new("permissions", key, ListLiteral(items), Shown(items)) };
        return Write(cli, path, edits, $"{noun} {(adding ? "added" : "removed")}: {item}", loosens: adding, "settings", $"permissions.{key}");
    }

    /// <summary><c>policy preset locked|standard|trusted</c>: the three lists as the preset has them, the change shown first.</summary>
    private static int Preset(VerbArgs words, Cli cli)
    {
        var rest = words.Words();
        if (words.Problem is not null || rest.Count != 1 || Presets.Of(rest[0]) is not { } preset)
        {
            return cli.Fail($"usage: diptick policy preset {string.Join("|", Presets.Names)}", Exit.Usage);
        }

        if (cli.Config.Path is not { } path)
        {
            return cli.Fail($"no {MasterConfig.FileName} in use to change. Run 'diptick init', or pass --config.", Exit.Usage);
        }

        // What it changes is shown first, in the question itself.
        var p = cli.Config.Permissions;
        var diff = string.Join("; ", new[] { ("allowed", p.Allow, preset.Allow), ("ask me first", p.Ask, preset.Ask), ("never", p.Deny, preset.Deny) }
            .Select(x => $"{x.Item1}: {Shown(x.Item2)} -> {Shown(x.Item3)}"));
        var lists = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["allow"] = [.. preset.Allow],
            ["ask"] = [.. preset.Ask],
            ["deny"] = [.. preset.Deny],
        };
        return WritePermissions(cli, path, lists, $"the {rest[0]} preset ({diff})", loosens: true, "settings", $"preset {rest[0]}");
    }

    private static int WritePermissions(Cli cli, string path, Dictionary<string, List<string>> lists, string change, bool loosens, string where, string rule)
    {
        var edits = Lists.Select(list => new MasterConfigWriter.Edit("permissions", list, ListLiteral(lists[list]), Shown(lists[list]))).ToList();
        return Write(cli, path, edits, change, loosens, where, rule);
    }

    /// <summary>The edits proved, then (after a yes, when they loosen anything) written, with their audit line first.</summary>
    private static int Write(Cli cli, string path, List<MasterConfigWriter.Edit> edits, string change, bool loosens, string where, string rule)
    {
        if (MasterConfigWriter.Apply(path, edits, write: false) is { } refused)
        {
            return cli.Fail($"not changed: {refused}");
        }

        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] {change}; nothing written.");
            return Exit.Ok;
        }

        if (loosens)
        {
            var answer = cli.Confirm($"Change what agents may do: {change}?");
            if (answer != Answer.Yes)
            {
                return cli.NotConfirmed(answer, change);
            }
        }

        try
        {
            AuditLog.ForThisUser().RecordPolicy(cli.Who, where, rule, change);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            return cli.Fail($"the audit log cannot be written ({SafeText.Line(ex.Message)}); nothing changed.");
        }

        if (MasterConfigWriter.Apply(path, edits, write: true) is { } failed)
        {
            return cli.Fail($"not changed: {failed}");
        }

        if (!cli.WriteJson(new { changed = change }))
        {
            cli.Note(change);
        }

        return Exit.Ok;
    }

    private static bool Same(string a, string b) => string.Equals(Capabilities.Canonical(a), Capabilities.Canonical(b), StringComparison.OrdinalIgnoreCase);

    private static string ListLiteral(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(i => JsonSerializer.Serialize(i))) + "]";

    private static string Shown(IReadOnlyCollection<string> items) => items.Count == 0 ? "(none)" : string.Join(", ", items);

    private static string Word(string list) => list switch
    {
        "allow" => "allowed",
        "ask" => "ask me first",
        "deny" => "never",
        _ => "no rule (never)",
    };
}
