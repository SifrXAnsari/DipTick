namespace DipTick.Models.Chat.Seam;

/// <summary>Whom the person's words go to, when they say.</summary>
internal enum AddressKind
{
    /// <summary>Nobody named: the chat's mode decides.</summary>
    None,

    /// <summary>The agents named, at once and blind to each other.</summary>
    Seats,

    /// <summary>Every agent at the table, at once and blind.</summary>
    Team,

    /// <summary>A chain, <c>@a&gt;b&gt;c</c>: each sees the ones before it.</summary>
    Relay,
}

/// <summary>An address and the words after it.</summary>
internal sealed record Address(AddressKind Kind, IReadOnlyList<string> Seats, string Text);

/// <summary>
/// The person's <c>@</c> forms, read only from the start of what they say:
/// <c>@dip text</c>, <c>@dip @tick text</c>, <c>@team</c> or <c>@all</c> (and <c>@both</c> with
/// two at the table), <c>@dip&gt;tick text</c>; <c>\@</c> is a plain @. An <c>@name</c> anywhere
/// else is plain text, and one an agent writes addresses nobody. Pure.
/// </summary>
internal static class Addressing
{
    /// <summary>Words an agent's name may not be, since they already mean something here.</summary>
    public static IReadOnlySet<string> Reserved { get; } = new HashSet<string>(["team", "all", "both", "person", "you", "seam", "work", "diptick"], StringComparer.Ordinal);

    /// <summary>What the person said, addressed; or null and why, before anything is sent.</summary>
    public static Address? Parse(string input, IReadOnlyList<string> table, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(table);
        problem = null;
        var text = input.TrimStart();
        if (text.StartsWith("\\@", StringComparison.Ordinal))
        {
            return new Address(AddressKind.None, [], text[1..]);
        }

        var names = new List<string>();
        var team = false;
        string[]? relay = null;
        var tokens = 0;
        while (text.StartsWith('@'))
        {
            var end = text.IndexOfAny([' ', '\t', '\n', '\r']);
            var token = Lower((end < 0 ? text : text[..end])[1..]);
            text = end < 0 ? string.Empty : text[end..].TrimStart();
            tokens++;
            if (token.Contains('>', StringComparison.Ordinal))
            {
                relay = token.Split('>');
                continue;
            }

            if (token is "team" or "all" || (token == "both" && table.Count == 2))
            {
                team = true;
            }
            else if (!table.Contains(token))
            {
                problem = $"no agent @{token} at this table; it has {Names(table)}";
                return null;
            }
            else if (!names.Contains(token))
            {
                names.Add(token);
            }
        }

        if (tokens == 0)
        {
            return new Address(AddressKind.None, [], input);
        }

        if (text.Length == 0)
        {
            problem = "nothing to say after the address";
            return null;
        }

        if (relay is not null)
        {
            if (tokens > 1)
            {
                problem = "a chain is one @a>b, on its own";
                return null;
            }

            if (relay.Length is < 2 or > Table.Most || relay.Distinct(StringComparer.Ordinal).Count() != relay.Length)
            {
                problem = $"a chain names 2 to {Table.Most} different agents: @a>b";
                return null;
            }

            if (relay.FirstOrDefault(n => !table.Contains(n)) is { } unknown)
            {
                problem = $"no agent @{unknown} at this table; it has {Names(table)}";
                return null;
            }

            return new Address(AddressKind.Relay, relay, text);
        }

        return team ? new Address(AddressKind.Team, [.. table], text) : new Address(AddressKind.Seats, names, text);
    }

    /// <summary>
    /// A name as the person typed it, in the only case names have: A to Z made a to z and nothing else
    /// touched, since a name is ASCII (a letter, then letters, digits and hyphens) and anything else in
    /// it is simply not a name.
    /// </summary>
    public static string Lower(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = source[i] is >= 'A' and <= 'Z' ? (char)(source[i] + ('a' - 'A')) : source[i];
            }
        });
    }

    private static string Names(IReadOnlyList<string> table) => table.Count == 0 ? "nobody" : string.Join(" ", table.Select(n => "@" + n));
}
