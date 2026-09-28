using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DipTick.Models;
using DipTick.Models.Harness;
using DipTick.ViewModels;

namespace DipTick.Views.Desktop;

/// <summary>
/// Activity, as a sheet beside the chat: the decisions the harness made, the chain check, doctor's
/// checks and where each key comes from. Read-only. F2 filters the decisions by who, what and the
/// verdict; F4 sets their range (today, the last 7 days, everything); F7 exports them
/// (<c>diptick audit export</c>); F5 reads everything again, F8 checks the chain now, F3 finds a word.
/// </summary>
internal sealed class ActivityPage : Grid
{
    private const int Decisions = 0;

    private static readonly (string Title, string[] Verb)[] Sections =
    [
        ("Decisions", ["audit"]),
        ("The chain", ["audit", "verify"]),
        ("Checks", ["doctor"]),
        ("Keys", ["key", "status"]),
    ];

    /// <summary>The ranges F4 steps through, in order, with what the decisions' title says of each.</summary>
    private static readonly (string Name, string Title)[] Ranges =
    [
        ("week", "the last 7 days"),
        ("everything", "everything"),
        ("today", "today"),
    ];

    private static readonly string[] Verdicts = ["any verdict", .. Enum.GetNames<Verdict>()];

    private readonly Func<ChatViewModel> _front;
    private readonly TextBox _find = new() { PlaceholderText = "find in the decisions and checks", IsVisible = false, Margin = new Thickness(0, 0, 0, 10), FontSize = 13, Padding = new Thickness(8, 4) };
    private readonly TextBox _who = new() { PlaceholderText = "who: a name, or agent:<seat>", MinWidth = 150, FontSize = 13, Padding = new Thickness(8, 4) };
    private readonly ComboBox _what = new() { ItemsSource = new[] { "any capability" }.Concat(Capabilities.Names).ToList(), SelectedIndex = 0, MinWidth = 130 };
    private readonly ComboBox _verdict = new() { ItemsSource = Verdicts, SelectedIndex = 0, MinWidth = 110 };
    private readonly WrapPanel _filters = new() { IsVisible = false, Margin = new Thickness(0, 0, 0, 10), ItemSpacing = 8, LineSpacing = 6 };
    private readonly TextBlock _found = new() { Classes = { "dim" }, FontSize = 12, IsVisible = false, Margin = new Thickness(2, 0, 0, 8) };
    private readonly TextBlock _note = new() { Classes = { "dim" }, FontSize = 12, IsVisible = false, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 8) };
    private readonly StackPanel _column = new() { Spacing = 14 };
    private readonly List<SelectableTextBlock> _bodies = [];
    private readonly List<Border> _cards = [];
    private readonly List<TextBlock> _titles = [];
    private readonly ScrollViewer _scroll;
    private string[] _texts = [.. Sections.Select(_ => string.Empty)];
    private int _range;

    public ActivityPage(Func<ChatViewModel> front)
    {
        _front = front;
        RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*");
        Margin = new Thickness(16, 4, 16, 12);
        _filters.Children.Add(_who);
        _filters.Children.Add(_what);
        _filters.Children.Add(_verdict);
        Children.Add(_filters);
        SetRow(_find, 1);
        Children.Add(_find);
        SetRow(_found, 2);
        Children.Add(_found);
        SetRow(_note, 3);
        Children.Add(_note);
        _find.TextChanged += (_, _) => ShowFound();
        _who.LostFocus += (_, _) => Reload();
        _who.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                Reload();
                e.Handled = true;
            }
        };
        _what.SelectionChanged += (_, _) => Reload();
        _verdict.SelectionChanged += (_, _) => Reload();

        // Colours from the styles (TextBlock.dim, SelectableTextBlock), which follow the theme as it changes.
        _column.Children.Add(new TextBlock { Text = "Every decision the harness made, the audit chain, doctor's checks, and where each key comes from. Nothing here changes anything.", Classes = { "dim" }, TextWrapping = TextWrapping.Wrap });
        foreach (var (title, _) in Sections)
        {
            var card = new StackPanel { Spacing = 6 };
            var head = new TextBlock { Text = title.ToUpperInvariant(), FontSize = 12, LetterSpacing = 1.2, Classes = { "dim" } };
            _titles.Add(head);
            card.Children.Add(head);
            var body = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = Palette.Mono, FontSize = 13, Text = "…" };
            _bodies.Add(body);
            var border = new Border { Classes = { "card" }, Child = body };
            _cards.Add(border);
            card.Children.Add(border);
            _column.Children.Add(card);
        }

        _scroll = new ScrollViewer { Content = _column };
        SetRow(_scroll, 4);
        Children.Add(_scroll);
        DrawTitle();
    }

    /// <summary>Whether the find box is open.</summary>
    public bool FindOpen => _find.IsVisible;

    /// <summary>F5: runs the verbs off the UI thread and shows what they printed.</summary>
    public void Reload()
    {
        DrawTitle();
        var verbs = Sections.Select((s, i) => i == Decisions ? [.. s.Verb, .. Chosen()] : s.Verb).ToArray();
        _ = Task.Run(() =>
        {
            var texts = verbs.Select(v => _front().Capture(false, v).Text).ToArray();
            Dispatcher.UIThread.Post(() =>
            {
                _texts = texts;
                for (var i = 0; i < texts.Length; i++)
                {
                    _bodies[i].Text = SafeText.Block(texts[i].Length == 0 ? "(nothing)" : texts[i]);
                }

                ShowFound();
            });
        });
    }

    /// <summary>F8: checks the audit log's chain now (<c>diptick audit verify</c>) and shows what it found.</summary>
    public void Verify()
    {
        var chain = Array.FindIndex(Sections, s => s.Verb.SequenceEqual(["audit", "verify"]));
        _bodies[chain].Text = "checking…";
        _cards[chain].BringIntoView();
        _ = Task.Run(() =>
        {
            var text = _front().Capture(false, "audit", "verify").Text;
            Dispatcher.UIThread.Post(() =>
            {
                _texts[chain] = text;
                _bodies[chain].Text = SafeText.Block(text.Length == 0 ? "(nothing)" : text);
                ShowFound();
            });
        });
    }

    /// <summary>F3, Ctrl+F: the find box; again, it closes.</summary>
    public void ToggleFind()
    {
        if (FindOpen)
        {
            _find.IsVisible = false;
            _find.Text = string.Empty;
            ShowFound();
            return;
        }

        _find.IsVisible = true;
        _find.Focus();
    }

    /// <summary>F2: who, what and the verdict, for the decisions; again, the filters close and are cleared.</summary>
    public void ToggleFilter()
    {
        if (_filters.IsVisible)
        {
            _filters.IsVisible = false;
            _who.Text = string.Empty;
            _what.SelectedIndex = 0;
            _verdict.SelectedIndex = 0;
            Reload();
            return;
        }

        _filters.IsVisible = true;
        _who.Focus();
    }

    /// <summary>F4: the next range: the last 7 days, everything, today.</summary>
    public void NextRange()
    {
        _range = (_range + 1) % Ranges.Length;
        Reload();
    }

    /// <summary>
    /// F7: the decisions shown, to a file the person picks, as JSON Lines, CSV or text by the kind
    /// chosen; <c>diptick audit export</c> writes it and says whether the chain verified.
    /// </summary>
    public async Task ExportAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage)
        {
            return;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export the decisions shown, then whether the audit chain verified",
            SuggestedFileName = string.Create(CultureInfo.InvariantCulture, $"DipTick audit {DateTime.Now:yyyy-MM-dd}"),
            DefaultExtension = "jsonl",
            FileTypeChoices =
            [
                new FilePickerFileType("JSON Lines") { Patterns = ["*.jsonl"] },
                new FilePickerFileType("CSV") { Patterns = ["*.csv"] },
                new FilePickerFileType("Text") { Patterns = ["*.txt"] },
            ],
            ShowOverwritePrompt = false,
        }).ConfigureAwait(true);
        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        var format = Path.GetExtension(path).ToUpperInvariant() switch { ".CSV" => "csv", ".TXT" => "text", _ => "jsonl" };
        string[] verb = ["audit", "export", path, "--format", format, .. Chosen().Where(w => w != "--all")];
        var said = await Task.Run(() => _front().Capture(false, verb)).ConfigureAwait(true);
        _note.Text = SafeText.Line(said.Text);
        _note.IsVisible = said.Text.Length > 0;
    }

    /// <summary>The range and the filters as <c>diptick audit</c>'s words.</summary>
    private List<string> Chosen()
    {
        var words = Ranges[_range].Name switch
        {
            "today" => new List<string> { "--since", "today" },
            "week" => ["--since", DateTimeOffset.Now.AddDays(-7).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture)],
            _ => ["--all"],
        };

        if ((_who.Text ?? string.Empty).Trim() is { Length: > 0 } who)
        {
            words.AddRange(["--who", who]);
        }

        if (_what.SelectedIndex > 0 && _what.SelectedItem is string what)
        {
            words.AddRange(["--what", what]);
        }

        if (_verdict.SelectedIndex > 0 && _verdict.SelectedItem is string verdict)
        {
            words.AddRange(["--verdict", verdict]);
        }

        return words;
    }

    private void DrawTitle()
    {
        var filtered = _filters.IsVisible && ((_who.Text ?? string.Empty).Trim().Length > 0 || _what.SelectedIndex > 0 || _verdict.SelectedIndex > 0);
        _titles[Decisions].Text = $"DECISIONS · {Ranges[_range].Title.ToUpperInvariant()}{(filtered ? " · FILTERED" : string.Empty)}";
    }

    /// <summary>
    /// With words in the find box, each section shows only its lines that hold them, and the count
    /// says how many; without, every line.
    /// </summary>
    private void ShowFound()
    {
        var words = FindOpen ? (_find.Text ?? string.Empty).Trim() : string.Empty;
        var total = 0;
        for (var i = 0; i < _bodies.Count; i++)
        {
            var text = _texts[i];
            if (words.Length == 0)
            {
                _bodies[i].Text = SafeText.Block(text.Length == 0 ? "(nothing)" : text);
                continue;
            }

            var lines = text.ReplaceLineEndings("\n").Split('\n').Where(l => l.Contains(words, StringComparison.OrdinalIgnoreCase)).ToList();
            total += lines.Count;
            _bodies[i].Text = lines.Count == 0 ? "(nothing found)" : SafeText.Block(string.Join('\n', lines));
        }

        _found.IsVisible = words.Length > 0;
        _found.Text = total == 1 ? "1 line found" : $"{total.ToString(CultureInfo.InvariantCulture)} lines found";
    }
}
