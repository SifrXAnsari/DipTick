using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DipTick.Models;
using DipTick.ViewModels;

namespace DipTick.Views.Desktop;

/// <summary>
/// "What they're told", a sheet beside the chat: this chat's own brief, which the person can change,
/// then for each agent at the chat's table exactly the text it is sent before the chat, and about how
/// many tokens that is. Seen, not hidden; it calls nothing (<c>diptick chat brief</c>).
/// </summary>
internal sealed class ToldPage : Grid
{
    private readonly Func<ChatViewModel> _model;
    private readonly Func<string, Task> _copy;
    private readonly Action<Func<Task>> _offThread;
    private readonly StackPanel _agents = new() { Spacing = 16 };
    private readonly TextBox _brief = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MaxHeight = 150,
        MinHeight = 60,
        FontSize = 13,
        PlaceholderText = "What every agent at this table should know, for this chat only (at most 2000 characters)",
    };

    private readonly TextBlock _note = new() { Classes = { "dim" }, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly ScrollViewer _scroll;
    private readonly List<string> _headings = [];

    /// <param name="model">The chat in front.</param>
    /// <param name="copy">Puts text on the clipboard.</param>
    /// <param name="offThread">Runs work off the screen's thread, its failure said.</param>
    public ToldPage(Func<ChatViewModel> model, Func<string, Task> copy, Action<Func<Task>> offThread)
    {
        _model = model;
        _copy = copy;
        _offThread = offThread;
        RowDefinitions = new RowDefinitions("Auto,*");
        Margin = new Thickness(16, 4, 16, 12);

        var top = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 14) };
        top.Children.Add(new TextBlock { Text = "THIS CHAT'S BRIEF", FontSize = 11, LetterSpacing = 1.4, Classes = { "dim" } });
        top.Children.Add(_brief);
        var save = new Button { Content = "Save the brief", Classes = { "primary" } };
        ToolTip.SetTip(save, "Every agent at this table is told it from the next round (diptick chat brief --set)");
        save.Click += (_, _) => Save();
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
        line.Children.Add(save);
        SetColumn(_note, 1);
        _note.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(_note);
        top.Children.Add(line);
        Children.Add(top);

        _scroll = new ScrollViewer { Content = _agents };
        SetRow(_scroll, 1);
        Children.Add(_scroll);
    }

    /// <summary>What each agent is told now, and the chat's brief; with <paramref name="seat"/>, that agent's text in view.</summary>
    public void Reload(string? seat = null)
    {
        var model = _model();
        var (agents, brief, problem) = model.Told();
        _brief.Text = brief;
        _note.Text = model.SessionId is null
            ? "Saving it keeps this chat from now, with its table."
            : "Every agent here reads it from the next round, under your name.";
        _agents.Children.Clear();
        _headings.Clear();
        if (problem is not null)
        {
            _agents.Children.Add(new TextBlock { Text = problem, Foreground = Palette.Warning, TextWrapping = TextWrapping.Wrap });
            return;
        }

        if (agents.Count == 0)
        {
            _agents.Children.Add(new TextBlock { Text = "Nobody sits at this chat's table, so nobody is told anything.", Classes = { "dim" }, TextWrapping = TextWrapping.Wrap });
            return;
        }

        Control? wanted = null;
        foreach (var agent in agents)
        {
            var heading = string.Create(CultureInfo.InvariantCulture, $"{agent.Seat} is told · about {agent.Tokens:N0} tokens");
            _headings.Add(heading);
            var column = new StackPanel { Spacing = 6 };
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var color = model.Table.FirstOrDefault(s => s.Name == agent.Seat)?.Color ?? "white";
            head.Children.Add(new TextBlock { Text = heading, Foreground = Palette.Seat(color), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var copy = new Button { Content = "Copy", Classes = { "tool" } };
            ToolTip.SetTip(copy, $"Copy what {agent.Seat} is told");
            var text = agent.Text;
            copy.Click += (_, _) => _ = _copy(text);
            SetColumn(copy, 1);
            head.Children.Add(copy);
            column.Children.Add(head);
            column.Children.Add(new SelectableTextBlock
            {
                Text = SafeText.Block(agent.Text),
                FontFamily = Palette.Mono,
                FontSize = 12.5,
                LineSpacing = 3,
                TextWrapping = TextWrapping.Wrap,
                SelectionBrush = Palette.Selection,
                ContextFlyout = null,
            });
            _agents.Children.Add(column);
            if (agent.Seat == seat)
            {
                wanted = column;
            }
        }

        if (wanted is not null)
        {
            Dispatcher.UIThread.Post(() => wanted.BringIntoView(), DispatcherPriority.Loaded);
        }
    }

    private void Save()
    {
        var text = (_brief.Text ?? string.Empty).ReplaceLineEndings("\n").Trim();
        if (text.Length > 2000)
        {
            _note.Text = string.Create(CultureInfo.InvariantCulture, $"That is {text.Length:N0} characters; a chat's brief is at most 2,000.");
            return;
        }

        var model = _model();
        _offThread(async () =>
        {
            await model.SetChatBriefAsync(text).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => Reload());
        });
    }
}
