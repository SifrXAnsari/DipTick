using System.Text;

namespace DipTick.Models;

/// <summary>What kind of block a stretch of an answer is.</summary>
internal enum MdKind
{
    Paragraph,
    Heading,
    Item,
    Code,
}

/// <summary>
/// One block of an answer: a paragraph, a heading (<paramref name="Level"/> 1-4), a list item with
/// its <paramref name="Mark"/> ("•", "1."), or a code block with its fence's info in
/// <paramref name="Mark"/>.
/// </summary>
internal sealed record MdBlock(MdKind Kind, string Text, int Level = 0, string Mark = "");

/// <summary>
/// A piece of a line as it is meant to be read: plain, bold, italic or code, or a link, whose
/// <paramref name="Link"/> is where it goes: another page by its id, or an https address.
/// </summary>
internal sealed record MdSpan(string Text, bool Bold = false, bool Italic = false, bool Code = false, string? Link = null);

/// <summary>
/// The Markdown every model writes, read into blocks and spans, with no screen code in it: the
/// window draws these, and Copy and <c>diptick chat show --seq</c> print their plain text, so the
/// two never differ. Small on purpose, and it never throws: anything it does not understand is a
/// paragraph.
/// </summary>
internal static class MarkdownText
{
    /// <summary>The blocks of <paramref name="text"/>, in order.</summary>
    public static IReadOnlyList<MdBlock> Parse(string text)
    {
        var blocks = new List<MdBlock>();
        var lines = (text ?? string.Empty).ReplaceLineEndings("\n").Split('\n');
        var paragraph = new List<string>();
        var i = 0;

        void Flush()
        {
            if (paragraph.Count > 0)
            {
                blocks.Add(new MdBlock(MdKind.Paragraph, string.Join(' ', paragraph.Select(l => l.Trim()))));
                paragraph.Clear();
            }
        }

        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                Flush();
                var info = trimmed[3..].Trim();
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    code.Add(lines[i]);
                    i++;
                }

