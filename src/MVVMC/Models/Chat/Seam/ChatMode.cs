using System.Globalization;
using System.Text.RegularExpressions;

namespace DipTick.Models.Chat.Seam;

/// <summary>Who answers when the person addresses nobody.</summary>
internal enum ModeKind
{
    /// <summary>Every agent at once, blind to the others' answers in the same round.</summary>
    All,

    /// <summary>One agent.</summary>
    Solo,

    /// <summary>Nobody until the person addresses someone.</summary>
    Pick,

    /// <summary>A chain: each agent sees the ones before it.</summary>
    Relay,

    /// <summary>One builds, another reviews; with <c>revise</c>, the builder answers the review.</summary>
    Review,

    /// <summary>The agents answer each other in turns (<c>debate</c> is the same).</summary>
    Stack,
}

/// <summary>
/// A chat's mode, in its grammar: <c>all</c> (or <c>both</c>), <c>solo &lt;a&gt;</c>,
/// <c>pick</c>, <c>relay &lt;a&gt; &lt;b&gt; [&lt;c&gt; &lt;d&gt;]</c>, <c>review &lt;builder&gt;
/// &lt;reviewer&gt; [revise]</c>, <c>stack &lt;a&gt; &lt;b&gt; [&lt;c&gt; &lt;d&gt;] [turns]</c> (or
/// <c>debate</c>). The names are checked against a chat's table when it is set or planned.
/// </summary>
internal sealed partial record ChatMode(ModeKind Kind, IReadOnlyList<string> Seats, int Turns = 0, bool Revise = false)
{
    public static ChatMode All { get; } = new(ModeKind.All, []);

    /// <summary>The mode a text names, or null with why not.</summary>
    public static ChatMode? Parse(string text, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(text);
        problem = null;
        var words = Addressing.Lower(text.Trim()).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            problem = "is empty; it is all, solo, pick, relay, review or stack";
            return null;
        }

        var names = words.Skip(1).ToList();
        if (names.FirstOrDefault(n => !Name().IsMatch(n) && !IsCount(n) && n != "revise") is { } odd)
        {
            problem = $"names '{odd}', which is not an agent's name";
            return null;
        }

        switch (words[0])
        {
            case "all" or "both" when names.Count == 0:
                return All;
            case "pick" when names.Count == 0:
                return new ChatMode(ModeKind.Pick, []);
            case "solo" when names.Count == 1 && !IsCount(names[0]):
                return new ChatMode(ModeKind.Solo, names);
            case "relay" when names.Count is >= 2 and <= Table.Most && names.All(n => !IsCount(n)) && Distinct(names):
                return new ChatMode(ModeKind.Relay, names);
            case "review" when names.Count is 2 or 3 && !IsCount(names[0]) && !IsCount(names[1]) && names[0] != names[1] && (names.Count == 2 || names[2] == "revise"):
                return new ChatMode(ModeKind.Review, names.Take(2).ToList(), Revise: names.Count == 3);
            case "stack" or "debate":
                var turns = names.Count > 0 && IsCount(names[^1]) ? int.Parse(names[^1], CultureInfo.InvariantCulture) : 0;
                var seats = turns > 0 ? names.Take(names.Count - 1).ToList() : names;
                if (seats.Count is >= 2 and <= Table.Most && seats.All(n => !IsCount(n) && n != "revise") && Distinct(seats) && turns <= RoundLimits.TurnCeiling)
                {
                    return new ChatMode(ModeKind.Stack, seats, turns);
                }

                problem = $"'{words[0]}' takes 2 to {Table.Most} agents' names and at most {RoundLimits.TurnCeiling} turns: stack <a> <b> [turns]";
                return null;
        }

        problem = words[0] switch
        {
            "all" or "both" or "pick" => $"'{words[0]}' takes no names",
            "solo" => "'solo' takes one agent's name: solo <a>",
            "relay" => $"'relay' takes 2 to {Table.Most} different agents' names: relay <a> <b>",
            "review" => "'review' takes the builder's and the reviewer's names: review <builder> <reviewer> [revise]",
            _ => "must start with all, solo, pick, relay, review or stack",
        };
        return null;
    }

    /// <summary>Why a text is not a mode, or null: for the settings, where no table is known yet.</summary>
    public static string? Problem(string text)
    {
        _ = Parse(text, out var problem);
        return problem;
    }

    /// <summary>Why this mode cannot run at a table, or null: it names an agent not sitting there.</summary>
    public string? Check(IReadOnlyCollection<string> table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return Seats.FirstOrDefault(s => !table.Contains(s)) is { } missing
            ? $"names {missing}, who is not at this chat's table ({string.Join(", ", table)})"
            : null;
    }

    /// <summary>The mode in its own grammar, as the store and the settings keep it.</summary>
    public override string ToString() => Kind switch
    {
        ModeKind.All => "all",
        ModeKind.Pick => "pick",
        ModeKind.Solo => $"solo {Seats[0]}",
        ModeKind.Relay => $"relay {string.Join(' ', Seats)}",
        ModeKind.Review => $"review {Seats[0]} {Seats[1]}{(Revise ? " revise" : string.Empty)}",
        _ => $"stack {string.Join(' ', Seats)}{(Turns > 0 ? string.Create(CultureInfo.InvariantCulture, $" {Turns}") : string.Empty)}",
    };

    /// <summary>The mode in words, for the chip that names it: "All answer", "dip answers", "dip, then tick".</summary>
    public string Words => Kind switch
    {
        ModeKind.All => "All answer",
        ModeKind.Pick => "You pick who answers",
        ModeKind.Solo => $"{Seats[0]} answers",
        ModeKind.Relay => string.Join(", then ", Seats),
        ModeKind.Review => $"{Seats[1]} reviews {Seats[0]}{(Revise ? ", who revises" : string.Empty)}",
        _ => Turns > 0 ? string.Create(CultureInfo.InvariantCulture, $"Debate, {Turns} turns") : "Debate",
    };

    private static bool IsCount(string word) => word.Length is 1 or 2 && word.All(char.IsAsciiDigit) && word[0] != '0';

    private static bool Distinct(List<string> names) => names.Distinct(StringComparer.Ordinal).Count() == names.Count;

    [GeneratedRegex("^[a-z][a-z0-9-]{0,11}$", RegexOptions.CultureInvariant)]
    private static partial Regex Name();
}
