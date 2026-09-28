using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DipTick.Models.Research;

/// <summary>
/// The numbered citations in an answer, <c>[n]</c>, checked against the chat's sources:
/// a number the chat has no source for is shown as <c>[n: not fetched]</c>, so a made-up
/// source reads as one. Only for showing: the kept answer is never changed. Code, fenced or inline, is
/// left alone, and so is a Markdown link's text, <c>[n](…)</c>.
/// </summary>
internal static partial class Citations
{
    /// <summary>What a made-up citation is shown with.</summary>
    public const string NotFetched = "not fetched";

    /// <summary>The answer as it is shown: each <c>[n]</c> with no source <c>n</c> in the chat marked as not fetched.</summary>
    public static string Mark(string text, IReadOnlySet<int> known)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(known);
        return Walk(text, (number, marker) => known.Contains(number) ? null : string.Create(CultureInfo.InvariantCulture, $"[{number}: {NotFetched}]"));
    }

    /// <summary>
    /// Goes through the answer's prose, outside code, and hands each citation to <paramref name="each"/>,
    /// which gives what to show in its place, or null to leave it; returns the text as shown.
    /// </summary>
    private static string Walk(string text, Func<int, string, string?> each)
    {
        var shown = new StringBuilder(text.Length);
        var fenced = false;
        foreach (var line in text.Split('\n'))
        {
            if (shown.Length > 0)
            {
                shown.Append('\n');
            }

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
                shown.Append(line);
                continue;
            }

            if (fenced)
            {
                shown.Append(line);
                continue;
            }

            // Inline code spans alternate with prose between backticks.
            var parts = line.Split('`');
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                {
                    shown.Append('`');
                }

                shown.Append(i % 2 == 1 ? parts[i] : Marker().Replace(parts[i], m =>
                {
                    var number = int.Parse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
                    return each(number, m.Value) ?? m.Value;
                }));
            }
        }

        return shown.ToString();
    }

    /// <summary>A citation: [n], n from 1 to 9999, not a Markdown link's text.</summary>
    [GeneratedRegex(@"\[([1-9][0-9]{0,3})\](?!\()", RegexOptions.CultureInvariant)]
    private static partial Regex Marker();
}
