using System.Globalization;
using DipTick.Models;
using DipTick.Models.Chat.Seam;
using DipTick.Models.Chat.Store;
using DipTick.Models.Harness;

namespace DipTick.Controllers;

/// <summary>
/// <c>diptick chat list|show|new|say|title|pin|export|close|delete</c>: the person's sessions.
/// The window's chat and its session list run these verbs and draw
/// what they print; the window does nothing with sessions that these cannot.
/// </summary>
/// <remarks>
/// Every subcommand uses the store <see cref="ChatStores.Open"/> gives: the file, or with
/// <c>chat.save</c> false this process's memory; with <c>--private</c>, a private chat's, this process's
/// memory apart from every other chat, whatever <c>chat.save</c> says. A
/// private chat's decisions go to that day's private audit log, and it is never written to a file.
/// A dry run reads and writes nothing, and says what it would have done. <c>delete</c> is severe: it takes <c>--confirm=&lt;id&gt;</c>, and
/// <c>--yes</c> alone is refused. Words after a lone <c>--</c> are text, never options.
/// </remarks>
internal static partial class ChatController
{
    private const string Usage = "usage: diptick chat list [--all] | show [--session <id|last>] [--seq <n> [--markdown]] | new [--title <t>] | say [--session <id|last|new>] [--to <name>|team|a,b] [--mode <mode>] [--attach <file>]... [--events] [--] <text…>|- | pass | retry [--seq <n>] [--to <name>] | seats [list|add <agent>|remove <name>|model <name> <model>] | mode [<mode>] | brief [--seat <name>] [--set <text>] | cost [--today] | research [on|off] | sources [--seq <n>] | files [--seq <n>] [--file <id>] [--bytes] | save (--seq <n> [--block <k>] | --file <id>) <file> | connectors [add|remove <name>] | skills [add|remove <name>] | title [--session <id|last>] [--] <title> | pin [--session <id|last>] [--off] | export [--session <id|last>] [--format text|jsonl] [--] <file> | close [--session <id|last>] | delete <id> --confirm=<id>; each takes --private for a private chat";

    public static int Run(IReadOnlyList<string> args, Cli cli)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(cli);
        var sub = args.Count > 1 ? args[1] : "list";
        var words = new VerbArgs(args, 2, cli.LiteralFrom);
        var isPrivate = words.Switch("--private");

