using System.Globalization;
using DipTick.Models;
using DipTick.Models.Harness;

namespace DipTick.Controllers;

/// <summary>
/// <c>diptick policy [show|check|try]</c> and <c>diptick audit [show|verify]</c>: what agents may
/// do, what it would be told for one request, and what it has been told.
/// </summary>
internal static partial class PolicyController
{
    private const string PolicyUsage = "usage: diptick policy [show] | check <capability> [target] | try <capability> [target] | set <rule> allow|ask|deny [--ai <id>] [--session <id>] | unset <rule> --session <id> | chat [--session <id>] | folders [add|remove <path>] | hosts [add|remove <host>] | preset locked|standard|trusted; --private for a private chat's rules";

    public static int Policy(IReadOnlyList<string> args, Cli cli)
    {
        var sub = args.Count > 1 ? args[1] : "show";
        switch (sub)
        {
            case "show" when args.Count <= 2:
                return Show(cli);
            case "check" or "try" when args.Count is 3 or 4:
                var cap = Capabilities.Parse(args[2]);
                if (cap is null)
                {
                    return cli.Fail($"'{args[2]}' is not a capability. Known: {string.Join(", ", Capabilities.Names)}.", Exit.Usage);
                }

                var target = args.Count == 4 ? args[3] : null;
                return sub == "check" ? Check(cap.Value, target, cli) : Try(cap.Value, target, cli);
            case "set":
                return SetRule(new VerbArgs(args, 2, cli.LiteralFrom), cli);
            case "unset":
                return UnsetRule(new VerbArgs(args, 2, cli.LiteralFrom), cli);
            case "chat":
                return ChatRules(new VerbArgs(args, 2, cli.LiteralFrom), cli);
            case "folders":
                return EditList(new VerbArgs(args, 2, cli.LiteralFrom), cli, "roots", "folder");
            case "hosts":
                return EditList(new VerbArgs(args, 2, cli.LiteralFrom), cli, "hosts", "host");
            case "preset":
                return Preset(new VerbArgs(args, 2, cli.LiteralFrom), cli);
            default:
                return cli.Fail(PolicyUsage, Exit.Usage);
        }
    }

    private const string AuditUsage = "usage: diptick audit [show|<count>|--all] [--since <date>] [--who <name>] [--what <capability>] [--verdict <verdict>] [--private] | audit verify [--private] | audit accept | audit protect [--status|--archive] | audit export <file> [--format jsonl|csv|text] [--since <date>] [--who <name>] [--what <capability>] [--verdict <verdict>] [--private]";

    public static int Audit(IReadOnlyList<string> args, Cli cli)
    {
        var log = AuditLog.ForThisUser();
        var sub = args.Count > 1 ? args[1] : "show";
        if (sub == "verify")
        {
            var verifying = new VerbArgs(args, 2, cli.LiteralFrom);
            var privateOnes = verifying.Switch("--private");
            return verifying.Words().Count > 0 || verifying.Problem is not null
                ? cli.Fail(AuditUsage, Exit.Usage)
                : privateOnes ? VerifyPrivate(cli) : Verify(log, cli);
        }

        if (sub == "accept")
        {
            return args.Count == 2 ? Accept(log, cli) : cli.Fail(AuditUsage, Exit.Usage);
        }

        if (sub == "export")
        {
            return Export(log, new VerbArgs(args, 2, cli.LiteralFrom), cli);
        }

        if (sub == "protect")
        {
            return Protect(new VerbArgs(args, 2, cli.LiteralFrom), cli);
        }

        // A count, the word show, or options straight after the verb.
        var first = sub is "show" || int.TryParse(sub, NumberStyles.None, CultureInfo.InvariantCulture, out _) ? 2 : 1;
        var words = new VerbArgs(args, first, cli.LiteralFrom);
        var all = words.Switch("--all");
        var privateDays = words.Switch("--private");
        var sinceText = words.Option("--since");
        var filter = Filter(words);
        var rest = words.Words();
        var most = 20;
        if (words.Problem is not null || rest.Count > 0 || (first == 2 && sub != "show" && (!int.TryParse(sub, NumberStyles.None, CultureInfo.InvariantCulture, out most) || most < 1)))
        {
            return cli.Fail(AuditUsage, Exit.Usage);
        }

        if (Since(sinceText, cli) is not { } since || Checked(filter, cli) is not { } keep)
        {
            return Exit.Usage;
        }

        bool Wanted(AuditEntry e) => (since.Value is not { } from || e.When >= from) && keep(e);
        var kept = privateDays ? PrivateEntries(cli, Wanted) : log.ReadAll(Wanted);
        var entries = all || sinceText is not null ? kept : [.. kept.TakeLast(most)];
        if (cli.WriteJson(entries))
        {
            return Exit.Ok;
        }

        if (entries.Count == 0)
        {
            var where = privateDays ? "No private chat's decision is" : $"{log.Path}: nothing";
            cli.Note($"{where} recorded{(sinceText is null && filter == default ? string.Empty : " that matches")}.");
            return Exit.Ok;
        }

        foreach (var e in entries)
        {
            cli.Data(AuditQuery.Line(e));
        }

        return Exit.Ok;
    }

