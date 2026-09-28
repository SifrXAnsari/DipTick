using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace DipTick.Models.Research;

/// <summary>What a page reads as: its title, its main text, whether that text is its article, and the addresses it links to.</summary>
internal sealed record ReadablePage(string Title, string Text, bool Article, IReadOnlyList<string> Links);

/// <summary>
/// A web page as text a model can read: the page parsed by AngleSharp, an
/// HTML5 parser; its links and its title read first; then its main text by SmartReader, a port of
/// Mozilla's Readability; or, where SmartReader finds no article, the text of the page's body, without
/// its scripts, styles and templates, a line for each block. Nothing here fetches anything:
/// SmartReader is handed the parsed page and its address, and never asked to download.
/// </summary>
internal static class Readable
{
    /// <summary>The most links a page gives, so a page of ten thousand links cannot fill the chat's memory of addresses.</summary>
    public const int MostLinks = 2000;

    /// <summary>Elements whose text is never a page's words.</summary>
    private static readonly string[] Silent = ["script", "style", "noscript", "template", "svg", "canvas", "iframe", "object", "head"];

    /// <summary>Elements that start a line of their own.</summary>
    private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "body", "br", "dd", "details", "div", "dl", "dt", "fieldset", "figcaption", "figure",
        "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "li", "main", "nav", "ol", "p", "pre", "section", "summary",
        "table", "tbody", "td", "tfoot", "th", "thead", "tr", "ul",
    };

    /// <summary>Reads a page fetched from <paramref name="address"/>; <paramref name="html"/> is its text as decoded.</summary>
    public static ReadablePage From(Uri address, string html)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(html);
        var parser = new HtmlParser(new HtmlParserOptions { IsScripting = false });
        using var document = parser.ParseDocument(html);
        var title = Line(document.Title ?? string.Empty);
        var links = Links(document, address);

        // The body's own text first, since SmartReader rearranges the page it is given.
        foreach (var silent in document.QuerySelectorAll(string.Join(", ", Silent)).ToList())
        {
            silent.Remove();
        }

        var body = document.Body is { } b ? Tidy(TextOf(b)) : string.Empty;

        try
        {
            using var reader = new SmartReader.Reader(address.AbsoluteUri, document) { Debug = false };
            var article = reader.GetArticle();
            if (article.IsReadable && article.TextContent is { Length: > 0 } text)
            {
                var main = Tidy(text);
                return new ReadablePage(title.Length > 0 ? title : Line(article.Title ?? string.Empty), main, true, links);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException or FormatException)
        {
            // SmartReader could not make an article of it; the body's text stands.
        }

        return new ReadablePage(title, body, false, links);
    }

    /// <summary>Every link on the page, absolute, http or https, each once, in the page's order.</summary>
    private static List<string> Links(IHtmlDocument document, Uri address)
    {
        var baseUri = address;
        if (document.QuerySelector("base[href]")?.GetAttribute("href") is { Length: > 0 } declared && Uri.TryCreate(address, declared.Trim(), out var declaredBase))
        {
            baseUri = declaredBase;
        }

        var links = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var anchor in document.QuerySelectorAll("a[href], area[href]"))
        {
            if (links.Count >= MostLinks)
            {
                break;
            }

            var href = anchor.GetAttribute("href")?.Trim();
            if (string.IsNullOrEmpty(href) || !Uri.TryCreate(baseUri, href, out var link) || (link.Scheme != Uri.UriSchemeHttps && link.Scheme != Uri.UriSchemeHttp))
            {
                continue;
            }

            var kept = new UriBuilder(link) { Fragment = string.Empty }.Uri.AbsoluteUri;
            if (seen.Add(kept))
            {
                links.Add(kept);
            }
        }

        return links;
    }

    /// <summary>The text of an element and everything in it, a line break around each block.</summary>
    private static string TextOf(INode root)
    {
        var text = new StringBuilder();
        void Walk(INode node)
        {
            switch (node.NodeType)
            {
                case NodeType.Text:
                    text.Append(node.TextContent);
                    break;
                case NodeType.Element when node is IElement element:
                    var block = Blocks.Contains(element.LocalName);
                    if (block)
                    {
                        text.Append('\n');
                    }

                    foreach (var child in element.ChildNodes)
                    {
                        Walk(child);
                    }

                    if (block)
                    {
                        text.Append('\n');
                    }

                    break;
            }
        }

        Walk(root);
        return text.ToString();
    }

    /// <summary>Runs of spaces made one, blank lines at most one in a row, each line trimmed.</summary>
    internal static string Tidy(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<string>();
        var blank = false;
        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = CollapseSpaces(raw);
            if (line.Length == 0)
            {
                if (!blank && lines.Count > 0)
                {
                    lines.Add(string.Empty);
                }

                blank = true;
                continue;
            }

            lines.Add(line);
            blank = false;
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join('\n', lines);
    }

    private static string CollapseSpaces(string line)
    {
        var text = new StringBuilder(line.Length);
        var space = false;
        foreach (var c in line)
        {
            if (char.IsWhiteSpace(c))
            {
                space = text.Length > 0;
                continue;
            }

            if (space)
            {
                text.Append(' ');
                space = false;
            }

            text.Append(c);
        }

        return text.ToString();
    }

    private static string Line(string text) => CollapseSpaces(text.ReplaceLineEndings(" "));
}
