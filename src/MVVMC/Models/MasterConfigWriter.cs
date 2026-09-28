using System.Text;
using System.Text.RegularExpressions;

namespace DipTick.Models;

/// <summary>
/// Changes one value in the master config without disturbing its comments or layout.
/// </summary>
/// <remarks>
/// <para>
/// The file is JSON with comments, which no serializer here writes back, so values are
/// replaced in the text: the key inside its named section. Sections hold only scalars and
/// single-line lists, never nested objects, which is what lets a section be matched as
/// <c>{ ... }</c> with no braces inside.
/// </para>
/// <para>
/// Every change is proved before it is kept: the new text is read back through
/// <see cref="MasterConfig"/>, and the change is refused unless that key now reads as intended
/// and no new problem appeared. Then it is written beside the old file and swapped in with
/// <see cref="File.Replace(string, string, string?)"/>, so a crash leaves one whole file or
/// the other.
/// </para>
/// </remarks>
internal static partial class MasterConfigWriter
{
    /// <summary>
    /// One change: <c>section.key</c> set to a JSON literal, or removed when the literal is null.
    /// <paramref name="ExpectValue"/> is how it must read back (null: any value).
    /// </summary>
    internal sealed record Edit(string Section, string Key, string? Literal, string? ExpectValue);

    /// <summary>Sets <c>user.passwordHash</c>. Returns null on success, or why not.</summary>
    public static string? SetPasswordHash(string path, string hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        return hash.Contains('"', StringComparison.Ordinal) || hash.Contains('\\', StringComparison.Ordinal)
            ? "The hash has a character JSON would need to escape."
            : Apply(path, [new Edit("user", "passwordHash", $"\"{hash}\"", "(set)")], write: true);
    }

    /// <summary>
    /// Sets <c>section.key</c> to a JSON literal. Nothing is written when <paramref name="write"/>
    /// is false: the change is only proved.
    /// </summary>
    public static string? Set(string path, string section, string key, string literal, string expectValue, bool write) =>
        Apply(path, [new Edit(section, key, literal, expectValue)], write);

    /// <summary>Removes <c>section.key</c> so its default applies.</summary>
    public static string? Unset(string path, string section, string key, bool write) =>
        Apply(path, [new Edit(section, key, null, null)], write);

    /// <summary>
    /// Several changes that only make sense together (a seat's section and its place in
    /// <c>chat.seats</c>), made in the text in order and proved once, as one: every change must
    /// read back as intended and no new problem may appear. All are kept, or none.
    /// </summary>
    /// <param name="tolerate">New problems the caller expects and accepts (a seat's left-over section reported as unknown); any other new problem refuses the change.</param>
    public static string? Apply(string path, IReadOnlyList<Edit> edits, bool write, Func<string, bool>? tolerate = null)
    {
        ArgumentNullException.ThrowIfNull(edits);
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{path}: cannot be read ({ex.Message}).";
        }

        var before = MasterConfig.Load(path);
        if (before.Problems.Any(p => p.Contains("appears twice", StringComparison.Ordinal)))
        {
            return $"{path} has a key given twice; fix that by hand first, so there is no doubt which one is changed.";
        }

        var updated = text;

        // The main model's section still under its former name: the first change to a main.* key
        // renames it, so the change lands in the section that is read.
        if (before.LegacyMain && edits.Any(e => string.Equals(e.Section, "main", StringComparison.OrdinalIgnoreCase)))
        {
            var (renamed, why) = RenameSection(updated, "ai", "main");
            if (renamed is null)
            {
                return why;
            }

            updated = renamed;
        }

        foreach (var edit in edits)
        {
            var (next, error) = EditText(updated, edit);
            if (next is null)
            {
                return error ?? $"{path} is not a JSON object.";
            }

            updated = next;
        }

        if (updated == text)
        {
            return null;
        }

