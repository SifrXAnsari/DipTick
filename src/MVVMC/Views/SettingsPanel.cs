using DipTick.ViewModels;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace DipTick.Views;

/// <summary>
/// The settings screen: it takes the transcript's place instead of floating over it. Sections on the
/// left, the section's settings on the right, the chosen one's help below, a hint line. Its editors
/// open in the prompt zone. Its F-keys are the window's: F2 changes the setting you are on, F8 puts
/// it back, F3 finds one in any section, F1 says all about it. It decides nothing;
/// <see cref="SettingsViewModel"/> does.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Usage",
    "CA2213:Disposable fields should be disposed",
    Justification = "Each field is a subview added in the constructor; Terminal.Gui disposes a view's subviews when the view itself " +
                    "is disposed. Disposing them here as well would dispose them twice.")]
internal sealed class SettingsPanel : View
{
    private const int SectionWidth = 18;
    private const int LabelWidth = 26;

    /// <summary>Columns a widened name column always leaves for the values.</summary>
    private const int ValueRoom = 16;
    private const int HelpRows = 4;

    private readonly IApplication _app;
    private readonly RowsView _sections = new() { X = 0, Y = 0, Width = SectionWidth, Height = Dim.Fill(HelpRows + 2) };
    private readonly RowsView _rows = new() { X = SectionWidth + 1, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(HelpRows + 2) };
    private readonly RowsView _help = new() { X = 0, Y = Pos.AnchorEnd(HelpRows + 1), Width = Dim.Fill(), Height = HelpRows };
    private readonly RowsView _hint = new() { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1 };
    private SettingsViewModel? _settings;
    private IReadOnlyList<SettingRow> _current = [];
    private IReadOnlyList<string> _foundIn = [];
    private string? _finding;
    private string? _said;
    private int _section = 1;
    private int _row;
    private bool _onRows;
    private bool _all;
    private bool _busy;

    public SettingsPanel(IApplication app)
    {
        _app = app;
        CanFocus = true;
        Visible = false;
        BorderStyle = LineStyle.Rounded;
        Title = "Settings";
        SetScheme(Theme.Lit);
        Add(_sections, _rows, _help, _hint);
        KeyDown += (_, key) =>
        {
            if (OnKey(key))
            {
                key.Handled = true;
            }
        };
    }

    /// <summary>Raised when the person leaves the settings screen.</summary>
    public event Action? Closed;

    /// <summary>Whether a find is showing its rows in place of a section's.</summary>
    public bool Finding => _finding is not null;

    public void Open(SettingsViewModel settings)
    {
        _settings = settings;
        _onRows = false;
        _finding = null;
        Visible = true;
        Reload();
        SetFocus();
    }

    /// <summary>Reads the rows again: after a change, the values are the new ones.</summary>
    public void Reload()
    {
        if (_settings is not { } settings)
        {
            return;
        }

        _section = Math.Clamp(_section, 0, settings.Sections.Count - 1);
        if (_finding is { } words)
        {
            var found = settings.Find(words);
            _current = [.. found.Select(x => x.Row)];
            _foundIn = [.. found.Select(x => x.Section)];
        }
        else
        {
            _current = settings.Rows(settings.Sections[_section]);
            _foundIn = [];
        }

        _row = Math.Clamp(_row, 0, Math.Max(0, _current.Count - 1));
        Draw();
    }

    /// <summary>F2 (and Enter): the settings of the section, then the change of the setting you are on.</summary>
    public void ChangeSelected()
    {
        if (_settings is not { } settings)
        {
            return;
        }

        if (!_onRows)
        {
            _onRows = true;
            Draw();
            return;
        }

        Act(settings.ChangeAsync);
    }

    /// <summary>F8 (and Del): the setting you are on, back to its default.</summary>
    public void DefaultSelected()
    {
        if (_settings is { } settings && _onRows)
        {
            Act(settings.DefaultAsync);
        }
        else
        {
            Say("Choose a setting first: → or Enter goes to the section's settings.");
        }
    }

