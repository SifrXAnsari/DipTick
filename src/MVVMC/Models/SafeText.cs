using System.Globalization;
using System.Text;

namespace DipTick.Models;

/// <summary>
/// Text from a model, or chosen by one, made safe to show a person.
/// </summary>
/// <remarks>
/// A terminal obeys what it is sent. A carriage return rewinds the line, so "db.read" can be
/// drawn over "db.write" in a confirmation the person then approves; an escape sequence can
/// recolour, move or retitle; bidirectional overrides reorder what is read. Every one of those is
/// replaced here by a visible escape (<c>\r</c>, <c>\x1B</c>, <c>U+202E</c>), so what is shown is
/// what is there. Line feeds and tabs are kept where the caller allows them.
/// </remarks>
internal static class SafeText
{
    /// <summary>For a single line: a prompt, a path, a URL, an audit field.</summary>
    public static string Line(string? text) => Clean(text, keepLineFeeds: false);

    /// <summary>For text shown as several lines: an answer, a transcript.</summary>
    public static string Block(string? text) => Clean(text, keepLineFeeds: true);

    /// <summary>
    /// Text going to a model, or held as a model's words: invisible characters removed rather
    /// than escaped (zero-width marks, bidirectional controls, soft hyphens, C0/C1 controls but
    /// line feed and tab, and the Unicode tag characters U+E0000 to U+E007F, the channel hidden
    /// instructions are smuggled in), line endings made LF, and Unicode normalised to NFC, so two
    /// texts that look the same compare the same.
    /// </summary>
    public static string ForModel(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var kept = new StringBuilder(text.Length);
        foreach (var rune in LineFeedsOnly(text).EnumerateRunes())
        {
            if (!Invisible(rune))
            {
                kept.Append(rune.ToString());
            }
        }

        return kept.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>How many characters <see cref="ForModel"/> takes out of the text as invisible, for telling the person when it was theirs.</summary>
    public static int InvisibleIn(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : LineFeedsOnly(text).EnumerateRunes().Count(Invisible);

    private static string LineFeedsOnly(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static bool Invisible(Rune rune)
    {
        var n = rune.Value;
        return (rune.IsBmp && Dangerous((char)n, keepLineFeeds: true))
            || n is 0x00AD or 0x200B or 0x200C or 0x200D or 0x2060 or 0xFEFF or 0x180E
            || n is >= 0xE0000 and <= 0xE007F;
    }

    /// <summary>Whether the text holds anything <see cref="Line"/> would change.</summary>
    public static bool HasControls(string? text) => text is not null && text.Any(c => Dangerous(c, keepLineFeeds: false));

    private static string Clean(string? text, bool keepLineFeeds)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (!text.Any(c => Dangerous(c, keepLineFeeds)))
        {
            return text;
        }

        var clean = new StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (keepLineFeeds && c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                continue;
            }

            if (!Dangerous(c, keepLineFeeds))
            {
                clean.Append(c);
            }
            else if (c == '\r')
            {
                clean.Append("\\r");
            }
            else if (c == '\n')
            {
                clean.Append("\\n");
            }
            else if (c < 0x100)
            {
                clean.Append(CultureInfo.InvariantCulture, $"\\x{(int)c:X2}");
            }
            else
            {
                clean.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
            }
        }

        return clean.ToString();
    }

    private static bool Dangerous(char c, bool keepLineFeeds)
    {
        int n = c;
        return n switch
        {
            0x0A => !keepLineFeeds,
            0x09 => false,
            < 0x20 => true,
            >= 0x7F and <= 0x9F => true,

            // Bidirectional embeddings, overrides and isolates, the directional marks, and the
            // line and paragraph separators.
            >= 0x202A and <= 0x202E => true,
            >= 0x2066 and <= 0x2069 => true,
            0x2028 or 0x2029 or 0x200E or 0x200F or 0x061C => true,
            _ => false,
        };
    }
}
