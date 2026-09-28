using System.Globalization;

namespace DipTick.Models.Chat.Seam;

/// <summary>What an agent is to do in a step, which is its role line.</summary>
internal enum StepRole
{
    /// <summary>Answer the person; nobody else answers.</summary>
    Answer,

    /// <summary>Answer the person while others answer the same, blind.</summary>
    Parallel,

    /// <summary>Answer after the agents before it in a chain, having seen them.</summary>
    Relay,

    /// <summary>Do the work; another reviews it.</summary>
    Builder,

    /// <summary>Review the builder's answer.</summary>
    Reviewer,

    /// <summary>Answer the review.</summary>
    Revise,

    /// <summary>Turn k of n: answer the last speaker.</summary>
    Stack,
}

/// <summary>One step of a round: the agents that answer at the same time, and what they are to do.</summary>
/// <param name="Turn">For a stack, this turn's number from 1.</param>
/// <param name="Of">For a stack, how many turns it has.</param>
internal sealed record PlanStep(IReadOnlyList<string> Seats, StepRole Role, int Turn = 0, int Of = 0);

/// <summary>
/// A round's plan, fixed before the first call and only ever shortened:
/// its steps, the line the person sees before sending, and whether the turn limit cut it.
/// </summary>
internal sealed record Plan(IReadOnlyList<PlanStep> Steps, string Line, bool Clamped)
{
    /// <summary>How many agent turns the plan holds.</summary>
    public int Turns => Steps.Sum(s => s.Seats.Count);

    /// <summary>The steps' agents, as the store keeps a round's plan: [["dip"],["tick"]].</summary>
    public IReadOnlyList<IReadOnlyList<string>> Seats => [.. Steps.Select(s => s.Seats)];
}

/// <summary>
/// A round's plan from the chat's mode, the person's address, the table and the turn limit. An
/// address overrides the mode for one round. Every plan is cut to the turn limit, never past 8.
/// Pure: a table of inputs to plans.
/// </summary>
internal static class Planner
{
    public static Plan For(ChatMode mode, Address? address, IReadOnlyList<string> table, int maxTurns)
    {
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(table);
        var limit = Math.Clamp(maxTurns, 1, RoundLimits.TurnCeiling);
        List<PlanStep> steps;
        if (address is { Kind: AddressKind.Team or AddressKind.Seats } named)
        {
            steps = named.Seats.Count == 1 ? [new PlanStep(named.Seats, StepRole.Answer)] : [new PlanStep(named.Seats, StepRole.Parallel)];
        }
        else if (address is { Kind: AddressKind.Relay } chain)
        {
            steps = Chain(chain.Seats);
        }
        else
        {
            steps = FromMode(mode, table, limit);
        }

        var clamped = Clamp(steps, limit, out var kept);
        return new Plan(kept, Line(kept, mode, address, table, clamped, limit), clamped);
    }

    private static List<PlanStep> FromMode(ChatMode mode, IReadOnlyList<string> table, int limit) => mode.Kind switch
    {
        ModeKind.All => table.Count switch
        {
            0 => [],
            1 => [new PlanStep([.. table], StepRole.Answer)],
            _ => [new PlanStep([.. table], StepRole.Parallel)],
        },
        ModeKind.Solo => [new PlanStep(mode.Seats, StepRole.Answer)],
        ModeKind.Pick => [],
        ModeKind.Relay => Chain(mode.Seats),
        ModeKind.Review => mode.Revise
            ? [new PlanStep([mode.Seats[0]], StepRole.Builder), new PlanStep([mode.Seats[1]], StepRole.Reviewer), new PlanStep([mode.Seats[0]], StepRole.Revise)]
            : [new PlanStep([mode.Seats[0]], StepRole.Builder), new PlanStep([mode.Seats[1]], StepRole.Reviewer)],
        _ => Stack(mode.Seats, mode.Turns > 0 ? mode.Turns : Math.Min(2 * mode.Seats.Count, limit)),
    };

    private static List<PlanStep> Chain(IReadOnlyList<string> seats) =>
        [.. seats.Select((s, i) => new PlanStep([s], i == 0 ? StepRole.Answer : StepRole.Relay))];

    private static List<PlanStep> Stack(IReadOnlyList<string> seats, int turns) =>
        [.. Enumerable.Range(0, turns).Select(k => new PlanStep([seats[k % seats.Count]], StepRole.Stack, k + 1, turns))];

    /// <summary>
    /// The turns that fit in the limit, in order: a step of several at once keeps as many of its agents
    /// as fit, in the table's order, and one left alone simply answers; a stack's count is the turns it keeps.
    /// </summary>
    private static bool Clamp(List<PlanStep> steps, int limit, out List<PlanStep> kept)
    {
        kept = [];
        var used = 0;
        foreach (var step in steps)
        {
            var room = limit - used;
            if (room <= 0)
            {
                break;
            }

            var fits = step.Seats.Count <= room ? step : step with
            {
                Seats = [.. step.Seats.Take(room)],
                Role = room == 1 ? StepRole.Answer : step.Role,
            };
            kept.Add(fits);
            used += fits.Seats.Count;
        }

        var clamped = used < steps.Sum(s => s.Seats.Count);
        if (clamped && kept.Count > 0 && kept[0].Role == StepRole.Stack)
        {
            var turns = kept.Count;
            kept = [.. kept.Select(s => s with { Of = turns })];
        }

        return clamped;
    }

    /// <summary>What the person sees before sending: who will answer, in order.</summary>
    private static string Line(List<PlanStep> steps, ChatMode mode, Address? address, IReadOnlyList<string> table, bool clamped, int limit)
    {
        if (steps.Count == 0)
        {
            return mode.Kind == ModeKind.Pick && address is null
                ? $"pick: start with {string.Join(", ", table.Select(n => "@" + n))} or @team"
                : "nobody is at this chat's table";
        }

        var line = steps[0].Role switch
        {
            StepRole.Parallel => $"{string.Join(", ", steps[0].Seats)} answer at once",
            StepRole.Answer when steps.Count == 1 => $"{steps[0].Seats[0]} answers",
            StepRole.Builder => $"{steps[0].Seats[0]} builds, {string.Join(", then ", steps.Skip(1).Select(s => s.Role == StepRole.Revise ? s.Seats[0] + " revises" : s.Seats[0] + " reviews"))}",
            StepRole.Stack => string.Create(CultureInfo.InvariantCulture, $"debate: {string.Join(", ", steps.Select(s => s.Seats[0]))} ({steps.Count} turns)"),
            _ => string.Join(", then ", steps.Select(s => s.Seats[0])),
        };
        return clamped ? string.Create(CultureInfo.InvariantCulture, $"{line}; cut to {limit} {(limit == 1 ? "turn" : "turns")} by chat.maxAiTurns") : line;
    }
}
