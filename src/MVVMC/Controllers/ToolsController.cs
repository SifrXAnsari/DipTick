using System.Globalization;
using System.Text;
using DipTick.Models;
using DipTick.Models.Tools;

namespace DipTick.Controllers;

/// <summary>
/// <c>diptick tools [list]</c> and one verb per tool: DipTick's own tools, the
/// same ones the Tools sheet shows and agents may call. <c>time</c> the clock, <c>cal</c> the calendar,
/// <c>calc</c> the calculator, <c>net</c> the network check. Here the person asks, so no gate: the
/// network check reaches any host the person types, where an agent's reaches only DipTick's own.
/// </summary>
internal static class ToolsController
{
    private const string Usage = "usage: diptick tools [list] | time [--zone <id>] | cal [<yyyy-mm>] [--between <date> <date>] [--add <date> <days>] | calc <expression> | net [<host>|<url>]";

    private static readonly (string Name, string Agents, string What)[] Tools =
    [
        ("time", "time_now", "the date, the time, the time zone and its offset from UTC, here or in another zone"),
        ("cal", "calendar_days", "a month with its ISO weeks; the days between two dates; a date plus days"),
        ("calc", "calc", "exact decimal arithmetic: + - * / ^ ( ), %, abs round floor ceil min max sqrt"),
        ("net", "net_check", "is this PC online, and can DipTick reach each service it uses; any host you name"),
    ];

    public static int Run(IReadOnlyList<string> args, Cli cli) =>
        (args.Count > 1 ? args[1] : "list") switch
        {
            "list" when args.Count <= 2 => List(cli),
            "time" => Time(args, cli),
            "cal" => Calendar(new VerbArgs(args, 2, cli.LiteralFrom), cli),
            "calc" => Calc(args, cli),
            "net" => Net(new VerbArgs(args, 2, cli.LiteralFrom), cli),
            _ => cli.Fail(Usage, Exit.Usage),
        };

    private static int List(Cli cli)
    {
        if (cli.WriteJson(new { tools = Tools.Select(t => new { name = t.Name, agents = t.Agents, what = t.What }) }))
        {
            return Exit.Ok;
        }

        foreach (var (name, agents, what) in Tools)
        {
            cli.Data($"  {name,-5} {what} (agents: {agents})");
        }

        return Exit.Ok;
    }

    /// <summary>
    /// The time now, here or in another zone. Windows' own zone ids work, and so do IANA ids
    /// (America/New_York), which .NET converts on Windows.
    /// </summary>
    private static int Time(IReadOnlyList<string> args, Cli cli)
    {
        string? zoneId = null;
        if (args.Count == 4 && args[2] == "--zone")
        {
            zoneId = args[3];
        }
        else if (args.Count != 2)
        {
            return cli.Fail(Usage, Exit.Usage);
        }

        if (ClockReading.Read(zoneId, DateTimeOffset.UtcNow) is not { } now)
        {
            return cli.Fail($"no time zone '{SafeText.Line(zoneId ?? string.Empty)}' on this PC. Windows' ids (Eastern Standard Time) and IANA ids (America/New_York) both work.", Exit.Usage);
        }

        if (cli.WriteJson(new
        {
            date = now.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            weekday = now.Now.ToString("dddd", CultureInfo.InvariantCulture),
            time = now.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            zone = now.Zone.Id,
            zoneName = now.ZoneName,
            offset = now.Offset,
            utc = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        }))
        {
            return Exit.Ok;
        }

        // For a person: the date and time in their own Windows format, then the zone.
        cli.Data($"{now.Now.ToString("F", CultureInfo.CurrentCulture)}  ({now.ZoneName}, UTC{now.Offset})");
        return Exit.Ok;
    }

    /// <summary><c>tools cal</c>: a month with its ISO weeks, or the days between two dates, or a date plus days.</summary>
    private static int Calendar(VerbArgs words, Cli cli)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var between = words.Options("--between", 2);
        var add = words.Options("--add", 2);
        var rest = words.Words();
        if (words.Problem is not null || rest.Count > 1 || (between is not null && add is not null))
        {
            return cli.Fail(Usage, Exit.Usage);
        }

        if (between is [var a, var b])
        {
            if (CalendarMath.Read(a, today) is not { } from || CalendarMath.Read(b, today) is not { } to)
            {
                return cli.Fail("the dates are yyyy-mm-dd, or today, yesterday, tomorrow.", Exit.Usage);
            }

            var days = CalendarMath.Between(from, to);
            if (!cli.WriteJson(new { from = $"{from:yyyy-MM-dd}", to = $"{to:yyyy-MM-dd}", days }))
            {
                cli.Data(string.Create(CultureInfo.InvariantCulture, $"From {CalendarMath.Describe(from)} to {CalendarMath.Describe(to)}: {days} days."));
            }

            return Exit.Ok;
        }