        // The private days past audit.privateDays go whenever a chat verb runs, as the window and the
        // terminal screen run them all the time: none waits for the next private chat.
        _ = AuditLog.SweepPrivate(DateTimeOffset.Now, cli.Config.AuditPrivateDays);
        try
        {
            using var store = ChatStores.Open(cli.Config, isPrivate);
            return sub switch
            {
                "list" => List(store, cli, words),
                "show" => Show(store, cli, words),
                "new" => New(store, cli, words),
                "say" => Say(store, cli, words, isPrivate),
                "pass" => Pass(store, cli, words, isPrivate),
                "retry" => Retry(store, cli, words, isPrivate),
                "seats" => Seats(store, cli, words),
                "mode" => Mode(store, cli, words),
                "brief" => Brief(store, cli, words),
                "cost" => Cost(store, cli, words),
                "research" => Research(store, cli, words, isPrivate),
                "sources" => Sources(store, cli, words),
                "files" => Files(store, cli, words),
                "connectors" => Connectors(store, cli, words),
                "skills" => Skills(store, cli, words),
                "save" => Save(store, cli, words),
                "title" => Title(store, cli, words),
                "pin" when isPrivate => cli.Fail("a private chat is never in the list, so it cannot be pinned.", Exit.Usage),
                "pin" => Pin(store, cli, words),
                "export" when isPrivate => cli.Fail("a private chat is never written to a file by DipTick; what you copy from it yourself is yours to keep.", Exit.Usage),
                "export" => Export(store, cli, words),
                "close" => Close(store, cli, words),
                "delete" => Delete(store, cli, words),
                _ => cli.Fail(Usage, Exit.Usage),
            };
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            return cli.Fail($"the session store could not be used: {ex.Message}");
        }
        catch (IOException ex)
        {
            return cli.Fail($"the session store could not be used: {ex.Message}");
        }
    }

    private static int List(IChatStore store, Cli cli, VerbArgs words)
    {
        var all = words.Switch("--all");
        if (words.Words().Count > 0 || words.Problem is not null)
        {
            return cli.Fail(Usage, Exit.Usage);
        }

        // Pinned ones first, as the window's list has them; each group newest first.
        List<SessionInfo> sessions = [.. store.List(all).OrderByDescending(s => s.Pinned)];
        if (cli.WriteJson(new
        {
            store = store.Location,
            sessions = sessions.Select(s => new { s.Id, s.Title, s.Person, s.Mode, created = s.Created, updated = s.Updated, status = s.Open ? "open" : "closed", lines = s.Messages, busyIn = s.LeasePid, pinned = s.Pinned }),
        }))
        {
            return Exit.Ok;
        }

        if (sessions.Count == 0)
        {
            cli.Note(all ? "No sessions." : "No open sessions. 'diptick chat list --all' includes closed ones.");
            return Exit.Ok;
        }

        foreach (var s in sessions)
        {
            var busy = s.LeasePid is { } pid ? $" · busy in process {pid.ToString(CultureInfo.InvariantCulture)}" : string.Empty;
            var title = s.Title.Length == 0 ? "(untitled)" : s.Title;
            var pinned = s.Pinned ? " · pinned" : string.Empty;
            cli.Data(SafeText.Line(string.Create(CultureInfo.InvariantCulture, $"  {s.Id}  {s.Updated.ToLocalTime():yyyy-MM-dd HH:mm}  {s.Messages,4} lines  {(s.Open ? "open  " : "closed")}  {title}{pinned}{busy}")));
        }

        return Exit.Ok;
    }

    private static int Show(IChatStore store, Cli cli, VerbArgs words)
    {
        var session = words.Option("--session") ?? "last";
        var seq = words.Option("--seq");
        var markdown = words.Switch("--markdown");
        if (words.Words().Count > 0 || words.Problem is not null || (markdown && seq is null))
        {
            return cli.Fail(Usage, Exit.Usage);
        }

        if (store.Find(session) is not { } found)
        {
            return cli.Fail(NoSuch(session));
        }

        var lines = store.Messages(found.Id);
        if (seq is not null)
        {
            return ShowOne(cli, found.Id, lines, seq, markdown);
        }
        if (cli.WriteJson(new
        {
            session = found.Id,
            found.Title,
            found.Person,
            found.Mode,
            seats = Table.From(store.Table(found.Id)).Select(s => new { name = s.Name, agent = s.Profile, model = s.Model, color = Table.Resolve(cli.Config, s)?.Color }),
            lines = lines.Select(l => new
            {
                l.Seq, l.Round, l.Step, l.Author, l.Kind, l.Status, l.Text, started = l.Started, ended = l.Ended,
                tokensIn = l.TokensIn, tokensOut = l.TokensOut, estimated = l.TokensEstimated, cost = Cost(l.CostMicros),

                // A tool line's answer, by its number: the answer it was called for, which it is shown under.
                under = l.Kind == MessageKind.Tool && l.ReplyTo is { } answer ? lines.FirstOrDefault(a => a.Id == answer)?.Seq : null,
            }),
            acts = store.Acts(found.Id).Select(a => new
            {
                a.Seq, a.Seat, a.Kind, to = a.Target.Split(',', StringSplitOptions.RemoveEmptyEntries), question = a.Text, a.Accepted, a.Code,
            }),
        }))
        {
            return Exit.Ok;
        }

        cli.Note($"{found.Id} · {(found.Title.Length == 0 ? "(untitled)" : found.Title)} · {lines.Count.ToString(CultureInfo.InvariantCulture)} lines");
        foreach (var line in lines)
        {
            // Every line carries its author's mark, so piped output cannot be spoofed by a model's words.
            var mark = line.Author switch { Author.Person => $"{found.Person} ›", Author.DipTick => "·", _ => $"{line.Author} ┊" };
            var state = line.Status is MessageStatus.Done or MessageStatus.Passed ? string.Empty : $" [{line.Status}]";
            var text = SafeText.Block(line.Text).Replace("\n", "\n    ", StringComparison.Ordinal);
            cli.Data($"{mark} {text}{state}");
        }

        return Exit.Ok;
    }

    /// <summary>
    /// <c>show --seq &lt;n&gt;</c>: one line's text alone, the same text the window's Copy puts on the
    /// clipboard: an answer as it reads, without its Markdown marks, unless <c>--markdown</c>
    /// asks for it as written.
    /// </summary>
    private static int ShowOne(Cli cli, string id, IReadOnlyList<StoredMessage> lines, string seq, bool markdown)
    {
        if (!int.TryParse(seq, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            return cli.Fail($"--seq takes a line's number, like 3; '{SafeText.Line(seq)}' is not one.", Exit.Usage);
        }

        if (lines.FirstOrDefault(l => l.Seq == n) is not { } line)
        {
            return cli.Fail($"{id} has no line {n.ToString(CultureInfo.InvariantCulture)}. 'diptick chat show --session {id} --output json' numbers them.");
        }

        var answer = line.Author is not (Author.Person or Author.DipTick);
        var text = answer && !markdown ? MarkdownText.Plain(line.Text) : SafeText.Block(line.Text);
        if (!cli.WriteJson(new { session = id, seq = n, line.Author, text }))
        {
            cli.Data(text);
        }

        return Exit.Ok;
    }

    private static int New(IChatStore store, Cli cli, VerbArgs words)
    {
        var title = words.Option("--title");
        if (words.Words().Count > 0 || words.Problem is not null)
        {
            return cli.Fail(Usage, Exit.Usage);
        }

        if (cli.Flags.DryRun)
        {
            cli.Note("[dry run] would create a session; nothing created.");
            return Exit.Ok;
        }

        // The table and the mode a new chat starts with, the same as a first 'chat say' gives it.
        var id = store.Create(cli.Who, Starting(cli.Config), title is null ? null : SafeText.Line(title).Trim(), [.. Table.Starting(cli.Config).Select(s => Table.Record(cli.Config, s))]);
        if (!cli.WriteJson(new { session = id }))
        {
            // The id alone on stdout, so a script can take it; the words go to stderr.
            cli.Data(id);
            cli.Note($"Created session {id}. 'diptick chat say --session {id} -- …' talks in it; 'diptick ui --session {id}' opens it.");
        }

        return Exit.Ok;
    }

    private static int Title(IChatStore store, Cli cli, VerbArgs words)
    {
        var session = words.Option("--session") ?? "last";
        var title = words.Words();
        if (words.Problem is not null || title.Count == 0)
        {
            return cli.Fail(Usage, Exit.Usage);
        }

        if (store.Find(session) is not { } found)
        {
            return cli.Fail(NoSuch(session));
        }

        var clean = SafeText.Line(string.Join(' ', title)).Trim();
        if (clean.Length == 0)
        {
            return cli.Fail("a title needs some words.", Exit.Usage);
        }

        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would name {found.Id} '{clean}'; nothing changed.");
            return Exit.Ok;
        }

        store.Title(found.Id, clean);
        if (!cli.WriteJson(new { session = found.Id, title = clean }))
        {
            cli.Note($"{found.Id} is now '{clean}'.");
        }

        return Exit.Ok;
    }

    /// <summary><c>chat pin</c>: keeps a session at the top of the list; <c>--off</c> lets it go back among the rest.</summary>
    private static int Pin(IChatStore store, Cli cli, VerbArgs words)
    {
        var session = words.Option("--session") ?? "last";
        var off = words.Switch("--off");
        if (words.Words().Count > 0 || words.Problem is not null)
        {
            return cli.Fail(Usage, Exit.Usage);
        }

        if (store.Find(session) is not { } found)
        {
            return cli.Fail(NoSuch(session));
        }

        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would {(off ? "unpin" : "pin")} {found.Id}; nothing changed.");
            return Exit.Ok;
        }

        store.Pin(found.Id, !off);
        if (!cli.WriteJson(new { session = found.Id, pinned = !off }))
        {
            cli.Note(off ? $"{found.Id} is no longer pinned." : $"Pinned {found.Id}: it stays at the top of the list.");
        }

        return Exit.Ok;
    }

    private static int Close(IChatStore store, Cli cli, VerbArgs words)
    {
        var session = words.Option("--session") ?? "last";
        if (words.Words().Count > 0 || words.Problem is not null)
        {
            return cli.Fail(Usage, Exit.Usage);
        }

        if (store.Find(session) is not { } found)
        {
            return cli.Fail(NoSuch(session));
        }

        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would close {found.Id}; nothing changed.");
            return Exit.Ok;
        }

        store.Close(found.Id);
        if (!cli.WriteJson(new { session = found.Id, status = "closed" }))
        {
            cli.Note($"Closed {found.Id}. 'diptick chat list --all' still shows it.");
        }

        return Exit.Ok;
    }

    private static int Delete(IChatStore store, Cli cli, VerbArgs words)
    {
        var confirm = words.Option("--confirm");
        var rest = words.Words();
        if (words.Problem is not null || rest.Count != 1)
        {
            return cli.Fail(Usage, Exit.Usage);
        }

        var id = rest[0];
        if (string.Equals(id, "last", StringComparison.OrdinalIgnoreCase) || store.Find(id) is not { } found)
        {
            return cli.Fail(NoSuch(id));
        }

        if (confirm is null)
        {
            return cli.Fail($"deleting a session is for keeps: say which one twice, 'diptick chat delete {found.Id} --confirm={found.Id}'. --yes does not delete.", Exit.NoYes);
        }

        if (!string.Equals(confirm, found.Id, StringComparison.Ordinal))
        {
            return cli.Fail("--confirm names a different session; nothing deleted.", Exit.Usage);
        }

        if (found.LeasePid is { } pid && pid != Environment.ProcessId)
        {
            return cli.Fail($"{found.Id} is busy in process {pid.ToString(CultureInfo.InvariantCulture)}; wait for its turn to end.");
        }

        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would delete {found.Id} and its {found.Messages.ToString(CultureInfo.InvariantCulture)} lines; nothing changed.");
            return Exit.Ok;
        }

        store.Delete(found.Id);
        if (!cli.WriteJson(new { session = found.Id, deleted = true }))
        {
            cli.Note($"Deleted {found.Id}.");
        }

        return Exit.Ok;
    }

    private static string NoSuch(string session) => string.Equals(session, "last", StringComparison.OrdinalIgnoreCase)
        ? "no open session yet. 'diptick chat say -- <words>' starts one; 'diptick chat list --all' shows closed ones."
        : $"no session '{SafeText.Line(session)}'. 'diptick chat list --all' shows them.";
}
