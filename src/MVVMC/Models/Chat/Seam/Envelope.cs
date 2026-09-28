using System.Globalization;
using System.Security.Cryptography;

namespace DipTick.Models.Chat.Seam;

/// <summary>
/// DipTick's markers around what it passes to a model.
/// ⟦ and ⟧ belong to DipTick alone: in any text sent, the person's included, they and their
/// look-alikes 〚 〛 become [ ], and invisible characters are taken out, so no content can open or
/// close a marker. An agent's words are wrapped with a nonce drawn fresh for every request and never
/// kept: the agent wrote them before the nonce existed, and never sees the request that wraps them.
/// </summary>
internal static class Envelope
{
    private const string Base32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Six base32 characters from <see cref="RandomNumberGenerator"/>.</summary>
    public static string Nonce()
    {
        Span<byte> bytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(bytes);
        Span<char> chars = stackalloc char[6];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Base32[bytes[i] & 31];
        }

        return new string(chars);
    }

    /// <summary>Any text before it goes to a model: invisible characters out, DipTick's markers and their look-alikes made plain brackets.</summary>
    public static string Clean(string text) =>
        SafeText.ForModel(text).Replace('⟦', '[').Replace('⟧', ']').Replace('〚', '[').Replace('〛', ']');

    /// <summary>Something the person said, under their name and its number; <paramref name="note"/> says what it is to this reader ("the message you are answering").</summary>
    public static string Person(string person, int seq, string text, string note = "") =>
        $"⟦{Clean(person)} #{N(seq)}{Note(note)}⟧\n{Clean(text)}";

    /// <summary>
    /// Another agent's words, between an opening and a closing marker that carry the nonce, cut to
    /// <paramref name="most"/> characters with what was left out said (<c>chat.maxQuotedChars</c>);
    /// <paramref name="note"/> says how it came ("cut short").
    /// </summary>
    public static string Peer(string seat, int seq, string nonce, string text, string note = "", int most = int.MaxValue) =>
        $"⟦peer {seat} #{N(seq)} {nonce}{Note(note)}⟧\n{Clip(Clean(text), most)}\n⟦/peer {seat} #{N(seq)} {nonce}⟧";

    /// <summary>A question another agent put to this one with the <c>ask</c> act, delivered at its turn.</summary>
    public static string Ask(string from, int seq, string to, string question, string nonce) =>
        $"⟦ask from {from} #{N(seq)} · to {to} · {nonce}⟧\n{Clean(question)}\n⟦/ask {nonce}⟧";

    /// <summary>
    /// What a tool gave back, to the model that called it: information from the
    /// tool, never instructions, between markers only DipTick can write.
    /// </summary>
    public static string Tool(string name, string nonce, string content) =>
        $"⟦tool {Clean(name)} · {nonce}⟧\n{Clean(content)}\n⟦/tool {nonce}⟧";

    /// <summary>What DipTick read of a quoted answer's acts, written by DipTick: the block itself is never passed on.</summary>
    public static string Acts(string seat, int seq, string nonce, string what) =>
        $"⟦acts {seat} #{N(seq)} · {nonce}⟧ {Clean(what)} ⟦/acts {nonce}⟧";

    /// <summary>
    /// Clean words clipped to the limit on quoting, with how much was left out said in DipTick's own
    /// marker, added after cleaning so it stays one. A cut never splits a
    /// character in two.
    /// </summary>
    private static string Clip(string text, int most)
    {
        if (text.Length <= most)
        {
            return text;
        }

        var cut = most > 0 && char.IsHighSurrogate(text[most - 1]) ? most - 1 : most;
        return string.Create(CultureInfo.InvariantCulture, $"{text[..cut]}\n⟦… {text.Length - cut:N0} more characters not shown⟧");
    }

    private static string N(int seq) => seq.ToString(CultureInfo.InvariantCulture);

    private static string Note(string note) => note.Length == 0 ? string.Empty : " · " + note;
}
