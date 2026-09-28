using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DipTick.Models;
using DipTick.Models.Tools;
using DipTick.ViewModels;

namespace DipTick.Views.Desktop;

/// <summary>
/// The Tools sheet: DipTick's own tools for the person, the same ones agents may call. The clock
/// with the other places you keep (<c>tools.zones</c>); the month, with ISO weeks and the days between two
/// dates; the network check, of every service DipTick uses or any host you type; the calculator, exact.
/// F2 Clock, F4 Calendar, F5 Check again, F7 Network.
/// </summary>
internal sealed class ToolsPage : ScrollViewer
{
    private readonly Func<ChatViewModel> _model;
    private readonly StackPanel _column = new() { Spacing = 18, Margin = new Thickness(16, 4, 16, 16) };
    private readonly StackPanel _clock = new() { Spacing = 4 };
    private readonly Border _clockCard;
    private readonly Border _calendarCard;
    private readonly Border _networkCard;
    private readonly TextBlock _week = new() { Classes = { "dim" }, FontSize = 13 };
    private readonly TextBlock _between = new() { FontSize = 13 };
    private readonly CalendarDatePicker _from = new() { SelectedDate = DateTime.Today };
    private readonly CalendarDatePicker _to = new() { SelectedDate = DateTime.Today };
    private readonly SelectableTextBlock _network = new() { FontFamily = Palette.Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap, ContextFlyout = null };
    private readonly TextBox _host = new() { PlaceholderText = "any host, like example.com, or leave it empty for every service DipTick uses", FontSize = 13 };
    private readonly TextBox _sum = new() { PlaceholderText = "17% of 2345.60", FontSize = 15, FontFamily = Palette.Mono };
    private readonly SelectableTextBlock _result = new() { FontSize = 17, FontFamily = Palette.Mono, ContextFlyout = null };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _checking;

