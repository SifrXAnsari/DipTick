using System.Globalization;
using System.Text;

namespace DipTick.Models.Protocol;

/// <summary>
/// The rule that makes agreeing cost something: every agree and every
/// critique must name a flaw or risk, pinned to text it quotes.
/// </summary>
/// <remarks>
/// No mechanical check can judge whether a flaw is real. These check that the flaw is pinned
/// to real text in the version (a verbatim quote from the clause or item it names), is more
/// than filler, and is not a copy of another seat's flaw or of the same seat's earlier one. The
/// person sees every flaw beside the approval prompt and judges the rest.
/// </remarks>
internal static class FlawRules
{
    public const int MinFlaw = 20;
    public const int MaxFlaw = 400;
    public const int MinQuoteWords = 3;
    public const int MaxQuoteWords = 30;
    public const double RepeatThreshold = 0.8;

    private static readonly string[] Filler =
        ["none", "n/a", "nothing", "no issues", "no flaws", "no risk", "lgtm", "looks good", "minor"];

    /// <summary>Null when the flaw counts; otherwise the code that voids the act.</summary>
    /// <param name="body">The version the act is about.</param>
    /// <param name="where">The clause or item it names.</param>
    /// <param name="quote">The words it quotes from there.</param>
    /// <param name="flaw">The flaw or risk it names.</param>
    /// <param name="others">Flaws already counted on this version by other seats, and this seat's earlier flaws.</param>
    public static string? Check(ProposalBody body, string where, string quote, string flaw, IEnumerable<string> others)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(others);

        if (body.At(where) is not { } text)
        {
            return Codes.BadWhere;
        }

        var words = Normal(quote).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (words < MinQuoteWords || words > MaxQuoteWords || !Normal(text).Contains(Normal(quote), StringComparison.Ordinal))
        {
            return Codes.QuoteNotFound;
        }

        var said = flaw.Trim();
        if (said.Length < MinFlaw || said.Length > MaxFlaw || IsFiller(said))
        {
            return Codes.FlawTooThin;
        }

        return others.Any(o => Jaccard(said, o) >= RepeatThreshold) ? Codes.FlawRepeated : null;
    }

    /// <summary>Whether a flaw, a risk or a check says nothing: "none", "LGTM", "looks good" and the like.</summary>
    public static bool IsFiller(string text) =>
        Filler.Contains(Normal(text).TrimEnd('.', '!'), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Text as quotes are compared: NFC, one case, quote marks straightened, whitespace
    /// collapsed to single spaces.
    /// </summary>
    public static string Normal(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        // Upper case, not lower: the comparison only needs one case, and upper is the one that
        // round-trips for every culture (CA1308).
        var s = SafeText.ForModel(text).ToUpperInvariant();
        var clean = new StringBuilder(s.Length);
        var space = false;
        foreach (var c in s)
        {
            var d = c switch
            {
                '‘' or '’' or '‚' or '‛' or '′' => '\'',
                '“' or '”' or '„' or '‟' or '″' => '"',
                _ => c,
            };
            if (char.IsWhiteSpace(d))
            {
                space = clean.Length > 0;
                continue;
            }

            if (space)
            {
                clean.Append(' ');
                space = false;
            }

            clean.Append(d);
        }

        return clean.ToString();
    }

    /// <summary>Token overlap of two texts: the size of the intersection over the union of their words.</summary>
    public static double Jaccard(string a, string b)
    {
        var x = Tokens(a);
        var y = Tokens(b);
        if (x.Count == 0 && y.Count == 0)
        {
            return 1;
        }

        var union = new HashSet<string>(x, StringComparer.Ordinal);
        union.UnionWith(y);
        var both = x.Count(y.Contains);
        return (double)both / union.Count;
    }

    private static HashSet<string> Tokens(string text) =>
        Normal(text).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('.', ',', ';', ':', '!', '?', '"', '\'', '(', ')'))
            .Where(w => w.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
}
