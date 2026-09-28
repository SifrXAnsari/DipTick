using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DipTick.Models;
using DipTick.Models.Chat;
using DipTick.Models.Chat.Store;
using DipTick.Models.Files;
using DipTick.ViewModels;

namespace DipTick.Views.Desktop;

/// <summary>
/// What an answer's card offers beyond copying: what it used, for under it; the other agents at the
/// table; and asking its question again, of the same agent or another. With research: the sources
/// the answer served, its words as shown (a citation to a number the chat has no source for marked),
/// and opening a source's page in the browser. With files: an image's bytes to draw; Save…, Open and
/// Show in folder on a file; Save… on an answer's words or one of its code blocks.
/// </summary>
internal sealed record AnswerActions(
    Func<ChatLine, string> Usage,
    Func<IReadOnlyList<string>> Seats,
    Action<ChatLine, string?> Retry,
    Func<int, IReadOnlyList<StoredSource>>? Sources = null,
    Func<string, string>? Marked = null,
    Action<string>? Open = null)
{
    /// <summary>An image's bytes to draw, or null; called off the window's thread.</summary>
    public Func<ChatFile, byte[]?>? Image { get; init; }

    /// <summary>Save… on a file of the chat.</summary>
    public Action<ChatFile>? SaveFile { get; init; }

    /// <summary>Open, for a file saved on disk: Windows' own app for it.</summary>
    public Action<ChatFile>? OpenFile { get; init; }

    /// <summary>Show in folder, for a file saved on disk.</summary>
    public Action<ChatFile>? ShowFile { get; init; }

    /// <summary>Save… on a line's words, or on one of its code blocks, from 1.</summary>
    public Action<ChatLine, int?>? SaveWords { get; init; }
}

/// <summary>
/// The transcript as cards: <c>❯</c> the person, <c>┊</c> an agent (from Markdown) under its name
/// and colour, <c>·</c> DipTick's own lines. Only what changed is rebuilt, so a streaming answer
/// redraws one card. A message's text selects across its paragraphs; Copy and ⋯ show while the
/// pointer is over it; a right-click opens the same menu. "Select messages" ticks whole messages, to
/// copy several at once. Ctrl+F dims every card that does not match. Answers given at the same time
/// can be laid side by side (Compare), the way Open WebUI's compare mode lays parallel answers in
/// columns.
/// </summary>
internal sealed class TranscriptView : StackPanel
{
    private readonly List<Card> _cards = [];
    private readonly HashSet<ChatLine> _selected = new(ReferenceEqualityComparer.Instance);

    /// <summary>The rounds laid side by side: their answers given at the same time, each in a column.</summary>
    private readonly HashSet<int> _compared = [];
    private readonly Func<string, Task> _copy;
    private readonly Action<string> _quote;
    private readonly Action _selectionChanged;
    private readonly AnswerActions? _answers;
    private string _filter = string.Empty;
    private bool _compact;

    /// <param name="copy">Puts text on the clipboard.</param>
    /// <param name="quote">Puts a message, quoted, into the composer.</param>
    /// <param name="selectionChanged">Called when messages are ticked or unticked, or the ticking starts or stops.</param>
    /// <param name="answers">What an answer's card offers: its usage, Retry and Ask another agent; none in a view that only shows.</param>
    public TranscriptView(Func<string, Task> copy, Action<string> quote, Action selectionChanged, AnswerActions? answers = null)
    {
        _copy = copy;
        _quote = quote;
        _selectionChanged = selectionChanged;
        _answers = answers;
        Spacing = 10;
    }

    /// <summary>The first card that matches the filter, to scroll to; null without a filter or a match.</summary>
    public Control? FirstMatch { get; private set; }

    /// <summary>Whether whole messages are being ticked (Ctrl+A, or "Select messages").</summary>
    public bool Selecting { get; private set; }

    /// <summary>The ticked messages, in the order they were said.</summary>
    public IReadOnlyList<ChatLine> Selected => [.. _cards.Select(c => c.Line).Where(_selected.Contains)];

