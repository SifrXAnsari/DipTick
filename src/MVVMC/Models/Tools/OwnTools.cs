using System.Globalization;
using System.Text.Json;
using DipTick.Models.Harness;

namespace DipTick.Models.Tools;

/// <summary><c>time_now</c>: the date, weekday, time, zone and offset, here or in a zone; it reads the clock, so no gate.</summary>
internal sealed class TimeNowTool : IAgentTool
{
    public string Name => "time_now";

    public string Description => "The current date, weekday, time of day, time zone and UTC offset, on the person's PC or in another time zone. Call it for the time; today's date is already in your instructions.";

    public JsonElement Parameters { get; } = ToolArgs.Schema(("zone", "string", "A time zone id, like Europe/Paris or Tokyo Standard Time; leave it out for the person's own.", false));

    public Task<ToolOutcome> RunAsync(ToolContext context, JsonElement arguments, CancellationToken cancel)
    {
        var zone = ToolArgs.Text(arguments, "zone");
        return Task.FromResult(ClockReading.Read(zone, DateTimeOffset.UtcNow) is { } now
            ? new ToolOutcome(now.Words, $"{now.Now:yyyy-MM-dd HH:mm} {now.ZoneName}")
            : ToolOutcome.Failed($"no time zone '{SafeText.Line(zone ?? string.Empty)}' on this PC; IANA ids like Asia/Tokyo and Windows ids both work"));
    }
}

/// <summary><c>calendar_days</c>: days between two dates, a date plus days, a date's weekday and ISO week; no gate.</summary>
internal sealed class CalendarDaysTool : IAgentTool
{
    public string Name => "calendar_days";

    public string Description => "Calendar arithmetic, exact: the days between two dates, a date plus or minus some days, and any date's weekday and ISO week. Dates are yyyy-MM-dd, or today, yesterday, tomorrow.";

    public JsonElement Parameters { get; } = ToolArgs.Schema(
        ("from", "string", "The first date: yyyy-MM-dd, or today.", true),
        ("to", "string", "A second date, for the days between the two.", false),
        ("add", "integer", "Days to add to the first date; negative to go back.", false));

    public Task<ToolOutcome> RunAsync(ToolContext context, JsonElement arguments, CancellationToken cancel)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (ToolArgs.Text(arguments, "from") is not { } fromText || CalendarMath.Read(fromText, today) is not { } from)
        {
            return Task.FromResult(ToolOutcome.Failed("'from' must be a date, yyyy-MM-dd, or today"));
        }

        if (ToolArgs.Text(arguments, "to") is { } toText)
        {
            if (CalendarMath.Read(toText, today) is not { } to)
            {
                return Task.FromResult(ToolOutcome.Failed("'to' must be a date, yyyy-MM-dd"));
            }

            var days = CalendarMath.Between(from, to);
            return Task.FromResult(new ToolOutcome(
                string.Create(CultureInfo.InvariantCulture, $"From {CalendarMath.Describe(from)} to {CalendarMath.Describe(to)}: {days} days."),
                string.Create(CultureInfo.InvariantCulture, $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd}: {days} days")));
        }

        if (ToolArgs.Number(arguments, "add") is { } add)
        {
            return Task.FromResult(CalendarMath.Add(from, add) is { } then
                ? new ToolOutcome(string.Create(CultureInfo.InvariantCulture, $"{CalendarMath.Describe(from)} plus {add} days is {CalendarMath.Describe(then)}."), string.Create(CultureInfo.InvariantCulture, $"{from:yyyy-MM-dd} + {add} = {then:yyyy-MM-dd}"))
                : ToolOutcome.Failed("that is past the calendar's ends"));
        }

        return Task.FromResult(new ToolOutcome(CalendarMath.Describe(from), CalendarMath.Describe(from)));
    }
}

/// <summary><c>calc</c>: exact decimal arithmetic; parsed, never run as code; no gate.</summary>
internal sealed class CalcTool : IAgentTool
{
    public string Name => "calc";

    public string Description => "Exact decimal arithmetic. Use it for any sum you would otherwise work out yourself. Numbers, + - * / ^ ( ), % after a number (17% is 0.17), 'of' for times, and abs, round(x[, places]), floor, ceil, min, max, sqrt. Example: 17% of 2345.60.";

    public JsonElement Parameters { get; } = ToolArgs.Schema(("expression", "string", "The arithmetic to do, like 0.17 * 2345.60.", true));

    public Task<ToolOutcome> RunAsync(ToolContext context, JsonElement arguments, CancellationToken cancel)
    {
        if (ToolArgs.Text(arguments, "expression") is not { } expression)
        {
            return Task.FromResult(ToolOutcome.Failed("'expression' is needed"));
        }

        var result = Calculator.Evaluate(expression);
        if (result.Problem is { } problem)
        {
            return Task.FromResult(ToolOutcome.Failed(problem));
        }

        var note = result.Exact ? string.Empty : " (not exact: a power with a fractional exponent is worked in binary floating point)";
        return Task.FromResult(new ToolOutcome($"{result.Text}{note}", $"{SafeText.Line(expression)} = {result.Text}{(result.Exact ? string.Empty : " (approximate)")}"));
    }
}

/// <summary>
/// <c>net_check</c> (<c>net.probe</c>, Ask by default): whether DipTick can reach
/// a service it uses. Only hosts DipTick already talks to, or ones the person's list names, on their own
/// port; one at a time; never a scan. Without a host, is this PC online.
/// </summary>
internal sealed class NetCheckTool : IAgentTool
{
    public string Name => "net_check";

    public string Description => "Checks whether this PC is online and whether DipTick can reach a service it uses (a model's provider, the search service, a connector): the name looked up, the connection, TLS and the HTTP status, each timed. Only those hosts; leave the host out to check whether this PC is online at all.";

    public JsonElement Parameters { get; } = ToolArgs.Schema(("host", "string", "A host DipTick uses, like api.anthropic.com, or its address.", false));

    public async Task<ToolOutcome> RunAsync(ToolContext context, JsonElement arguments, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(context);
        var asked = ToolArgs.Text(arguments, "host");
        if (asked is null)
        {
            var (online, interfaces) = NetworkCheck.Here();
            var words = online ? $"This PC is online, through {string.Join(", ", interfaces)}." : "Windows says no network is up on this PC.";
            return new ToolOutcome(words, online ? "online" : "offline");
        }

        var host = Uri.TryCreate(asked, UriKind.Absolute, out var uri) ? uri.IdnHost : asked.Trim().TrimEnd('/');
        var known = NetworkCheck.Known(context.Config).FirstOrDefault(k => string.Equals(k.Host, host, StringComparison.OrdinalIgnoreCase));
        if (known is null)
        {
            // Only what DipTick already talks to: the model is told no more than that it may not.
            return ToolOutcome.Refused($"net_check {SafeText.Line(host)}");
        }

        var target = string.Create(CultureInfo.InvariantCulture, $"{known.Host}:{known.Port}");
        if (!context.Gate.Request("tool net_check", Capability.NetProbe, target))
        {
            return ToolOutcome.Refused($"net_check {target}");
        }

        var result = await NetworkCheck.ProbeAsync(known.Host, known.Port, known.Https, known.Why, cancel).ConfigureAwait(false);
        return new ToolOutcome(result.Words, $"{target}: {(result.Ok ? "reachable" : "not reachable")}");
    }
}
