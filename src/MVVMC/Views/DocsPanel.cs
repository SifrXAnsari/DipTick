using DipTick.Controllers;
using DipTick.Models;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace DipTick.Views;

/// <summary>
/// Help in the terminal screen: DipTick's official pages (<see cref="Docs"/>), the same the
/// window's Help and <c>diptick docs</c> show. The pages down the left, the page on the right, drawn
/// from its Markdown: headings in the accent, a link's words in the accent too (the page it names is
/// in the list), code dim. ↑/↓ choose in the list or scroll the page, Tab moves between the two,
/// Enter opens the page chosen, Esc goes back to the chat. Its F-keys are the window's: F1 Contents,
/// F2 Back, F3 Search, F4 Pages, F7 Save as files. While a search shows, the list takes half the
/// panel: each line found under its page's title, cut around the words.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Usage",
    "CA2213:Disposable fields should be disposed",
    Justification = "Each field is a subview added in the constructor; Terminal.Gui disposes a view's subviews when the view itself " +
                    "is disposed. Disposing them here as well would dispose them twice.")]
internal sealed class DocsPanel : View
{
    /// <summary>The list's width: the longest title, and a mark before it.</summary>
    private const int ListWidth = 28;

    /// <summary>About how many characters of a found line a search result shows.</summary>
    private const int SnippetLength = 140;

