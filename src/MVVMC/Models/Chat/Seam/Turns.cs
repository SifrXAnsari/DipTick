using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DipTick.Models.Chat.Seam;

/// <summary>
/// How an agent's turn ended, read from its words: <c>[pass]</c> as the whole
/// answer, <c>[hand back]</c> as its last line outside code, and a repeat of an earlier answer. Each can
/// only shorten a round. Pure.
/// </summary>
internal static partial class Turns
{
    public const string Pass = "[pass]";
    public const string HandBack = "[hand back]";

    /// <summary>Whether the whole answer, trimmed, is <c>[pass]</c>.</summary>
    public static bool IsPass(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return string.Equals(answer.Trim(), Pass, StringComparison.Ordinal);
    }

    /// <summary>Whether the answer's last line outside code fences, its act block set aside, is exactly <c>[hand back]</c>.</summary>
    public static bool HandsBack(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var inFence = false;
        string? last = null;
        foreach (var line in Protocol.ActParser.Prose(answer).Split('\n'))
        {
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (!inFence && line.Trim().Length > 0)
            {
                last = line.Trim();
            }
        }

        return string.Equals(last, HandBack, StringComparison.Ordinal);
    }

    /// <summary>
    /// What makes two answers the same answer: the words in one case, spaces collapsed, NFC, hashed
    /// (SHA-256). Upper case, since only one case is needed and upper round-trips in every culture
    /// (CA1308). A new answer with the fingerprint of an earlier one in the chat is a repeat.
    /// </summary>
    public static string Fingerprint(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var plain = Spaces().Replace(SafeText.ForModel(answer).ToUpperInvariant(), " ").Trim();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plain)));
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();
}