        if (add is [var start, var count])
        {
            if (CalendarMath.Read(start, today) is not { } date || !long.TryParse(count, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) || CalendarMath.Add(date, n) is not { } then)
            {
                return cli.Fail("--add takes a date (yyyy-mm-dd, or today) and a whole number of days.", Exit.Usage);
            }

            if (!cli.WriteJson(new { from = $"{date:yyyy-MM-dd}", add = n, date = $"{then:yyyy-MM-dd}" }))
            {
                cli.Data(string.Create(CultureInfo.InvariantCulture, $"{CalendarMath.Describe(date)} plus {n} days is {CalendarMath.Describe(then)}."));
            }

            return Exit.Ok;
        }

        var month = today;
        if (rest.Count == 1)
        {
            if (!DateOnly.TryParseExact(rest[0] + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out month))
            {
                return cli.Fail("a month is yyyy-mm, like 2026-09.", Exit.Usage);
            }
        }

        var weeks = CalendarMath.Month(month.Year, month.Month);
        if (cli.WriteJson(new { month = $"{month:yyyy-MM}", weeks = weeks.Select(w => new { week = w.Week, days = w.Days.Select(d => d?.Day) }) }))
        {
            return Exit.Ok;
        }

        // Three columns a day; today is marked with a * before it.
        cli.Data(new DateTime(month.Year, month.Month, 1).ToString("MMMM yyyy", CultureInfo.CurrentCulture));
        cli.Data(" Wk   Mo Tu We Th Fr Sa Su");
        foreach (var (week, days) in weeks)
        {
            var line = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $" {week,2}  "));
            foreach (var day in days)
            {
                line.Append(day is { } d ? string.Create(CultureInfo.InvariantCulture, $"{(d == today ? '*' : ' ')}{d.Day,2}") : "   ");
            }

            cli.Data(line.ToString().TrimEnd());
        }

        return Exit.Ok;
    }

    /// <summary><c>tools calc &lt;expression&gt;</c>: exact decimal arithmetic; exit 1 when it comes to nothing.</summary>
    private static int Calc(IReadOnlyList<string> args, Cli cli)
    {
        var expression = string.Join(' ', args.Skip(2).Where(a => a != "--")).Trim();
        if (expression.Length == 0)
        {
            return cli.Fail(Usage, Exit.Usage);
        }

        var result = Calculator.Evaluate(expression);
        if (result.Problem is { } problem)
        {
            return cli.Fail(problem);
        }

        if (!cli.WriteJson(new { expression, value = result.Text, exact = result.Exact }))
        {
            cli.Data(result.Exact ? result.Text : $"{result.Text}  (not exact: a fractional power is worked in binary floating point)");
        }

        return Exit.Ok;
    }

    /// <summary>
    /// <c>tools net [&lt;host&gt;]</c>: with a host (or an address), that host, any the person names, on its
    /// port (443 unless the address says); with none, whether this PC is online, then each service
    /// DipTick uses, one at a time.
    /// </summary>
    private static int Net(VerbArgs words, Cli cli)
    {
        var rest = words.Words();
        if (words.Problem is not null || rest.Count > 1)
        {
            return cli.Fail(Usage, Exit.Usage);
        }

        List<KnownHost> targets;
        if (rest.Count == 1)
        {
            var typed = rest[0].Trim();
            if (Uri.TryCreate(typed.Contains("://", StringComparison.Ordinal) ? typed : "https://" + typed, UriKind.Absolute, out var uri) && uri.IdnHost.Length > 0)
            {
                targets = [new KnownHost(uri.IdnHost, uri.Port, uri.Scheme == Uri.UriSchemeHttps, "you asked")];
            }
            else
            {
                return cli.Fail($"not a host or an address: {SafeText.Line(typed)}", Exit.Usage);
            }
        }
        else
        {
            var (online, interfaces) = NetworkCheck.Here();
            if (!cli.Json)
            {
                cli.Data(online ? $"This PC is online, through {string.Join(", ", interfaces)}." : "Windows says no network is up on this PC.");
            }

            targets = [.. NetworkCheck.Known(cli.Config)];
            if (targets.Count == 0 && !cli.Json)
            {
                cli.Data("DipTick talks to no service yet: no model is set up.");
            }
        }

        var results = new List<ProbeResult>();
        foreach (var target in targets)
        {
            var result = NetworkCheck.ProbeAsync(target.Host, target.Port, target.Https, target.Why, cli.Stop).GetAwaiter().GetResult();
            results.Add(result);
            if (!cli.Json)
            {
                cli.Data(result.Words);
            }
        }

        cli.WriteJson(new
        {
            online = NetworkCheck.Here().Online,
            hosts = results.Select(r => new
            {
                host = r.Host,
                port = r.Port,
                why = r.Why,
                reachable = r.Ok,
                steps = r.Steps.Select(s => new { step = s.Name, ok = s.Ok, ms = (int)s.Took.TotalMilliseconds, detail = s.Detail }),
            }),
        });
        return results.TrueForAll(r => r.Ok) ? Exit.Ok : Exit.Failed;
    }
}