    private readonly RowsView _list = new() { X = 0, Y = 0, Width = ListWidth, Height = Dim.Fill() };
    private readonly RowsView _page = new() { Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
    private readonly Stack<string> _back = new();
    private IReadOnlyList<DocHit> _hits = [];
    private List<int> _hitRows = [];
    private string? _searched;
    private string? _note;
    private DocPage _shown;
    private int _mark;
    private int _listWidth = ListWidth;
    private bool _inList = true;

    public DocsPanel()
    {
        CanFocus = true;
        Visible = false;
        BorderStyle = LineStyle.Rounded;
        Title = "Help";
        SetScheme(Theme.Lit);
        _page.X = Pos.Right(_list) + 1;
        Add(_list, _page);
        _shown = Docs.Find(Docs.Start) ?? Docs.Pages[0];
        KeyDown += (_, key) =>
        {
            if (OnKey(key))
            {
                key.Handled = true;
            }
        };
        ViewportChanged += (_, _) => Draw();
    }

    /// <summary>Raised when the person leaves Help.</summary>
    public event Action? Closed;

    /// <summary>Opens Help at a page, by its id; the page it leaves is kept for Back.</summary>
    public void Open(string id)
    {
        if (Docs.Find(id) is not { } page)
        {
            return;
        }

        if (page.Id != _shown.Id)
        {
            _back.Push(_shown.Id);
        }

        _note = null;
        _shown = page;
        _mark = Index(page.Id);
        _page.Top = 0;
        Visible = true;
        Draw();
        SetFocus();
    }

    /// <summary>F1: the first page, and the list to choose from.</summary>
    public void Contents()
    {
        ClearSearch();
        Open(Docs.Start);
        _inList = true;
        Draw();
    }

    /// <summary>F2: the page before this one.</summary>
    public void Back()
    {
        if (_back.TryPop(out var id) && Docs.Find(id) is { } page)
        {
            _note = null;
            _shown = page;
            _mark = Index(page.Id);
            _page.Top = 0;
            Draw();
        }
    }

    /// <summary>F4: the list of pages, with any search cleared.</summary>
    public void Pages()
    {
        ClearSearch();
        _inList = true;
        _mark = Index(_shown.Id);
        Draw();
    }

    /// <summary>F6: from the list to the page, and back.</summary>
    public void NextPane()
    {
        _inList = !_inList;
        Draw();
    }

    /// <summary>F3's answer: the lines that hold the words, in the list, each under its page's title; Enter opens one.</summary>
    public void Search(string words)
    {
        ArgumentNullException.ThrowIfNull(words);
        _note = null;
        _searched = words.Trim().Length == 0 ? null : words.Trim();
        _hits = _searched is null ? [] : Docs.Search(_searched);
        _mark = 0;
        _inList = true;
        Draw();
    }

    /// <summary>Esc from the chat's side: back to the chat.</summary>
    public void Close()
    {
        ClearSearch();
        _note = null;
        Visible = false;
        Closed?.Invoke();
    }

    /// <summary>A line above the page, until the next move: what F7 wrote, where the person is looking.</summary>
    public void Note(string text)
    {
        _note = SafeText.Line(text);
        Draw();
    }

    /// <summary>A page's Markdown as rows no wider than <paramref name="width"/>, for the page pane.</summary>
    internal static List<IReadOnlyList<Span>> Render(string markdown, int width)
    {
        var rows = new List<IReadOnlyList<Span>>();
        MdKind? last = null;
        foreach (var block in MarkdownText.Parse(markdown))
        {
            if (last is not null && !(last == MdKind.Item && block.Kind == MdKind.Item))
            {
                rows.Add([]);
            }

            switch (block.Kind)
            {
                case MdKind.Heading:
                    var title = string.Concat(MarkdownText.Spans(block.Text).Select(s => s.Text));
                    rows.AddRange(Flowed([(block.Level == 1 ? title.ToUpperInvariant() : title, Theme.Accent)], width, " ", " "));
                    break;
                case MdKind.Item:
                    rows.AddRange(Flowed(Pieces(block.Text), width, $" {block.Mark} ", new string(' ', block.Mark.Length + 2)));
                    break;
                case MdKind.Code:
                    foreach (var line in block.Text.Split('\n'))
                    {
                        var shown = "   " + line;
                        rows.Add(Theme.Row(shown.Length <= width ? shown : shown[..Math.Max(1, width - 1)] + "…", Theme.Dim));
                    }

                    break;
                default:
                    // A paragraph that is all italic (a page's "For DipTick" line) recedes.
                    rows.AddRange(Flowed(MarkdownText.Spans(block.Text) is [{ Italic: true } only] ? [(only.Text, Theme.Dim)] : Pieces(block.Text), width, " ", " "));
                    break;
            }

            last = block.Kind;
        }

        return rows;
    }

    /// <summary>A line's spans as pieces to draw: a link's words in the accent, with an outside address after them; code dim.</summary>
    private static List<(string Text, Attribute Look)> Pieces(string text) =>
    [
        .. MarkdownText.Spans(text).Select(s => s.Link is { } to
            ? (to.StartsWith("https://", StringComparison.Ordinal) ? $"{s.Text} ({to})" : s.Text, Theme.Accent)
            : (s.Text, s.Code ? Theme.Dim : Theme.Text)),
    ];

    /// <summary>
    /// Pieces flowed into rows no wider than <paramref name="width"/>, word by word, each word in its
    /// piece's look: the first row starts with <paramref name="first"/>, the others with
    /// <paramref name="rest"/>. A word wider than a row is cut across rows.
    /// </summary>
    private static List<IReadOnlyList<Span>> Flowed(IReadOnlyList<(string Text, Attribute Look)> pieces, int width, string first, string rest)
    {
        var rows = new List<IReadOnlyList<Span>>();
        var row = new List<Span> { new(first, Theme.Dim) };
        var start = first.Length;
        var used = start;

        void NewRow()
        {
            rows.Add(row);
            row = [new Span(rest, Theme.Dim)];
            start = rest.Length;
            used = start;
        }

        foreach (var (word, look, glued) in Words(pieces))
        {
            var left = word;
            while (left.Length > 0)
            {
                // A word goes after a space, unless it touched the one before it in the text ("Chats:").
                var space = used > start && !glued ? 1 : 0;
                if (used + space + left.Length > width && used > start)
                {
                    NewRow();
                    continue;
                }

                var room = Math.Max(1, width - used - space);
                var piece = left.Length <= room ? left : left[..room];
                if (space == 1)
                {
                    row.Add(new Span(" ", Theme.Text));
                }

                row.Add(new Span(piece, look));
                used += space + piece.Length;
                left = left[piece.Length..];
                if (left.Length > 0)
                {
                    NewRow();
                }
            }
        }

        rows.Add(row);
        return rows;
    }

    /// <summary>The pieces' words, each with its look and whether it touches the word before it (no space between in the text).</summary>
    private static List<(string Word, Attribute Look, bool Glued)> Words(IReadOnlyList<(string Text, Attribute Look)> pieces)
    {
        var words = new List<(string Word, Attribute Look, bool Glued)>();
        var touching = false;
        foreach (var (text, look) in pieces)
        {
            var parts = text.Split(' ');
            for (var i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0)
                {
                    touching = false;
                    continue;
                }

                words.Add((parts[i], look, i == 0 && touching));
                touching = true;
            }

            // A space between two parts of one piece parts its words; the last part touches what follows.
            touching = parts[^1].Length > 0;
        }

        return words;
    }

