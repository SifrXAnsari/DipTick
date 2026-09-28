using System.Reflection;
using DipTick.Models;

namespace DipTick.Controllers;

/// <summary>One page of DipTick's own docs: its id (its file's name), its title, and its Markdown.</summary>
internal sealed record DocPage(string Id, string Title, string Text);

/// <summary>A line a search found: its page, its number on the page, and its text.</summary>
internal sealed record DocHit(DocPage Page, int Line, string Text);

/// <summary>
/// DipTick's official docs, inside diptick.exe: the Markdown pages in the repository's
/// <c>docs\user</c>, compiled in at build, so the window, the terminal screen and <c>diptick docs</c>
/// show the same pages, offline, and each says which version it is for. The command line's page is
/// made from <c>diptick help</c> itself, so the two cannot differ. A page ships with what it
/// describes: none describes something DipTick does not do.
/// </summary>
internal static class Docs
{
    /// <summary>The page made from <c>diptick help</c>.</summary>
    public const string CommandLine = "command-line";

    /// <summary>The first page.</summary>
    public const string Start = "start-here";

    /// <summary>The page about the files DipTick keeps: Settings, Data's "What is this?".</summary>
    public const string YourData = "your-data";

    /// <summary>The order the pages are listed in.</summary>
    private static readonly string[] Order =
    [
        Start, "the-words", "chats", "agents", "tools", "research", "files", "connectors", "skills", "running-code", "private-chats", "keyboard", "permissions", YourData, "export-and-backup", "activity", "providers", "keys",
        "what-providers-see", "when-something-is-wrong", "whats-new", CommandLine, "about", "licences",
    ];

    /// <summary>What a page writes where the version goes; nothing else in double braces is touched.</summary>
    private const string VersionToken = "{{version}}";

    private static readonly Lazy<IReadOnlyList<DocPage>> Loaded = new(Load);

    /// <summary>Every page, in order.</summary>
    public static IReadOnlyList<DocPage> Pages => Loaded.Value;

    /// <summary>A page by its id, or by its title, whatever the case; null when there is none.</summary>
    public static DocPage? Find(string idOrTitle)
    {
        ArgumentNullException.ThrowIfNull(idOrTitle);
        var wanted = idOrTitle.Trim();
        var asId = string.Join('-', wanted.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return Pages.FirstOrDefault(p => string.Equals(p.Id, asId, StringComparison.OrdinalIgnoreCase))
            ?? Pages.FirstOrDefault(p => string.Equals(p.Title, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The page F1 opens on a screen: Chat, What they're told, Activity; any other screen, the first page.</summary>
    public static DocPage For(string screen) => screen switch
    {
        "Chat" => Find("chats")!,
        "What they're told" => Find("agents")!,
        "Tools" => Find("tools")!,
        "Activity" => Find("activity")!,
        _ => Find(Start)!,
    };

    /// <summary>Every line, on every page, that holds all of <paramref name="words"/>, whatever the case; a page's title counts as its first line.</summary>
    public static IReadOnlyList<DocHit> Search(string words)
    {
        ArgumentNullException.ThrowIfNull(words);
        var each = words.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (each.Length == 0)
        {
            return [];
        }

        var hits = new List<DocHit>();
        foreach (var page in Pages)
        {
            var lines = page.Text.ReplaceLineEndings("\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length > 0 && each.All(w => line.Contains(w, StringComparison.OrdinalIgnoreCase)))
                {
                    hits.Add(new DocHit(page, i + 1, line));
                }
            }
        }

        return hits;
    }

    /// <summary>
    /// A found line as it reads, cut to about <paramref name="most"/> characters around the first
    /// of <paramref name="words"/> it holds, never through a word, an ellipsis where it was cut:
    /// what a search result shows of it.
    /// </summary>
    public static string Snippet(string line, string words, int most)
    {
        ArgumentNullException.ThrowIfNull(words);
        var text = MarkdownText.Read(line);
        if (text.Length <= most)
        {
            return text;
        }

        var found = words.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => text.IndexOf(w, StringComparison.OrdinalIgnoreCase))
            .Where(i => i >= 0)
            .DefaultIfEmpty(0)
            .Min();

        // A quarter of the room before the word, so it reads in its sentence.
        var start = Math.Clamp(found - (most / 4), 0, text.Length - most);
        if (start > 0 && text.IndexOf(' ', start) is var space && space >= 0 && space < found)
        {
            start = space + 1;
        }

        var end = Math.Min(text.Length, start + most);
        if (end < text.Length && text.LastIndexOf(' ', end) is var last && last > found)
        {
            end = last;
        }

        return (start > 0 ? "…" : string.Empty) + text[start..end].Trim() + (end < text.Length ? "…" : string.Empty);
    }

    /// <summary>The line under each page's title: which DipTick it describes.</summary>
    public static string VersionLine => $"*For DipTick {CommandController.VersionText}.*";

    private static List<DocPage> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var pages = new List<DocPage>();
        foreach (var id in Order)
        {
            string? text;
            if (id == CommandLine)
            {
                text = HelpText.Page();
            }
            else
            {
                using var stream = assembly.GetManifestResourceStream($"docs/{id}.md");
                if (stream is null)
                {
                    // A page listed in Order and not built in is skipped.
                    continue;
                }

                using var reader = new StreamReader(stream);
                text = reader.ReadToEnd();
            }

            pages.Add(Page(id, text));
        }

        return pages;
    }

    /// <summary>
    /// A page from its Markdown: the title is its first heading, the version line goes under it, and
    /// the version is written in where the page names it.
    /// </summary>
    private static DocPage Page(string id, string markdown)
    {
        var lines = markdown.Replace(VersionToken, CommandController.VersionText, StringComparison.Ordinal).ReplaceLineEndings("\n").Split('\n').ToList();
        var heading = lines.FindIndex(l => l.StartsWith("# ", StringComparison.Ordinal));
        var title = heading < 0 ? id : lines[heading][2..].Trim();
        lines.Insert(heading < 0 ? 0 : heading + 1, string.Empty);
        lines.Insert(heading < 0 ? 1 : heading + 2, VersionLine);
        return new DocPage(id, title, SafeText.Block(string.Join('\n', lines)).TrimEnd() + "\n");
    }
}