    /// <summary>How many cards are drawn: a round laid side by side is one card.</summary>
    internal int CardsShown => _cards.Count(c => c.Box.IsVisible);

    /// <summary>Compact: the messages sit closer (Settings, Appearance, Density).</summary>
    public bool Compact
    {
        get => _compact;
        set
        {
            _compact = value;
            Spacing = value ? 4 : 10;
            Invalidate();
        }
    }

    /// <summary>The text a message puts on the clipboard: an answer as it reads, anything else as it was said.</summary>
    public static string Plain(ChatLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return line.Role == Role.Agent ? MarkdownText.Plain(line.Text) : SafeText.Block(line.Text);
    }

    public void Render(IReadOnlyList<ChatLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        while (_cards.Count > lines.Count)
        {
            Children.Remove(_cards[^1].Box);
            _cards.RemoveAt(_cards.Count - 1);
        }

        for (var i = 0; i < lines.Count; i++)
        {
            if (i < _cards.Count)
            {
                if (_cards[i].Line != lines[i])
                {
                    var wasTicked = _selected.Remove(_cards[i].Line);
                    _cards[i].Line = lines[i];
                    if (wasTicked)
                    {
                        _selected.Add(lines[i]);
                    }
                }
            }
            else
            {
                var card = new Card(new Border { Classes = { "message" }, Padding = CardPadding() }, lines[i]);
                card.Box.AddHandler(PointerPressedEvent, (_, e) => OnCardPressed(card, e), RoutingStrategies.Tunnel);
                _cards.Add(card);
                Children.Add(card.Box);
            }
        }

        // Each card is drawn again only when what it shows changed: its line, or its round's lines laid side by side.
        var views = Views(lines);
        for (var i = 0; i < _cards.Count; i++)
        {
            var card = _cards[i];
            if (!views[i].SameAs(card.View))
            {
                card.View = views[i];
                if (views[i].Kind != ViewKind.Hidden)
                {
                    Fill(card);
                }
            }

            card.Box.IsVisible = views[i].Kind != ViewKind.Hidden;
        }

        var shown = new HashSet<ChatLine>(lines, ReferenceEqualityComparer.Instance);
        if (_selected.RemoveWhere(l => !shown.Contains(l)) > 0)
        {
            _selectionChanged();
        }

        ApplyFilter();
    }

    /// <summary>Rebuilds every card: after a theme change, the brushes are new.</summary>
    public void Invalidate()
    {
        foreach (var card in _cards)
        {
            card.Box.Padding = CardPadding();
            if (card.View.Kind != ViewKind.Hidden)
            {
                Fill(card);
            }
        }
    }

    public void Filter(string text)
    {
        _filter = text.Trim();
        ApplyFilter();
    }

    /// <summary>Starts ticking whole messages, with <paramref name="first"/> ticked when given.</summary>
    public void StartSelecting(ChatLine? first)
    {
        Selecting = true;
        if (first is not null)
        {
            _selected.Add(first);
        }

        ShowSelection();
    }

    /// <summary>Ticks every message (Ctrl+A in the chat).</summary>
    public void SelectAll()
    {
        Selecting = true;
        foreach (var card in _cards)
        {
            _selected.Add(card.Line);
        }

        ShowSelection();
    }

    /// <summary>Stops ticking, and unticks everything (Esc).</summary>
    public void StopSelecting()
    {
        Selecting = false;
        _selected.Clear();
        ShowSelection();
    }

    /// <summary>A round's answers given at the same time, side by side or one under another again.</summary>
    public void ToggleCompare(int round)
    {
        if (!_compared.Remove(round))
        {
            _compared.Add(round);
        }

        Render([.. _cards.Select(c => c.Line)]);
    }

