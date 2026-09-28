using System.Globalization;
using DipTick.Models;
using DipTick.Models.Chat.Seam;
using DipTick.Models.Chat.Store;
using DipTick.Models.Harness;
using DipTick.Models.Research;

namespace DipTick.Controllers;

/// <summary>
/// A chat's research: <c>chat research [on|off]</c>, the switch the Research
/// chip turns; and <c>chat sources</c>, what the chat's answers were given to read, numbered.
/// </summary>
internal static partial class ChatController
{
    private const string ResearchUsage = "usage: diptick chat research [on|off] [--session <id|last>]";
    private const string SourcesUsage = "usage: diptick chat sources [--session <id|last>] [--seq <n>]";

    /// <summary><c>chat research</c>: the chat's research state, or switches it; every change is an audit line first.</summary>
    private static int Research(IChatStore store, Cli cli, VerbArgs words, bool isPrivate)
    {
        var session = words.Option("--session") ?? "last";
        var rest = words.Words();
        if (words.Problem is not null || rest.Count > 1 || (rest.Count == 1 && rest[0] is not ("on" or "off")))
        {
            return cli.Fail(ResearchUsage, Exit.Usage);
        }

        if (store.Find(session) is not { } found)
        {
            return cli.Fail(NoSuch(session));
        }

        if (rest.Count == 1)
        {
            var log = isPrivate ? AuditLog.ForPrivate(cli.Config.AuditPrivateDays) : AuditLog.ForThisUser();
            if (!SetResearch(store, cli, log, found.Id, rest[0] == "on", cli.Flags.DryRun))
            {
                return Exit.Failed;
            }
        }

        var agents = Table.From(store.Table(found.Id)).Select(s => Table.Resolve(cli.Config, s)).OfType<SeatConfig>().Where(a => a.Filled).ToList();
        var state = ResearchSwitch.State(cli.Config, agents, store.ChatRules(found.Id));
        if (!cli.WriteJson(new { session = found.Id, research = state switch { ResearchState.On => "on", ResearchState.Off => "off", _ => "not-set-up" }, service = SearchService.Name(cli.Config), problem = SearchService.Problem(cli.Config) }))
        {
            cli.Data(ResearchSwitch.Words(state));
            if (state == ResearchState.NotSetUp && SearchService.Problem(cli.Config) is { } missing)
            {
                cli.Note(missing);
            }
        }

        return Exit.Ok;
    }

    /// <summary>
    /// Research on or off for a chat: off keeps the chat's rules denying <c>web.search</c> and
    /// <c>net.fetch</c>; on takes them away. Each change is written in the audit log first; a dry run
    /// says what it would do and changes nothing.
    /// </summary>
    internal static bool SetResearch(IChatStore store, Cli cli, AuditLog log, string session, bool on, bool dry = false)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(cli);
        ArgumentNullException.ThrowIfNull(log);
        if (dry)
        {
            cli.Note($"[dry run] would switch research {(on ? "on" : "off")} in this chat; nothing changed.");
            return true;
        }

        foreach (var rule in ResearchSwitch.Rules)
        {
            if (!on)
            {
                if (!KeepChatRule(store, cli, log, session, rule, Verdict.Deny))
                {
                    return false;
                }

                continue;
            }

            if (!store.ChatRules(session).Any(r => r.Verdict == Verdict.Deny && string.Equals(r.Rule, rule, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            try
            {
                _ = log.RecordPolicy(cli.Who, $"chat {session}", rule, "deny taken away in this chat (research on)");
                store.ClearChatRule(session, rule);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or Microsoft.Data.Sqlite.SqliteException)
            {
                cli.Err.WriteLine($"diptick: research was not switched on: {SafeText.Line(ex.Message)}");
                return false;
            }
        }

        return true;
    }

    /// <summary><c>chat sources</c>: every source of the chat, or of one answer, by number: its title, address, how it came, the answer it served and when.</summary>
    private static int Sources(IChatStore store, Cli cli, VerbArgs words)
    {
        var session = words.Option("--session") ?? "last";
        var seqText = words.Option("--seq");
        if (words.Problem is not null || words.Words().Count > 0)
        {
            return cli.Fail(SourcesUsage, Exit.Usage);
        }

        int? seq = null;
        if (seqText is not null)
        {
            if (!int.TryParse(seqText, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1)
            {
                return cli.Fail("--seq takes a line's number.", Exit.Usage);
            }

            seq = n;
        }

        if (store.Find(session) is not { } found)
        {
            return cli.Fail(NoSuch(session));
        }

        var sources = store.Sources(found.Id).Where(s => seq is null || s.Seq == seq).ToList();
        if (cli.WriteJson(new
        {
            session = found.Id,
            sources = sources.Select(s => new { number = s.Id, seq = s.Seq, address = s.Address, title = s.Title, kind = s.Kind, fetched = s.Fetched, sha256 = s.Sha256 }),
        }))
        {
            return Exit.Ok;
        }

        if (sources.Count == 0)
        {
            cli.Note(seq is null ? "No sources in this chat yet." : "No sources for that line.");
            return Exit.Ok;
        }

        foreach (var s in sources)
        {
            var answer = s.Seq is { } n ? string.Create(CultureInfo.InvariantCulture, $" · answer #{n}") : string.Empty;
            var when = s.Fetched is { } at ? $" · {at.ToLocalTime():yyyy-MM-dd HH:mm}" : string.Empty;
            cli.Data(SafeText.Line(string.Create(CultureInfo.InvariantCulture, $"  [{s.Id}] {(s.Title.Length > 0 ? s.Title : s.Address)}")));
            cli.Data(SafeText.Line($"      {s.Address} · {SourceKind.Words(s.Kind)}{answer}{when}"));
        }

        return Exit.Ok;
    }
}