    /// <summary>F1: all about the setting you are on, with the command that does the same from a terminal.</summary>
    public void HelpSelected()
    {
        // A file DipTick keeps: what it is, on the page that says (Data's "What is this?").
        if (_onRows && _current.Count > 0 && _current[_row].Action == RowAction.OpenPath && _settings is { } settings)
        {
            settings.WhatIsThis();
            return;
        }

        _all = _onRows && _current.Count > 0;
        if (!_all)
        {
            Say("F1 on a setting says all about it. → or Enter goes to the section's settings.");
            return;
        }

        Draw();
    }

    /// <summary>F6: from the sections to the settings, and back.</summary>
    public void NextPane()
    {
        _onRows = !_onRows && _current.Count > 0;
        _all = false;
        Draw();
    }

    /// <summary>F3: the rows of every section that hold <paramref name="words"/>; empty words show the section again.</summary>
    public void Find(string words)
    {
        ArgumentNullException.ThrowIfNull(words);
        _finding = words.Trim().Length == 0 ? null : words.Trim();
        _row = 0;
        _onRows = _finding is not null;
        Reload();
    }

    /// <summary>F7: the settings file, selected in Explorer; what stood in the way, said in the help rows.</summary>
    public void ShowFile()
    {
        if (_settings?.RevealFile() is { } why)
        {
            Say(why);
        }
    }

    /// <summary>F9, and Esc from the sections: back to the chat.</summary>
    public void Close()
    {
        _finding = null;
        Visible = false;
        Closed?.Invoke();
    }

    /// <summary>Esc, nearest first: a find shows the sections again, the settings go back to the sections, and the sections close.</summary>
    public void Back()
    {
        _said = null;
        if (_finding is not null)
        {
            Find(string.Empty);
        }
        else if (_onRows)
        {
            _onRows = false;
            _all = false;
            Draw();
        }
        else
        {
            Close();
        }
    }

    /// <summary>A line in the help rows in place of the setting's help, until the next move.</summary>
    private void Say(string text)
    {
        _said = text;
        Draw();
    }

