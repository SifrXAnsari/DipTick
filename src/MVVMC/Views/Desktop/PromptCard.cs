using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DipTick.Models;
using DipTick.Models.Chat;
using DipTick.ViewModels;

namespace DipTick.Views.Desktop;

/// <summary>
/// The prompt zone of the window: a card above the composer where every question appears, one at a
/// time: setup's steps, the model picker, a key, an approval. Every card can be left: its ✕, Esc, or
/// <c>exit</c> typed alone in its field leaves the whole question (an approval left is a Deny); Back
/// goes one step back. Enter takes the marked choice; 1-9 pick one directly; a click on a choice
/// takes it.
/// </summary>
internal sealed class PromptCard : Border
{
    private const int MostRows = 10;

    private readonly Queue<(Prompt Prompt, Action<PromptResult> Done)> _waiting = new();
    private Prompt? _prompt;
    private Action<PromptResult>? _done;
    private int _mark;
    private string? _error;
    private string? _confirmName;
    private List<ModelInfo> _shown = [];
    private List<PickItem> _shownItems = [];
    private TextBox? _field;
    private StackPanel? _list;
    private TextBlock? _errorBlock;
    private TextBlock? _countBlock;
    private TextBlock? _markBlock;
    private bool _untouched;

    public PromptCard()
    {
        Classes.Add("card");
        IsVisible = false;
        Focusable = true;
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    /// <summary>Raised when a question opens or closes, so the screen lays itself out again.</summary>
    public event Action? Changed;

    public bool Open => _prompt is not null;

    /// <summary>The question open now; null when none.</summary>
    internal Prompt? Current => _prompt;

    /// <summary>Styled as the Border it is: a selector matches the style key, not the class.</summary>
    protected override Type StyleKeyOverride => typeof(Border);

    /// <summary>
    /// Takes back a question nobody answered in time (an approval): closed as a Stop if it is open,
    /// dropped if it is still waiting behind another. Any other question stays as it is.
    /// </summary>
    public void Withdraw(Prompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (ReferenceEquals(_prompt, prompt))
        {
            Finish(new PromptResult(Reply.Stop, 0, string.Empty));
            return;
        }

        var kept = _waiting.Where(w => !ReferenceEquals(w.Prompt, prompt)).ToList();
        if (kept.Count != _waiting.Count)
        {
            _waiting.Clear();
            foreach (var waiting in kept)
            {
                _waiting.Enqueue(waiting);
            }
        }
    }

    /// <summary>
    /// Leaves every question, open and waiting, as a Stop: quitting, so no flow is left waiting for
    /// an answer that cannot come.
    /// </summary>
    public void LeaveAll()
    {
        var waiting = _waiting.ToList();
        _waiting.Clear();
        foreach (var (_, then) in waiting)
        {
            then(new PromptResult(Reply.Stop, 0, string.Empty));
        }

        if (_prompt is not null)
        {
            Finish(new PromptResult(Reply.Stop, 0, string.Empty));
        }
    }

    /// <summary>Opens a question; <paramref name="done"/> is called on the UI thread with the answer.</summary>
    public void Ask(Prompt prompt, Action<PromptResult> done)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (_prompt is { } open)
        {
            // One at a time: another question (an approval during setup) waits its turn, unless it
            // is for now, when the open one steps back to the head of the line and is asked again after.
            if (!prompt.Now || _done is not { } openDone)
            {
                _waiting.Enqueue((prompt, done));
                return;
            }

            var behind = _waiting.ToList();
            _waiting.Clear();
            _waiting.Enqueue((open, openDone));
            foreach (var waiting in behind)
            {
                _waiting.Enqueue(waiting);
            }

            _prompt = null;
            _done = null;
        }

        _prompt = prompt;
        _done = done;
        _error = null;
        _confirmName = null;
        _mark = Math.Max(0, prompt.Selected);
        if (prompt.Kind == PromptKind.Model && prompt.Models is { } models)
        {
            _shown = [.. models];
            _mark = Math.Max(0, _shown.FindIndex(m => m.Id == prompt.Text));
        }

        if (prompt.Kind == PromptKind.Pick && prompt.Items is { } items)
        {
            _shownItems = [.. items];
            _mark = Math.Max(0, _shownItems.FindIndex(i => i.Id == prompt.Text));
        }

        Classes.Set("alert", prompt.Alert);
        Classes.Set("lit", !prompt.Alert);
        IsVisible = true;
        Build();
        Changed?.Invoke();
        TakeFocus();
    }