    private static int Verify(AuditLog log, Cli cli)
    {
        var check = log.Check();
        var broken = check.Problem;
        var seen = check.Witnessed;
        var earlier = check.Earlier;

        // How far back the Application log remembers: it is circular, so older anchors roll out of it; and
        // whether DipTick has a log of its own, which keeps them.
        var reaches = check.WitnessRead ? AuditLog.WitnessReaches() : null;
        var own = ProtectedLog.State();
        if (cli.WriteJson(new
        {
            log = log.Path,
            whole = broken is null,
            problem = broken,
            witnessed = seen is null ? null : new { lines = seen.Count, at = seen.When },
            eventLogRead = check.WitnessRead,
            eventLogReaches = reaches,
            ownLog = new { exists = own.Exists, kept = own.Protected, full = own.Full, since = own.Since, archives = own.Archives.Count },
            earlier = earlier is null ? null : new { lines = earlier.Lines, until = earlier.Until, accepted = earlier.Accepted },
        }))
        {
            return broken is null ? Exit.Ok : Exit.Failed;
        }

        if (broken is not null)
        {
            // An earlier log the event log saw: the person may have deleted it, and can say so.
            return cli.Fail(check.Unaccepted.Count == 0
                ? $"{log.Path}: {broken}."
                : $"{log.Path}: {broken}. If you deleted DipTick's data yourself, 'diptick audit accept' says so; the Windows event log keeps what it saw either way.");
        }

        if (!File.Exists(log.Path))
        {
            // No log, and nothing the event log saw of one: there is no chain to speak of yet.
            cli.Note($"{log.Path}: nothing recorded yet, so there is no chain to check.");
            return Exit.Ok;
        }

        var witness = !check.WitnessRead
            ? "the Windows event log could not be read, so its anchors were not compared"
            : seen is null
                ? "the Windows event log holds no anchor for it yet"
                : string.Create(CultureInfo.InvariantCulture, $"it agrees with every {(earlier is null ? string.Empty : "later ")}anchor the Windows event log kept (the newest: {seen.Count} {(seen.Count == 1 ? "line" : "lines")}, {seen.When.ToLocalTime():yyyy-MM-dd HH:mm})");
        var before = earlier is null
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $"an earlier log here ({earlier.Lines} {(earlier.Lines == 1 ? "line" : "lines")}, seen until {earlier.Until.ToLocalTime():yyyy-MM-dd HH:mm}) was deleted, as you said on {earlier.Accepted.ToLocalTime():yyyy-MM-dd HH:mm}; ");
        var reach = reaches is { } since ? string.Create(CultureInfo.InvariantCulture, $"; the Application log reaches back to {since.ToLocalTime():yyyy-MM-dd}") : string.Empty;
        cli.Note($"{log.Path}: the chain is whole, keyed and anchored; {before}{witness}{reach}. {char.ToUpperInvariant(own.Words[0])}{own.Words[1..]}.");
        return Exit.Ok;
    }

    /// <summary>
    /// <c>audit protect</c>: DipTick's own event log made, run as
    /// administrator; <c>--status</c> says what it is, for anyone; <c>--archive</c> saves it to ProgramData and
    /// clears it, run as administrator, when it is full.
    /// </summary>
    private static int Protect(VerbArgs words, Cli cli)
    {
        var status = words.Switch("--status");
        var archive = words.Switch("--archive");
        if (words.Problem is not null || words.Words().Count > 0 || (status && archive))
        {
            return cli.Fail(AuditUsage, Exit.Usage);
        }

        if (status)
        {
            var state = ProtectedLog.State();
            if (!cli.WriteJson(new { exists = state.Exists, kept = state.Protected, full = state.Full, size = state.FileSize, maxSize = state.MaxSize, records = state.Records, since = state.Since, archives = state.Archives, problem = state.Problem }))
            {
                cli.Note($"{char.ToUpperInvariant(state.Words[0])}{state.Words[1..]}.");
            }

            return !state.Exists || (state.Protected && !state.Full) ? Exit.Ok : Exit.Failed;
        }

        if (cli.Flags.DryRun)
        {
            cli.Note(archive ? "[dry run] would save DipTick's own event log to an archive and clear it; nothing changed." : "[dry run] would make DipTick's own event log; nothing changed.");
            return Exit.Ok;
        }

        // Said before any question: a yes this process could not act on is not asked for.
        if (ProtectedLog.AdministratorProblem(archive) is { } notAdministrator)
        {
            return cli.Fail($"{notAdministrator}.");
        }

        if (archive)
        {
            var sure = cli.Confirm($"Save DipTick's own event log whole to {ProtectedLog.ArchiveFolder}, where only administrators write, then clear it?");
            if (sure != Answer.Yes)
            {
                return cli.NotConfirmed(sure, "archiving DipTick's event log");
            }

            var (file, problem) = ProtectedLog.Archive();
            if (problem is not null)
            {
                return cli.Fail($"{problem}.");
            }

            cli.Note($"Saved to {file}, which verify reads too. The log is empty, and takes anchors again.");
            return Exit.Ok;
        }

        var answer = cli.Confirm("Make DipTick a log of its own in Windows' event log: 1 GiB, never overwritten, cleared only by an administrator, which programs running as you can write to but not clear?");
        if (answer != Answer.Yes)
        {
            return cli.NotConfirmed(answer, "making DipTick's event log");
        }

        if (ProtectedLog.Protect() is { } why)
        {
            return cli.Fail($"{why}.");
        }

        cli.Note("DipTick's own event log is made. The anchors go to it from now on; those already in the Application log are still read by verify.");
        return Exit.Ok;
    }

    /// <summary>The private days' decisions, oldest first, that <paramref name="keep"/> keeps.</summary>
    private static List<AuditEntry> PrivateEntries(Cli cli, Func<AuditEntry, bool> keep) =>
        [.. AuditLog.PrivateDays(cli.Config.AuditPrivateDays).SelectMany(day => day.ReadAll(keep))];

    /// <summary>What verifying each private day found: the first day that breaks, or null when every one is whole.</summary>
    private static string? PrivateProblem(Cli cli) =>
        AuditLog.PrivateDays(cli.Config.AuditPrivateDays).Select(d => d.Verify() is { } problem ? $"{Path.GetFileName(d.Path)}: {problem}" : null).FirstOrDefault(p => p is not null);

    /// <summary>
    /// <c>audit verify --private</c>: each private day's log checked against its own chain, key and
    /// anchor. They are not witnessed in the Windows event log: each is deleted whole after
    /// <c>audit.privateDays</c> days, by design.
    /// </summary>
    private static int VerifyPrivate(Cli cli)
    {
        var days = AuditLog.PrivateDays(cli.Config.AuditPrivateDays)
            .Select(d => (Day: Path.GetFileNameWithoutExtension(d.Path), Problem: d.Verify(), Lines: d.ReadAll(_ => true).Count))
            .ToList();
        var whole = days.TrueForAll(d => d.Problem is null);
        if (cli.WriteJson(new { days = days.Select(d => new { day = d.Day, lines = d.Lines, whole = d.Problem is null, problem = d.Problem }), keptFor = cli.Config.AuditPrivateDays }))
        {
            return whole ? Exit.Ok : Exit.Failed;
        }

        foreach (var (day, problem, lines) in days)
        {
            cli.Data(string.Create(CultureInfo.InvariantCulture, $"{day}  {lines,5} {(lines == 1 ? "line " : "lines")}  {problem ?? "whole, keyed and anchored"}"));
        }

        cli.Note(days.Count == 0
            ? $"No private chat's decision is kept. Each day's are kept {cli.Config.AuditPrivateDays} days, then deleted whole."
            : $"Each private day's decisions are kept {cli.Config.AuditPrivateDays} days, then deleted whole, so the Windows event log keeps no note of them.");
        return whole ? Exit.Ok : Exit.Failed;
    }

    /// <summary>
    /// <c>audit accept</c>: the person's word that the earlier log the Windows event log saw
    /// here was deleted by them, starting over. It is a line of the log, chained and keyed, so
    /// verify passes after it and still names the earlier log; the event log keeps what it saw
    /// either way. Only what the event log saw is accepted: a chain that does not hold together
    /// stays broken. A person's act: it asks first, and honours a dry run.
    /// </summary>
    private static int Accept(AuditLog log, Cli cli)
    {
        var check = log.Check();
        if (check.Unaccepted.Count == 0)
        {
            if (check.Problem is { } broken)
            {
                return cli.Fail($"{log.Path}: {broken}. That is not an earlier log the Windows event log saw, so there is nothing to accept.");
            }

            cli.Note($"{log.Path}: nothing to accept; the log agrees with every anchor the Windows event log kept.");
            return Exit.Ok;
        }

        var lines = check.Unaccepted.Max(w => w.Count);
        var earlier = string.Create(CultureInfo.InvariantCulture, $"an earlier log here, {lines} {(lines == 1 ? "line" : "lines")}, seen until {check.Unaccepted.Max(w => w.When).ToLocalTime():yyyy-MM-dd HH:mm}");
        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would record that you deleted {earlier}; nothing written.");
            return Exit.Ok;
        }

        var answer = cli.Confirm($"The Windows event log saw {earlier}, and this log is not it. Did you delete it yourself? A yes is kept in the log; verify passes after it and still names the earlier log.");
        if (answer != Answer.Yes)
        {
            return cli.NotConfirmed(answer, "accepting that the earlier log was deleted");
        }

        try
        {
            log.RecordAccepted(cli.Who, earlier);
        }
        catch (InvalidDataException ex)
        {
            return cli.Fail($"{log.Path}: {ex.Message}");
        }

        if (!cli.WriteJson(new { log = log.Path, accepted = earlier }))
        {
            cli.Note($"Recorded: you deleted {earlier}. 'diptick audit verify' names it from now on.");
        }

        return Exit.Ok;
    }

    /// <summary>
    /// <c>audit export</c>: the entries asked for, then whether the chain verified, to a file the
    /// person names. A person's act, like <c>chat export</c>: it honours a dry run and never
    /// overwrites without a yes.
    /// </summary>
    private static int Export(AuditLog log, VerbArgs words, Cli cli)
    {
        var format = words.Option("--format") ?? "jsonl";
        var privateDays = words.Switch("--private");
        var sinceText = words.Option("--since");
        var filter = Filter(words);
        var rest = words.Words();
        if (words.Problem is not null || rest.Count != 1)
        {
            return cli.Fail(AuditUsage, Exit.Usage);
        }

        if (!AuditQuery.Formats.Contains(format))
        {
            return cli.Fail("--format is jsonl, csv or text.", Exit.Usage);
        }

        if (Since(sinceText, cli) is not { } since || Checked(filter, cli) is not { } keep)
        {
            return Exit.Usage;
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

        bool Wanted(AuditEntry e) => (since.Value is not { } from || e.When >= from) && keep(e);
        var entries = privateDays ? PrivateEntries(cli, Wanted) : log.ReadAll(Wanted);
        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would write {Entries(entries.Count)} and the chain's check to {path}; nothing written.");
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

        var problem = privateDays ? PrivateProblem(cli) : log.Verify();
        try
        {
            File.WriteAllText(path, AuditQuery.Write(entries, format, problem, DateTimeOffset.UtcNow), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return cli.Fail($"could not write {path}: {ex.Message}");
        }

        if (!cli.WriteJson(new { path, format, entries = entries.Count, whole = problem is null, problem }))
        {
            cli.Note($"Wrote {Entries(entries.Count)} to {path}; the chain {(problem is null ? "is whole" : $"is broken: {problem}")}.");
        }

        return Exit.Ok;
    }

    private static string Entries(int count) => count == 1 ? "1 entry" : count.ToString(CultureInfo.InvariantCulture) + " entries";

    /// <summary>--since as a moment, or no moment when it was not given; a usage failure (null) when it cannot be read.</summary>
    private static Box? Since(string? text, Cli cli)
    {
        if (text is null)
        {
            return new Box(null);
        }

        if (AuditQuery.Since(text, DateTimeOffset.Now) is { } from)
        {
            return new Box(from);
        }

        _ = cli.Fail($"--since takes today, yesterday, a date like 2026-09-20, or an ISO date and time; '{SafeText.Line(text)}' is none of these.", Exit.Usage);
        return null;
    }

    /// <summary>A moment, or none: what --since came to.</summary>
    private sealed record Box(DateTimeOffset? Value);

    /// <summary>--who, --what and --verdict, as typed; each null when not given.</summary>
    private static (string? Who, string? What, string? Verdict) Filter(VerbArgs words) =>
        (words.Option("--who"), words.Option("--what"), words.Option("--verdict"));

    /// <summary>The filter as a test on an entry; null, after saying why, when --what or --verdict names nothing DipTick has.</summary>
    private static Func<AuditEntry, bool>? Checked((string? Who, string? What, string? Verdict) filter, Cli cli)
    {
        Capability? what = null;
        if (filter.What is { } name)
        {
            what = Capabilities.Parse(name);
            if (what is null)
            {
                _ = cli.Fail($"--what takes a capability: {string.Join(", ", Capabilities.Names)}.", Exit.Usage);
                return null;
            }
        }

        if (filter.Verdict is { } verdict && !Enum.GetNames<Verdict>().Contains(verdict, StringComparer.OrdinalIgnoreCase))
        {
            _ = cli.Fail($"--verdict takes {string.Join(", ", Enum.GetNames<Verdict>())}.", Exit.Usage);
            return null;
        }

        return e => (filter.Who is not { } who || AuditQuery.IsBy(e, who))
            && (what is not { } cap || Capabilities.Parse(e.Capability) == cap)
            && (filter.Verdict is not { } v || string.Equals(e.Verdict, v, StringComparison.OrdinalIgnoreCase));
    }

    private static int Check(Capability cap, string? target, Cli cli)
    {
        var decision = Models.Harness.Policy.Decide(cap, target, cli.Config.Permissions, cli.Flags);
        if (!cli.WriteJson(new { capability = Capabilities.Name(cap), target, verdict = decision.Verdict.ToString(), reason = decision.Reason }))
        {
            cli.Data($"{decision.Verdict}: {decision.Reason}");
        }

        return ExitFor(decision);
    }

    private static int Try(Capability cap, string? target, Cli cli)
    {
        var decision = Models.Harness.Policy.Decide(cap, target, cli.Config.Permissions, cli.Flags);
        var gate = new Gate(cli.Config.Permissions, cli.Flags, AuditLog.ForThisUser(), cli.Who, cli.Err, question => cli.Flags.Interactive ? ConsoleInput.Ask(question, cli.Err) : null);
        var yes = gate.Request("policy try", cap, target);
        if (!cli.WriteJson(new { capability = Capabilities.Name(cap), target, allowed = yes }))
        {
            cli.Data(yes ? "yes" : "no");
        }

        if (yes)
        {
            return Exit.Ok;
        }

        // A dry run in which nothing would be refused is a success. A policy "allow" the
        // gate still refused (a person said no, or the audit log could not be written) is not.
        return decision.Verdict switch
        {
            Verdict.DryRun => Exit.Ok,
            Verdict.Allow => Exit.Failed,
            _ => ExitFor(decision),
        };
    }

    /// <summary>
    /// 0 only when the request would go ahead as asked, or is a dry run the person chose; an Ask
    /// is 3 (nothing may happen without a yes), so a script gating on the exit code cannot act
    /// on an unanswered question.
    /// </summary>
    private static int ExitFor(Decision decision) => decision.Verdict switch
    {
        Verdict.Allow or Verdict.DryRun => Exit.Ok,
        Verdict.Ask => Exit.NoYes,
        Verdict.Deny when decision.NeedsPerson => Exit.NoYes,
        _ => Exit.Failed,
    };

    private static int Show(Cli cli)
    {
        var p = cli.Config.Permissions;
        var each = Capabilities.Names.Select(name =>
        {
            var cap = Capabilities.Parse(name)!.Value;
            var rule = p.Deny.Any(r => Capabilities.Matches(r, cap)) ? "deny"
                : p.Ask.Any(r => Capabilities.Matches(r, cap)) ? "ask"
                : p.Allow.Any(r => Capabilities.Matches(r, cap)) ? "allow"
                : "deny (no rule)";
            return (name, rule, changes: Capabilities.ChangesState(cap));
        }).ToList();

        if (cli.WriteJson(new
        {
            allow = p.Allow,
            ask = p.Ask,
            deny = p.Deny,
            roots = p.Roots,
            hosts = p.Hosts,
            capabilities = each.Select(e => new { e.name, e.rule, changesState = e.changes }),
        }))
        {
            return Exit.Ok;
        }

        cli.Note("Anything no rule names is denied.");
        foreach (var problem in cli.Config.Problems.Where(p => p.Contains("permissions", StringComparison.OrdinalIgnoreCase)))
        {
            cli.Err.WriteLine($"  ! {problem}");
        }
        Line(cli, "allow", p.Allow);
        Line(cli, "ask", p.Ask);
        Line(cli, "deny", p.Deny);
        Line(cli, "roots", p.Roots);
        Line(cli, "hosts", p.Hosts);
        cli.Data(string.Empty);
        foreach (var (name, rule, changes) in each)
        {
            cli.Data($"  {name,-10} {rule}{(changes ? "  changes state" : string.Empty)}");
        }

        return Exit.Ok;
    }

    private static void Line(Cli cli, string name, IReadOnlyList<string> items) =>
        cli.Data($"  {name,-6} {(items.Count == 0 ? "(none)" : string.Join(", ", items))}");
}