    /// <summary>
    /// What each card shows: its own line; or, for the first answer of a round laid side by side, every
    /// answer of its step in columns, the others' cards hidden. A step's answers are the agents' lines of
    /// one round and step that stand together.
    /// </summary>
    private List<CardView> Views(IReadOnlyList<ChatLine> lines)
    {
        var views = new List<CardView>(lines.Count);
        var i = 0;
        while (i < lines.Count)
        {
            var end = i + 1;
            if (Answer(lines[i]))
            {
                while (end < lines.Count && Answer(lines[end]) && lines[end].Round == lines[i].Round && lines[end].Step == lines[i].Step)
                {
                    end++;
                }
            }

            var group = lines.Skip(i).Take(end - i).ToList();
            if (group.Count > 1 && _compared.Contains(lines[i].Round))
            {
                views.Add(new CardView(ViewKind.Columns, group));
                views.AddRange(Enumerable.Repeat(new CardView(ViewKind.Hidden, []), group.Count - 1));
            }
            else
            {
                views.AddRange(group.Select(l => new CardView(group.Count > 1 ? ViewKind.InStep : ViewKind.Alone, [l])));
            }

            i = end;
        }

        return views;
    }

    /// <summary>An agent's answer that belongs to a round, which may have been given beside others.</summary>
    private static bool Answer(ChatLine line) => line.Role == Role.Agent && line.Round > 0 && line.Seat.Length > 0;

    private Thickness CardPadding() => _compact ? new Thickness(4, 0) : new Thickness(4, 2);