    /// <summary>
    /// The keyboard to the open question: its field, else the card, whose keys answer it. As it opens,
    /// and when the tab it waits in comes in front. A card not on screen takes nothing.
    /// </summary>
    public void TakeFocus()
    {
        if (!Open)
        {
            return;
        }

        if (_field is { } field)
        {
            field.Focus();

            // A value given before (a default, the setting as it is) is selected until the person types,
            // so typing replaces it, as in a Windows dialog; → or a click keeps it.
            if (_untouched && _prompt is { Secret: false } && field.Text is { Length: > 0 })
            {
                field.SelectAll();
            }
            else
            {
                field.CaretIndex = field.Text?.Length ?? 0;
            }
        }
        else
        {
            Focus();
        }
    }

    private void Finish(PromptResult result)
    {
        var done = _done;
        _prompt = null;
        _done = null;
        _field = null;
        _list = null;
        IsVisible = false;
        Child = null;
        Changed?.Invoke();
        done?.Invoke(result);
        if (_prompt is null && _waiting.Count > 0)
        {
            var (next, then) = _waiting.Dequeue();
            Ask(next, then);
        }
    }

    private void Build()
    {
        if (_prompt is not { } prompt)
        {
            return;
        }

        // Nothing of the question before this one, which may have stepped back unanswered.
        _field = null;
        _list = null;
        _countBlock = null;
        _markBlock = null;
        _untouched = false;

        // The question at the top, the buttons at the bottom, and between them the list, which scrolls:
        // the window caps the card's height (MainWindow), so a long list never pushes the composer out.
        var column = new DockPanel();
        void Top(Control part)
        {
            DockPanel.SetDock(part, Dock.Top);
            part.Margin = new Thickness(0, 0, 0, 8);
            column.Children.Add(part);
        }

        // Bottom parts go from the bottom up, each above the one before.
        void Bottom(Control part)
        {
            DockPanel.SetDock(part, Dock.Bottom);
            part.Margin = new Thickness(0, 6, 0, 0);
            column.Children.Add(part);
        }

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(new TextBlock
        {
            Text = prompt.Title,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            LetterSpacing = 1.2,
            Foreground = prompt.Alert ? Palette.Warning : Palette.Accent,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var close = new Button { Content = "✕", Classes = { "tool" } };
        ToolTip.SetTip(close, prompt.Alert ? "Deny (Esc)" : "Leave (Esc): nothing more is asked, and what was not saved is not saved");
        close.Click += (_, _) => Finish(new PromptResult(Reply.Stop, 0, string.Empty));
        Grid.SetColumn(close, 1);
        head.Children.Add(close);
        Top(head);
        if (prompt.Note.Length > 0)
        {
            Top(new TextBlock { Text = prompt.Note, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Dim, FontSize = 14 });
        }

        Top(new SelectableTextBlock { Text = prompt.Question, TextWrapping = TextWrapping.Wrap, FontSize = 16, Foreground = Palette.Ink, SelectionBrush = Palette.Selection });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var hint = new TextBlock { Foreground = Palette.Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Text = Hint(prompt) };
        if (prompt.Kind != PromptKind.Tell)
        {
            buttons.Children.Add(Button(prompt.Alert ? "Deny" : "Back", () => Finish(new PromptResult(Reply.Back, 0, string.Empty)), accent: false));
        }

        buttons.Children.Add(Button(prompt.Kind switch
        {
            PromptKind.Tell => "OK",
            PromptKind.Choose => prompt.Alert ? "Choose" : "Next",
            _ => "Next",
        }, Accept, accent: !prompt.Alert));
        buttons.Children.Add(hint);
        Bottom(buttons);
        _errorBlock = new TextBlock { Foreground = Palette.Error, TextWrapping = TextWrapping.Wrap, FontSize = 14, IsVisible = false };
        Bottom(_errorBlock);

        switch (prompt.Kind)
        {
            case PromptKind.Choose:
                _list = new StackPanel { Spacing = 2 };
                DrawChoices();
                break;
            case PromptKind.Text:
                _field = new TextBox { Text = prompt.Text, PasswordChar = prompt.Secret ? '•' : '\0', PlaceholderText = prompt.Secret ? "not shown as you type" : string.Empty };
                _untouched = true;
                _field.TextChanged += (_, _) => _untouched = false;
                _field.AddHandler(PointerPressedEvent, (_, _) => _untouched = false, RoutingStrategies.Tunnel);
                _field.AddHandler(KeyDownEvent, (_, e) => _untouched &= e.Key is not (Key.Left or Key.Right or Key.Home or Key.End), RoutingStrategies.Tunnel);
                Top(_field);
                break;
            case PromptKind.Model:
                _field = new TextBox { PlaceholderText = "type to narrow the list" };
                _field.TextChanged += (_, _) =>
                {
                    _mark = 0;
                    _confirmName = null;
                    _error = null;
                    DrawModels();
                };
                Top(_field);
                _countBlock = new TextBlock { Foreground = Palette.Dim, FontSize = 12 };
                Bottom(_countBlock);
                _list = new StackPanel { Spacing = 2 };
                DrawModels();
                break;
            case PromptKind.Pick:
                _field = new TextBox { PlaceholderText = "type to narrow the list" };
                _field.TextChanged += (_, _) =>
                {
                    _mark = 0;
                    _error = null;
                    DrawItems();
                };
                Top(_field);
                _countBlock = new TextBlock { Foreground = Palette.Dim, FontSize = 12 };
                Bottom(_countBlock);

                // The marked one's whole line, which its row may cut short: what it is and what it needs.
                _markBlock = new TextBlock { Foreground = Palette.Ink, FontSize = 13, TextWrapping = TextWrapping.Wrap };
                Bottom(_markBlock);
                _list = new StackPanel { Spacing = 2 };
                DrawItems();
                break;
        }

        // Last, so it fills what is left: the list, or nothing for a question without one.
        column.Children.Add(new ScrollViewer
        {
            Content = prompt.Kind is PromptKind.Choose or PromptKind.Model or PromptKind.Pick ? _list : null,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        Child = column;
        ShowError();
    }

    /// <summary>Brings the marked row into the list's view once it is laid out: the card may show fewer rows than the list draws.</summary>
    private static void Reveal(Control row) => Dispatcher.UIThread.Post(row.BringIntoView, DispatcherPriority.Loaded);

    private static Button Button(string text, Action click, bool accent)
    {
        var button = new Button { Content = text };
        if (accent)
        {
            button.Classes.Add("primary");
        }

        button.Click += (_, _) => click();
        return button;
    }

    private void DrawChoices()
    {
        if (_prompt is not { Choices: { } choices } || _list is null)
        {
            return;
        }

        _list.Children.Clear();
        for (var i = 0; i < choices.Count; i++)
        {
            var index = i;
            var on = i == _mark;
            var row = new Button { Classes = { "row" } };
            if (on)
            {
                row.Classes.Add("current");
            }

            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            line.Children.Add(new TextBlock { Text = on ? "›" : (i + 1).ToString(CultureInfo.InvariantCulture), Foreground = on ? Palette.Accent : Palette.Dim, Width = 14 });
            line.Children.Add(new TextBlock { Text = choices[i].Label, Foreground = on ? Palette.Accent : Palette.Ink, FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal });
            if (choices[i].Detail.Length > 0)
            {
                line.Children.Add(new TextBlock { Text = choices[i].Detail, Foreground = Palette.Dim });
            }

            row.Content = line;
            row.Click += (_, _) => Finish(new PromptResult(Reply.Answered, index, string.Empty));
            _list.Children.Add(row);
        }
    }

    private void DrawModels()
    {
        if (_prompt is not { Models: { } models } || _list is null)
        {
            return;
        }

        var filter = (_field?.Text ?? string.Empty).Trim();
        _shown = filter.Length == 0 ? [.. models] : [.. models.Where(m => m.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) || m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))];
        _mark = _shown.Count == 0 ? 0 : Math.Clamp(_mark, 0, _shown.Count - 1);
        _list.Children.Clear();
        var top = Math.Clamp(_mark - (MostRows - 1), 0, Math.Max(0, _shown.Count - MostRows));
        for (var i = top; i < Math.Min(_shown.Count, top + MostRows); i++)
        {
            var model = _shown[i];
            var on = i == _mark;
            var row = new Button { Classes = { "row" } };
            if (on)
            {
                row.Classes.Add("current");
            }

            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("18,2*,Auto,3*") };
            var mark = new TextBlock { Text = on ? "›" : string.Empty, Foreground = Palette.Accent };
            var id = new TextBlock { Text = model.Id, Foreground = on ? Palette.Accent : Palette.Ink, FontFamily = Palette.Mono, FontSize = 13 };
            var when = new TextBlock { Text = model.Created is { } created ? created.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty, Foreground = Palette.Dim, FontSize = 12, Margin = new Thickness(12, 0) };
            var name = new TextBlock { Text = model.Name, Foreground = Palette.Dim, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(id, 1);
            Grid.SetColumn(when, 2);
            Grid.SetColumn(name, 3);
            line.Children.Add(mark);
            line.Children.Add(id);
            line.Children.Add(when);
            line.Children.Add(name);
            row.Content = line;
            row.Click += (_, _) => Finish(new PromptResult(Reply.Answered, 0, model.Id));
            _list.Children.Add(row);
            if (on)
            {
                Reveal(row);
            }
        }

        if (_countBlock is not null)
        {
            _countBlock.Text = _shown.Count == 0 && filter.Length > 0
                ? $"Nothing matches '{SafeText.Line(filter)}'. Enter uses it as the name anyway, after a second Enter."
                : $"{_shown.Count.ToString(CultureInfo.InvariantCulture)} of {models.Count.ToString(CultureInfo.InvariantCulture)}";
        }

        ShowError();
    }

    /// <summary>
    /// A long list narrowed by the field: name, group, and what it needs or "not yet", dimmed when
    /// it cannot be picked. A click takes an item as Enter does.
    /// </summary>
    private void DrawItems()
    {
        if (_prompt is not { Items: { } items } || _list is null)
        {
            return;
        }

        var words = (_field?.Text ?? string.Empty).Trim();
        _shownItems = PickItem.Narrow(items, words);
        _mark = _shownItems.Count == 0 ? 0 : Math.Clamp(_mark, 0, _shownItems.Count - 1);
        _list.Children.Clear();
        var top = Math.Clamp(_mark - (MostRows - 1), 0, Math.Max(0, _shownItems.Count - MostRows));
        for (var i = top; i < Math.Min(_shownItems.Count, top + MostRows); i++)
        {
            var item = _shownItems[i];
            var index = i;
            var on = i == _mark;
            var later = item.Unavailable.Length > 0;
            var row = new Button { Classes = { "row" } };
            if (on)
            {
                row.Classes.Add("current");
            }

            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("18,2*,Auto,3*") };
            var mark = new TextBlock { Text = on ? "›" : string.Empty, Foreground = Palette.Accent };
            var name = new TextBlock { Text = item.Label, Foreground = on ? Palette.Accent : later ? Palette.Dim : Palette.Ink, FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
            var group = new TextBlock { Text = item.Group, Foreground = Palette.Dim, FontSize = 12, Margin = new Thickness(12, 0), VerticalAlignment = VerticalAlignment.Center };
            var detail = new TextBlock { Text = later ? "not yet" : item.Detail, Foreground = Palette.Dim, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(name, 1);
            Grid.SetColumn(group, 2);
            Grid.SetColumn(detail, 3);
            line.Children.Add(mark);
            line.Children.Add(name);
            line.Children.Add(group);
            line.Children.Add(detail);
            row.Content = line;
            ToolTip.SetTip(row, later ? $"Not yet: {item.Unavailable}" : item.Detail);
            row.Click += (_, _) =>
            {
                _mark = index;
                Accept();
            };
            _list.Children.Add(row);
            if (on)
            {
                Reveal(row);
            }
        }

        if (_countBlock is not null)
        {
            _countBlock.Text = _shownItems.Count == 0 && words.Length > 0
                ? $"Nothing matches '{SafeText.Line(words)}'."
                : $"{_shownItems.Count.ToString(CultureInfo.InvariantCulture)} of {items.Count.ToString(CultureInfo.InvariantCulture)}";
        }

        if (_markBlock is not null)
        {
            var marked = _shownItems.Count > 0 ? _shownItems[_mark] : null;
            _markBlock.Text = marked is null ? string.Empty
                : marked.Unavailable.Length > 0 ? $"{marked.Label}: not yet. {marked.Unavailable}"
                : marked.Detail.Length > 0 ? $"{marked.Label}: {marked.Detail}"
                : marked.Label;
            _markBlock.IsVisible = marked is not null;
        }

        ShowError();
    }

    private void ShowError()
    {
        if (_errorBlock is not null)
        {
            _errorBlock.Text = _error ?? string.Empty;
            _errorBlock.IsVisible = _error is not null;
        }
    }

    private void Accept()
    {
        if (_prompt is not { } prompt)
        {
            return;
        }

        switch (prompt.Kind)
        {
            case PromptKind.Choose:
                Finish(new PromptResult(Reply.Answered, _mark, string.Empty));
                break;
            case PromptKind.Tell:
                Finish(new PromptResult(Reply.Answered, 0, string.Empty));
                break;
            case PromptKind.Text:
                var typed = _field?.Text ?? string.Empty;
                if (ChatViewModel.IsExitWord(typed))
                {
                    Finish(new PromptResult(Reply.Stop, 0, string.Empty));
                    break;
                }

                var value = prompt.Secret ? typed : typed.Trim();
                if (!prompt.Secret && value.Length == 0 && prompt.Text.Length > 0)
                {
                    value = prompt.Text;
                }

                if (prompt.Check?.Invoke(value) is { } why)
                {
                    _error = $"That will not do: {why}.";
                    if (prompt.Secret && _field is not null)
                    {
                        _field.Text = string.Empty;
                    }

                    ShowError();
                    return;
                }

                Finish(new PromptResult(Reply.Answered, 0, value));
                break;
            case PromptKind.Model:
                var text = (_field?.Text ?? string.Empty).Trim();
                if (ChatViewModel.IsExitWord(text))
                {
                    Finish(new PromptResult(Reply.Stop, 0, string.Empty));
                }
                else if (_shown.Count > 0)
                {
                    Finish(new PromptResult(Reply.Answered, 0, _shown[_mark].Id));
                }
                else if (!ModelLister.UsableId(text))
                {
                    _error = "That will not do: a model's name has no spaces and only plain characters.";
                    ShowError();
                }
                else if (_confirmName == text)
                {
                    Finish(new PromptResult(Reply.Answered, 0, text));
                }
                else
                {
                    _confirmName = text;
                    _error = $"'{text}' is not in the platform's list. Enter again to use it as the model's name anyway.";
                    ShowError();
                }

                break;
            case PromptKind.Pick:
                if (ChatViewModel.IsExitWord((_field?.Text ?? string.Empty).Trim()))
                {
                    Finish(new PromptResult(Reply.Stop, 0, string.Empty));
                }
                else if (_shownItems.Count == 0)
                {
                    _error = "Nothing matches; type less, or other words.";
                    ShowError();
                }
                else if (_shownItems[_mark] is { Unavailable.Length: > 0 } later)
                {
                    _error = $"Not yet: {later.Unavailable}";
                    DrawItems();
                }
                else
                {
                    Finish(new PromptResult(Reply.Answered, 0, _shownItems[_mark].Id));
                }

                break;
        }
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (_prompt is not { } prompt)
        {
            return;
        }

        var count = prompt.Kind switch
        {
            PromptKind.Choose => prompt.Choices?.Count ?? 0,
            PromptKind.Model => _shown.Count,
            PromptKind.Pick => _shownItems.Count,
            _ => 0,
        };
        switch (e.Key)
        {
            case Key.Escape:
                // Esc leaves the question, as ✕ does; Back is the button for one step back.
                Finish(new PromptResult(Reply.Stop, 0, string.Empty));
                e.Handled = true;
                break;
            case Key.Enter when e.KeyModifiers == KeyModifiers.None:
                Accept();
                e.Handled = true;
                break;
            case Key.Up or Key.Down when count > 0:
                _mark = Math.Clamp(_mark + (e.Key == Key.Up ? -1 : 1), 0, count - 1);
                if (prompt.Kind == PromptKind.Choose)
                {
                    DrawChoices();
                }
                else if (prompt.Kind == PromptKind.Pick)
                {
                    _error = null;
                    DrawItems();
                }
                else
                {
                    DrawModels();
                }

                e.Handled = true;
                break;
            case >= Key.D1 and <= Key.D9 when prompt.Kind == PromptKind.Choose && e.KeyModifiers == KeyModifiers.None:
                var digit = e.Key - Key.D0;
                if (digit <= count)
                {
                    Finish(new PromptResult(Reply.Answered, digit - 1, string.Empty));
                    e.Handled = true;
                }

                break;
        }
    }

    private static string Hint(Prompt prompt) => prompt.Kind switch
    {
        PromptKind.Choose => "↑/↓ · Enter · 1-9 · " + (prompt.Alert ? "Esc denies" : "Esc leaves"),
        PromptKind.Text => "Enter · Esc or exit leaves",
        PromptKind.Model or PromptKind.Pick => "type to narrow · ↑/↓ · Enter · Esc or exit leaves",
        _ => "Enter",
    };
}
