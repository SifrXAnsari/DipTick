using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using DipTick.Models;
using DipTick.Models.Chat;
using DipTick.Models.Chat.Store;
using DipTick.Models.Harness;
using DipTick.ViewModels;

namespace DipTick.Views.Desktop;

/// <summary>
/// Settings, as a sheet beside the chat: the one place a setting changes. Sections on the left, the
/// section's settings on the right; F3 finds a setting in any section by its name or by what it does.
/// One click does the thing: a yes-or-no is a switch and a choice a list, both changed where they
/// stand; any other row opens its editor at once. The setting you are on is the one a click or Tab
/// last reached: F2 changes it, F8 puts it back to its default, F1 says all about it. Default shows
/// only where the file sets a value. Every change runs the verb that makes it, through
/// <see cref="SettingsViewModel"/>, and asks first where the setting needs a yes.
/// </summary>
internal sealed class SettingsPage : Grid
{
    private readonly Func<ChatViewModel> _front;
    private ChatViewModel? _settingsFor;
    private SettingsViewModel? _settingsOf;
    private readonly Func<string, Task> _copy;
    private readonly TextBox _find = new() { PlaceholderText = "find a setting: its name, or what it does", IsVisible = false, Margin = new Thickness(0, 0, 0, 10), FontSize = 13, Padding = new Thickness(8, 4) };
    private readonly StackPanel _sections = new() { Spacing = 2 };
    private readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(8, 0, 0, 6) };
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly ScrollViewer _rowsScroll;
    private readonly TextBlock _help = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    private readonly ScrollViewer _helpScroll;

    /// <summary>The least the help card is given, however small the page: a few lines.</summary>
    private const double HelpLeast = 96;
    private readonly StackPanel _tryPanel = new() { Spacing = 8, IsVisible = false };
    private readonly SelectableTextBlock _tryAnswer = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    private readonly Dictionary<string, Control> _controls = new(StringComparer.Ordinal);
    private IReadOnlyList<SettingRow> _current = [];
    private int _section = 1;
    private string? _marked;
    private SettingRow? _selected;
    private bool _pinned;
    private bool _busy;

    /// <param name="front">The chat in front: the tab whose questions Settings asks in, and whose verbs it runs.</param>
    /// <param name="copy">Puts text on the clipboard (Copy value).</param>
    public SettingsPage(Func<ChatViewModel> front, Func<string, Task> copy)
    {
        _front = front;
        _copy = copy;
        RowDefinitions = new RowDefinitions("Auto,*");
        Margin = new Thickness(16, 4, 16, 12);

        Children.Add(_find);
        _find.TextChanged += (_, _) => Reload();

        // The sections down the whole left side, so every one shows; the settings, their help, the
        // try panel and the keys' line on the right.
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("128,*"), RowDefinitions = new RowDefinitions("*,Auto,Auto,Auto") };
        Grid.SetRow(body, 1);
        var sections = new ScrollViewer { Content = _sections };
        Grid.SetRowSpan(sections, 4);
        body.Children.Add(sections);
        var right = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(12, 0, 0, 0) };
        right.Children.Add(_title);

        // Try a request scrolls with the settings above it: below them it would take the room the settings
        // need, and on a short page leave them none.
        BuildTry();
        _rowsScroll = new ScrollViewer { Content = new StackPanel { Children = { _rows, _tryPanel } } };
        Grid.SetRow(_rowsScroll, 1);
        right.Children.Add(_rowsScroll);
        Grid.SetColumn(right, 1);
        body.Children.Add(right);
        Children.Add(body);

        // The help card keeps to two fifths of the page and scrolls what is longer, so the settings above it
        // stay in view: a connector's whole command and sandbox can run to many lines.
        _helpScroll = new ScrollViewer { Content = _help, Focusable = false };
        var helpCard = new Border { Classes = { "card" }, Margin = new Thickness(12, 10, 0, 0), Child = _helpScroll };
        Grid.SetRow(helpCard, 1);
        Grid.SetColumn(helpCard, 1);
        body.Children.Add(helpCard);
        body.SizeChanged += (_, e) => helpCard.MaxHeight = Math.Max(HelpLeast, e.NewSize.Height * 0.4);
        _help.Foreground = Palette.Dim;

        var foot =new TextBlock { Text = "Enter or F2 changes · F8 default · F1 all about it · Esc closes", Classes = { "dim" }, FontSize = 11, Margin = new Thickness(14, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(foot, 3);
        Grid.SetColumn(foot, 1);
        body.Children.Add(foot);
    }

    /// <summary>The chat in front, whichever tab it is.</summary>
    private ChatViewModel Chat => _front();

    /// <summary>Settings over the chat in front, made again when another tab comes in front.</summary>
    private SettingsViewModel Settings
    {
        get
        {
            var chat = _front();
            if (!ReferenceEquals(chat, _settingsFor) || _settingsOf is null)
            {
                _settingsFor = chat;
                _settingsOf = new SettingsViewModel(chat);
            }

            return _settingsOf;
        }
    }

    /// <summary>The section on show, by name.</summary>
    internal string Section => Settings.Sections[Math.Clamp(_section, 0, Settings.Sections.Count - 1)];

    /// <summary>Whether the find box is open.</summary>
    public bool FindOpen => _find.IsVisible;

    /// <summary>Reads the rows again: after a change, the values are the new ones.</summary>
    public void Reload()
    {
        _section = Math.Clamp(_section, 0, Settings.Sections.Count - 1);
        _sections.Children.Clear();
        var finding = FindOpen && (_find.Text ?? string.Empty).Trim().Length > 0;
        for (var i = 0; i < Settings.Sections.Count; i++)
        {
            var index = i;
            var button = new Button { Classes = { "rail" }, Content = Settings.Sections[i] };
            if (i == _section && !finding)
            {
                button.Classes.Add("current");
            }

            button.Click += (_, _) =>
            {
                _section = index;
                _marked = null;
                _selected = null;
                _pinned = false;
                if (FindOpen)
                {
                    CloseFind();
                }

                Reload();
            };
            _sections.Children.Add(button);
        }

        List<(SettingRow Row, string? Section)> shown;
        if (finding)
        {
            // Found rows say which section they live in, above their own name.
            shown = [.. Settings.Find(_find.Text ?? string.Empty).Select(x => (x.Row, (string?)x.Section))];
            _title.Text = shown.Count switch
            {
                0 => "Nothing found",
                1 => "1 setting found",
                _ => $"{shown.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} settings found",
            };
        }
        else
        {
            var section = Settings.Sections[_section];
            _title.Text = section;
            shown = [.. Settings.Rows(section).Select(row => (row, (string?)null))];
        }

        _current = [.. shown.Select(x => x.Row)];
        _rows.Children.Clear();
        _controls.Clear();

        // Permissions as controls: a row per capability in words, then the lists below for the rest.
        if (!finding && Section == "Permissions")
        {
            _rows.Children.Add(PermissionControls());
        }

        Control? marked = null;
        foreach (var (row, foundIn) in shown)
        {
            var control = RowControl(row, foundIn);
            _controls[Key(row)] = control;
            if (_marked is not null && Key(row) == _marked)
            {
                control.Classes.Add("current");
                marked = control;
            }

            _rows.Children.Add(control);
        }

        // The setting you were on stays the one you are on, with its value as it now stands.
        _selected = _marked is null ? null : _current.FirstOrDefault(r => Key(r) == _marked);
        if (!_pinned || _selected is null)
        {
            _pinned = false;
            ShowHelp(_selected);
        }

        _tryPanel.IsVisible = !finding && Section == "Permissions";
        marked?.BringIntoView();
    }

    /// <summary>Opens a section by name, with the row whose key is <paramref name="row"/> marked.</summary>
    public void Show(string section, string? row = null)
    {
        var index = Settings.Sections.ToList().IndexOf(section);
        if (index >= 0)
        {
            _section = index;
        }

        if (FindOpen)
        {
            CloseFind();
        }

        _marked = row;
        _pinned = false;
        Reload();
    }

    /// <summary>Models, with the model marked: <c>main</c>, or a seat's id.</summary>
    public void ShowModel(string id) => Show("Models", ModelKey(id));

    /// <summary>
    /// Starts the model's own editor (provider, key, check, model) without leaving the page on show:
    /// the one click behind "Set up a model". The questions come in the prompt card.
    /// </summary>
    public void StartModel(string id)
    {
        var row = Settings.Rows("Models").FirstOrDefault(r => Key(r) == ModelKey(id));
        if (row is not null)
        {
            Act(Settings.ChangeAsync, row);
        }
    }

    /// <summary>
    /// Settings, Models, Add a model, started without leaving the page on show: the one click behind
    /// "Set up another agent" at a chat's table. The questions come in the prompt card.
    /// </summary>
    public void StartAddModel()
    {
        var row = Settings.Rows("Models").FirstOrDefault(r => r.Action == RowAction.AddModel);
        if (row is not null)
        {
            Act(Settings.ChangeAsync, row);
        }
    }

    /// <summary>
    /// The permissions as controls: the presets, each saying what it changes before
    /// it is written; then a row per capability in plain words with Allowed, Ask me first and Never, and a
    /// column per agent that can only narrow (as the settings, ask, never). Each click is the verb that
    /// makes the change (<c>diptick policy</c>), which asks first when it lets agents do more.
    /// </summary>
    private StackPanel PermissionControls()
    {
        var config = Chat.Config;
        var p = config.Permissions;
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(8, 0, 0, 12) };

        var presets = new WrapPanel { ItemSpacing = 8, LineSpacing = 6 };
        var current = Presets.Match(p);
        presets.Children.Add(new TextBlock { Text = current is null ? "Custom:" : "Preset:", VerticalAlignment = VerticalAlignment.Center, Classes = { "dim" } });
        foreach (var name in Presets.Names)
        {
            var button = new Button { Content = char.ToUpperInvariant(name[0]) + name[1..] };
            if (name == current)
            {
                button.Classes.Add("primary");
            }

            ToolTip.SetTip(button, name switch
            {
                "locked" => "Everything asks or is never done",
                "standard" => "The starter settings: model calls and searching allowed; files, the web and the rest ask; deleting and programs never",
                _ => "Reads, model calls, searching and the network check allowed; writing, the web, programs, media and connectors ask",
            });
            button.Click += (_, _) => Run("policy", "preset", name);
            presets.Children.Add(button);
        }

        panel.Children.Add(presets);

        // A capability a block: its words, then everyone's rule and each agent's, wrapping as the sheet narrows,
        // so nothing is cut or squeezed away beside the chat, as it would be in a grid of columns.
        var agents = config.Seats.ToList();
        panel.Children.Add(new TextBlock
        {
            Text = agents.Count == 0 ? "Agents may, capability by capability:" : "Agents may, for everyone, then for each agent, which can only narrow it:",
            Classes = { "dim" },
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });

        foreach (var cap in Capabilities.All)
        {
            var name = Capabilities.Name(cap);
            var state = p.Deny.Any(r => Capabilities.Matches(r, cap)) ? "deny" : p.Ask.Any(r => Capabilities.Matches(r, cap)) ? "ask" : p.Allow.Any(r => Capabilities.Matches(r, cap)) ? "allow" : "deny";
            var block = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 4) };
            block.Children.Add(new TextBlock { Text = $"{Capabilities.Describe(cap)}  ({name})", TextWrapping = TextWrapping.Wrap });
            var controls = new WrapPanel { ItemSpacing = 10, LineSpacing = 4 };
            block.Children.Add(controls);
            panel.Children.Add(block);

            var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            foreach (var (verdict, label) in new[] { ("allow", "Allowed"), ("ask", "Ask me first"), ("deny", "Never") })
            {
                var choice = new Button { Content = label, Classes = { "chip" }, FontSize = 12 };
                if (verdict == state)
                {
                    choice.Classes.Add("primary");
                }

                choice.Click += (_, _) =>
                {
                    if (verdict != state)
                    {
                        Run("policy", "set", name, verdict);
                    }
                };
                choices.Children.Add(choice);
            }

            controls.Children.Add(choices);

            // Each agent can only narrow what everyone may do.
            foreach (var seat in agents)
            {
                var narrowed = !seat.Lane.Any(r => Capabilities.SpeaksOf(r, cap)) ? "deny" : seat.Ask.Any(r => Capabilities.SpeaksOf(r, cap)) ? "ask" : "allow";
                var box = new ComboBox { ItemsSource = new[] { "as everyone", "ask", "never" }, SelectedIndex = narrowed switch { "ask" => 1, "deny" => 2, _ => 0 }, FontSize = 12, MinWidth = 110 };
                var id = seat.Id;
                box.SelectionChanged += (_, _) =>
                {
                    var picked = box.SelectedIndex switch { 1 => "ask", 2 => "deny", _ => "allow" };
                    if (picked != narrowed)
                    {
                        Run("policy", "set", name, picked, "--ai", id);
                    }
                };
                controls.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Children = { new TextBlock { Text = id, Classes = { "dim" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }, box },
                });
            }
        }
        panel.Children.Add(new TextBlock
        {
            Text = "A chat can tighten any of this, or allow an ask in itself: the permissions chip in its header. Folders and web hosts are below.",
            Classes = { "dim" },
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });
        return panel;
    }

    /// <summary>A permissions verb from a control, off the screen's thread (it may ask), then the page read again.</summary>
    private void Run(params string[] args)
    {
        var chat = Chat;
        _ = Task.Run(() =>
        {
            chat.Run(false, args);
            chat.Reload();
            Dispatcher.UIThread.Post(Reload);
        });
    }

    /// <summary>Where the keyboard goes when the sheet opens: the setting you are on, else the section's first.</summary>
    public void FocusStart()
    {
        if (_selected is { } row && _controls.GetValueOrDefault(Key(row)) is { } control)
        {
            FocusIn(control);
        }
        else if (_rows.Children.FirstOrDefault() is Control first)
        {
            FocusIn(first);
        }
    }

    /// <summary>F2: changes the setting you are on, as a click on it does; a switch or a list asks in the prompt card.</summary>
    public void ChangeSelected()
    {
        if (_selected is not { } row)
        {
            Say("Choose a setting first, with a click or Tab; then F2 changes it.");
            return;
        }

        Act(Settings.ChangeAsync, row);
    }

    /// <summary>F8: the setting you are on, back to its default; a saved key is removed, after a yes.</summary>
    public void DefaultSelected()
    {
        if (_selected is not { } row)
        {
            Say("Choose a setting first, with a click or Tab; then F8 puts it back to its default.");
            return;
        }

        if (!HasDefault(row))
        {
            Say($"{row.Label.Trim()} has nothing to put back: the file does not set it, so its default applies already.");
            return;
        }

        Act(Settings.DefaultAsync, row);
    }

    /// <summary>F1: all about the setting you are on, kept in the help card until another is chosen.</summary>
    public void HelpSelected()
    {
        if (_selected is not { } row)
        {
            Say("F1 on a setting says all about it: what it does, its default, what it takes, and the command that changes it from a terminal. Choose one with a click or Tab first.");
            return;
        }

        _pinned = true;
        _help.Text = row.Help + (SettingsViewModel.CommandFor(row) is { Length: > 0 } command ? $"\nFrom a terminal: {command}" : string.Empty);
        _help.Foreground = Palette.Ink;
        _helpScroll.ScrollToHome();
    }

    /// <summary>F3, Ctrl+F: the find box, which searches every section; again, it closes.</summary>
    public void ToggleFind()
    {
        if (FindOpen)
        {
            CloseFind();
            Reload();
            return;
        }

        _find.IsVisible = true;
        _find.Focus();
    }

    /// <summary>F7: the settings file, selected in Explorer.</summary>
    public void ShowFile()
    {
        if (Settings.RevealFile() is { } why)
        {
            Say(why);
        }
    }

    private void CloseFind()
    {
        _find.IsVisible = false;
        _find.Text = string.Empty;
    }

    /// <summary>The key a row is found by: its setting's name, or what it does and for whom.</summary>
    private static string Key(SettingRow row) => row.Info?.Name ?? $"{row.Action}:{row.Section}:{row.Label.Trim()}";

    private static string ModelKey(string id) => id is ChatStores.MainSeat or ChatStores.FormerMainSeat ? "main.provider" : $"{RowAction.SeatHeader}:{id}:Seat {id}";

    /// <summary>Default, where the file sets the value; Remove, where a key is saved.</summary>
    private static bool HasDefault(SettingRow row) =>
        row.Set && (row.Action == RowAction.Key || (row.Action == RowAction.Setting && row.Info is not { Kind: SettingKind.Password }));

    private static void FocusIn(Control control)
    {
        if (control.Focusable)
        {
            control.Focus();
            return;
        }

        foreach (var child in control.GetLogicalDescendants())
        {
            if (child is InputElement { Focusable: true, IsEffectivelyEnabled: true } input)
            {
                input.Focus();
                return;
            }
        }
    }

    private void Select(SettingRow row)
    {
        var key = Key(row);
        if (_marked == key && _selected is not null)
        {
            return;
        }

        if (_marked is { } before && _controls.GetValueOrDefault(before) is { } old)
        {
            old.Classes.Remove("current");
        }

        _marked = key;
        _selected = row;
        _pinned = false;
        _controls.GetValueOrDefault(key)?.Classes.Add("current");
        ShowHelp(row);
    }

    /// <param name="foundIn">The section the row lives in, shown above its name when a find brought it here; else null.</param>
    private Control RowControl(SettingRow row, string? foundIn)
    {
        var header = row.Action == RowAction.SeatHeader;
        var label = row.Label;

        // The label and the value share the width, so a narrow sheet squeezes both, never over Default.
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("9*,11*,Auto") };
        var name = new TextBlock
        {
            Text = label,
            FontWeight = header || row.Action is RowAction.RunSetup or RowAction.AddModel ? FontWeight.SemiBold : FontWeight.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        // A place DipTick keeps things: its name, then its path in full below it, a clipped path being
        // no use to go and look at. A page, or the export: its name across the row, with no value beside it.
        var whole = row.Action is RowAction.OpenPath or RowAction.OpenDoc or RowAction.ExportData;
        Control nameHost;
        if (foundIn is null && row.Action != RowAction.OpenPath)
        {
            name.Margin = new Thickness(0, 0, 8, 0);
            nameHost = name;
        }
        else
        {
            var both = new StackPanel { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            if (foundIn is not null)
            {
                both.Children.Add(new TextBlock { Text = foundIn, Classes = { "dim" }, FontSize = 11 });
            }

            name.TextWrapping = TextWrapping.Wrap;
            both.Children.Add(name);
            if (row.Action == RowAction.OpenPath)
            {
                both.Children.Add(new TextBlock { Text = row.Value, Classes = { "dim" }, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            }

            nameHost = both;
        }

        if (whole)
        {
            name.TextTrimming = TextTrimming.None;
            name.TextWrapping = TextWrapping.Wrap;
            Grid.SetColumnSpan(nameHost, 2);
        }

        line.Children.Add(nameHost);
        var (editor, inPlace) = Editor(row);
        if (!whole)
        {
            Grid.SetColumn(editor, 1);
            line.Children.Add(editor);
        }

        var reset = DefaultButton(row);
        if (reset is not null)
        {
            Grid.SetColumn(reset, 2);
            line.Children.Add(reset);
        }
        else if (row.Action == RowAction.OpenPath)
        {
            // A file DipTick keeps: what it is, on the page that says.
            var what = new Button { Content = "What is this?", Classes = { "tool" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            ToolTip.SetTip(what, "Help, Your data: what DipTick keeps, and why");
            what.Click += (_, e) =>
            {
                e.Handled = true;
                Settings.WhatIsThis();
            };
            Grid.SetColumn(what, 2);
            line.Children.Add(what);
        }

        Control control;
        if (inPlace)
        {
            control = new Border { Classes = { "setting" }, Child = line };
        }
        else
        {
            var button = new Button { Classes = { "row" }, Content = line };
            button.Click += (_, _) =>
            {
                Select(row);
                Act(Settings.ChangeAsync, row);
            };
            control = button;
        }

        ToolTip.SetTip(control, $"{(foundIn is null ? string.Empty : foundIn + " · ")}{label.Trim()}: {(row.Action == RowAction.Setting ? row.Raw : row.Value)}");
        control.PointerEntered += (_, _) =>
        {
            if (!_pinned)
            {
                ShowHelp(row);
            }
        };
        control.PointerExited += (_, _) =>
        {
            if (!_pinned)
            {
                ShowHelp(_selected);
            }
        };
        control.AddHandler(GotFocusEvent, (_, _) => Select(row), RoutingStrategies.Bubble);
        control.AddHandler(PointerPressedEvent, (_, _) => Select(row), RoutingStrategies.Tunnel);
        control.ContextMenu = Menus.Of(
            inPlace ? null : Menus.Item(row.Action switch { RowAction.OpenPath => "Open folder", RowAction.OpenDoc => "Open page", RowAction.ExportData => "Export…", _ => "Change…" }, () => Act(Settings.ChangeAsync, row), "F2"),
            Menus.Item("Copy value", () => _ = _copy(row.Action == RowAction.Setting ? row.Raw : row.Value)),
            reset is null ? null : Menus.Item(row.Action == RowAction.Key ? "Remove key" : "Default", () => Act(Settings.DefaultAsync, row), "F8"),
            Menus.Item("All about it", () =>
            {
                Select(row);
                HelpSelected();
            }, "F1"));
        return control;
    }

    /// <summary>
    /// What stands in the value's place: a switch for yes-or-no, a list for a choice (both changed
    /// right there), else the value as text for the row's own click to change.
    /// </summary>
    private (Control Editor, bool InPlace) Editor(SettingRow row)
    {
        if (row.Action == RowAction.Setting && row.Info is { Kind: SettingKind.YesNo })
        {
            var toggle = new ToggleSwitch { IsChecked = row.Raw == "true", OnContent = "yes", OffContent = "no", HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            toggle.IsCheckedChanged += (_, _) =>
            {
                // Read here, on the window's thread: a control's value cannot be read from the worker.
                var value = toggle.IsChecked == true ? "true" : "false";
                Act(r => Settings.SetValueAsync(r, value), row);
            };
            return (toggle, true);
        }

        if (row.Action == RowAction.Setting && row.Info is { Kind: SettingKind.Choice, Choices: { } choices })
        {
            var combo = new ComboBox
            {
                ItemsSource = choices.Select(c => c.Length == 0 ? "(the default)" : c).ToList(),
                SelectedIndex = Math.Max(0, choices.ToList().IndexOf(row.Raw)),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MaxWidth = 260,
                VerticalAlignment = VerticalAlignment.Center,
            };
            combo.SelectionChanged += (_, _) =>
            {
                var index = combo.SelectedIndex;
                if (index >= 0 && index < choices.Count)
                {
                    var value = choices[index];
                    Act(r => Settings.SetValueAsync(r, value), row);
                }
            };
            return (combo, true);
        }

        var shown = row.Action switch
        {
            RowAction.RunSetup => "Start",
            RowAction.AddModel => "Choose",
            RowAction.AddSeat or RowAction.RemoveSeat => string.Empty,
            _ => row.Value,
        };
        return (new TextBlock
        {
            Text = shown,
            Foreground = row.Action is RowAction.RunSetup or RowAction.AddModel ? Palette.Accent : Palette.Dim,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        }, false);
    }

    /// <summary>Default, where the file sets the value; Remove, where a key is saved. Null where there is nothing to put back.</summary>
    private Button? DefaultButton(SettingRow row)
    {
        if (!HasDefault(row))
        {
            return null;
        }

        var button = new Button { Content = row.Action == RowAction.Key ? "Remove" : "Default", Classes = { "tool" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        ToolTip.SetTip(button, row.Action == RowAction.Key ? "Remove the saved key (asks first)" : "Put this setting back to its default (F8)");
        button.Click += (_, e) =>
        {
            e.Handled = true;
            Select(row);
            Act(Settings.DefaultAsync, row);
        };
        return button;
    }

    private void ShowHelp(SettingRow? row)
    {
        var note = FindOpen ? string.Empty : SettingsViewModel.Note(Section);
        _help.Foreground = Palette.Dim;
        _help.Text = row is null
            ? note.Length > 0 ? note : "Point at a setting to read what it does. One click changes it."
            : row.Help + (note.Length > 0 ? $"\n({note})" : string.Empty);
        _helpScroll.ScrollToHome();
    }

    /// <summary>A line in the help card, in place of the setting's help: what a key could not do, and why.</summary>
    private void Say(string text)
    {
        _pinned = true;
        _help.Text = text;
        _help.Foreground = Palette.Warning;
        _helpScroll.ScrollToHome();
    }

    /// <summary>
    /// Runs a change on a worker thread, as every flow does, so the prompt card can be waited for;
    /// the rows are read again after. A failure nobody expected is said in the chat, never lost.
    /// </summary>
    private void Act(Func<SettingRow, Task> action, SettingRow row)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _marked = Key(row);
        _ = Task.Run(async () =>
        {
            try
            {
                await action(row).ConfigureAwait(false);
            }
            finally
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _busy = false;
                    _pinned = false;
                    Reload();
                });
            }
        }).ContinueWith(
            failed => Chat.Say(Role.System, $"Settings · {row.Label.Trim()}: that did not work ({failed.Exception?.GetBaseException().Message})."),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    /// <summary>Try a request: the policy's answer to one capability and target, made nowhere, recorded nowhere.</summary>
    private void BuildTry()
    {
        _tryPanel.Margin = new Thickness(12, 12, 0, 0);
        _tryPanel.Children.Add(new TextBlock { Text = "TRY A REQUEST", FontSize = 12, LetterSpacing = 1.2, Classes = { "dim" } });
        // The capability and its target on one line, the question under them: on a narrow sheet nothing is cut.
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnSpacing = 8, RowSpacing = 8 };
        var capability = new ComboBox { Width = 130, ItemsSource = Capabilities.Names.ToList(), SelectedIndex = 0 };
        var target = new TextBox { PlaceholderText = "a path or a URL, for the file and web capabilities" };
        var check = new Button { Content = "What would the policy say?", HorizontalAlignment = HorizontalAlignment.Left };
        Grid.SetRow(check, 1);
        Grid.SetColumnSpan(check, 2);
        check.Click += (_, _) =>
        {
            var cap = capability.SelectedItem as string ?? Capabilities.Name(Capability.ModelCall);
            var where = target.Text?.Trim() ?? string.Empty;
            var args = where.Length == 0 ? new[] { "policy", "check", cap } : new[] { "policy", "check", cap, where };
            var (text, code) = Chat.Capture(false, args);
            _tryAnswer.Text = code == 0 ? $"Allowed. {text}" : SafeText.Block(text);
            _tryAnswer.Foreground = code == 0 ? Palette.Accent : Palette.Warning;
        };
        Grid.SetColumn(target, 1);
        line.Children.Add(capability);
        line.Children.Add(target);
        line.Children.Add(check);
        _tryPanel.Children.Add(line);
        _tryPanel.Children.Add(_tryAnswer);
    }
}
