using System.Globalization;
using DipTick.Models.Chat;
using DipTick.ViewModels;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DipTick.Views;

/// <summary>What a question panel shows and takes.</summary>
internal enum PromptKind
{
    /// <summary>One of a list of choices.</summary>
    Choose,

    /// <summary>A line of text, or a secret.</summary>
    Text,

    /// <summary>A model from a list narrowed by typing, or a name the list lacks.</summary>
    Model,

    /// <summary>One item of a long list narrowed by typing (the providers); only an item the list holds.</summary>
    Pick,

    /// <summary>Something to read, then Enter.</summary>
    Tell,
}

/// <summary>A question for the prompt zone. <paramref name="Now"/>: shown before the one open, which comes back after it.</summary>
internal sealed record Prompt(
    string Title,
    string Note,
    string Question,
    PromptKind Kind,
    IReadOnlyList<Choice>? Choices = null,
    int Selected = 0,
    string Text = "",
    bool Secret = false,
    Func<string, string?>? Check = null,
    IReadOnlyList<ModelInfo>? Models = null,
    bool Alert = false,
    IReadOnlyList<PickItem>? Items = null,
    bool Now = false);

/// <summary>How a question ended: the reply, the chosen index, the text typed or the model picked.</summary>
internal readonly record struct PromptResult(Reply Reply, int Index, string Text);

/// <summary>
/// The prompt zone: a rounded panel above the composer where every question appears, as in Hermes'
/// layout. Never a window floating over the transcript. Its title sits in the border; the note is
/// dim, the question in the text colour, the chosen choice marked › in the accent; a dim hint line
/// says which keys do what. It has the keyboard while open. Esc, or <c>exit</c> typed alone, leaves
/// the whole question; Shift+Tab goes one step back.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Usage",
    "CA2213:Disposable fields should be disposed",
    Justification = "Each field is a subview added in the constructor; Terminal.Gui disposes a view's subviews when the view itself " +
                    "is disposed. Disposing them here as well would dispose them twice.")]
internal sealed class PromptZone : View
{
    private const int MostChoiceRows = 10;

    private readonly RowsView _head = new() { X = 0, Y = 0, Width = Dim.Fill() };
    private readonly Label _caret = new() { X = 0, Text = "›" };
    private readonly TextField _field = new() { X = 2, Width = Dim.Fill(1) };
    private readonly RowsView _body = new() { X = 0, Width = Dim.Fill() };
    private readonly Queue<(Prompt Prompt, Action<PromptResult> Done)> _waiting = new();
    private Prompt? _prompt;
    private Action<PromptResult>? _done;
    private int _mark;
    private string? _error;
    private string? _confirmName;
    private List<ModelInfo> _shown = [];
    private List<PickItem> _shownItems = [];

    public PromptZone()
    {
        CanFocus = true;
        Visible = false;
        BorderStyle = LineStyle.Rounded;
        SetScheme(Theme.Lit);
        _caret.SetScheme(Theme.Uniform(Theme.Accent));
        _field.SetScheme(Theme.Base);
        Add(_head, _caret, _field, _body);
        KeyDown += (_, key) => OnKey(key);
        _field.KeyDown += (_, key) => OnKey(key);
        _field.TextChanged += (_, _) =>
        {
            if (_prompt is { Kind: PromptKind.Model or PromptKind.Pick })
            {
                _mark = 0;
                _confirmName = null;
            }

            _error = null;
            Render();
        };
    }

    /// <summary>Raised when the panel's height changes or it closes, so the screen lays itself out again.</summary>
    public event Action? Changed;

    /// <summary>The panel's height in rows, border included; 0 when closed.</summary>
    public int Rows { get; private set; }

