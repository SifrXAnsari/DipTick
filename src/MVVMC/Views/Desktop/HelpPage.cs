using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DipTick.Controllers;
using DipTick.Models;
using DipTick.ViewModels;

namespace DipTick.Views.Desktop;

/// <summary>
/// Help, as a sheet beside the chat: DipTick's official pages (<see cref="Docs"/>), the same
/// <c>diptick docs</c> prints and the terminal screen shows. The pages down the left, the page on the
/// right, a search box on top. A link between pages opens the page; a link outside DipTick says
/// where it goes and opens in the browser. F1 Contents, F2 Back, F3 Search, F4 Pages, F7 Save as
/// files. While words are in the search box, the list takes half the sheet and shows each line
/// found under its page's title, cut around the words.
/// </summary>
internal sealed class HelpPage : Grid
{
    /// <summary>The list's width while it holds the pages: the longest title wraps once at most.</summary>
    private const double ListWidth = 200;

    /// <summary>About how many characters of a found line a search result shows.</summary>
    private const int SnippetLength = 140;

    private readonly Func<ChatViewModel> _front;
    private readonly Func<string, Task> _copy;
    private readonly TextBox _search = new() { PlaceholderText = "search every page", Margin = new Thickness(0, 0, 0, 8), FontSize = 13, Padding = new Thickness(8, 4) };
    private readonly ListBox _list = new() { Margin = new Thickness(0, 0, 10, 0) };
    private readonly ScrollViewer _body = new();
    private readonly TextBlock _note = new() { Classes = { "dim" }, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(0, 0, 0, 6) };
    private readonly Stack<string> _back = new();
    private IReadOnlyList<DocHit> _hits = [];
    private DocPage _page;
    private bool _listing;

    public HelpPage(Func<ChatViewModel> front, Func<string, Task> copy)
    {
        _front = front;
        _copy = copy;
        _page = Docs.Find(Docs.Start) ?? Docs.Pages[0];
        ColumnDefinitions = [new ColumnDefinition(ListWidth, GridUnitType.Pixel), new ColumnDefinition(1, GridUnitType.Star)];
        RowDefinitions = new RowDefinitions("Auto,Auto,*");
        Margin = new Thickness(16, 4, 16, 12);
        SetColumnSpan(_search, 2);
        Children.Add(_search);
        SetRow(_note, 1);
        SetColumnSpan(_note, 2);
        Children.Add(_note);
        SetRow(_list, 2);
        Children.Add(_list);
        SetRow(_body, 2);
        SetColumn(_body, 1);
        Children.Add(_body);

        _search.TextChanged += (_, _) => ShowList();

        // Seen before the text box's own handling, which takes ↓ for its caret.
        _search.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && _hits.Count > 0)
            {
                // The first line found: its page opens, and the list marks it.
                _list.SelectedIndex = 0;
                e.Handled = true;
            }
            else if (e.Key == Key.Down && _list.ItemCount > 0)
            {
                _list.SelectedIndex = Math.Max(0, _list.SelectedIndex);
                FocusList();
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _list.SelectionChanged += (_, _) =>
        {
            if (_listing || _list.SelectedIndex < 0)
            {
                return;
            }

            var id = _hits.Count > 0 && _list.SelectedIndex < _hits.Count ? _hits[_list.SelectedIndex].Page.Id : Docs.Pages[_list.SelectedIndex].Id;
            if (id != _page.Id)
            {
                Open(id);
            }
        };

        ShowList();
        Draw();
    }

    /// <summary>Opens a page by its id; the page it leaves is kept for Back.</summary>
    public void Open(string id)
    {
        if (Docs.Find(id) is not { } page || page.Id == _page.Id)
        {
            return;
        }

        _back.Push(_page.Id);
        _page = page;
        _note.IsVisible = false;
        Select();
        Draw();
    }

    /// <summary>F1: the first page, and the pages' list to choose from.</summary>
    public void Contents()
    {
        _search.Text = string.Empty;
        Open(Docs.Start);
        FocusList();
    }