        // Prove it before keeping it.
        var probe = path + ".check";
        try
        {
            File.WriteAllText(probe, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var after = MasterConfig.Load(probe);
            var fresh = after.Problems.Except(before.Problems.Select(p => p.Replace(path, probe, StringComparison.Ordinal))).Where(p => tolerate?.Invoke(p) != true).ToList();
            if (fresh.Count > 0)
            {
                return $"refused: {fresh[0].Replace(probe, path, StringComparison.Ordinal)}";
            }

            foreach (var edit in edits)
            {
                var name = $"{edit.Section}.{edit.Key}";
                var setting = after.Settings.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
                var expectSet = edit.Literal is not null;
                if (setting is null || setting.FromDefault == expectSet || (edit.ExpectValue is not null && setting.Value != edit.ExpectValue)
                    || (name == "user.passwordHash" && edit.Literal is not null && after.UserPasswordHash != edit.Literal.Trim('"')))
                {
                    return $"refused: '{name}' would not read back as intended.";
                }
            }
        }
        finally
        {
            File.Delete(probe);
        }

        if (!write)
        {
            return null;
        }

        var temporary = path + ".new";
        try
        {
            File.WriteAllText(temporary, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Replace(temporary, path, destinationBackupFileName: null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            File.Delete(temporary);
            return $"{path}: cannot be written ({ex.Message}).";
        }

        return null;
    }

    /// <summary>One change made in the text: the new text, or why it cannot be made.</summary>
    private static (string? Text, string? Error) EditText(string text, Edit edit)
    {
        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var comments = CommentMask(text);
        var sectionMatch = SectionRegex(edit.Section).Matches(text).FirstOrDefault(m => !comments[m.Index]) ?? Match.Empty;

        if (!sectionMatch.Success)
        {
            if (edit.Literal is null)
            {
                return (text, null);
            }

            // A new section goes before the file's last brace.
            var end = text.LastIndexOf('}');
            if (end < 0)
            {
                return (null, null);
            }

            var head = text[..end].TrimEnd();
            var comma = head.EndsWith(',') || head.EndsWith('{') ? string.Empty : ",";
            return ($"{head}{comma}{nl}{nl}  \"{edit.Section}\": {{{nl}    \"{edit.Key}\": {edit.Literal},{nl}  }},{nl}{text[end..]}", null);
        }

        var body = sectionMatch.Groups["body"];
        var slot = KeyRegex(edit.Key).Matches(body.Value).FirstOrDefault(m => !comments[body.Index + m.Index]) ?? Match.Empty;
        if (edit.Literal is not null && slot.Success)
        {
            var value = slot.Groups["value"];
            var at = body.Index + value.Index;
            return (string.Concat(text.AsSpan(0, at), edit.Literal, text.AsSpan(at + value.Length)), null);
        }

        if (edit.Literal is not null)
        {
            return (string.Concat(text.AsSpan(0, body.Index), $"{nl}    \"{edit.Key}\": {edit.Literal},", text.AsSpan(body.Index)), null);
        }

        if (!slot.Success)
        {
            return (text, null);
        }

        var line = LineRegex(edit.Key).Match(body.Value);
        if (!line.Success)
        {
            return (null, $"'{edit.Section}.{edit.Key}' shares its line with something else; remove it by hand.");
        }

        var from = body.Index + line.Index;
        return (string.Concat(text.AsSpan(0, from), text.AsSpan(from + line.Length)), null);
    }

    /// <summary>A section renamed in the text, its body untouched: the new text, or why it cannot be.</summary>
    private static (string? Text, string? Error) RenameSection(string text, string from, string to)
    {
        var comments = CommentMask(text);
        var match = SectionRegex(from).Matches(text).FirstOrDefault(m => !comments[m.Index]);
        if (match is null)
        {
            return (null, $"the '{from}' section could not be found to rename it to '{to}'.");
        }

        // The match starts at the name's opening quote, so the name and its quotes are replaced whole.
        var nameLength = from.Length + 2;
        return (string.Concat(text.AsSpan(0, match.Index), $"\"{to}\"", text.AsSpan(match.Index + nameLength)), null);
    }

    /// <summary>
    /// For each character of a JSON-with-comments text, whether it sits inside a comment. Strings
    /// are skipped as strings, so "//" inside a value is not a comment.
    /// </summary>
    private static bool[] CommentMask(string text)
    {
        var mask = new bool[text.Length + 1];
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"')
            {
                for (i++; i < text.Length && text[i] != '"'; i++)
                {
                    if (text[i] == '\\')
                    {
                        i++;
                    }
                }

                i++;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    mask[i++] = true;
                }
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 2;
                while (i < end)
                {
                    mask[i++] = true;
                }
            }
            else
            {
                i++;
            }
        }

        return mask;
    }

    private static Regex SectionRegex(string section) => new(
        $"\"{Regex.Escape(section)}\"\\s*:\\s*\\{{(?<body>[^{{}}]*)\\}}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static Regex KeyRegex(string key) => new(
        $"\"{Regex.Escape(key)}\"\\s*:\\s*(?<value>\"(?:[^\"\\\\\\r\\n]|\\\\.)*\"|\\[[^\\]\\r\\n]*\\]|true|false|null|-?\\d+(?:\\.\\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    // The whole line holding the key and nothing else, with its trailing comma.
    private static Regex LineRegex(string key) => new(
        $"\\r?\\n[ \\t]*\"{Regex.Escape(key)}\"\\s*:\\s*(\"(?:[^\"\\\\\\r\\n]|\\\\.)*\"|\\[[^\\]\\r\\n]*\\]|true|false|null|-?\\d+(?:\\.\\d+)?)[ \\t]*,?[ \\t]*(?=\\r?\\n)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
}