    /// <summary>Opens a question; <paramref name="done"/> is called on the screen's thread with the answer.</summary>
    public void Ask(Prompt prompt, Action<PromptResult> done)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        // One question at a time: another (an approval during setup) waits its turn, unless it is
        // for now, when the open one steps back to the head of the line and is asked again after.
        if (_prompt is { } open)
        {
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
        Title = prompt.Title;
        SetScheme(prompt.Alert ? Theme.Alert : Theme.Lit);

        // Visible first: setting the field's text below lays the screen out, and a panel laid out
        // while still hidden gets no height.
        Visible = true;
        var typed = prompt.Kind is PromptKind.Text or PromptKind.Model or PromptKind.Pick;
        _field.Secret = prompt.Secret;
        _field.Text = prompt.Kind == PromptKind.Text ? prompt.Text : string.Empty;
        _field.Visible = typed;
        _caret.Visible = typed;

        // A filter says so, so its caret is not read as one of the list's marks.
        _caret.Text = prompt.Kind is PromptKind.Model or PromptKind.Pick ? "filter ›" : "›";
        _field.X = prompt.Kind is PromptKind.Model or PromptKind.Pick ? 9 : 2;
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

        Render();
        Changed?.Invoke();
        if (typed)
        {
            _field.SetFocus();
            _field.MoveEnd();
        }
        else
        {
            SetFocus();
        }
    }

    private void Finish(PromptResult result)
    {
        var done = _done;
        _prompt = null;
        _done = null;
        Visible = false;
        Rows = 0;
        Changed?.Invoke();
        done?.Invoke(result);
        if (_prompt is null && _waiting.Count > 0)
        {
            var (next, then) = _waiting.Dequeue();
            Ask(next, then);
        }
    }

    /// <summary>Whether a question is open.</summary>
    public bool Open => _prompt is not null;

    private void OnKey(Key key)
    {
        if (_prompt is not { } prompt)
        {
            return;
        }

        var handled = prompt.Kind switch
        {
            PromptKind.Choose => ChooseKey(prompt, key),
            PromptKind.Text => TextKey(prompt, key),
            PromptKind.Model => ModelKey(key),
            PromptKind.Pick => PickKey(key),
            _ => TellKey(key),
        };
        if (handled)
        {
            key.Handled = true;
        }
    }

    private bool ChooseKey(Prompt prompt, Key key)
    {
        var count = prompt.Choices?.Count ?? 0;
        if (key == Key.CursorUp || key == Key.CursorDown)
        {
            _mark = Math.Clamp(_mark + (key == Key.CursorUp ? -1 : 1), 0, Math.Max(0, count - 1));
            Render();
            return true;
        }

        if (key == Key.Enter)
        {
            Finish(new PromptResult(Reply.Answered, _mark, string.Empty));
            return true;
        }

        if (LeaveOrBack(key))
        {
            return true;
        }

        var digit = key.AsRune.Value - '0';
        if (!key.IsCtrl && !key.IsAlt && digit is >= 1 and <= 9 && digit <= count)
        {
            Finish(new PromptResult(Reply.Answered, digit - 1, string.Empty));
            return true;
        }

        return false;
    }

    /// <summary>Esc leaves the whole question; Shift+Tab goes one step back.</summary>
    private bool LeaveOrBack(Key key)
    {
        if (key == Key.Esc)
        {
            Finish(new PromptResult(Reply.Stop, 0, string.Empty));
            return true;
        }

        if (key == Key.Tab.WithShift)
        {
            Finish(new PromptResult(Reply.Back, 0, string.Empty));
            return true;
        }

        return false;
    }

    private bool TextKey(Prompt prompt, Key key)
    {
        if (LeaveOrBack(key))
        {
            return true;
        }

        if (key != Key.Enter)
        {
            return false;
        }

        var typed = _field.Text ?? string.Empty;
        if (ChatViewModel.IsExitWord(typed))
        {
            Finish(new PromptResult(Reply.Stop, 0, string.Empty));
            return true;
        }

        var value = prompt.Secret ? typed : typed.Trim();
        if (!prompt.Secret && value.Length == 0 && prompt.Text.Length > 0)
        {
            value = prompt.Text;
        }

        if (prompt.Check?.Invoke(value) is { } why)
        {
            _error = $"That will not do: {why}.";
            if (prompt.Secret)
            {
                _field.Text = string.Empty;
            }

            Render();
            return true;
        }

        Finish(new PromptResult(Reply.Answered, 0, value));
        return true;
    }