    /// <summary>F2: the page before this one.</summary>
    public void Back()
    {
        if (_back.TryPop(out var id) && Docs.Find(id) is { } page)
        {
            _page = page;
            Select();
            Draw();
        }
    }

    /// <summary>F3, Ctrl+F: the search box.</summary>
    public void FocusSearch()
    {
        _search.Focus();
        _search.SelectAll();
    }

    /// <summary>F4: the pages' list, with any search cleared.</summary>
    public void FocusPages()
    {
        _search.Text = string.Empty;
        FocusList();
    }

    /// <summary>The page drawn again in the theme in force.</summary>
    public void Redraw() => Draw();

    /// <summary>
    /// The keyboard to the list's marked line, so ↑/↓ move along it. A list box takes the keyboard
    /// through its lines, not itself; a list just filled has its lines only after the next layout.
    /// </summary>
    private void FocusList()
    {
        _list.UpdateLayout();
        if (_list.ContainerFromIndex(Math.Max(0, _list.SelectedIndex)) is { } line)
        {
            _ = line.Focus(NavigationMethod.Directional);
        }
    }

    /// <summary>F7: every page as a Markdown file in a folder the person picks (<c>diptick docs export</c>).</summary>
    public async Task SaveAsFilesAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage)
        {
            return;
        }

        var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Save DipTick's pages as Markdown files, in this folder", AllowMultiple = false }).ConfigureAwait(true);
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } folder)
        {
            return;
        }

        var said = await Task.Run(() => _front().Capture(false, "docs", "export", folder)).ConfigureAwait(true);
        _note.Text = SafeText.Line(said.Text);
        _note.IsVisible = said.Text.Length > 0;
    }

    /// <summary>A link: another page opens here; an https address opens in the browser.</summary>
    private void Follow(string target)
    {
        if (!target.StartsWith("https://", StringComparison.Ordinal))
        {
            Open(target);
            return;
        }

        try
        {
            using var browser = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _note.Text = $"The browser could not be opened for {target}: {ex.Message}";
            _note.IsVisible = true;
        }
    }

    /// <summary>The list: the pages, or, with words in the search box, the lines that hold them, each under its page's title.</summary>
    private void ShowList()
    {
        var words = (_search.Text ?? string.Empty).Trim();
        _hits = words.Length == 0 ? [] : Docs.Search(words);
        _listing = true;
        _list.Items.Clear();

        // A found line needs room to be read: half the sheet while searching, the titles' width otherwise.
        ColumnDefinitions[0].Width = words.Length == 0 ? new GridLength(ListWidth, GridUnitType.Pixel) : new GridLength(1, GridUnitType.Star);
        if (words.Length == 0)
        {
            foreach (var page in Docs.Pages)
            {
                _list.Items.Add(new ListBoxItem { Content = new TextBlock { Text = page.Title, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, Tag = page.Title });
            }
        }
        else
        {
            foreach (var hit in _hits.Take(200))
            {
                var line = Docs.Snippet(hit.Text, words, SnippetLength);
                var item = new StackPanel { Spacing = 2 };
                item.Children.Add(new TextBlock { Text = hit.Page.Title, Classes = { "dim" }, FontSize = 12 });
                item.Children.Add(new TextBlock { Text = line, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 13 });
                _list.Items.Add(new ListBoxItem { Content = item, Tag = $"{hit.Page.Title}: {line}" });
            }

            if (_hits.Count == 0)
            {
                _list.Items.Add(new ListBoxItem { Content = "(nothing found)", Tag = "(nothing found)", IsEnabled = false });
            }
        }

        _listing = false;
        Select();
    }

    private void Select()
    {
        // While words are in the search box, the list holds what they found, not the pages.
        if ((_search.Text ?? string.Empty).Trim().Length > 0)
        {
            return;
        }

        _listing = true;
        _list.SelectedIndex = Docs.Pages.ToList().FindIndex(p => p.Id == _page.Id);
        _listing = false;
    }

    private void Draw()
    {
        _body.Content = Markdown.Render(_page.Text, Palette.Look, code => _ = _copy(code), Follow);
        _body.Offset = default;
        Dispatcher.UIThread.Post(() => _body.Offset = default, DispatcherPriority.Loaded);
    }
}
