using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DipTick.ViewModels;

namespace DipTick.Views.Desktop;

/// <summary>What the Tabs sheet can do beyond going to a tab: the window's own actions, each also a key.</summary>
internal sealed record TabActions(
    Action<AppWindow, ChatViewModel> GoTo,
    Action<AppWindow, ChatViewModel> Close,
    Action<AppWindow, ChatViewModel> MoveToNewWindow,
    Action<bool> NewTab,
    Action NewWindow,
    Action Reopen);

/// <summary>
/// F2 Tabs, as a sheet beside the chat, so the tabs are one F-key away: every tab in every DipTick
/// window and what each is doing, this window's first; a find box; one click goes to a tab, and its
/// ✕ closes it. Below them, New tab, New private tab, New window, Reopen closed tab and Move this tab
/// to a new window, each with its key.
/// </summary>
internal sealed class TabsPage : Grid
{
    private readonly AppModel _app;
    private readonly AppWindow _window;
    private readonly TabActions _actions;
    private readonly TextBox _find = new() { PlaceholderText = "find a tab: its title", IsVisible = false, Margin = new Thickness(0, 0, 0, 10), FontSize = 13, Padding = new Thickness(8, 4) };
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly List<string> _rowTexts = [];
    private readonly List<string> _headings = [];

    /// <summary>What the rows were last built from.</summary>
    private string? _drawnFrom;

    public TabsPage(AppModel app, AppWindow window, TabActions actions)
    {
        _app = app;
        _window = window;
        _actions = actions;
        RowDefinitions = new RowDefinitions("Auto,*,Auto");
        Margin = new Thickness(16, 4, 16, 12);
        Children.Add(_find);
        _find.TextChanged += (_, _) => Reload();

        var scroll = new ScrollViewer { Content = _rows };
        SetRow(scroll, 1);
        Children.Add(scroll);

        var buttons = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(Action("New tab", "Ctrl+T", () => _actions.NewTab(false)));
        buttons.Children.Add(Action("New private tab", "Ctrl+Shift+N", () => _actions.NewTab(true)));
        buttons.Children.Add(Action("New window", "Ctrl+N", _actions.NewWindow));
        buttons.Children.Add(Action("Reopen closed tab", "Ctrl+Shift+T", _actions.Reopen));
        buttons.Children.Add(Action("Move this tab to a new window", string.Empty, () => _actions.MoveToNewWindow(_window, _window.CurrentTab)));
        SetRow(buttons, 2);
        Children.Add(buttons);
    }

    /// <summary>Whether the find box is open.</summary>
    public bool FindOpen => _find.IsVisible;

    /// <summary>F3: the find box; again, it closes and every tab shows.</summary>
    public void ToggleFind()
    {
        if (FindOpen)
        {
            _find.IsVisible = false;
            _find.Text = string.Empty;
            Reload();
            return;
        }

        _find.IsVisible = true;
        _find.Focus();
    }

    /// <summary>
    /// The tabs as they stand: this window's first, then each other window's, with what each is doing.
    /// Built again only when that changed, so a click on a row is never lost to an answer streaming.
    /// </summary>
    public void Reload()
    {
        var words = (_find.Text ?? string.Empty).Trim();
        var windows = _app.Windows.OrderBy(w => ReferenceEquals(w, _window) ? 0 : 1).ThenBy(w => w.Number).ToList();
        var from = string.Join('\u001f', windows.Select(w => string.Create(CultureInfo.InvariantCulture, $"{w.Number}:{w.Current}:{string.Join(',', w.Tabs.Select(t => $"{Identity.Of(t)}{t.Busy}{t.Waiting}{t.WaitingForYes}{t.IsPrivate}{Title(t)}"))}"))) + "\u001f" + words;
        if (from == _drawnFrom)
        {
            return;
        }

        _drawnFrom = from;
        _rows.Children.Clear();
        _rowTexts.Clear();
        _headings.Clear();
        var shown = 0;
        foreach (var window in windows)
        {
            var tabs = window.Tabs.Where(t => words.Length == 0 || Title(t).Contains(words, StringComparison.OrdinalIgnoreCase)).ToList();
            if (tabs.Count == 0)
            {
                continue;
            }

            var heading = ReferenceEquals(window, _window) ? "This window" : string.Create(CultureInfo.InvariantCulture, $"Window {window.Number}");
            _headings.Add(heading);
            _rows.Children.Add(new TextBlock { Text = heading.ToUpperInvariant(), FontSize = 11, LetterSpacing = 1.4, Classes = { "dim" }, Margin = new Thickness(10, shown == 0 ? 0 : 12, 0, 4) });
            foreach (var tab in tabs)
            {
                _rows.Children.Add(Row(window, tab));
                shown++;
            }
        }

        if (shown == 0)
        {
            _rows.Children.Add(new TextBlock { Text = words.Length == 0 ? "No tab is open." : $"No tab's title holds '{words}'.", Classes = { "dim" }, Margin = new Thickness(10, 0, 0, 0) });
        }
    }