    /// <param name="model">The chat in front: its settings, and its verbs for a change to them.</param>
    public ToolsPage(Func<ChatViewModel> model)
    {
        _model = model;
        Content = _column;

        _clockCard = Card("Clock", _clock);
        var addZone = new Button { Content = "Add a place…" };
        ToolTip.SetTip(addZone, "Another time zone beside yours (tools.zones in Settings, Tools)");
        addZone.Click += (_, _) => _ = Task.Run(AddZoneAsync);
        ((StackPanel)_clockCard.Child!).Children.Add(addZone);
        _column.Children.Add(_clockCard);

        var month = new Avalonia.Controls.Calendar { SelectionMode = CalendarSelectionMode.SingleDate, SelectedDate = DateTime.Today, DisplayDate = DateTime.Today, HorizontalAlignment = HorizontalAlignment.Left };
        month.SelectedDatesChanged += (_, _) => ShowWeek(month.SelectedDate);
        var betweenRow = new WrapPanel { ItemSpacing = 8, LineSpacing = 6 };
        betweenRow.Children.Add(new TextBlock { Text = "Days from", VerticalAlignment = VerticalAlignment.Center });
        betweenRow.Children.Add(_from);
        betweenRow.Children.Add(new TextBlock { Text = "to", VerticalAlignment = VerticalAlignment.Center });
        betweenRow.Children.Add(_to);
        betweenRow.Children.Add(_between);
        _from.SelectedDateChanged += (_, _) => ShowBetween();
        _to.SelectedDateChanged += (_, _) => ShowBetween();
        _calendarCard = Card("Calendar", month, _week, betweenRow);
        _column.Children.Add(_calendarCard);

        var check = new Button { Content = "Check", Classes = { "primary" } };
        ToolTip.SetTip(check, "Is this PC online, and can DipTick reach each service it uses (F5)");
        check.Click += (_, _) => CheckAgain();
        _host.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                CheckAgain();
                e.Handled = true;
            }
        };
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        line.Children.Add(_host);
        Grid.SetColumn(check, 1);
        line.Children.Add(check);
        _networkCard = Card("Network", line, _network);
        _column.Children.Add(_networkCard);

        _sum.TextChanged += (_, _) => ShowSum();
        _column.Children.Add(Card("Calculator", _sum, _result, new TextBlock
        {
            Text = "Exact decimal arithmetic: + − × ÷ ^ ( ), % after a number, 'of' for times, and abs, round, floor, ceil, min, max, sqrt. Nothing typed here is run as code.",
            Classes = { "dim" },
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        }));

        _tick.Tick += (_, _) => ShowClock();
        AttachedToVisualTree += (_, _) => _tick.Start();
        DetachedFromVisualTree += (_, _) => _tick.Stop();
        ShowWeek(DateTime.Today);
        ShowBetween();
    }

    /// <summary>Everything read again: the zones from the settings, the clock now.</summary>
    public void Reload() => ShowClock();

    /// <summary>F2: the clock, in view.</summary>
    public void ShowClockCard() => _clockCard.BringIntoView();

    /// <summary>F4: the calendar, in view.</summary>
    public void ShowCalendarCard() => _calendarCard.BringIntoView();

    /// <summary>F7: the network check, in view, the host box ready.</summary>
    public void ShowNetworkCard()
    {
        _networkCard.BringIntoView();
        _host.Focus();
    }

    /// <summary>F5: the network check again, of the host typed, or of every service DipTick uses.</summary>
    public void CheckAgain()
    {
        if (_checking)
        {
            return;
        }

        _checking = true;
        _network.Text = "Checking…";
        var typed = (_host.Text ?? string.Empty).Trim();
        var config = _model().Config;
        _ = Task.Run(async () =>
        {
            var lines = new List<string>();
            try
            {
                List<KnownHost> targets;
                if (typed.Length > 0)
                {
                    targets = Uri.TryCreate(typed.Contains("://", StringComparison.Ordinal) ? typed : "https://" + typed, UriKind.Absolute, out var uri) && uri.IdnHost.Length > 0
                        ? [new KnownHost(uri.IdnHost, uri.Port, uri.Scheme == Uri.UriSchemeHttps, "you asked")]
                        : [];
                    if (targets.Count == 0)
                    {
                        lines.Add($"Not a host or an address: {SafeText.Line(typed)}");
                    }
                }
                else
                {
                    var (online, interfaces) = NetworkCheck.Here();
                    lines.Add(online ? $"This PC is online, through {string.Join(", ", interfaces)}." : "Windows says no network is up on this PC.");
                    targets = [.. NetworkCheck.Known(config)];
                    if (targets.Count == 0)
                    {
                        lines.Add("DipTick talks to no service yet: no model is set up.");
                    }
                }

                // One at a time: a check, never a scan.
                foreach (var target in targets)
                {
                    var result = await NetworkCheck.ProbeAsync(target.Host, target.Port, target.Https, target.Why, CancellationToken.None).ConfigureAwait(false);
                    lines.Add(result.Words);
                    var sofar = string.Join("\n\n", lines);
                    Dispatcher.UIThread.Post(() => _network.Text = sofar);
                }
            }
            finally
            {
                var all = string.Join("\n\n", lines);
                Dispatcher.UIThread.Post(() =>
                {
                    _network.Text = all;
                    _checking = false;
                });
            }
        });
    }

    private void ShowClock()
    {
        var now = DateTimeOffset.UtcNow;
        var config = _model().Config;
        var lines = new List<(string Text, bool Big)>();
        if (ClockReading.Read(null, now) is { } here)
        {
            lines.Add((here.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture), true));
            lines.Add(($"{here.Now.ToString("D", CultureInfo.CurrentCulture)} · {here.ZoneName} (UTC{here.Offset})", false));
        }

        foreach (var zone in config.ToolZones)
        {
            if (ClockReading.Read(zone, now) is { } there)
            {
                lines.Add(($"{zone}: {there.Now.ToString("HH:mm ddd", CultureInfo.CurrentCulture)} · UTC{there.Offset}", false));
            }
        }

        // Rebuilt only when a line changes, so the button below the lines never moves under a click.
        var texts = _clock.Children.OfType<TextBlock>().ToList();
        if (texts.Count != lines.Count)
        {
            foreach (var old in texts)
            {
                _clock.Children.Remove(old);
            }

            for (var i = 0; i < lines.Count; i++)
            {
                _clock.Children.Insert(i, new TextBlock { FontSize = lines[i].Big ? 30 : 13, FontFamily = lines[i].Big ? Palette.Mono : FontFamily.Default, Foreground = lines[i].Big ? Palette.Ink : Palette.Dim });
            }

            texts = [.. _clock.Children.OfType<TextBlock>()];
        }

        for (var i = 0; i < lines.Count; i++)
        {
            texts[i].Text = lines[i].Text;
        }
    }

    private void ShowWeek(DateTime? day)
    {
        if (day is not { } d)
        {
            _week.Text = string.Empty;
            return;
        }

        _week.Text = CalendarMath.Describe(DateOnly.FromDateTime(d));
    }

    private void ShowBetween()
    {
        if (_from.SelectedDate is not { } from || _to.SelectedDate is not { } to)
        {
            _between.Text = string.Empty;
            return;
        }

        var days = CalendarMath.Between(DateOnly.FromDateTime(from), DateOnly.FromDateTime(to));
        _between.Text = string.Create(CultureInfo.InvariantCulture, $"= {days} days");
    }

    private void ShowSum()
    {
        var text = (_sum.Text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            _result.Text = string.Empty;
            return;
        }

        var result = Calculator.Evaluate(text);
        _result.Text = result.Problem is { } problem ? problem : result.Exact ? $"= {result.Text}" : $"≈ {result.Text}";
        _result.Foreground = result.Problem is null ? Palette.Ink : Palette.Dim;
    }

    /// <summary>Another place for the clock, asked in the prompt card: a zone id this PC knows, then <c>config set tools.zones</c>.</summary>
    private async Task AddZoneAsync()
    {
        var model = _model();
        if (model.Asker is not { } asker)
        {
            return;
        }

        var zone = asker.Text(
            new Question("Tools · Clock", "Which place? A time zone id.", "IANA ids like Asia/Tokyo or Europe/Paris, or Windows ids like Tokyo Standard Time."),
            string.Empty,
            t => TimeZoneInfo.TryFindSystemTimeZoneById(t.Trim(), out _) ? null : "not a time zone this PC knows");
        if (zone.Reply != Reply.Answered)
        {
            return;
        }

        var zones = model.Config.ToolZones.Append(zone.Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase);
        model.Run(false, "config", "set", "tools.zones", string.Join(",", zones));
        model.Reload();
        await Dispatcher.UIThread.InvokeAsync(ShowClock);
    }

    private static Border Card(string title, params Control[] children)
    {
        var column = new StackPanel { Spacing = 8 };
        column.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), FontSize = 11, LetterSpacing = 1.4, Classes = { "dim" } });
        foreach (var child in children)
        {
            column.Children.Add(child);
        }

        return new Border { Classes = { "card" }, Padding = new Thickness(14, 10), Child = column };
    }
}