    private static int Index(string id) => Math.Max(0, Docs.Pages.ToList().FindIndex(p => p.Id == id));

    private void ClearSearch()
    {
        _searched = null;
        _hits = [];
        _note = null;
    }

    private bool OnKey(Key key)
    {
        if (key == Key.Esc)
        {
            if (_searched is not null)
            {
                Pages();
            }
            else
            {
                Close();
            }

            return true;
        }

        if (key == Key.Tab || key == Key.Tab.WithShift)
        {
            NextPane();
            return true;
        }

        var count = _searched is null ? Docs.Pages.Count : _hits.Count;
        if (_inList && (key == Key.CursorUp || key == Key.CursorDown) && count > 0)
        {
            _mark = Math.Clamp(_mark + (key == Key.CursorUp ? -1 : 1), 0, count - 1);
            Draw();
            return true;
        }

        if (key == Key.Enter && _inList && count > 0)
        {
            var id = _searched is null ? Docs.Pages[_mark].Id : _hits[_mark].Page.Id;
            ClearSearch();
            Open(id);
            _inList = false;
            Draw();
            return true;
        }

        if (!_inList && (key == Key.CursorUp || key == Key.CursorDown))
        {
            _page.ScrollBy(key == Key.CursorUp ? -1 : 1);
            return true;
        }

        if (key == Key.PageUp || key == Key.PageDown)
        {
            _page.ScrollBy((key == Key.PageUp ? -1 : 1) * Math.Max(1, _page.Viewport.Height - 1));
            return true;
        }

        return false;
    }

    private void Draw()
    {
        // A found line needs room to be read: half the panel while a search shows, the titles' width otherwise.
        var panel = Viewport.Width > 0 ? Viewport.Width : Math.Max(60, Theme.ScreenWidth() - 2);
        var listWidth = _searched is null ? ListWidth : Math.Max(ListWidth, (panel - 1) / 2);
        if (_listWidth != listWidth)
        {
            _listWidth = listWidth;
            _list.Width = listWidth;
        }

        var listRows = new List<IReadOnlyList<Span>>();
        _hitRows = [];
        if (_searched is null)
        {
            for (var i = 0; i < Docs.Pages.Count; i++)
            {
                var on = i == _mark;
                var current = Docs.Pages[i].Id == _shown.Id;
                listRows.Add([new Span(on ? "› " : "  ", _inList ? Theme.Accent : Theme.Dim), new Span(Docs.Pages[i].Title, current ? Theme.Accent : Theme.Text)]);
            }
        }
        else
        {
            listRows.Add(Theme.Row(_hits.Count == 0 ? $"  nothing holds '{_searched}'" : $"  '{_searched}': {_hits.Count}", Theme.Dim));
            for (var i = 0; i < _hits.Count; i++)
            {
                // Each found line under its page's title, cut around the words and wrapped to the list.
                var on = i == _mark;
                _hitRows.Add(listRows.Count);
                listRows.Add([new Span(on ? "› " : "  ", Theme.Accent), new Span(_hits[i].Page.Title, on ? Theme.Accent : Theme.Dim)]);
                foreach (var line in Theme.Wrap(Docs.Snippet(_hits[i].Text, _searched, SnippetLength), Math.Max(10, listWidth - 4)))
                {
                    listRows.Add([new Span("    ", Theme.Text), new Span(line, on ? Theme.Text : Theme.Dim)]);
                }
            }
        }

        _list.Rows = listRows;
        _list.Show(_searched is null ? _mark : _hitRows.Count > 0 ? _hitRows[_mark] : 0);
        var width = Math.Max(20, panel - listWidth - 1);
        var pageRows = Render(_shown.Text, width);
        if (_note is { } note)
        {
            pageRows.InsertRange(0, [.. Theme.Wrap(note, width - 1).Select(line => Theme.Row(" " + line, Theme.Accent)), []]);
        }

        _page.Rows = pageRows;
        SetNeedsDraw();
    }
}
