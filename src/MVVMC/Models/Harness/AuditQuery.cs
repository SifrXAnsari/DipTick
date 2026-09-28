using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DipTick.Models.Harness;

/// <summary>
/// Which of the audit log's entries a person asks for: from a date, by
/// who asked, and how they are written out. The command line and the Activity page ask the same
/// way, so the two cannot differ.
/// </summary>
internal static class AuditQuery
{
    /// <summary>The formats <c>diptick audit export</c> writes.</summary>
    public static readonly IReadOnlyList<string> Formats = ["jsonl", "csv", "text"];

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// A moment from what a person typed: <c>today</c>, <c>yesterday</c>, a date (<c>2026-09-20</c>,
    /// from that day's start here), or a date and time in ISO 8601. Null when it is none of these.
    /// </summary>
    public static DateTimeOffset? Since(string text, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(text);
        var local = now.ToLocalTime();
        if (string.Equals(text.Trim(), "today", StringComparison.OrdinalIgnoreCase))
        {
            return new DateTimeOffset(local.Date, local.Offset);
        }

        if (string.Equals(text.Trim(), "yesterday", StringComparison.OrdinalIgnoreCase))
        {
            return new DateTimeOffset(local.Date.AddDays(-1), local.Offset);
        }

        if (DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var day))
        {
            return new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day));
        }

        return DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at) ? at : null;
    }

    /// <summary>Whether an entry was asked for by <paramref name="who"/>: the person it was for, or the agent that asked.</summary>
    public static bool IsBy(AuditEntry entry, string who)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(who);
        return string.Equals(entry.Who, who, StringComparison.OrdinalIgnoreCase) || string.Equals(entry.Actor, who, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One entry as <c>diptick audit</c> prints it.</summary>
    public static string Line(AuditEntry e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var actor = e.Actor is { Length: > 0 } a ? $", {SafeText.Line(a)}" : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{e.When:yyyy-MM-dd HH:mm:ss}Z  {SafeText.Line(e.Verdict),-6}  {SafeText.Line(e.Capability),-9}  {SafeText.Line(e.Target)}  ({SafeText.Line(e.Reason)}; {SafeText.Line(e.Who)}{actor} via {SafeText.Line(e.Verb)})");
    }

    /// <summary>
    /// The entries written out in <paramref name="format"/>, then whether the chain verified
    /// (<paramref name="problem"/> null: whole): JSON Lines ending in a <c>verify</c> object, CSV
    /// ending in a <c>#verify</c> row, or text ending in a <c>chain:</c> line.
    /// </summary>
    public static string Write(IReadOnlyList<AuditEntry> entries, string format, string? problem, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var text = new StringBuilder();
        var verdict = problem is null ? "whole" : "broken";
        switch (format)
        {
            case "jsonl":
                foreach (var e in entries)
                {
                    text.Append(JsonSerializer.Serialize(e, Json)).Append('\n');
                }

                text.Append(JsonSerializer.Serialize(new { verify = new { whole = problem is null, problem, at } }, Json)).Append('\n');
                break;
            case "csv":
                text.Append("when,who,actor,verb,capability,target,verdict,reason,previous,hash\r\n");
                foreach (var e in entries)
                {
                    text.AppendJoin(',', Cells(e).Select(Csv)).Append("\r\n");
                }

                text.AppendJoin(',', new[] { "#verify", verdict, problem ?? string.Empty, at.ToString("O", CultureInfo.InvariantCulture) }.Select(Csv)).Append("\r\n");
                break;
            default:
                foreach (var e in entries)
                {
                    text.Append(Line(e)).Append('\n');
                }

                text.Append("chain: ").Append(problem is null ? "whole, keyed and anchored" : $"broken: {problem}").Append('\n');
                break;
        }

        return text.ToString();
    }

    private static string[] Cells(AuditEntry e) =>
    [
        e.When.ToString("O", CultureInfo.InvariantCulture), e.Who, e.Actor ?? string.Empty, e.Verb, e.Capability, e.Target, e.Verdict, e.Reason, e.Previous, e.Hash,
    ];

    /// <summary>
    /// A CSV cell (RFC 4180), guarded as OWASP's CSV Injection page says (community.owasp.org, read
    /// 2026-09-27): every cell in double quotes, its quotes doubled, so no separator (a comma,
    /// or the semicolon some locales use) or line break inside it ends it; and a cell that starts with
    /// =, +, -, @, a tab, a carriage return or a line feed kept as text by a tab before it, inside its
    /// quotes. A single quote before it "is not reliable in Microsoft Excel after saving and re-opening
    /// the CSV file"; the tab is what the page gives for Excel. The tab stays in the cell: this file is
    /// for reading, and JSON Lines is the export for programs.
    /// </summary>
    private static string Csv(string cell)
    {
        // A spreadsheet reads a cell starting with these as a formula (CWE-1236).
        var safe = cell.Length > 0 && cell[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n' ? "\t" + cell : cell;
        return "\"" + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