    private bool ModelKey(Key key)
    {
        var step = key == Key.CursorUp ? -1 : key == Key.CursorDown ? 1 : key == Key.PageUp ? -MostChoiceRows : key == Key.PageDown ? MostChoiceRows : 0;
        if (step != 0)
        {
            _mark = Math.Clamp(_mark + step, 0, Math.Max(0, _shown.Count - 1));
            Render();
            return true;
        }

        if (LeaveOrBack(key))
        {
            return true;
        }

        if (key != Key.Enter)
        {
            return false;
        }

        var text = (_field.Text ?? string.Empty).Trim();
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
            Render();
        }
        else if (_confirmName == text)
        {
            Finish(new PromptResult(Reply.Answered, 0, text));
        }
        else
        {
            _confirmName = text;
            _error = $"'{text}' is not in the platform's list. Enter again to use it as the model's name anyway.";
            Render();
        }

        return true;
    }

    /// <summary>A long list: arrows and pages move, Enter takes the marked item; one not yet usable says why and stays.</summary>
    private bool PickKey(Key key)
    {
        var step = key == Key.CursorUp ? -1 : key == Key.CursorDown ? 1 : key == Key.PageUp ? -MostChoiceRows : key == Key.PageDown ? MostChoiceRows : 0;
        if (step != 0)
        {
            _mark = Math.Clamp(_mark + step, 0, Math.Max(0, _shownItems.Count - 1));
            _error = null;
            Render();
            return true;
        }

        if (LeaveOrBack(key))
        {
            return true;
        }

        if (key != Key.Enter)
        {
            return false;
        }

        if (ChatViewModel.IsExitWord((_field.Text ?? string.Empty).Trim()))
        {
            Finish(new PromptResult(Reply.Stop, 0, string.Empty));
        }
        else if (_shownItems.Count == 0)
        {
            _error = "Nothing matches; type less, or other words.";
            Render();
        }
        else if (_shownItems[_mark] is { Unavailable.Length: > 0 } later)
        {
            _error = $"Not yet: {later.Unavailable}";
            Render();
        }
        else
        {
            Finish(new PromptResult(Reply.Answered, 0, _shownItems[_mark].Id));
        }

        return true;
    }

    private bool TellKey(Key key)
    {
        if (key == Key.Enter || key == Key.Esc)
        {
            Finish(new PromptResult(Reply.Answered, 0, string.Empty));
            return true;
        }

        return false;
    }

    /// <summary>Draws the question as it stands, and sizes the panel to it.</summary>
    private void Render()
    {
        if (_prompt is not { } prompt)
        {
            return;
        }

        var width = Math.Max(20, Theme.ScreenWidth() - 6);
        var head = new List<IReadOnlyList<Span>>();
        foreach (var line in Theme.Wrap(prompt.Note, width))
        {
            head.Add(Theme.Row(line, Theme.Dim));
        }

        if (head.Count > 0)
        {
            head.Add(Theme.Row(string.Empty, Theme.Text));
        }

        foreach (var line in Theme.Wrap(prompt.Question, width))
        {
            head.Add(Theme.Row(line, Theme.Text));
        }

        var body = new List<IReadOnlyList<Span>>();
        switch (prompt.Kind)
        {
            case PromptKind.Choose:
                var choices = prompt.Choices ?? [];
                var labelWidth = choices.Count == 0 ? 0 : choices.Max(c => c.Label.Length);
                var first = Math.Clamp(_mark - (MostChoiceRows - 1), 0, Math.Max(0, choices.Count - MostChoiceRows));
                for (var i = first; i < Math.Min(choices.Count, first + MostChoiceRows); i++)
                {
                    var on = i == _mark;
                    body.Add(
                    [
                        new Span(on ? "› " : "  ", Theme.Accent),
                        new Span(choices[i].Label.PadRight(labelWidth), on ? Theme.Accent : Theme.Text),
                        new Span(choices[i].Detail.Length > 0 ? "   " + choices[i].Detail : string.Empty, Theme.Dim),
                    ]);
                }

                break;
            case PromptKind.Model:
                var filter = (_field.Text ?? string.Empty).Trim();
                var models = prompt.Models ?? [];
                _shown = filter.Length == 0 ? [.. models] : [.. models.Where(m => m.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) || m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))];
                _mark = _shown.Count == 0 ? 0 : Math.Clamp(_mark, 0, _shown.Count - 1);
                var idWidth = Math.Min(40, models.Select(m => m.Id.Length).DefaultIfEmpty(10).Max());
                var top = Math.Clamp(_mark - (MostChoiceRows - 1), 0, Math.Max(0, _shown.Count - MostChoiceRows));
                for (var i = top; i < Math.Min(_shown.Count, top + MostChoiceRows); i++)
                {
                    var m = _shown[i];
                    var on = i == _mark;
                    var when = m.Created is { } created ? created.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;
                    body.Add(
                    [
                        new Span(on ? "› " : "  ", Theme.Accent),
                        new Span(m.Id.PadRight(idWidth), on ? Theme.Accent : Theme.Text),
                        new Span($"  {when,-10}  {m.Name}", Theme.Dim),
                    ]);
                }

                body.Add(Theme.Row($"{_shown.Count.ToString(CultureInfo.InvariantCulture)} of {models.Count.ToString(CultureInfo.InvariantCulture)}", Theme.Dim));
                break;
            case PromptKind.Pick:
                var words = (_field.Text ?? string.Empty).Trim();
                var items = prompt.Items ?? [];
                _shownItems = PickItem.Narrow(items, words);
                _mark = _shownItems.Count == 0 ? 0 : Math.Clamp(_mark, 0, _shownItems.Count - 1);
                var nameWidth = Math.Min(32, items.Select(i => i.Label.Length).DefaultIfEmpty(10).Max());
                var groupWidth = Math.Min(14, items.Select(i => i.Group.Length).DefaultIfEmpty(0).Max());
                var from = Math.Clamp(_mark - (MostChoiceRows - 1), 0, Math.Max(0, _shownItems.Count - MostChoiceRows));
                for (var i = from; i < Math.Min(_shownItems.Count, from + MostChoiceRows); i++)
                {
                    var item = _shownItems[i];
                    var on = i == _mark;
                    var label = item.Label.Length <= nameWidth ? item.Label.PadRight(nameWidth) : item.Label[..(nameWidth - 1)] + "…";
                    var later = item.Unavailable.Length > 0;
                    body.Add(
                    [
                        new Span(on ? "› " : "  ", Theme.Accent),
                        new Span(label, on ? Theme.Accent : later ? Theme.Dim : Theme.Text),
                        new Span($"  {item.Group.PadRight(groupWidth)}  {(later ? "not yet" : item.Detail)}", Theme.Dim),
                    ]);
                }

                body.Add(Theme.Row($"{_shownItems.Count.ToString(CultureInfo.InvariantCulture)} of {items.Count.ToString(CultureInfo.InvariantCulture)}", Theme.Dim));
                break;
        }

        if (_error is not null)
        {
            foreach (var line in Theme.Wrap(_error, width))
            {
                body.Add(Theme.Row(line, Theme.Error));
            }
        }

        body.Add(Theme.Row(Hint(prompt), Theme.Dim));

        var typed = prompt.Kind is PromptKind.Text or PromptKind.Model or PromptKind.Pick;
        _head.Height = head.Count;
        _head.Rows = head;
        _caret.Y = head.Count;
        _field.Y = head.Count;
        _body.Y = head.Count + (typed ? 1 : 0);
        _body.Height = body.Count;
        _body.Rows = body;
        var rows = head.Count + (typed ? 1 : 0) + body.Count + 2;
        if (rows != Rows)
        {
            Rows = rows;
            Height = rows;
            Changed?.Invoke();
        }

        SetNeedsDraw();
    }

    private static string Hint(Prompt prompt) => prompt.Kind switch
    {
        PromptKind.Choose => "↑/↓ move · Enter choose · 1-9 pick · " + (prompt.Alert ? "Esc deny" : "Shift+Tab back · Esc leave"),
        PromptKind.Text => (prompt.Secret ? "Not shown as you type. " : string.Empty) + "Enter next · Shift+Tab back · Esc or exit leave",
        PromptKind.Model or PromptKind.Pick => "Type to narrow · ↑/↓ move · Enter choose · Shift+Tab back · Esc or exit leave",
        _ => "Enter to go on",
    };
}