    private bool OnKey(Key key)
    {
        if (_settings is not { } settings || _busy)
        {
            return _busy;
        }

        _said = null;
        if (key == Key.Esc)
        {
            Back();
            return true;
        }

        if (key == Key.Tab || key == Key.CursorRight || key == Key.CursorLeft || key == Key.Tab.WithShift)
        {
            // Tab switches sides; → goes to the settings, ← and Shift+Tab back to the sections.
            _onRows = (key == Key.Tab ? !_onRows : key == Key.CursorRight) && _current.Count > 0;
            _all = false;
            Draw();
            return true;
        }

        if (key == Key.CursorUp || key == Key.CursorDown || key == Key.PageUp || key == Key.PageDown)
        {
            var step = key == Key.CursorUp ? -1 : key == Key.CursorDown ? 1 : key == Key.PageUp ? -10 : 10;
            _all = false;
            if (_onRows)
            {
                _row = Math.Clamp(_row + step, 0, Math.Max(0, _current.Count - 1));
            }
            else if (_finding is null)
            {
                _section = Math.Clamp(_section + step, 0, settings.Sections.Count - 1);
                _row = 0;
                _current = settings.Rows(settings.Sections[_section]);
            }

            Draw();
            return true;
        }

        if (key == Key.Enter)
        {
            ChangeSelected();
            return true;
        }

        if (_onRows && (key == Key.Delete || key == Key.D))
        {
            DefaultSelected();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Change and Default ask their questions from a worker thread, as every flow does, so the
    /// prompt zone can be waited for; the screen redraws and takes the keyboard back when done.
    /// </summary>
    private void Act(Func<SettingRow, Task> action)
    {
        if (_current.Count == 0 || _busy)
        {
            return;
        }

        _busy = true;
        var row = _current[_row];
        _ = Task.Run(async () =>
        {
            try
            {
                await action(row).ConfigureAwait(false);
            }
            finally
            {
                _app.Invoke(() =>
                {
                    _busy = false;
                    Reload();
                    if (Visible)
                    {
                        SetFocus();
                    }

                    _app.LayoutAndDraw();
                });
            }
        });
    }

    private void Draw()
    {
        if (_settings is not { } settings)
        {
            return;
        }

        _sections.Rows = [.. settings.Sections.Select((name, i) =>
        {
            var on = i == _section && _finding is null;
            return (IReadOnlyList<Span>)
            [
                new Span(on ? "› " : "  ", on && !_onRows ? Theme.Accent : Theme.Dim),
                new Span(name, on ? (_onRows ? Theme.Text : Theme.Accent) : Theme.Dim),
            ];
        })];

        var rows = new List<IReadOnlyList<Span>>();
        if (_finding is { } words)
        {
            rows.Add(Theme.Row(_current.Count == 0 ? $"  nothing holds '{words}' · Esc shows the sections" : $"  found '{words}' in {_current.Count} · Esc shows the sections", Theme.Dim));
        }

        // A found setting is named under its section, dim, so the name column widens to the longest
        // name on show, as far as leaves the values room; a name longer than that is cut with "…".
        var sectionOf = new string[_current.Count];
        var names = new string[_current.Count];
        for (var i = 0; i < _current.Count; i++)
        {
            sectionOf[i] = i < _foundIn.Count ? _foundIn[i] + " · " : string.Empty;
            names[i] = i < _foundIn.Count ? _current[i].Label.Trim() : _current[i].Label;
        }

        var longest = _current.Count == 0 ? 0 : Enumerable.Range(0, _current.Count).Max(i => sectionOf[i].Length + names[i].Length);
        var nameWidth = Math.Clamp(longest + 2, LabelWidth, Math.Max(LabelWidth, _rows.Viewport.Width - ValueRoom));
        for (var i = 0; i < _current.Count; i++)
        {
            var row = _current[i];
            var on = _onRows && i == _row;
            var header = row.Action == RowAction.SeatHeader;
            var room = nameWidth - 2 - sectionOf[i].Length;
            var name = names[i].Length <= room ? names[i] : string.Concat(names[i].AsSpan(0, Math.Max(0, room - 1)), "…");
            rows.Add(
            [
                new Span(on ? "› " : "  ", Theme.Accent),
                new Span(sectionOf[i], Theme.Dim),
                new Span(name.PadRight(nameWidth - sectionOf[i].Length), on ? Theme.Accent : Theme.Text),
                new Span(row.Value, header ? Theme.Dim : on ? Theme.Text : Theme.Dim),
            ]);
        }

        _rows.Rows = rows;
        if (_onRows)
        {
            _rows.Show(_row + (_finding is null ? 0 : 1));
        }

        var width = Math.Max(20, Viewport.Width - 2);
        var note = _finding is null ? SettingsViewModel.Note(settings.Sections[_section]) : string.Empty;
        string help;
        if (_said is { } said)
        {
            help = said;
        }
        else if (_current.Count == 0 || !_onRows)
        {
            help = note;
        }
        else
        {
            var chosen = _current[_row];
            help = chosen.Help + (note.Length > 0 ? $" ({note})" : string.Empty);
            if (_all && SettingsViewModel.CommandFor(chosen) is { Length: > 0 } command)
            {
                help += $"\nFrom a terminal: {command}";
            }
        }

        _help.Rows = [.. Theme.Wrap(help, width).Take(HelpRows).Select(line => Theme.Row(line, _said is null ? Theme.Dim : Theme.Warning))];
        _hint.Rows = [Theme.Row(_onRows ? "↑/↓ move · Enter or F2 change · F8 default · F1 all about it · Esc back" : "↑/↓ section · Enter or → its settings · F3 find · Esc close", Theme.Dim)];
        SetNeedsDraw();
    }
}