    /// <summary>What a tab is doing, in words: answering, waiting for a yes, private, or quiet.</summary>
    internal static string Doing(ChatViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var parts = new List<string>();
        if (tab.Busy)
        {
            parts.Add("answering");
        }

        if (tab.Waiting)
        {
            parts.Add(WaitingWords(tab));
        }

        if (tab.IsPrivate)
        {
            parts.Add("nothing kept");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>What a tab with a question waiting says: "needs your yes" for an approval, "waiting for you" for any other question.</summary>
    internal static string WaitingWords(ChatViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        return tab.WaitingForYes ? "needs your yes" : "waiting for you";
    }

    /// <summary>A tab's name as the strip and this list show it: "Private: " before a private chat's.</summary>
    internal static string Title(ChatViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        return tab.IsPrivate ? "Private: " + tab.SessionTitle : tab.SessionTitle;
    }

    private Button Row(AppWindow window, ChatViewModel tab)
    {
        var front = ReferenceEquals(window, _window) && ReferenceEquals(tab, window.CurrentTab);
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("16,*,Auto,Auto"), ColumnSpacing = 8 };
        line.Children.Add(new TextBlock { Text = tab.Busy ? "●" : string.Empty, Foreground = Palette.Accent, VerticalAlignment = VerticalAlignment.Center });
        var title = new TextBlock { Text = Title(tab), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontWeight = front ? FontWeight.SemiBold : FontWeight.Normal };
        SetColumn(title, 1);
        line.Children.Add(title);
        var doing = Doing(tab);
        var status = new TextBlock { Text = doing, Foreground = tab.Waiting ? Palette.Warning : Palette.Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        SetColumn(status, 2);
        line.Children.Add(status);
        var close = new Button { Content = "✕", Classes = { "tool" }, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(close, tab.IsPrivate ? "Close this tab; its private chat goes for good" : "Close this tab; its chat is kept (Ctrl+Shift+T brings it back)");
        close.Click += (_, e) =>
        {
            e.Handled = true;
            _actions.Close(window, tab);
            Reload();
        };
        SetColumn(close, 3);
        line.Children.Add(close);

        var row = new Button { Classes = { "row" }, Content = line };
        if (front)
        {
            row.Classes.Add("current");
        }

        ToolTip.SetTip(row, front ? "The tab you are on" : "Go to this tab");
        row.Click += (_, _) => _actions.GoTo(window, tab);
        row.ContextMenu = Menus.Of(
            Menus.Item("Go to it", () => _actions.GoTo(window, tab)),
            Menus.Item("Move to a new window", () => _actions.MoveToNewWindow(window, tab)),
            Menus.Line(),
            Menus.Item("Close", () => _actions.Close(window, tab)));
        _rowTexts.Add(doing.Length == 0 ? Title(tab) : $"{Title(tab)} · {doing}");
        return row;
    }

    private static Button Action(string text, string key, Action click)
    {
        var button = new Button { Content = key.Length == 0 ? text : $"{text}  {key}" };
        button.Click += (_, _) => click();
        return button;
    }
}