                // Past the closing fence; an answer cut off mid-block ends the block at its end.
                i++;
                blocks.Add(new MdBlock(MdKind.Code, string.Join('\n', code), Mark: info));
                continue;
            }

            if (trimmed.Length == 0)
            {
                Flush();
                i++;
                continue;
            }

            var level = 0;
            while (level < trimmed.Length && level < 4 && trimmed[level] == '#')
            {
                level++;
            }

            if (level > 0 && level < trimmed.Length && trimmed[level] == ' ')
            {
                Flush();
                blocks.Add(new MdBlock(MdKind.Heading, trimmed[(level + 1)..].Trim(), Level: level));
                i++;
                continue;
            }

            if (Bullet(trimmed) is { } item)
            {
                Flush();
                blocks.Add(new MdBlock(MdKind.Item, item.Text.Trim(), Mark: item.Mark));
                i++;
                continue;
            }

            paragraph.Add(line);
            i++;
        }

        Flush();
        return blocks;
    }

    /// <summary>
    /// <c>`code`</c>, <c>[text](target)</c> links, <c>**bold**</c> and <c>*italic*</c>, in that order of
    /// precedence; anything unmatched is text. A link's target is kept only when it is a page's id
    /// (letters, digits and hyphens) or an https address; any other is text as written.
    /// </summary>
    public static IReadOnlyList<MdSpan> Spans(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var spans = new List<MdSpan>();
        var plain = new StringBuilder();
        var i = 0;

        void Emit(MdSpan span)
        {
            if (span.Text.Length > 0)
            {
                spans.Add(span);
            }
        }

        void EmitPlain()
        {
            Emit(new MdSpan(plain.ToString()));
            plain.Clear();
        }

        while (i < text.Length)
        {
            if (text[i] == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    EmitPlain();
                    Emit(new MdSpan(text[(i + 1)..end], Code: true));
                    i = end + 1;
                    continue;
                }
            }

            if (text[i] == '[' && Link(text, i) is { } link)
            {
                EmitPlain();
                Emit(new MdSpan(link.Text, Link: link.Target));
                i = link.End;
                continue;
            }

            if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 1)
                {
                    EmitPlain();
                    Emit(new MdSpan(text[(i + 2)..end], Bold: true));
                    i = end + 2;
                    continue;
                }
            }

            if (text[i] == '*' && i + 1 < text.Length && text[i + 1] != ' ')
            {
                var end = text.IndexOf('*', i + 1);
                if (end > i + 1)
                {
                    EmitPlain();
                    Emit(new MdSpan(text[(i + 1)..end], Italic: true));
                    i = end + 1;
                    continue;
                }
            }

            plain.Append(text[i]);
            i++;
        }

        EmitPlain();
        return spans;
    }

    /// <summary>
    /// The answer as it reads on the screen, without the Markdown marks: paragraphs a blank line
    /// apart, a list's items line under line, code as it is. What Copy puts on the clipboard.
    /// </summary>
    public static string Plain(string text)
    {
        var shown = new StringBuilder();
        MdKind? last = null;
        foreach (var block in Parse(SafeText.Block(text ?? string.Empty)))
        {
            if (last is { } before)
            {
                shown.Append(before == MdKind.Item && block.Kind == MdKind.Item ? "\n" : "\n\n");
            }

            switch (block.Kind)
            {
                case MdKind.Code:
                    shown.Append(block.Text);
                    break;
                case MdKind.Item:
                    shown.Append(block.Mark).Append(' ').Append(Joined(block.Text));
                    break;
                default:
                    shown.Append(Joined(block.Text));
                    break;
            }

            last = block.Kind;
        }

        return shown.ToString();
    }

    /// <summary>A line as it reads, a list item after its mark and a link as its words alone: what a search result shows of it.</summary>
    public static string Read(string line) =>
        string.Join(' ', Parse(SafeText.Block(line ?? string.Empty)).Select(b => b.Kind switch
        {
            MdKind.Code => b.Text,
            MdKind.Item => b.Mark + " " + string.Concat(Spans(b.Text).Select(s => s.Text)),
            _ => string.Concat(Spans(b.Text).Select(s => s.Text)),
        }));

    /// <summary>The code blocks of an answer, each as written, for "Copy code".</summary>
    public static IReadOnlyList<string> CodeBlocks(string text) =>
        [.. Parse(SafeText.Block(text ?? string.Empty)).Where(b => b.Kind == MdKind.Code).Select(b => b.Text)];

    /// <summary>A line's spans as text; a link keeps where it goes, in brackets after its words, so a copy loses nothing.</summary>
    private static string Joined(string text) => string.Concat(Spans(text).Select(s => s.Link is { } to ? $"{s.Text} ({to})" : s.Text));

    /// <summary>A link at <paramref name="at"/>: its text, where it goes, and the index just past it; null when it is not one.</summary>
    private static (string Text, string Target, int End)? Link(string text, int at)
    {
        var close = text.IndexOf("](", at + 1, StringComparison.Ordinal);
        if (close <= at + 1)
        {
            return null;
        }

        var end = text.IndexOf(')', close + 2);
        if (end < 0)
        {
            return null;
        }

        var target = text[(close + 2)..end];
        var label = text[(at + 1)..close];
        if (label.Contains('[', StringComparison.Ordinal) || !IsTarget(target))
        {
            return null;
        }

        return (label, target, end + 1);
    }

    /// <summary>A page's id, or an https address with a host: the only places a link may go.</summary>
    private static bool IsTarget(string target) =>
        target.Length > 0 && (target.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
            || (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0));

    private static (string Mark, string Text)? Bullet(string line)
    {
        if (line.Length > 2 && line[0] is '-' or '*' or '•' && line[1] == ' ')
        {
            return ("•", line[2..]);
        }

        var dot = line.IndexOf(". ", StringComparison.Ordinal);
        if (dot is > 0 and < 4 && line[..dot].All(char.IsAsciiDigit))
        {
            return (line[..(dot + 1)], line[(dot + 2)..]);
        }

        return null;
    }
}