    private void OnCardPressed(Card card, PointerPressedEventArgs e)
    {
        // While ticking, a click anywhere on a message ticks or unticks it; the text is not selected.
        if (!Selecting || !e.GetCurrentPoint(card.Box).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (!_selected.Remove(card.Line))
        {
            _selected.Add(card.Line);
        }

        ShowSelection();
        e.Handled = true;
    }

    private void ShowSelection()
    {
        foreach (var card in _cards)
        {
            var ticked = _selected.Contains(card.Line);
            card.Tick.IsVisible = Selecting;
            card.Tick.IsChecked = ticked;
            card.Box.Classes.Set("selected", ticked);
            card.Box.Classes.Set("selecting", Selecting);
            card.Body.IsHitTestVisible = !Selecting;
        }

        _selectionChanged();
    }

    private void ApplyFilter()
    {
        FirstMatch = null;
        foreach (var card in _cards)
        {
            var matches = _filter.Length == 0 || card.View.Lines.DefaultIfEmpty(card.Line).Any(l => l.Text.Contains(_filter, StringComparison.OrdinalIgnoreCase));
            card.Box.Opacity = matches ? 1 : 0.3;
            if (matches && _filter.Length > 0 && card.Box.IsVisible)
            {
                FirstMatch ??= card.Box;
            }
        }
    }

    private void Fill(Card card)
    {
        var line = card.Line;
        var (mark, markBrush) = line.Role switch
        {
            Role.Person => ("❯", Palette.Accent),
            Role.Agent => ("┊", line.Color.Length > 0 ? Palette.Seat(line.Color) : Palette.Plasma),
            Role.Tool => ("»", Palette.Dim),
            _ => ("·", Palette.Dim),
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,28,*") };

        // The tick is drawn only; the card takes the click (OnCardPressed).
        card.Tick = new CheckBox { IsVisible = Selecting, IsChecked = _selected.Contains(line), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 6, 0) };
        var markBlock = new TextBlock { Text = mark, Foreground = markBrush, FontSize = 18, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
        card.Body = card.View.Kind == ViewKind.Columns ? Columns(card.View.Lines) : Body(line);
        card.Body.IsHitTestVisible = !Selecting;
        Grid.SetColumn(markBlock, 1);
        Grid.SetColumn(card.Body, 2);
        grid.Children.Add(card.Tick);
        grid.Children.Add(markBlock);
        grid.Children.Add(card.Body);

        var menu = Menu(card);
        var tools = Tools(card, menu);
        Grid.SetColumn(tools, 2);
        grid.Children.Add(tools);

        card.Box.ContextMenu = menu;
        card.Box.Classes.Set("selected", _selected.Contains(line));
        card.Box.Classes.Set("selecting", Selecting);
        card.Box.Child = grid;
    }

    /// <summary>One line's body, and under it the files that came with it (an answer draws its own among its parts).</summary>
    private Control Body(ChatLine line)
    {
        var body = Said(line);
        if (line.Files.Count == 0 || (line.Role == Role.Agent && line.Seat.Length > 0))
        {
            return body;
        }

        var column = new StackPanel { Spacing = 2 };
        column.Children.Add(body);
        column.Children.Add(FilesOf(line.Files));
        return column;
    }

    /// <summary>One line's words: an answer under its agent's name, with what it asked and what it used; anyone else's words as said.</summary>
    private Control Said(ChatLine line) => line.Role switch
    {
        Role.Tool => Folded(line),
        Role.Agent when line.Seat.Length > 0 => Answered(line),
        Role.Agent => Markdown.Render(line.Text.Length == 0 ? "…" : line.Text, Palette.Look, code => _ = _copy(code)),
        Role.Person => new SelectableTextBlock
        {
            Text = SafeText.Block(line.Text),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 15,
            LineSpacing = 5,
            Foreground = Palette.Ink,
            SelectionBrush = Palette.Selection,
            ContextFlyout = null,
        },
        _ => new SelectableTextBlock
        {
            Text = SafeText.Block(line.Text),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13.5,
            LineSpacing = 4,
            FontFamily = line.Text.Contains('\n', StringComparison.Ordinal) ? Palette.Mono : FontFamily.Default,
            Foreground = Palette.Dim,
            SelectionBrush = Palette.Selection,
            ContextFlyout = null,
        },
    };

    /// <summary>
    /// An agent's answer: its name in its colour and its model over it, how it ended when it did not
    /// end whole, its words, the questions it put to others, and its tokens and cost.
    /// </summary>
    private StackPanel Answered(ChatLine line)
    {
        var column = new StackPanel { Spacing = 4 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 96, 0) };
        var brush = Palette.Seat(line.Color);
        head.Children.Add(new TextBlock { Text = "●", Foreground = brush, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new TextBlock { Text = SafeText.Line(line.Seat), Foreground = brush, FontWeight = FontWeight.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        if (line.Model.Length > 0)
        {
            head.Children.Add(new TextBlock { Text = SafeText.Line(line.Model), Foreground = Palette.Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        }

        if (ChatViewModel.EndOf(line) is { Length: > 0 } ended)
        {
            var calm = line.Status is MessageStatus.Streaming or MessageStatus.Passed;
            head.Children.Add(new TextBlock { Text = "· " + ended, Foreground = calm ? Palette.Dim : Palette.Warning, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        }

        column.Children.Add(head);
        var words = line.Text.Length == 0 ? "…" : line.Status == MessageStatus.Streaming || _answers?.Marked is not { } mark ? line.Text : mark(line.Text);
        column.Children.Add(Markdown.Render(words, Palette.Look, code => _ = _copy(code)));
        foreach (var act in line.Acts)
        {
            column.Children.Add(new SelectableTextBlock { Text = "→ " + SafeText.Line(act), Foreground = brush, FontSize = 13, TextWrapping = TextWrapping.Wrap, SelectionBrush = Palette.Selection, ContextFlyout = null });
        }

        if (line.Files.Count > 0)
        {
            column.Children.Add(FilesOf(line.Files));
        }

        if (line.Seq is { } seq && _answers?.Sources?.Invoke(seq) is { Count: > 0 } sources)
        {
            column.Children.Add(SourcesOf(sources));
        }

        if (_answers?.Usage(line) is { Length: > 0 } usage)
        {
            column.Children.Add(new TextBlock { Text = usage, Foreground = Palette.Dim, FontSize = 11.5 });
        }

        return column;
    }

    /// <summary>
    /// The sources an answer served: each by its number and title, how it came (fetched by DipTick,
    /// cited or read by the provider, a search result) on the pointer with its address; one click opens
    /// its page in the browser.
    /// </summary>
    private StackPanel SourcesOf(IReadOnlyList<StoredSource> sources)
    {
        var list = new StackPanel { Spacing = 1, Margin = new Thickness(0, 4, 0, 0) };
        list.Children.Add(new TextBlock { Text = "Sources", Foreground = Palette.Dim, FontSize = 11.5, FontWeight = FontWeight.SemiBold });
        foreach (var source in sources)
        {
            var title = SafeText.Line(source.Title.Length > 0 ? source.Title : source.Address);
            var host = Uri.TryCreate(source.Address, UriKind.Absolute, out var uri) ? uri.Host : source.Address;
            var face = new TextBlock
            {
                Text = $"[{source.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)}] {title} · {SafeText.Line(host)}",
                FontSize = 12.5,
                Foreground = Palette.Accent,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            ToolTip.SetTip(face, $"{SafeText.Line(source.Address)}\n{SourceKind.Words(source.Kind)}");
            var address = source.Address;
            face.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(face).Properties.IsLeftButtonPressed)
                {
                    _answers?.Open?.Invoke(address);
                    e.Handled = true;
                }
            };
            list.Children.Add(face);
        }

        return list;
    }

    /// <summary>
    /// The files that came with a line: an image drawn, at most 360 wide, then for every file its name,
    /// type and size, Save…, and for one saved on disk Open and Show in folder.
    /// </summary>
    private StackPanel FilesOf(IReadOnlyList<ChatFile> files)
    {
        var list = new StackPanel { Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var file in files)
        {
            var card = new StackPanel { Spacing = 4 };
            if (Attachments.IsImage(file.Type) && _answers?.Image is { } load)
            {
                var image = new Image { MaxWidth = 360, MaxHeight = 360, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left };
                card.Children.Add(image);
                _ = DrawImageAsync(image, file, load);
            }

            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            line.Children.Add(new TextBlock { Text = SafeText.Line($"{file.Name} · {file.Type} · {AgentFolders.Size(file.Size)}"), Foreground = Palette.Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            if (_answers?.SaveFile is { } save)
            {
                line.Children.Add(FileButton("Save…", "A copy of it, where you say", () => save(file)));
            }

            if (file.Path is not null && _answers?.OpenFile is { } open)
            {
                line.Children.Add(FileButton("Open", "In Windows' own app for it", () => open(file)));
            }

            if (file.Path is not null && _answers?.ShowFile is { } show)
            {
                line.Children.Add(FileButton("Show in folder", "Where it is saved, in Explorer", () => show(file)));
            }

            card.Children.Add(line);
            list.Children.Add(card);
        }

        return list;
    }

    private static Button FileButton(string text, string tip, Action click)
    {
        var button = new Button { Content = text, Classes = { "tool" }, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>
    /// An image drawn once its bytes are read, off the window's thread. A large one is decoded at 720
    /// pixels wide, twice what it is shown at, so a chat of large images does not fill the memory.
    /// </summary>
    private static async Task DrawImageAsync(Image image, ChatFile file, Func<ChatFile, byte[]?> load)
    {
        var bytes = await Task.Run(() => load(file)).ConfigureAwait(true);
        if (bytes is null)
        {
            return;
        }

        try
        {
            using var whole = new MemoryStream(bytes);
            var bitmap = new Bitmap(whole);
            if (bitmap.PixelSize.Width > 720)
            {
                bitmap.Dispose();
                using var again = new MemoryStream(bytes);
                bitmap = Bitmap.DecodeToWidth(again, 720, BitmapInterpolationMode.HighQuality);
            }

            image.Source = bitmap;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or NotSupportedException)
        {
            // An image the window cannot draw keeps its name, size and Save… under the space it would take.
            image.IsVisible = false;
        }
    }

    /// <summary>
    /// A tool call, folded: one line, the agent and what the tool did; a click unfolds what it was
    /// asked and what it gave back.
    /// </summary>
    private static StackPanel Folded(ChatLine line)
    {
        var parts = SafeText.Block(line.Text).Split('\n', 2);
        var ended = line.Status == Models.Chat.Store.MessageStatus.Done ? string.Empty : $" · {line.Status}";
        var head = $"{(line.Seat.Length > 0 ? line.Seat + " · " : string.Empty)}{parts[0]}{ended}";
        var details = new SelectableTextBlock
        {
            Text = parts.Length > 1 ? parts[1] : string.Empty,
            FontFamily = Palette.Mono,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Palette.Dim,
            SelectionBrush = Palette.Selection,
            ContextFlyout = null,
            IsVisible = false,
            Margin = new Thickness(18, 2, 0, 0),
        };
        var face = new TextBlock { Text = "▸ " + head, Foreground = line.Color.Length > 0 ? Palette.Seat(line.Color) : Palette.Dim, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        var toggle = new Button { Classes = { "row" }, Content = face, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(2, 1) };
        ToolTip.SetTip(toggle, "What the tool was asked and what it gave back");
        toggle.Click += (_, _) =>
        {
            details.IsVisible = !details.IsVisible;
            face.Text = (details.IsVisible ? "▾ " : "▸ ") + head;
        };
        var column = new StackPanel { Spacing = 0 };
        column.Children.Add(toggle);
        column.Children.Add(details);
        return column;
    }

    /// <summary>A step's answers side by side, each in a column of its own, as wide as the others.</summary>
    private Grid Columns(IReadOnlyList<ChatLine> lines)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", lines.Count))), ColumnSpacing = 16 };
        for (var i = 0; i < lines.Count; i++)
        {
            var cell = new Border { Child = Answered(lines[i]), BorderBrush = Palette.Hairline, BorderThickness = new Thickness(i == 0 ? 0 : 1, 0, 0, 0), Padding = new Thickness(i == 0 ? 0 : 12, 0, 0, 0) };
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }

        return grid;
    }

    /// <summary>Copy, Compare and ⋯, top right, while the pointer is over the message (App.axaml).</summary>
    private StackPanel Tools(Card card, ContextMenu menu)
    {
        var tools = new StackPanel { Classes = { "tools" }, Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        if (card.View.Kind is ViewKind.InStep or ViewKind.Columns)
        {
            var side = card.View.Kind == ViewKind.Columns;
            var compare = new Button { Content = side ? "One under another" : "Compare", Classes = { "tool" } };
            ToolTip.SetTip(compare, side ? "These answers one under another again" : "The answers given at the same time, side by side");
            compare.Click += (_, _) => ToggleCompare(card.Line.Round);
            tools.Children.Add(compare);
        }

        var copy = new Button { Content = "Copy", Classes = { "tool" } };
        ToolTip.SetTip(copy, "Copy this message");
        copy.Click += (_, _) => _ = _copy(Plain(card.Line));
        var more = new Button { Content = "⋯", Classes = { "tool" } };
        ToolTip.SetTip(more, "More: ask again, ask another agent, copy as Markdown, quote, select messages");
        more.Click += (_, _) => menu.Open(more);
        tools.Children.Add(copy);
        tools.Children.Add(more);
        return tools;
    }

    /// <summary>The message's menu: what it offers depends on who said it and what is selected in it now.</summary>
    private ContextMenu Menu(Card card)
    {
        var line = card.Line;
        var selection = Menus.Item("Copy selection", () => _ = _copy(SelectionIn(card)), "Ctrl+C");
        IReadOnlyList<string> codes = line.Role == Role.Agent ? MarkdownText.CodeBlocks(line.Text) : [];
        MenuItem? code = null;
        MenuItem? saveCode = null;
        var canSave = _answers?.SaveWords is not null && line.Seq is not null;
        if (codes.Count == 1)
        {
            code = Menus.Item("Copy code", () => _ = _copy(codes[0]));
            saveCode = canSave ? Menus.Item("Save code…", () => _answers!.SaveWords!(line, 1)) : null;
        }
        else if (codes.Count > 1)
        {
            code = new MenuItem { Header = "Copy code" };
            saveCode = canSave ? new MenuItem { Header = "Save code" } : null;
            for (var i = 0; i < codes.Count; i++)
            {
                var block = codes[i];
                var number = i + 1;
                var first = block.Split('\n', 2)[0].Trim();
                var label = $"Block {number}{(first.Length == 0 ? string.Empty : $": {(first.Length > 40 ? first[..39] + "…" : first)}")}";
                code.Items.Add(Menus.Item(label, () => _ = _copy(block)));
                saveCode?.Items.Add(Menus.Item(label + "…", () => _answers!.SaveWords!(line, number)));
            }
        }

        // An answer's words, as written, to a file (chat save).
        var saveWords = canSave && line.Role == Role.Agent ? Menus.Item("Save…", () => _answers!.SaveWords!(line, null)) : null;

        // An answer kept in the chat can be asked again, of its agent or another at the table.
        MenuItem? retry = null;
        MenuItem? another = null;
        if (_answers is { } answers && line.Role == Role.Agent && line.Seat.Length > 0 && line.Seq is not null)
        {
            retry = Menus.Item($"Ask {line.Seat} again", () => answers.Retry(line, null), "F5");
            var others = answers.Seats().Where(s => s != line.Seat).ToList();
            if (others.Count > 0)
            {
                another = new MenuItem { Header = "Ask another agent" };
                foreach (var seat in others)
                {
                    another.Items.Add(Menus.Item(seat, () => answers.Retry(line, seat)));
                }
            }
        }

        var menu = Menus.Of(
            retry,
            another,
            retry is null ? null : Menus.Line(),
            selection,
            Menus.Item("Copy message", () => _ = _copy(Plain(card.Line))),
            line.Role == Role.Agent ? Menus.Item("Copy as Markdown", () => _ = _copy(SafeText.Block(card.Line.Text))) : null,
            code,
            saveWords,
            saveCode,
            Menus.Item("Quote in reply", () => _quote(Plain(card.Line))),
            Menus.Line(),
            Menus.Item("Select messages", () => StartSelecting(card.Line), "Ctrl+A"));
        menu.Opening += (_, _) => selection.IsEnabled = SelectionIn(card).Length > 0;
        return menu;
    }

    /// <summary>What is selected inside this message now, from whichever of its texts holds the selection.</summary>
    private static string SelectionIn(Card card) =>
        card.Box.GetLogicalDescendants().OfType<SelectableTextBlock>().Select(b => b.SelectedText).FirstOrDefault(t => t.Length > 0) ?? string.Empty;

    /// <summary>How a card is shown: its line alone, one of a step's answers, a step's answers in columns, or not at all (laid beside another).</summary>
    private enum ViewKind
    {
        Alone,
        InStep,
        Columns,
        Hidden,
    }

    /// <summary>What a card was drawn from: how it is shown, and the lines it shows.</summary>
    private sealed record CardView(ViewKind Kind, IReadOnlyList<ChatLine> Lines)
    {
        public static CardView None { get; } = new(ViewKind.Hidden, []);

        /// <summary>Whether a card drawn from this shows the same as one drawn from <paramref name="other"/>: the same kind, and the very same lines.</summary>
        public bool SameAs(CardView other) =>
            Kind == other.Kind && Lines.Count == other.Lines.Count && Lines.Zip(other.Lines).All(p => ReferenceEquals(p.First, p.Second));
    }

    /// <summary>A message's card and what is drawn in it.</summary>
    private sealed class Card(Border box, ChatLine line)
    {
        public Border Box { get; } = box;

        public ChatLine Line { get; set; } = line;

        /// <summary>What it was last drawn from; nothing yet, so the first render draws it.</summary>
        public CardView View { get; set; } = CardView.None;

        public CheckBox Tick { get; set; } = new();

        public Control Body { get; set; } = new Panel();
    }
}
