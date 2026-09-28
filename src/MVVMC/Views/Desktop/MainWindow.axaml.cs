using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DipTick.Models.Chat;
using DipTick.Models.Chat.Store;
using DipTick.ViewModels;

namespace DipTick.Views.Desktop;

/// <summary>
/// One window of <c>diptick ui</c>. It draws its tabs, each a <see cref="ChatViewModel"/> of the
/// <see cref="AppModel"/>, and sends the one in front what the person does; it decides nothing
/// itself, and nothing here does more than the command line can. The F-key bar runs across the
/// top, its keys the screen's own; Settings, Activity and Help open as a sheet beside the chat and
/// close on a click outside it; Esc three times quits and stops everything. Every card, sheet and
/// list closes with its ✕ or Esc; one click does what a control says; every message, chat, model
/// and setting has a right-click menu; settings change only in Settings.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "Every control made here is added to the window, which owns it and disposes it with itself; CA2000 does not follow ownership through Children.Add.")]
internal sealed partial class MainWindow : Window
{
    private const string ChatScreen = "Chat";
    private const string SettingsScreen = "Settings";
    private const string ActivityScreen = "Activity";
    private const string HelpScreen = "Help";
    private const string TabsScreen = "Tabs";
    private const string ToldScreen = "What they're told";
    private const string ToolsScreen = "Tools";

    // Window messages (WinUser.h). WM_TIMECHANGE: "the system will broadcast this message when the
    // application changes the system time". WM_SETTINGCHANGE: a setting changed, the time format
    // among them, and the time zone ("To inform Explorer that the time zone has changed, send the
    // WM_SETTINGCHANGE message", SetTimeZoneInformation, Microsoft Learn). WM_SYSKEYUP: a system
    // key came up; F10 is one.
    private const uint WmTimeChange = 0x001E;
    private const uint WmSettingChange = 0x001A;
    private const uint WmSysKeyUp = 0x0105;
    private const int VkF10 = 0x79;

    /// <summary>A click on this sheet's button this soon after a click outside closed it is that same click, not a second one to open it.</summary>
    private const long DismissEcho = 800;

    /// <summary>The least height a question card is given, however small the window: its question, one row and its buttons.</summary>
    private const double PromptLeast = 220;

    /// <summary>What the bar says after two quick presses of Esc.</summary>
    internal const string EscapeWarning = "Esc once more quits DipTick and stops everything, the local models included.";

    private readonly AppModel _app;
    private readonly AppWindow _window;
    private readonly Dictionary<ChatViewModel, TabView> _views = [];
    private readonly FKeyBar _keyBar = new();
    private readonly TabsPage _tabsPage;
    private readonly ToldPage _toldPage;
    private readonly ToolsPage _toolsPage;
    private readonly SettingsPage _settings;
    private readonly ActivityPage _activity;
    private readonly HelpPage _helpPage;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _minute = new();
    private readonly DispatcherTimer _escReset = new();
    private CultureInfo _timeCulture = new(CultureInfo.CurrentCulture.Name, useUserOverride: true);
    private string? _sheetName;
    private string? _keysFor;
    private string? _dismissedSheet;
    private long _dismissedAt;
    private readonly EscapeCounter _escapes = new();
    private bool _escHeld;
    private bool _quitting;
    private string _appearance = string.Empty;
    private int _redrawQueued;
    private int _stripQueued;
    private bool _opened;
    private bool _closingByApp;
    private KeyModifiers _railModifiers;

    /// <summary>The rail's folded sections when nothing remembers them.</summary>
    private readonly HashSet<string> _foldedHere = new(StringComparer.Ordinal);

    /// <summary>
    /// What each part of the window with buttons in it was last drawn from. A streaming answer redraws
    /// many times a second; a part is built again only when what it shows changed, so a click in
    /// progress never loses its button to a redraw between the press and the release.
    /// </summary>
    private readonly Dictionary<string, string> _drawnFrom = new(StringComparer.Ordinal);

    /// <summary>The tab in front: its page, its question card and its draft are the window's.</summary>
    private TabView _tab;

    /// <param name="app">DipTick's windows and tabs (<see cref="AppModel"/>).</param>
    /// <param name="window">Which of them this window draws; null for the app's first.</param>
    public MainWindow(AppModel app, AppWindow? window)
    {
        ArgumentNullException.ThrowIfNull(app);
        _app = app;
        _window = window ?? app.Windows[0];
        InitializeComponent();
        KeyBarHost.Child = _keyBar;

        // The sheets follow the tab in front: its questions, its verbs.
        _settings = new SettingsPage(() => Model, CopyAsync);
        _activity = new ActivityPage(() => Model);
        _helpPage = new HelpPage(() => Model, CopyAsync);
        _tabsPage = new TabsPage(app, _window, new TabActions(GoTo, (w, t) => CloseTab(w, t), (w, t) => _app.MoveToNewWindow(w, t), NewTab, NewWindow, Reopen));
        _toldPage = new ToldPage(() => Model, CopyAsync, work => OffThread(work));
        _toolsPage = new ToolsPage(() => Model);
        SheetClose.Click += (_, _) => CloseSheet();

        // The clock beside the name opens the Tools sheet: the clock, the calendar, the network check, the calculator.
        ToolTip.SetTip(Clock, "Tools: the clock, the calendar, the network check and the calculator");
        Clock.PointerPressed += (_, _) => ToggleSheet(ToolsScreen);

        // The tab in front: its page under the sheet, which the markup already holds, and its question card.
        _tab = ViewFor(_window.CurrentTab);
        PageHost.Children.Insert(0, _tab.Scroll);
        PromptHost.Child = _tab.Prompt;
        PageHost.SizeChanged += (_, _) => SizeSheet();

        // A question card takes at most three fifths of the column, so the page keeps some room and the
        // composer and the status line stay on the window; its list scrolls inside it.
        MainColumn.SizeChanged += (_, e) => PromptHost.MaxHeight = Math.Max(PromptLeast, e.NewSize.Height * 0.6);

        _app.Changed += OnAppChanged;
        _app.TabChanged += OnTabChanged;
        _app.WindowClosed += OnWindowClosed;
        _app.ActivateRequested += OnActivateRequested;

        // How far down each of this window's tabs is scrolled, for the next run; another window answers for its own.
        var next = _app.ScrollOf;
        _app.ScrollOf = tab => ScrollFraction(tab) ?? next?.Invoke(tab) ?? 1;

        BuildRailFoot();
        WireComposer();
        RailSearch.TextChanged += (_, _) => DrawRail();
        AddHandler(KeyDownEvent, OnWindowKey, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnWindowKeyUp, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnWindowPointer, RoutingStrategies.Tunnel);
        if (Application.Current is { } app0)
        {
            app0.ActualThemeVariantChanged += OnThemeVariantChanged;
        }

        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _timer.Stop();
            _minute.Stop();
            _escReset.Stop();
            Win32Properties.RemoveWndProcHookCallback(this, OnWndProc);
            _app.Changed -= OnAppChanged;
            _app.TabChanged -= OnTabChanged;
            _app.WindowClosed -= OnWindowClosed;
            _app.ActivateRequested -= OnActivateRequested;
            foreach (var view in _views.Values)
            {
                Retire(view);
            }

            if (Application.Current is { } current)
            {
                current.ActualThemeVariantChanged -= OnThemeVariantChanged;
            }
        };
        Opened += (_, _) =>
        {
            // The clock hears Windows change the time, the zone or the format; F10 stays DipTick's.
            Win32Properties.AddWndProcHookCallback(this, OnWndProc);
            _opened = true;
            Input.Focus();
            foreach (var tab in _window.Tabs)
            {
                StartTab(ViewFor(tab));
            }
        };

        // Questions for all of DipTick (quitting) are asked in the window in front. Coming back to the window,
        // from a terminal where a connector or a skill was just added, the rail reads them again; and once a
        // minute besides. It redraws only when what it shows has changed.
        Activated += (_, _) =>
        {
            _app.Active = _window;
            DrawRail();
        };

        // Key-ups sent while another window has the keyboard never come here: a key held then is let go.
        Deactivated += (_, _) => _escHeld = false;
        _timer.Tick += (_, _) => DrawMeters();
        _timer.Start();
        _minute.Tick += (_, _) =>
        {
            DrawClock();
            DrawRail();
        };
        _escReset.Tick += (_, _) =>
        {
            _escReset.Stop();
            _escapes.Reset();
            _keyBar.Warn(null);
        };
        DrawClock();
        Redraw();
    }

    // ---- tabs ---------------------------------------------------------------------------------

    /// <summary>
    /// A tab's own parts, made the first time this window shows it: its page (the transcript in its
    /// scroller, the empty page's mark) and its question card; and its chat's hooks, set to this
    /// window, so its questions come to its own card and an answer runs on in it while another tab
    /// is in front.
    /// </summary>
    private TabView ViewFor(ChatViewModel model)
    {
        if (_views.TryGetValue(model, out var known))
        {
            return known;
        }

        // An answer's card asks its question again, of its agent or another, in its own tab's chat; it
        // lists its sources, marks a citation to none, and opens a source's page.
        // A line's files draw under it, each with Save…, Open and Show in folder; an answer and its code
        // blocks save to a file.
        var answers = new AnswerActions(
            model.UsageOf,
            () => [.. model.Table.Select(s => s.Name)],
            (line, to) => OffThread(() => model.RetryAsync(line.Seq, to)),
            model.SourcesOf,
            model.Marked,
            OpenInBrowser)
        {
            Image = model.ImageOf,
            SaveFile = file => OffThread(() => model.SaveFileAsync(file)),
            OpenFile = file => SayIfAny(model, ChatViewModel.OpenFile(file)),
            ShowFile = file => SayIfAny(model, ChatViewModel.ShowFile(file)),
            SaveWords = (line, block) => OffThread(() => model.SaveWordsAsync(line, block)),
        };
        var transcript = new TranscriptView(CopyAsync, QuoteIntoComposer, DrawSelectBar, answers) { Margin = new Thickness(20, 14), Compact = Classes.Contains("compact") };
        var hero = new Border { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 12, 0, 8) };
        var column = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(transcript, 1);
        column.Children.Add(hero);
        column.Children.Add(transcript);

        // The chat's own menu on the whole page, below the last message too; a message's own menu wins over it.
        var scroll = new ScrollViewer { Content = column, ContextMenu = ChatMenu() };
        var view = new TabView(model, transcript, column, scroll, hero, new PromptCard());
        scroll.ScrollChanged += (_, e) =>
        {
            var (follow, toEnd) = AfterScroll(view.FollowTail, e.ExtentDelta, e.ViewportDelta, scroll.Offset.Y, scroll.Extent.Height, scroll.Viewport.Height);
            view.FollowTail = follow;
            if (toEnd)
            {
                scroll.ScrollToEnd();
            }
        };
        scroll.SizeChanged += (_, _) =>
        {
            // The page's own height, so the empty page's mark sits in the middle of it and never scrolls.
            column.MinHeight = hero.IsVisible ? scroll.Bounds.Height : 0;
        };

        var asker = new DesktopAsker(view.Prompt, note =>
        {
            model.Working = note;
            QueueRedraw();
        });
        asker.TimedOut += question => _ = model.Say(Role.System, $"No answer in {DesktopAsker.ApprovalTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s, so: denied. ({question})");
        model.Asker = asker;
        model.ConfirmHook = asker.Approve;
        model.ApproveHook = asker.ApproveInChat;
        model.ClipboardHook = text => CopyForCommandAsync(text, model.IsPrivate);
        model.SettingsHook = () =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                Show(model);
                OpenSheet(SettingsScreen);
            });
            return Task.CompletedTask;
        };
        model.DocsHook = page => Dispatcher.UIThread.Post(() => ShowPage(page));
        model.SaveHook = SaveFileAsync;

        // Quitting closes every window, once.
        void OnQuit() => Dispatcher.UIThread.Post(() =>
        {
            // No flow is left waiting on a question nobody can answer now.
            view.Prompt.LeaveAll();
            if (!_quitting)
            {
                _quitting = true;
                Close();
            }
        });
        model.QuitRequested += OnQuit;
        view.Unhook = () => model.QuitRequested -= OnQuit;
        view.Prompt.Changed += () =>
        {
            // A question in a tab behind others says so on its tab, and the taskbar button flashes.
            model.Waiting = view.Prompt.Open;
            model.WaitingForYes = view.Prompt.Current?.Alert == true;
            _app.Notify(model);
            var inFront = ReferenceEquals(view, _tab);
            if (view.Prompt.Open && (!inFront || !IsActive))
            {
                Flash();
            }

            if (inFront && !view.Prompt.Open && _sheetName is null)
            {
                Input.Focus();
            }

            QueueRedraw();
        };

        _views[model] = view;
        return view;
    }

    /// <summary>A tab of this window comes in front: the app is told, and the tab shown at once.</summary>
    private void Show(ChatViewModel model)
    {
        if (!_window.Tabs.Contains(model))
        {
            return;
        }

        if (!ReferenceEquals(_window.CurrentTab, model))
        {
            _app.Select(_window, model);
        }

        ShowTab(model);
    }

    /// <summary>A tab in front: its page and its question card in their places, its draft in the composer.</summary>
    private void ShowTab(ChatViewModel model)
    {
        var view = ViewFor(model);
        if (!ReferenceEquals(view, _tab))
        {
            // Ticked messages belong to the tab they were ticked in.
            StopSelecting();
            PageHost.Children.Remove(_tab.Scroll);
            _tab = view;
            _keysFor = null;

            // The composer is the window's: it takes this tab's own draft, never the last tab's text.
            view.DraftFor = null;
        }

        if (!PageHost.Children.Contains(view.Scroll))
        {
            PageHost.Children.Insert(0, view.Scroll);
        }

        if (!ReferenceEquals(PromptHost.Child, view.Prompt))
        {
            PromptHost.Child = view.Prompt;
        }

        SizeSheet();
        StartTab(view);
        Redraw();
        if (_sheetName is not null)
        {
            return;
        }

        if (view.Prompt.Open)
        {
            // A question waiting in the tab brought in front has the keyboard, as it has when it opens.
            Dispatcher.UIThread.Post(view.Prompt.TakeFocus, DispatcherPriority.Loaded);
        }
        else
        {
            Input.Focus();
        }
    }

    /// <summary>A tab's chat starts once the window is open: its first line, the chat it opens, and where it was scrolled.</summary>
    private void StartTab(TabView view)
    {
        if (!_opened || view.Started)
        {
            return;
        }

        view.Started = true;
        var model = view.Model;
        OffThread(async () =>
        {
            await model.StartAsync().ConfigureAwait(false);
            if (model.StartScroll is { } down)
            {
                Dispatcher.UIThread.Post(() => ScrollTo(view, down), DispatcherPriority.Background);
            }
        });
    }

    /// <summary>
    /// After a chat's scroll changed: whether it follows its end, and whether it is put back at its end. The chat
    /// or the page changing size (an answer grew, a row went in above the end, the sheet opened beside it) never
    /// stops the following, and a following chat is put back at its end; only the person's own scrolling stops
    /// it, and scrolling to the bottom starts it again. A change of size is never taken for the person
    /// scrolling up.
    /// </summary>
    internal static (bool Follow, bool ToEnd) AfterScroll(bool following, Vector extentDelta, Vector viewportDelta, double offset, double extent, double viewport)
    {
        if (extentDelta.Y != 0 || viewportDelta.Y != 0)
        {
            return (following, following);
        }

        return (offset >= extent - viewport - 24, false);
    }

    /// <summary>A tab put back where it was scrolled at the last quit.</summary>
    private static void ScrollTo(TabView view, double down)
    {
        var room = Math.Max(0, view.Scroll.Extent.Height - view.Scroll.Viewport.Height);
        view.Scroll.Offset = new Vector(0, room * down);
        view.FollowTail = down >= 0.999;
    }

    /// <summary>How far down a tab of this window is scrolled, 0 to 1; null for a tab it does not hold.</summary>
    private double? ScrollFraction(ChatViewModel tab)
    {
        if (!_views.TryGetValue(tab, out var view))
        {
            return null;
        }

        var room = view.Scroll.Extent.Height - view.Scroll.Viewport.Height;
        return room <= 0 ? 1 : Math.Clamp(view.Scroll.Offset.Y / room, 0, 1);
    }

    private void OnAppChanged() => Dispatcher.UIThread.Post(SyncTabs);

    /// <summary>
    /// The window's tabs as the app has them: a new one made and started, a closed or moved one let
    /// go, and the one in front shown.
    /// </summary>
    private void SyncTabs()
    {
        if (_window.Tabs.Count == 0)
        {
            return;
        }

        foreach (var gone in _views.Keys.Where(m => !_window.Tabs.Contains(m)).ToList())
        {
            Retire(_views[gone]);
            _views.Remove(gone);
        }

        foreach (var tab in _window.Tabs)
        {
            StartTab(ViewFor(tab));
        }

        ShowTab(_window.CurrentTab);
        if (_sheetName == TabsScreen)
        {
            _tabsPage.Reload();
        }
    }

    /// <summary>
    /// A tab leaves this window, closed, moved to another, or with the window: its chat's events are
    /// let go, and its questions are left as their ✕ leaves them (an approval left is a deny), so no
    /// answer waits on a card nobody can see. The flow that asked ends as it would on a ✕.
    /// </summary>
    private static void Retire(TabView view)
    {
        view.Unhook();
        view.Prompt.LeaveAll();
    }

    /// <summary>Something in a tab changed: the tab in front is drawn again; for any other, its tab and the bar's counts.</summary>
    private void OnTabChanged(ChatViewModel model)
    {
        if (ReferenceEquals(model, _tab.Model))
        {
            QueueRedraw();
        }
        else
        {
            QueueStrip();
        }
    }

    private void OnWindowClosed(AppWindow window)
    {
        if (ReferenceEquals(window, _window))
        {
            Dispatcher.UIThread.Post(() =>
            {
                _closingByApp = true;
                Close();
            });
        }
    }

    private void OnActivateRequested(AppWindow window)
    {
        if (ReferenceEquals(window, _window))
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (WindowState == WindowState.Minimized)
                {
                    WindowState = WindowState.Normal;
                }

                Activate();
            });
        }
    }

    /// <summary>Ctrl+T: a new chat in a new tab; with <paramref name="isPrivate"/>, Ctrl+Shift+N, a private one.</summary>
    private void NewTab(bool isPrivate)
    {
        CloseSheet();
        _ = _app.NewTab(_window, isPrivate);
    }

    /// <summary>Ctrl+N: a new window, with a new chat in it.</summary>
    private void NewWindow() => _app.OpenWindow();

    /// <summary>Ctrl+Shift+T: the last closed tab's chat back, with its draft.</summary>
    private void Reopen()
    {
        if (_app.ReopenClosed(_window) is null)
        {
            _ = Model.Say(Role.System, "No closed tab to bring back.");
        }
    }

    /// <summary>Ctrl+W, Ctrl+F4, a tab's ✕: the tab closes and its chat stays kept; closing the last tab of the last window is quitting.</summary>
    private void CloseTab(AppWindow window, ChatViewModel tab)
    {
        if (!_app.CloseTab(window, tab))
        {
            RequestQuit();
        }
    }

    /// <summary>A tab from the Tabs sheet or a count on the bar: in this window, shown; in another, that window comes to the front with it.</summary>
    private void GoTo(AppWindow window, ChatViewModel tab)
    {
        CloseSheet();
        if (ReferenceEquals(window, _window))
        {
            Show(tab);
        }
        else
        {
            _app.Select(window, tab);
        }
    }

    /// <summary>A tab's name on the strip: its chat's title, cut to fit.</summary>
    private static string TabName(ChatViewModel tab)
    {
        var title = tab.SessionTitle;
        return title.Length <= 28 ? title : title[..27] + "…";
    }

    /// <summary>
    /// The tabs across the top of the chat: each chat's title, a dot while
    /// it answers, "Private" in words, "needs your yes" when an approval waits in a tab behind ("waiting
    /// for you" for any other question), and ✕; + opens a new tab, and its ▾ the rest.
    /// </summary>
    private void DrawTabs()
    {
        var from = string.Join('\u001f', _window.Tabs.Select((t, i) => string.Create(CultureInfo.InvariantCulture, $"{Id(t)}:{i == _window.Current}:{t.Busy}:{t.IsPrivate}:{t.Waiting}:{t.WaitingForYes}:{t.SessionTitle}")));
        if (!NeedsDrawing("tabs", from))
        {
            return;
        }

        TabStrip.Children.Clear();
        for (var i = 0; i < _window.Tabs.Count; i++)
        {
            var tab = _window.Tabs[i];
            var front = i == _window.Current;
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (tab.Busy)
            {
                line.Children.Add(new TextBlock { Text = "●", Foreground = Palette.Accent, FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
            }

            if (tab.IsPrivate)
            {
                line.Children.Add(new TextBlock { Text = "Private", FontWeight = FontWeight.SemiBold, Foreground = Palette.Warning, VerticalAlignment = VerticalAlignment.Center });
            }

            line.Children.Add(new TextBlock { Text = TabName(tab), VerticalAlignment = VerticalAlignment.Center });
            if (tab.Waiting && !front)
            {
                line.Children.Add(new TextBlock { Text = TabsPage.WaitingWords(tab), Foreground = Palette.Warning, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            }

            var close = new Button { Content = "✕", Classes = { "tool" }, Padding = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(close, tab.IsPrivate ? "Close this tab; its private chat goes for good (Ctrl+W)" : "Close this tab; its chat is kept (Ctrl+W)");
            close.Click += (_, e) =>
            {
                e.Handled = true;
                CloseTab(_window, tab);
            };
            line.Children.Add(close);

            var button = new Button { Classes = { "tab" }, Content = line };
            if (front)
            {
                button.Classes.Add("current");
            }

            ToolTip.SetTip(button, $"{TabsPage.Title(tab)}{(TabsPage.Doing(tab) is { Length: > 0 } doing ? $" · {doing}" : string.Empty)} · Ctrl+{(i < 8 ? (i + 1).ToString(CultureInfo.InvariantCulture) : "9")}");
            button.Click += (_, _) => Show(tab);

            // A middle click closes a tab, as in a browser.
            button.AddHandler(PointerReleasedEvent, (_, e) =>
            {
                if (e.InitialPressMouseButton == MouseButton.Middle)
                {
                    e.Handled = true;
                    CloseTab(_window, tab);
                }
            }, RoutingStrategies.Tunnel);
            button.ContextMenu = Menus.Of(
                Menus.Item("New tab", () => NewTab(false), "Ctrl+T"),
                Menus.Item("New private tab", () => NewTab(true), "Ctrl+Shift+N"),
                Menus.Line(),
                Menus.Item("Move to new window", () => _app.MoveToNewWindow(_window, tab)),
                Menus.Item("Close tab", () => CloseTab(_window, tab), "Ctrl+W"));
            TabStrip.Children.Add(button);
        }

        var plus = new Button { Content = "+", Classes = { "tab", "new" } };
        ToolTip.SetTip(plus, "A new tab (Ctrl+T)");
        plus.Click += (_, _) => NewTab(false);
        TabStrip.Children.Add(plus);
        var more = new Button { Content = "▾", Classes = { "tab", "new" } };
        ToolTip.SetTip(more, "New private tab, new window, reopen a closed tab");
        var menu = Menus.Of(
            Menus.Item("New tab", () => NewTab(false), "Ctrl+T"),
            Menus.Item("New private tab", () => NewTab(true), "Ctrl+Shift+N"),
            Menus.Item("New window", NewWindow, "Ctrl+N"),
            Menus.Line(),
            Menus.Item("Reopen closed tab", Reopen, "Ctrl+Shift+T"),
            Menus.Item("Every tab…", () => ToggleSheet(TabsScreen), "F2"));
        more.ContextMenu = menu;
        more.Click += (_, _) => menu.Open(more);
        TabStrip.Children.Add(more);
    }

    // FLASHWINFO and FlashWindowEx (WinUser.h, Microsoft Learn): FLASHW_TRAY flashes the taskbar
    // button; FLASHW_TIMERNOFG flashes it until the window comes to the foreground.
    private const uint FlashTray = 0x00000002;
    private const uint FlashUntilInFront = 0x0000000C;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FlashInfo info);

    /// <summary>The taskbar button flashes until the window comes to the front: a question waits in a tab behind, or in a window behind.</summary>
    private void Flash()
    {
        if (TryGetPlatformHandle()?.Handle is { } handle && handle != 0)
        {
            var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = handle, Flags = FlashTray | FlashUntilInFront };
            _ = FlashWindowEx(ref info);
        }
    }

    /// <summary>One tab's own parts: its chat, its page (the transcript in its scroller, the empty page's mark), its question card, and where it was left.</summary>
    private sealed class TabView(ChatViewModel model, TranscriptView transcript, Grid column, ScrollViewer scroll, Border hero, PromptCard prompt)
    {
        public ChatViewModel Model { get; } = model;

        public TranscriptView Transcript { get; } = transcript;

        public Grid Column { get; } = column;

        public ScrollViewer Scroll { get; } = scroll;

        public Border Hero { get; } = hero;

        public PromptCard Prompt { get; } = prompt;

        /// <summary>Whether the page follows the end as an answer grows.</summary>
        public bool FollowTail { get; set; } = true;

        /// <summary>The chat whose draft the composer holds for this tab: "" for the chat nobody has spoken in, null before the tab was first drawn.</summary>
        public string? DraftFor { get; set; }

        /// <summary>What the empty page was last built from; null to build it again.</summary>
        public string? HeroFrom { get; set; }

        public bool Started { get; set; }

        /// <summary>Lets go of the chat's events when the tab leaves this window.</summary>
        public Action Unhook { get; set; } = () => { };
    }

    internal PromptCard Prompt => _tab.Prompt;

    /// <summary>The screen on show: the sheet's name, or Chat.</summary>
    internal string Page => _sheetName ?? ChatScreen;

    internal TranscriptView Messages => _tab.Transcript;

    /// <summary>The time now, in milliseconds, for counting Esc presses.</summary>
    internal Func<long> Ticks { get; set; } = () => Environment.TickCount64;

    /// <summary>The time the clock is drawn for.</summary>
    internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.Now;

    /// <summary>The chat in front: the tab's own.</summary>
    private ChatViewModel Model => _tab.Model;

    private Grid ChatColumn => _tab.Column;

    private ScrollViewer ChatScroll => _tab.Scroll;

    private Border Hero => _tab.Hero;

    // ---- the sheet: Settings, Activity, Help beside the chat ---------------------------------

    /// <summary>A sheet's button: opens it, or closes it when it is open. The click that just closed it from outside does not open it again.</summary>
    private void ToggleSheet(string name)
    {
        if (_dismissedSheet == name && Environment.TickCount64 - _dismissedAt < DismissEcho)
        {
            _dismissedSheet = null;
            return;
        }

        if (_sheetName == name)
        {
            CloseSheet();
        }
        else
        {
            OpenSheet(name);
        }
    }

    private void OpenSheet(string name)
    {
        _sheetName = name;
        _dismissedSheet = null;
        SheetTitle.Text = name;
        Control body = name switch
        {
            SettingsScreen => _settings,
            ActivityScreen => _activity,
            TabsScreen => _tabsPage,
            ToldScreen => _toldPage,
            ToolsScreen => _toolsPage,
            _ => _helpPage,
        };
        if (!ReferenceEquals(SheetBody.Children.FirstOrDefault(), body))
        {
            SheetBody.Children.Clear();
            SheetBody.Children.Add(body);
        }

        Sheet.IsVisible = true;
        SizeSheet();
        switch (name)
        {
            case SettingsScreen:
                _settings.Reload();
                break;
            case ActivityScreen:
                _activity.Reload();
                break;
            case TabsScreen:
                _tabsPage.Reload();
                break;
            case ToldScreen:
                _toldPage.Reload();
                break;
            case ToolsScreen:
                _toolsPage.Reload();
                break;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                if (_sheetName == SettingsScreen)
                {
                    _settings.FocusStart();
                }
                else
                {
                    FocusFirstIn(SheetBody);
                }
            },
            DispatcherPriority.Loaded);
        Redraw();
    }

    private void CloseSheet()
    {
        if (_sheetName is null)
        {
            return;
        }

        _sheetName = null;
        Sheet.IsVisible = false;
        ChatScroll.Margin = default;
        if (!Prompt.Open)
        {
            Input.Focus();
        }

        Redraw();
    }

    /// <summary>F1 on a screen: Help, open at that screen's page.</summary>
    private void ShowHelpFor(string screen) => ShowPage(Controllers.Docs.For(screen).Id);

    /// <summary>Help, open at one page: from F1, Settings' Help, or Data's "What is this?".</summary>
    private void ShowPage(string page)
    {
        OpenSheet(HelpScreen);
        _helpPage.Open(page);
    }

    /// <summary>
    /// Where a file DipTick writes for the person goes: Windows' own save dialog, whose title says
    /// what the file holds. Null when the person cancels.
    /// </summary>
    private Task<string?> SaveFileAsync(string title, string suggested, string kind, string extension)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = title,
                    SuggestedFileName = suggested,
                    DefaultExtension = extension,
                    FileTypeChoices = [new FilePickerFileType(kind) { Patterns = [$"*.{extension}"] }],
                    ShowOverwritePrompt = false,
                }).ConfigureAwait(true);
                done.TrySetResult(file?.TryGetLocalPath());
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                done.TrySetResult(null);
            }
        });
        return done.Task;
    }

    /// <summary>The sheet takes most of the page but never all of it: the chat keeps its left side and reflows into it.</summary>
    private void SizeSheet()
    {
        var width = Math.Clamp(PageHost.Bounds.Width * 0.62, 420, 700);
        Sheet.Width = width;
        ChatScroll.Margin = Sheet.IsVisible ? new Thickness(0, 0, width, 0) : default;
    }

    /// <summary>
    /// A click anywhere outside the sheet closes it and goes on to what it was aimed at, so one click
    /// on a chat in the rail opens that chat. The F-key bar and the prompt
    /// card are part of the sheet's work: a click there leaves it open.
    /// </summary>
    private void OnWindowPointer(object? sender, PointerPressedEventArgs e)
    {
        if (_sheetName is null || Inside(e, Sheet) || Inside(e, KeyBarHost) || Inside(e, PromptHost))
        {
            return;
        }

        _dismissedSheet = _sheetName;
        _dismissedAt = Environment.TickCount64;
        CloseSheet();
    }

    private static bool Inside(PointerEventArgs e, Visual visual)
    {
        if (!visual.IsEffectivelyVisible)
        {
            return false;
        }

        var at = e.GetPosition(visual);
        return at.X >= 0 && at.Y >= 0 && at.X < visual.Bounds.Width && at.Y < visual.Bounds.Height;
    }

    private static void FocusFirstIn(Visual root) =>
        root.GetVisualDescendants().OfType<InputElement>().FirstOrDefault(x => x.Focusable && x.IsEffectivelyVisible && x.IsEffectivelyEnabled)?.Focus();

    /// <summary>F6: the next pane, as Windows' F6 cycles through a window's parts: the chats, the chat, where you type, the sheet.</summary>
    private void NextPane()
    {
        var panes = new List<Visual> { Rail, ChatScroll, ComposerHost };
        if (_sheetName is not null)
        {
            panes.Add(Sheet);
        }

        var focused = FocusManager?.GetFocusedElement() as Visual;
        var at = panes.FindIndex(p => focused is not null && (ReferenceEquals(p, focused) || p.IsVisualAncestorOf(focused)));
        var next = panes[(at + 1) % panes.Count];
        if (ReferenceEquals(next, ComposerHost))
        {
            Input.Focus();
        }
        else if (ReferenceEquals(next, ChatScroll))
        {
            ChatScroll.Focusable = true;
            ChatScroll.Focus();
        }
        else if (ReferenceEquals(next, Sheet) && _sheetName == SettingsScreen)
        {
            _settings.FocusStart();
        }
        else
        {
            FocusFirstIn(next);
        }
    }

    // ---- the F-key bar -------------------------------------------------------------------------

    /// <summary>
    /// Each screen's keys. F1 Help for the screen, F3 Find, F6 the
    /// next pane, F9 Settings and F10 Quit mean the same everywhere; F2, F4, F5, F7 and F8 are the
    /// screen's own. A null is a key with nothing to do on that screen, shown dimmed so its place is
    /// learned once.
    /// </summary>
    private FKey?[] KeysFor(string screen) => screen switch
    {
        SettingsScreen =>
        [
            new("Help", _settings.HelpSelected, "all about the setting you are on: what it does, its default, and its command"),
            new("Change", _settings.ChangeSelected, "change the setting you are on"),
            new("Find", _settings.ToggleFind, "find a setting in any section, by its name or by what it does"),
            null,
            new("Reload", ReloadSettings, "read the settings file again, after a change made outside DipTick"),
            new("Pane", NextPane, "the next pane"),
            new("Show file", _settings.ShowFile, "the settings file, selected in Explorer"),
            new("Default", _settings.DefaultSelected, "the setting you are on, back to its default"),
            new("Close", CloseSheet, "close Settings (Esc, or a click outside them)"),
            new("Quit", RequestQuit, "quit DipTick; it asks first while an answer runs"),
        ],
        ActivityScreen =>
        [
            new("Help", () => ShowHelpFor(ActivityScreen), "the page about this sheet: the audit log and its keys"),
            new("Filter", _activity.ToggleFilter, "only the decisions of one person or agent, one capability, or one verdict"),
            new("Find", _activity.ToggleFind, "find a word in the decisions and checks"),
            new("Range", _activity.NextRange, "the decisions of today, of the last 7 days, or all of them"),
            new("Refresh", _activity.Reload, "read the decisions, the chain and the checks again"),
            new("Pane", NextPane, "the next pane"),
            new("Export", () => _ = _activity.ExportAsync(), "the decisions shown, to a file (JSON Lines, CSV or text), then whether the chain verified"),
            new("Verify", _activity.Verify, "check the audit log's chain now"),
            new("Settings", () => ToggleSheet(SettingsScreen), "open Settings"),
            new("Quit", RequestQuit, "quit DipTick; it asks first while an answer runs"),
        ],
        TabsScreen =>
        [
            new("Help", () => ShowHelpFor(ChatScreen), "the page about chats and tabs"),
            new("Close", CloseSheet, "close the list of tabs (Esc, or F2 again)"),
            new("Find", _tabsPage.ToggleFind, "find a tab by its title"),
            null,
            null,
            new("Pane", NextPane, "the next pane"),
            null,
            null,
            new("Settings", () => ToggleSheet(SettingsScreen), "open Settings"),
            new("Quit", RequestQuit, "quit DipTick; it asks first while an answer runs"),
        ],
        ToolsScreen =>
        [
            new("Help", () => ShowHelpFor(ToolsScreen), "the page about DipTick's own tools"),
            new("Clock", _toolsPage.ShowClockCard, "the clock, and the other places you keep"),
            null,
            new("Calendar", _toolsPage.ShowCalendarCard, "the month, its ISO weeks, and the days between two dates"),
            new("Check again", _toolsPage.CheckAgain, "the network check again"),
            new("Pane", NextPane, "the next pane"),
            new("Network", _toolsPage.ShowNetworkCard, "the network check: every service DipTick uses, or any host you type"),
            null,
            new("Settings", () => ToggleSheet(SettingsScreen), "open Settings"),
            new("Quit", RequestQuit, "quit DipTick; it asks first while an answer runs"),
        ],
        ToldScreen =>
        [
            new("Help", () => ShowHelpFor(ToldScreen), "the page about agents at one table, and what they are told"),
            new("Close", CloseSheet, "close this sheet (Esc, or a click outside it)"),
            null,
            new("Agents", ShowTableMenu, "the table: who sits here, another model for one, add or take one away, who answers"),
            new("Refresh", () => _toldPage.Reload(), "read what each agent is told again"),
            new("Pane", NextPane, "the next pane"),
            null,
            null,
            new("Settings", () => ToggleSheet(SettingsScreen), "open Settings"),
            new("Quit", RequestQuit, "quit DipTick; it asks first while an answer runs"),
        ],
        HelpScreen =>
        [
            new("Contents", _helpPage.Contents, "the first page, and the list of pages"),
            new("Back", _helpPage.Back, "the page before this one"),
            new("Search", _helpPage.FocusSearch, "search every page"),
            new("Pages", _helpPage.FocusPages, "the list of pages"),
            null,
            new("Pane", NextPane, "the next pane"),
            new("Save as files", () => _ = _helpPage.SaveAsFilesAsync(), "every page as a Markdown file, in a folder you pick"),
            null,
            new("Settings", () => ToggleSheet(SettingsScreen), "open Settings"),
            new("Quit", RequestQuit, "quit DipTick; it asks first while an answer runs"),
        ],
        _ =>
        [
            new("Help", () => ShowHelpFor(ChatScreen), "Help, at the page about chats (Ctrl+/ opens it where it was)"),
            new("Tabs", () => ToggleSheet(TabsScreen), "every tab in every window and what each is doing; new tabs, private tabs, windows"),
            new("Find", ToggleFind, "find in this chat (Ctrl+F)"),
            new("Agents", ShowTableMenu, "the table: who sits here, another model for one, add or take one away, who answers, what they are told"),
            new("Retry", () => OffThread(() => Model.RetryAsync()), "ask the last question again, of the agent that answered it"),
            new("Pane", NextPane, "the next pane: the chats, the chat, where you type"),
            new("Attach", () => Report(PickFilesAsync()), "attach an image, a PDF or a text file to your next message; a file dropped on the chat, or an image pasted, is attached too"),
            new("Stop", () => Model.Stop(), "stop the answer that is coming (Esc)"),
            new("Settings", () => ToggleSheet(SettingsScreen), "open Settings; again closes them (Ctrl+,)"),
            new("Quit", RequestQuit, "quit DipTick; it asks first while an answer runs (Ctrl+Q)"),
        ],
    };

    private void DrawKeys()
    {
        if (_keysFor == Page)
        {
            return;
        }

        _keysFor = Page;
        _keyBar.Show(KeysFor(Page));
    }

    private void ReloadSettings()
    {
        Model.Reload();
        _settings.Reload();
    }

    // ---- quitting --------------------------------------------------------------------------------

    /// <summary>F10, Ctrl+Q, the window's own ✕: quit, asking first while an answer runs.</summary>
    private void RequestQuit() => OffThread(() => _app.QuitAsync(ask: true));

    /// <summary>Esc three times: quit at once and stop everything, the local models included.</summary>
    private void QuitNow()
    {
        _keyBar.Warn("Quitting: stopping everything…");
        OffThread(() => _app.QuitAsync(ask: false));
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // The window's own ✕ and Alt+F4: with another window open, this one closes with its tabs, as a
        // browser's does; the last window's is a quit like any other, asked first while an answer runs,
        // and the local models let go. DipTick's own Close and a shutdown go through.
        if (!_quitting && !_closingByApp && !e.IsProgrammatic && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            if (!_app.CloseWindow(_window))
            {
                RequestQuit();
            }

            return;
        }

        foreach (var tab in _window.Tabs)
        {
            tab.Stop();
        }
    }

    /// <summary>
    /// Counts an Esc press toward the three that quit (<see cref="EscapeCounter"/>), within the
    /// person's own double-click time. After two, the bar says the third will quit, until the time runs out.
    /// </summary>
    private bool CountEscape()
    {
        var gap = DoubleClick.Gap();
        switch (_escapes.Press(Ticks(), gap))
        {
            case EscapeTurn.Quit:
                _escReset.Stop();
                return true;
            case EscapeTurn.Twice:
                _keyBar.Warn(EscapeWarning);
                _escReset.Stop();
                _escReset.Interval = TimeSpan.FromMilliseconds(gap);
                _escReset.Start();
                return false;
            default:
                return false;
        }
    }

    // ---- the empty page ---------------------------------------------------------------------------

    /// <summary>Settings, Models, with the model marked: what a click on a model in the rail opens.</summary>
    private void ShowModelSettings(string id)
    {
        OpenSheet(SettingsScreen);
        _settings.ShowModel(id);
    }

    /// <summary>
    /// The empty page, where the purpose shows: with no model, what to do first, in
    /// one click; with agents, the name, dim, and one line under it saying what a table of agents does,
    /// with + Add agent (or setting one up) as the one obvious action while only one sits here. Rebuilt
    /// only when that changes, or the theme does.
    /// </summary>
    private void DrawHero()
    {
        var withoutModel = Model.SetUpAgents.Count == 0;
        var seated = Model.Table.Count;
        var from = string.Create(CultureInfo.InvariantCulture, $"{withoutModel}:{seated}:{Model.SetUpAgents.Count}");
        if (_tab.HeroFrom == from)
        {
            return;
        }

        _tab.HeroFrom = from;
        if (!withoutModel)
        {
            var page = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
            page.Children.Add(new TextBlock { Text = "DipTick", FontSize = 15, LetterSpacing = 3, Foreground = Palette.Dim, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.6 });
            var (line, action, act) = seated switch
            {
                0 => ("Nobody sits at this chat's table yet.", "+ Add agent", (Action)ShowTableMenu),
                1 when Model.SetUpAgents.Count == 1 => ("One agent sits at this table. Set up another, and they answer you together.", "Set up another agent", StartAddModel),
                1 => ("One agent sits at this table. Seat another, and they answer you together.", "+ Add agent", ShowTableMenu),
                _ => ("Every agent at this table answers you together. @name asks one.", null, null),
            };
            page.Children.Add(new TextBlock { Text = line, Foreground = Palette.Dim, FontSize = 14, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Center });
            if (action is not null && act is not null)
            {
                var button = new Button { Content = action, HorizontalAlignment = HorizontalAlignment.Center };
                button.Click += (_, _) => act();
                page.Children.Add(button);
            }

            Hero.Child = page;
            return;
        }

        var column = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center };
        column.Children.Add(new TextBlock { Text = "No model is set up yet.", FontSize = 17, Foreground = Palette.Ink, HorizontalAlignment = HorizontalAlignment.Center });
        var start = new Button { Content = "Set up a model", Classes = { "primary" }, HorizontalAlignment = HorizontalAlignment.Center };
        start.Click += (_, _) => _settings.StartModel(ChatStores.MainSeat);
        column.Children.Add(start);
        column.Children.Add(new TextBlock
        {
            Text = "It asks which provider, its key and its model, and checks the key with a free call. Afterwards it lives in Settings, Models.",
            Foreground = Palette.Dim,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        Hero.Child = column;
    }

    // ---- the rail ----------------------------------------------------------------------------

    private void BuildRailFoot()
    {
        RailFoot.Children.Add(RailButton("Settings", "F9", () => ToggleSheet(SettingsScreen)));
        RailFoot.Children.Add(RailButton("Tools", string.Empty, () => ToggleSheet(ToolsScreen)));
        RailFoot.Children.Add(RailButton("Activity", string.Empty, () => ToggleSheet(ActivityScreen)));
        RailFoot.Children.Add(RailButton("Help", "F1", () => ToggleSheet(HelpScreen)));
        NewChat.Click += (_, _) => NewTab(isPrivate: false);
    }

    private static Button RailButton(string text, string key, Action click)
    {
        var button = new Button { Classes = { "rail" } };
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        line.Children.Add(new TextBlock { Text = text });
        var hint = new TextBlock { Text = key, Foreground = Palette.Dim, FontSize = 11 };
        Grid.SetColumn(hint, 1);
        line.Children.Add(hint);
        button.Content = line;
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>
    /// The rail: the chats, pinned first, then by day (Today, Yesterday, This week, Older), the search
    /// above them narrowing them by title; then the models. Each section folds, and the fold is
    /// remembered. One click on a chat opens it in this tab, Ctrl+click in a new tab; a chat already
    /// open goes to its tab.
    /// </summary>
    private void DrawRail()
    {
        // Drawn from the kept chats, the chat in front, which chats are open where, the search, the
        // folds, the day (Today turns to Yesterday at midnight) and the models' settings.
        var open = string.Join(',', _app.AllTabs.Where(x => x.Tab.SessionId is not null && !x.Tab.IsPrivate).Select(x => $"{x.Tab.SessionId}@{x.Window.Number}"));
        var from = string.Create(CultureInfo.InvariantCulture, $"{Id(Model.Sessions())}:{Id(Model)}:{Model.SessionId}:{Model.IsPrivate}:{Model.SessionTitle}:{RailSearch.Text}:{IsFolded("Chats")}:{IsFolded("Models")}:{IsFolded("Connectors")}:{IsFolded("Skills")}:{DateTime.Now.Date:yyyyMMdd}:{Id(Model.Config)}:{open}:{Model.ConnectorsStamp}:{string.Join(",", Model.ConnectorsOn)}:{Model.SkillsStamp}:{string.Join(",", Model.SkillsOn)}");
        if (!NeedsDrawing("rail", from))
        {
            return;
        }

        RailBody.Children.Clear();
        RailBody.Children.Add(SectionLabel("Chats"));
        if (!IsFolded("Chats"))
        {
            DrawChats();
        }

        RailBody.Children.Add(SectionLabel("Models"));
        if (!IsFolded("Models"))
        {
            DrawModels();
        }

        RailBody.Children.Add(SectionLabel("Connectors"));
        if (!IsFolded("Connectors"))
        {
            DrawConnectors();
        }

        RailBody.Children.Add(SectionLabel("Skills"));
        if (!IsFolded("Skills"))
        {
            DrawSkills();
        }
    }

    /// <summary>
    /// The rail's skills: each one in DipTick's folder, with a dot for its state
    /// (on in this chat, not in it, or waiting for your yes) and one click switching an approved one on or
    /// off in the chat on screen; with none, one row to Settings, Skills.
    /// </summary>
    private void DrawSkills()
    {
        var skills = Model.Skills;
        if (skills.Count == 0)
        {
            var add = new Button { Classes = { "rail" }, Content = new TextBlock { Text = "Add a skill…", Foreground = Palette.Dim } };
            ToolTip.SetTip(add, "A folder or a zip in the Agent Skills format: Settings, Skills");
            add.Click += (_, _) => ShowSkillSettings();
            RailBody.Children.Add(add);
            return;
        }

        var on = Model.SkillsOn;
        foreach (var skill in skills)
        {
            var here = on.Contains(skill.Name, StringComparer.Ordinal);
            var name = skill.Name;
            var approved = Models.Skills.SkillStore.StateOf(Model.Config, skill) == Models.Skills.SkillState.Approved;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new TextBlock { Text = "●", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Foreground = !approved ? Palette.Warning : here ? Palette.Accent : Palette.Dim });
            row.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(new TextBlock { Text = !approved ? "needs your yes" : here ? "on in this chat" : "not in this chat", Foreground = Palette.Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Classes = { "rail" }, Content = row };
            ToolTip.SetTip(button, $"{Models.SafeText.Line(skill.Description)} ·{(approved ? $"a click switches it {(here ? "off" : "on")} in this chat" : "Settings, Skills shows it for your yes")}");
            button.Click += (_, _) =>
            {
                if (approved)
                {
                    OffThread(() => Model.SetSkillAsync(name, !here));
                }
                else
                {
                    ShowSkillSettings();
                }
            };
            RailBody.Children.Add(button);
        }
    }

    /// <summary>Settings, open at Skills.</summary>
    private void ShowSkillSettings()
    {
        OpenSheet(SettingsScreen);
        _settings.Show("Skills");
    }

    /// <summary>
    /// The rail's connectors: each one added, with a dot for its state (on in
    /// this chat, not in it, or switched off) and one click switching it on or off in the chat on screen;
    /// with none added, one row to Settings, Connectors.
    /// </summary>
    private void DrawConnectors()
    {
        var connectors = Model.Connectors;
        if (connectors.Count == 0)
        {
            var add = new Button { Classes = { "rail" }, Content = new TextBlock { Text = "Add a connector…", Foreground = Palette.Dim } };
            ToolTip.SetTip(add, "Atlassian, ClickUp, Gmail, Outlook, or your own: Settings, Connectors");
            add.Click += (_, _) => ShowConnectorSettings();
            RailBody.Children.Add(add);
            return;
        }

        var on = Model.ConnectorsOn;
        foreach (var connector in connectors)
        {
            var here = on.Contains(connector.Name, StringComparer.Ordinal);
            var name = connector.Name;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new TextBlock { Text = "●", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Foreground = !connector.Enabled ? Palette.Warning : here ? Palette.Accent : Palette.Dim });
            row.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(new TextBlock { Text = !connector.Enabled ? "off" : here ? "on in this chat" : "not in this chat", Foreground = Palette.Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Classes = { "rail" }, Content = row };
            ToolTip.SetTip(button, $"{Controllers.ConnectorsController.State(connector)} · a click switches it {(here ? "off" : "on")} in this chat · right-click for more");
            button.Click += (_, _) => OffThread(() => Model.SetConnectorAsync(name, !here));
            button.ContextMenu = Menus.Of(
                Menus.Item(here ? "Off in this chat" : "On in this chat", () => OffThread(() => Model.SetConnectorAsync(name, !here))),
                Menus.Item("Settings", ShowConnectorSettings));
            RailBody.Children.Add(button);
        }
    }

    /// <summary>Settings, open at Connectors.</summary>
    private void ShowConnectorSettings()
    {
        OpenSheet(SettingsScreen);
        _settings.Show("Connectors");
    }

    private void DrawChats()
    {
        var current = Model.SessionId;
        if (current is null)
        {
            // The chat on screen that nobody has spoken in yet: kept from its first words; a private one never.
            var words = Model.IsPrivate ? "Private chat" : Model.SessionTitle;
            var fresh = new Button { Classes = { "rail", "current" }, Content = new TextBlock { Text = words, TextTrimming = TextTrimming.CharacterEllipsis } };
            ToolTip.SetTip(fresh, Model.IsPrivate ? "This tab's private chat: never in this list, nothing of it kept" : "This tab's new chat: kept from its first words");
            RailBody.Children.Add(fresh);
        }

        var find = (RailSearch.Text ?? string.Empty).Trim();
        var sessions = Model.Sessions().Where(s => find.Length == 0 || Named(s).Contains(find, StringComparison.OrdinalIgnoreCase)).ToList();
        if (sessions.Count == 0 && find.Length > 0)
        {
            RailBody.Children.Add(new TextBlock { Text = $"No chat's title holds '{find}'.", Foreground = Palette.Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 2, 0, 4) });
        }

        var today = DateTime.Now.Date;
        foreach (var group in sessions.GroupBy(s => s.Pinned ? 0 : Day(s.Updated.ToLocalTime().Date, today)).OrderBy(g => g.Key))
        {
            RailBody.Children.Add(new TextBlock { Text = group.Key switch { 0 => "Pinned", 1 => "Today", 2 => "Yesterday", 3 => "This week", _ => "Older" }, Foreground = Palette.Dim, FontSize = 11, Margin = new Thickness(10, 6, 0, 2) });
            foreach (var session in group)
            {
                RailBody.Children.Add(ChatRow(session, current));
            }
        }
    }

    /// <summary>Which of the day groups a chat last written on <paramref name="day"/> falls in: 1 today, 2 yesterday, 3 the week before, 4 older.</summary>
    private static int Day(DateTime day, DateTime today) => day >= today ? 1 : day >= today.AddDays(-1) ? 2 : day >= today.AddDays(-7) ? 3 : 4;

    private string Named(SessionInfo session) => session.Id == Model.SessionId ? Model.SessionTitle : session.Title.Length == 0 ? "New chat" : session.Title;

    private Button ChatRow(SessionInfo session, string? current)
    {
        var id = session.Id;
        var elsewhere = id != current && _app.Find(id) is { } open ? open : ((AppWindow Window, ChatViewModel Tab)?)null;
        var text = new TextBlock { Text = Trim(Named(session)), TextTrimming = TextTrimming.CharacterEllipsis };
        if (elsewhere is not null)
        {
            text.FontStyle = FontStyle.Italic;
        }

        var button = new Button { Classes = { "rail" }, Content = text };
        if (id == current)
        {
            button.Classes.Add("current");
        }

        var where = elsewhere is { } found
            ? ReferenceEquals(found.Window, _window) ? " · open in another tab" : string.Create(CultureInfo.InvariantCulture, $" · open in window {found.Window.Number}")
            : string.Empty;
        ToolTip.SetTip(button, $"{id} · {session.Messages.ToString(CultureInfo.InvariantCulture)} lines · {session.Updated.ToLocalTime():yyyy-MM-dd HH:mm}{(session.Pinned ? " · pinned" : string.Empty)}{where} · Ctrl+click opens it in a new tab · right-click for more");

        // Ctrl held as the click began: a new tab, as a browser opens a link.
        button.AddHandler(PointerPressedEvent, (_, e) => _railModifiers = e.KeyModifiers, RoutingStrategies.Tunnel);
        button.Click += (_, _) =>
        {
            var newTab = _railModifiers.HasFlag(KeyModifiers.Control);
            _railModifiers = KeyModifiers.None;
            OpenChat(id, newTab);
        };
        button.ContextMenu = SessionMenu(session);
        return button;
    }

    /// <summary>A kept chat's menu: each item one of the chat verbs, a tab or window to open it in, or the clipboard.</summary>
    private ContextMenu SessionMenu(SessionInfo session)
    {
        var id = session.Id;
        return Menus.Of(
            Menus.Item("Open", () => OpenChat(id, newTab: false)),
            Menus.Item("Open in new tab (Ctrl+click)", () => OpenChat(id, newTab: true)),
            Menus.Item("Open in new window", () =>
            {
                CloseSheet();
                _ = _app.OpenInNewWindow(id);
            }),
            Menus.Line(),
            Menus.Item("Rename…", () => OffThread(() => Model.AskRenameSession(id))),
            Menus.Item(session.Pinned ? "Unpin" : "Pin", () => OffThread(() => Model.PinSession(id, !session.Pinned))),
            Menus.Item("Export…", () => Report(ExportAsync(id))),
            Menus.Item("Copy chat", () => Report(CopyChatAsync(id))),
            Menus.Line(),
            Menus.Item("Close chat", () => OffThread(() => Model.CloseSession(id))),
            Menus.Item("Delete…", () => OffThread(() => Model.DeleteSession(id))));
    }

    /// <summary>A section's name on the rail, which folds it and opens it again; the fold is remembered.</summary>
    private Button SectionLabel(string name)
    {
        var folded = IsFolded(name);
        var button = new Button { Classes = { "rail", "label" }, Content = new TextBlock { Text = $"{name.ToUpperInvariant()}  {(folded ? "▸" : "▾")}", FontSize = 11, LetterSpacing = 1.4, Foreground = Palette.Dim } };
        ToolTip.SetTip(button, folded ? "Show this section again" : "Fold this section away; it stays folded next time");
        button.Click += (_, _) =>
        {
            if (_app.UiState is { } state)
            {
                _ = state.Fold(name, !folded);
            }
            else if (!_foldedHere.Remove(name))
            {
                _foldedHere.Add(name);
            }

            DrawRail();
        };
        return button;
    }

    private bool IsFolded(string name) => _app.UiState?.Folded.Contains(name) ?? _foldedHere.Contains(name);

    private void DrawModels()
    {
        var config = Model.Config;
        if (config.MainProvider.Length > 0)
        {
            RailBody.Children.Add(ModelRow(ChatStores.MainSeat, Engines.DisplayName(config.MainProvider, config.MainEndpoint), string.Empty, config.MainModel, Engines.KeysFor(config)?.Describe().Words ?? string.Empty));
        }
        else
        {
            var setup = new Button { Classes = { "rail" }, Content = new TextBlock { Text = "No model yet · set one up", Foreground = Palette.Warning } };
            ToolTip.SetTip(setup, "Which provider, its key and its model, in the card above the composer");
            setup.Click += (_, _) => _settings.StartModel(ChatStores.MainSeat);
            RailBody.Children.Add(setup);
        }

        foreach (var seat in config.Seats)
        {
            RailBody.Children.Add(ModelRow(seat.Id, Engines.DisplayName(seat.Provider, seat.Endpoint), seat.Name, seat.Model, Engines.KeysFor(seat)?.Describe().Words ?? string.Empty));
        }
    }

    /// <summary>A model in the rail: its provider (or its seat's name) and its model, and where its key comes from.</summary>
    private Button ModelRow(string id, string provider, string seatName, string model, string key)
    {
        var button = new Button { Classes = { "rail" } };
        var column = new StackPanel { Spacing = 1 };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        top.Children.Add(new TextBlock { Text = seatName.Length > 0 ? seatName : provider, FontWeight = FontWeight.SemiBold });
        if (seatName.Length > 0)
        {
            top.Children.Add(new TextBlock { Text = provider, Foreground = Palette.Dim });
        }

        column.Children.Add(top);
        column.Children.Add(new TextBlock { Text = model.Length == 0 ? "no model yet" : model, Foreground = model.Length == 0 ? Palette.Warning : Palette.Ink, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
        if (key.Length > 0)
        {
            column.Children.Add(new TextBlock { Text = $"key: {key}", Foreground = key.Contains("MISSING", StringComparison.Ordinal) || key.Contains("EMPTY", StringComparison.Ordinal) ? Palette.Warning : Palette.Dim, FontSize = 11 });
        }

        button.Content = column;
        ToolTip.SetTip(button, "Its settings, in Settings, Models · right-click for more");
        button.Click += (_, _) => ShowModelSettings(id);
        button.ContextMenu = Menus.Of(
            Menus.Item("Settings", () => ShowModelSettings(id)),
            Menus.Item("List its models (free)", () =>
            {
                CloseSheet();
                OffThread(() => Model.Run(false, "models", "--for", id));
            }),
            Menus.Item("Copy its name", () => _ = CopyAsync(model.Length > 0 ? model : provider)));
        return button;
    }

    // ---- sessions: the store's, through the view model ---------------------------------------

    private static string Trim(string title) => title.Length <= 34 ? title : title[..33] + "…";

    /// <summary>
    /// A kept chat from the rail or a menu: the tab it is already open in, in whichever window; else
    /// this tab, unless it is private or answering, or Ctrl+click asks for a new one (<paramref name="newTab"/>).
    /// </summary>
    private void OpenChat(string id, bool newTab)
    {
        CloseSheet();
        if (_app.Find(id) is { } open)
        {
            GoTo(open.Window, open.Tab);
            return;
        }

        if (newTab || Model.IsPrivate || Model.Busy)
        {
            _ = _app.OpenInNewTab(_window, id);
            return;
        }

        SwitchSession(id);
    }

    private void SwitchSession(string id)
    {
        StopSelecting();
        if (Model.OpenSession(id) is { } problem)
        {
            _ = Model.Say(Role.System, problem);
            return;
        }

        ShowDraft();
        Input.Focus();
    }

    /// <summary>Ticked messages belong to the chat on screen, and are let go when it changes.</summary>
    private void StopSelecting()
    {
        if (Messages.Selecting)
        {
            Messages.StopSelecting();
        }
    }

    /// <summary>
    /// The composer holds the draft of the chat on screen: when the chat changes, its own draft comes
    /// back, and what was typed stays with the chat it was typed in (the view model keeps it).
    /// </summary>
    private void ShowDraft()
    {
        var chat = Model.SessionId ?? string.Empty;
        if (chat == _tab.DraftFor)
        {
            return;
        }

        _tab.DraftFor = chat;
        var draft = Model.Draft;
        if ((Input.Text ?? string.Empty) != draft)
        {
            Input.Text = draft;
            Input.CaretIndex = draft.Length;
        }
    }

    // ---- work off the window's thread ---------------------------------------------------------

    /// <summary>Runs work off the window's thread, so the window never freezes; a failure nobody expected is said in the chat, never lost.</summary>
    private void OffThread(Action work) => Report(Task.Run(work));

    private void OffThread(Func<Task> work) => Report(Task.Run(work));

    /// <summary>What stood in the way of something done at once, said in its chat; nothing when nothing did.</summary>
    private void SayIfAny(ChatViewModel model, string? problem)
    {
        if (problem is not null)
        {
            OffThread(() => model.Say(Role.System, problem));
        }
    }

    private void Report(Task running) => _ = running.ContinueWith(
        failed => Model.Say(Role.System, $"That did not work: {failed.Exception?.GetBaseException().Message}"),
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted,
        TaskScheduler.Default);

    // ---- the clipboard, export, quoting --------------------------------------------------------

    /// <summary>The format Windows keeps out of its clipboard history and the cloud clipboard, whatever else is placed beside it (Microsoft Learn, Clipboard Formats).</summary>
    private static readonly DataFormat<byte[]> NotInHistory = DataFormat.CreateBytesPlatformFormat("ExcludeClipboardContentFromMonitorProcessing");

    /// <summary>Puts text on the clipboard, on the window's thread whatever thread asks: only ever for something the person asked to copy. A private chat's stays out of the clipboard's history.</summary>
    private Task CopyAsync(string text) => CopyAsync(text, Model.IsPrivate);

    /// <summary>
    /// A source's page in the browser: only an http or https address, handed
    /// to Windows as the address it parses to; anything else is not opened.
    /// </summary>
    private void OpenInBrowser(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return;
        }

        try
        {
            using var browser = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            var model = Model;
            OffThread(() => model.Say(Role.System, $"The browser could not be opened for {Models.SafeText.Line(uri.AbsoluteUri)}: {Models.SafeText.Line(ex.Message)}"));
        }
    }

    private Task CopyAsync(string text, bool isPrivate) => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        if (Clipboard is { } clipboard)
        {
            await PutAsync(clipboard, text, isPrivate).ConfigureAwait(true);
        }
    });

    /// <summary>/copy's way to the clipboard: as <see cref="CopyAsync(string)"/>, and whether there was a clipboard to reach.</summary>
    private Task<bool> CopyForCommandAsync(string text, bool isPrivate) => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        if (Clipboard is not { } clipboard)
        {
            return false;
        }

        await PutAsync(clipboard, text, isPrivate).ConfigureAwait(true);
        return true;
    });

    /// <summary>
    /// Text on the clipboard. From a private chat it goes with Windows'
    /// ExcludeClipboardContentFromMonitorProcessing format beside it, which keeps the whole copy out
    /// of Windows' clipboard history and off the person's other devices (Microsoft Learn, Clipboard
    /// Formats). Avalonia passes a platform format's name to Windows as it is.
    /// </summary>
    internal static async Task PutAsync(IClipboard clipboard, string text, bool isPrivate)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        if (!isPrivate)
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
            return;
        }

        // The clipboard owns what it is handed and disposes of it itself; only a transfer it never took is disposed here.
        DataTransfer? transfer = new();
        try
        {
            var item = new DataTransferItem();
            item.SetText(text);
            item.Set(NotInHistory, [0]);
            transfer.Add(item);
            await clipboard.SetDataAsync(transfer).ConfigureAwait(true);
            transfer = null;
        }
        finally
        {
            (transfer as IDisposable)?.Dispose();
        }
    }

    private async Task CopyChatAsync(string id)
    {
        var text = await Task.Run(() => Model.SessionText(id)).ConfigureAwait(true);
        if (text.Length > 0)
        {
            await CopyAsync(text).ConfigureAwait(true);
        }
    }

    /// <summary>Export…: Windows' own save dialog picks the file; <c>diptick chat export</c> writes it, and asks before it overwrites.</summary>
    private async Task ExportAsync(string id)
    {
        if (!StorageProvider.CanSave)
        {
            await Model.Say(Role.System, "This window cannot open a save dialog here; '/export <file>' writes the chat out.").ConfigureAwait(true);
            return;
        }

        var title = Model.Sessions().FirstOrDefault(s => s.Id == id)?.Title;
        using var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export chat",
            SuggestedFileName = FileNameFor(title is { Length: > 0 } ? title : id) + ".txt",
            DefaultExtension = "txt",
            FileTypeChoices = [new FilePickerFileType("Text") { Patterns = ["*.txt"] }],

            // The verb asks before it overwrites, in the prompt card, as it does everywhere.
            ShowOverwritePrompt = false,
        }).ConfigureAwait(true);
        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        await Task.Run(() => Model.ExportSession(id, path)).ConfigureAwait(true);
    }

    /// <summary>A chat's title as a file name: the characters Windows refuses replaced, and not too long.</summary>
    private static string FileNameFor(string title)
    {
        var bad = Path.GetInvalidFileNameChars();
        var name = string.Concat(title.Select(c => Array.IndexOf(bad, c) >= 0 ? '_' : c)).Trim().Trim('.');
        return name.Length == 0 ? "chat" : name.Length > 60 ? name[..60] : name;
    }

    /// <summary>Quote in reply: the message, each line marked with >, goes to the top of the composer.</summary>
    private void QuoteIntoComposer(string text)
    {
        var quoted = string.Join('\n', text.ReplaceLineEndings("\n").Split('\n').Select(line => "> " + line)) + "\n\n";
        CloseSheet();
        Input.Text = quoted + (Input.Text ?? string.Empty);
        Input.CaretIndex = Input.Text.Length;
        Input.Focus();
    }

    /// <summary>The chat's own menu: a right-click between its messages.</summary>
    private ContextMenu ChatMenu() => Menus.Of(
        Menus.Item("Copy chat", () => _ = CopyAsync(Model.AsText(Model.Transcript))),
        Menus.Item("Export…", () =>
        {
            if (Model.SessionId is { } id)
            {
                Report(ExportAsync(id));
            }
            else
            {
                _ = Model.Say(Role.System, "Nothing has been kept in this chat yet, so there is nothing to write out.");
            }
        }),
        Menus.Item("Select messages", () => Messages.StartSelecting(null), "Ctrl+A"),
        Menus.Line(),
        Menus.Item("New tab", () => NewTab(isPrivate: false), "Ctrl+T"));

    /// <summary>The bar under the chat while messages are being ticked: how many, Copy, Select all, Done.</summary>
    private void DrawSelectBar()
    {
        var on = Messages.Selecting;
        SelectBar.IsVisible = on;
        if (!on)
        {
            SelectBar.Child = null;
            return;
        }

        var count = Messages.Selected.Count;
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,*"), ColumnSpacing = 10 };
        line.Children.Add(new TextBlock { Text = count == 1 ? "1 message selected" : $"{count.ToString(CultureInfo.InvariantCulture)} messages selected", VerticalAlignment = VerticalAlignment.Center });
        var copy = new Button { Content = "Copy", Classes = { "primary" }, IsEnabled = count > 0 };
        copy.Click += (_, _) => _ = CopyAsync(Model.AsText(Messages.Selected));
        var all = new Button { Content = "Select all" };
        all.Click += (_, _) => Messages.SelectAll();
        var done = new Button { Content = "✕  Done" };
        ToolTip.SetTip(done, "Stop selecting (Esc)");
        done.Click += (_, _) => Messages.StopSelecting();

        // The hint gives way first on a narrow window: trimmed, never pushed past the edge.
        var hint = new TextBlock { Text = "a click ticks a message · Ctrl+C copies · Esc stops", Foreground = Palette.Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(copy, 1);
        Grid.SetColumn(all, 2);
        Grid.SetColumn(done, 3);
        Grid.SetColumn(hint, 4);
        line.Children.Add(copy);
        line.Children.Add(all);
        line.Children.Add(done);
        line.Children.Add(hint);
        SelectBar.Child = line;
    }

    // ---- the composer ------------------------------------------------------------------------

    private void WireComposer()
    {
        Input.AddHandler(KeyDownEvent, OnInputKey, RoutingStrategies.Tunnel);
        Input.TextChanged += (_, _) =>
        {
            var text = Input.Text ?? string.Empty;

            // Written through as it is typed, so a quit, however sudden, keeps it.
            if (_tab.DraftFor is not null)
            {
                Model.Draft = text;
            }

            if (text.Contains('\n', StringComparison.Ordinal))
            {
                Model.Composer.CloseList();
            }
            else
            {
                Model.Composer.Typed(text);
            }

            DrawPalette();
            DrawPlanLine();
        };
        Send.Click += (_, _) => SendDraft();
        Attach.Click += (_, _) => Report(PickFilesAsync());

        // A file dropped anywhere on the chat is attached; an image pasted into the composer too.
        DragDrop.SetAllowDrop(MainColumn, true);
        MainColumn.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        MainColumn.AddHandler(DragDrop.DropEvent, OnDrop);
        Input.PastingFromClipboard += OnPasting;
        Find.TextChanged += (_, _) =>
        {
            Messages.Filter(Find.Text ?? string.Empty);
            Messages.FirstMatch?.BringIntoView();
        };

        // The composer's own menu, in place of the theme's: Cut and Copy only with something selected.
        var cut = Menus.Item("Cut", () => Input.Cut(), "Ctrl+X");
        var copy = Menus.Item("Copy", () => Input.Copy(), "Ctrl+C");
        var menu = Menus.Of(cut, copy, Menus.Item("Paste", () => Input.Paste(), "Ctrl+V"), Menus.Line(), Menus.Item("Select all", () => Input.SelectAll(), "Ctrl+A"));
        menu.Opening += (_, _) =>
        {
            var selected = Input.SelectionStart != Input.SelectionEnd;
            cut.IsEnabled = selected;
            copy.IsEnabled = selected;
        };
        Input.ContextFlyout = null;
        Input.ContextMenu = menu;
    }

    private void OnInputKey(object? sender, KeyEventArgs e)
    {
        var composer = Model.Composer;
        switch (e.Key)
        {
            case Key.Enter when e.KeyModifiers == KeyModifiers.None:
                if (composer.Marked is { } marked)
                {
                    Submit(marked);
                }
                else
                {
                    SendDraft();
                }

                e.Handled = true;
                break;
            case Key.Enter when e.KeyModifiers == KeyModifiers.Shift:
                // The text box itself puts the line break in.
                break;
            case Key.J when e.KeyModifiers == KeyModifiers.Control:
                InsertAtCaret("\n");
                e.Handled = true;
                break;
            case Key.Tab when composer.Complete() is { } completed:
                Input.Text = completed;
                Input.CaretIndex = completed.Length;
                e.Handled = true;
                break;
            case Key.Up or Key.Down when composer.ListOpen:
                composer.Move(e.Key == Key.Up ? -1 : 1);
                DrawPalette();
                e.Handled = true;
                break;
            case Key.Up or Key.Down when !(Input.Text ?? string.Empty).Contains('\n', StringComparison.Ordinal):
                if (Model.Recall(e.Key == Key.Up ? -1 : 1) is { } recalled)
                {
                    Input.Text = recalled;
                    Input.CaretIndex = recalled.Length;
                }

                e.Handled = true;
                break;
        }
    }

    private void InsertAtCaret(string text)
    {
        var current = Input.Text ?? string.Empty;
        var at = Math.Clamp(Input.CaretIndex, 0, current.Length);
        Input.Text = current.Insert(at, text);
        Input.CaretIndex = at + text.Length;
    }

    private void SendDraft()
    {
        var text = (Input.Text ?? string.Empty).Trim();

        // Files alone are a message too.
        if (text.Length == 0 && Model.Pending.Count == 0)
        {
            return;
        }

        // The text box breaks lines the Windows way; what is said carries plain line feeds, as the
        // terminal's does, so a message reads the same whichever screen it came from.
        Submit(text.ReplaceLineEndings("\n"));
    }

    private void Submit(string text)
    {
        Model.Composer.Clear();
        Input.Text = string.Empty;
        DrawPalette();
        _tab.FollowTail = true;

        // Off the UI thread, so a long answer never freezes typing; every change comes back
        // through Changed and the dispatcher.
        OffThread(() => Model.SubmitAsync(text));
    }

    private void ClosePalette()
    {
        Model.Composer.CloseList();
        DrawPalette();
        Input.Focus();
    }

    private void DrawPalette()
    {
        var composer = Model.Composer;
        PaletteHost.IsVisible = composer.ListOpen;
        if (!composer.ListOpen)
        {
            PaletteHost.Child = null;
            return;
        }

        var column = new StackPanel { Spacing = 1 };
        var matches = composer.Matches;
        var first = Math.Clamp(composer.Mark - (ComposerViewModel.MostRows - 1), 0, Math.Max(0, matches.Count - ComposerViewModel.MostRows));
        for (var i = first; i < Math.Min(matches.Count, first + ComposerViewModel.MostRows); i++)
        {
            var entry = matches[i];
            var on = i == composer.Mark;
            var row = new Button { Classes = { "row" } };
            if (on)
            {
                row.Classes.Add("current");
            }

            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("16,150,*") };
            var mark = new TextBlock { Text = on ? "›" : string.Empty, Foreground = Palette.Accent };
            var name = new TextBlock { Text = entry.Name, Foreground = on ? Palette.Accent : Palette.Ink, FontFamily = Palette.Mono, FontSize = 13 };
            var help = new TextBlock { Text = entry.Help, Foreground = Palette.Dim, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(name, 1);
            Grid.SetColumn(help, 2);
            line.Children.Add(mark);
            line.Children.Add(name);
            line.Children.Add(help);
            row.Content = line;

            // One click runs it; one that takes a word waits for the word in the composer.
            row.Click += (_, _) =>
            {
                if (entry.TakesArgument)
                {
                    Input.Text = entry.Name + " ";
                    Input.CaretIndex = Input.Text.Length;
                    ClosePalette();
                }
                else
                {
                    Submit(entry.Name);
                }
            };
            column.Children.Add(row);
        }

        var foot = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(8, 4, 0, 0) };
        foot.Children.Add(new TextBlock { Text = "Tab completes · Enter runs · Esc closes", Foreground = Palette.Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        var close = new Button { Content = "✕", Classes = { "tool" } };
        ToolTip.SetTip(close, "Close the list (Esc)");
        close.Click += (_, _) => ClosePalette();
        Grid.SetColumn(close, 1);
        foot.Children.Add(close);
        column.Children.Add(foot);
        PaletteHost.Child = column;
    }

    // ---- keys for the whole window ----------------------------------------------------------

    private void OnWindowKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            OnEscape(e);
            return;
        }

        // F1 to F10: the bar's keys, the screen's own, working whatever else is open.
        if (e.KeyModifiers == KeyModifiers.None && e.Key >= Key.F1 && e.Key <= Key.F10)
        {
            _keyBar.Press((int)e.Key - (int)Key.F1 + 1);
            e.Handled = true;
            return;
        }

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.F10 when e.KeyModifiers == KeyModifiers.Shift:
            case Key.Apps:
                // Windows' Shift+F10 and the menu key: the right-click menu of what has focus.
                e.Handled = OpenFocusedMenu();
                break;
            case Key.OemQuestion when ctrl:
                ToggleSheet(HelpScreen);
                e.Handled = true;
                break;
            case Key.Q when ctrl:
                RequestQuit();
                e.Handled = true;
                break;
            case Key.K when ctrl:
                CloseSheet();
                Input.Text = "/";
                Input.CaretIndex = 1;
                Input.Focus();
                e.Handled = true;
                break;
            case Key.O when ctrl:
                Submit("/model");
                e.Handled = true;
                break;
            case Key.OemComma when ctrl:
                ToggleSheet(SettingsScreen);
                e.Handled = true;
                break;
            // Tabs and windows, with the browser's keys (Chrome's keyboard shortcuts page).
            case Key.T when ctrl && shift:
                Reopen();
                e.Handled = true;
                break;
            case Key.T when ctrl:
                NewTab(isPrivate: false);
                e.Handled = true;
                break;
            case Key.N when ctrl && shift:
                NewTab(isPrivate: true);
                e.Handled = true;
                break;
            case Key.N when ctrl:
                NewWindow();
                e.Handled = true;
                break;
            case Key.W when ctrl:
            case Key.F4 when ctrl:
                CloseTab(_window, Model);
                e.Handled = true;
                break;
            case Key.Tab when ctrl:
                _app.Step(_window, shift ? -1 : 1);
                e.Handled = true;
                break;
            case Key.PageDown or Key.PageUp when ctrl && shift:
                _app.Move(_window, e.Key == Key.PageDown ? 1 : -1);
                e.Handled = true;
                break;
            case Key.PageDown or Key.PageUp when ctrl:
                _app.Step(_window, e.Key == Key.PageDown ? 1 : -1);
                e.Handled = true;
                break;
            case >= Key.D1 and <= Key.D9 when ctrl && !shift:
                _app.Go(_window, e.Key - Key.D0);
                e.Handled = true;
                break;
            case >= Key.NumPad1 and <= Key.NumPad9 when ctrl && !shift:
                _app.Go(_window, e.Key - Key.NumPad0);
                e.Handled = true;
                break;
            case Key.F when ctrl:
                if (_sheetName == SettingsScreen)
                {
                    _settings.ToggleFind();
                }
                else if (_sheetName == ActivityScreen)
                {
                    _activity.ToggleFind();
                }
                else if (_sheetName == HelpScreen)
                {
                    _helpPage.FocusSearch();
                }
                else if (_sheetName == TabsScreen)
                {
                    _tabsPage.ToggleFind();
                }
                else
                {
                    ToggleFind();
                }

                e.Handled = true;
                break;
            case Key.A when ctrl && _sheetName is null && !Prompt.Open && !TypingInAField():
                Messages.SelectAll();
                e.Handled = true;
                break;
            case Key.C when ctrl && !Prompt.Open:
                e.Handled = CopyOnKey();
                break;
            case Key.Tab when e.KeyModifiers == KeyModifiers.Shift && !Prompt.Open:
                OffThread(Model.CycleModeAsync);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Esc: counted toward the three that quit, then what Esc always does. A press counts once the
    /// key has come up since the last one, so Esc held down, the keyboard repeating it, is one press
    /// and never quits.
    /// </summary>
    private void OnEscape(KeyEventArgs e)
    {
        if (_escHeld)
        {
            e.Handled = true;
            return;
        }

        _escHeld = true;
        if (CountEscape())
        {
            e.Handled = true;
            QuitNow();
            return;
        }

        // With a question open, the question's own Esc runs next: it leaves the question.
        if (!Prompt.Open)
        {
            Escape();
            e.Handled = true;
        }
    }

    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _escHeld = false;
        }
    }

    /// <summary>Esc closes the nearest thing open, in this order; with nothing open, it stops the answer.</summary>
    private void Escape()
    {
        if (Messages.Selecting)
        {
            Messages.StopSelecting();
        }
        else if (Model.Composer.ListOpen)
        {
            ClosePalette();
        }
        else if (_sheetName == SettingsScreen && _settings.FindOpen)
        {
            _settings.ToggleFind();
        }
        else if (_sheetName == ActivityScreen && _activity.FindOpen)
        {
            _activity.ToggleFind();
        }
        else if (_sheetName == TabsScreen && _tabsPage.FindOpen)
        {
            _tabsPage.ToggleFind();
        }
        else if (_sheetName is not null)
        {
            CloseSheet();
        }
        else if (Find.IsVisible)
        {
            ToggleFind();
        }
        else
        {
            Model.Stop();
        }
    }

    /// <summary>The right-click menu of what has focus, or of the nearest thing around it that has one.</summary>
    private bool OpenFocusedMenu()
    {
        for (var at = FocusManager?.GetFocusedElement() as Control; at is not null; at = at.Parent as Control)
        {
            if (at.ContextMenu is { } menu)
            {
                menu.Open(at);
                return true;
            }
        }

        return false;
    }

    private void ToggleFind()
    {
        CloseSheet();
        Find.IsVisible = !Find.IsVisible;
        if (Find.IsVisible)
        {
            Find.Focus();
        }
        else
        {
            Find.Text = string.Empty;
            Input.Focus();
        }
    }

    private bool TypingInAField() => FocusManager?.GetFocusedElement() is TextBox;

    /// <summary>
    /// Ctrl+C: the ticked messages, else what is selected in a message. A text box copies for
    /// itself. The window copies rather than the text, whose own Copy follows a CanCopy that Avalonia
    /// gets wrong when a selection ends below the last character (issue 21622).
    /// </summary>
    private bool CopyOnKey()
    {
        if (Messages.Selecting && Messages.Selected.Count > 0)
        {
            _ = CopyAsync(Model.AsText(Messages.Selected));
            return true;
        }

        if (FocusManager?.GetFocusedElement() is SelectableTextBlock { SelectedText.Length: > 0 } text)
        {
            _ = CopyAsync(text.SelectedText);
            return true;
        }

        return false;
    }

    // ---- the clock beside the name ----------------------------------------------------------

    /// <summary>
    /// The time of day beside "DipTick", in the person's Windows time
    /// format. One timer, set for the next minute, rather than a tick every second.
    /// </summary>
    private void DrawClock()
    {
        var now = Now();
        Clock.Text = now.ToString("t", _timeCulture);
        var zone = TimeZoneInfo.Local;
        var zoneName = zone.IsDaylightSavingTime(now) ? zone.DaylightName : zone.StandardName;
        var offset = (now.Offset < TimeSpan.Zero ? "-" : "+") + now.Offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        ToolTip.SetTip(Clock, $"{now.ToString("D", _timeCulture)}\n{zoneName}, UTC{offset}");
        _minute.Stop();
        _minute.Interval = TimeSpan.FromMilliseconds(60_000 - ((now.Second * 1000) + now.Millisecond) + 50);
        _minute.Start();
    }

    /// <summary>
    /// Windows' own messages to the window: a changed time, zone or format redraws the clock at once
    /// (.NET keeps its own copies of the culture and the zone, so they are read again first); F10's
    /// key-up is kept from Windows, whose default for F10 is to enter the window's menu.
    /// </summary>
    private IntPtr OnWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WmTimeChange or WmSettingChange)
        {
            Dispatcher.UIThread.Post(() =>
            {
                CultureInfo.CurrentCulture.ClearCachedData();
                TimeZoneInfo.ClearCachedData();
                _timeCulture = new CultureInfo(CultureInfo.CurrentCulture.Name, useUserOverride: true);
                DrawClock();
            });
        }
        else if (msg == WmSysKeyUp && wParam == VkF10)
        {
            handled = true;
        }

        return IntPtr.Zero;
    }

    // ---- appearance --------------------------------------------------------------------------

    /// <summary>Settings, Appearance, applied: the theme, the text size (the whole window scaled), the density. Only when one changed.</summary>
    private void ApplyAppearance()
    {
        var config = Model.Config;
        var now = $"{config.AppearanceTheme}|{config.AppearanceTextSize}|{config.AppearanceDensity}";
        if (now == _appearance)
        {
            return;
        }

        _appearance = now;

        // Every part is drawn again in the new look.
        _drawnFrom.Clear();
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = config.AppearanceTheme switch
            {
                "light" => ThemeVariant.Light,
                "system" => ThemeVariant.Default,
                _ => ThemeVariant.Dark,
            };
        }

        Zoom.LayoutTransform = config.AppearanceTextSize switch
        {
            "small" => new ScaleTransform(0.9, 0.9),
            "large" => new ScaleTransform(1.15, 1.15),
            "larger" => new ScaleTransform(1.3, 1.3),
            _ => null,
        };
        var compact = config.AppearanceDensity == "compact";
        Classes.Set("compact", compact);

        // Fluent's own controls (lists, switches, boxes) take the density too.
        if (Application.Current?.Styles.OfType<Avalonia.Themes.Fluent.FluentTheme>().FirstOrDefault() is { } fluent)
        {
            fluent.DensityStyle = compact ? Avalonia.Themes.Fluent.DensityStyle.Compact : Avalonia.Themes.Fluent.DensityStyle.Normal;
        }

        foreach (var view in _views.Values.Where(v => v.Transcript.Compact != compact))
        {
            view.Transcript.Compact = compact;
        }
    }

    /// <summary>The theme changed (a setting, or Windows itself under "system"): everything drawn with the old brushes is drawn again.</summary>
    private void OnThemeVariantChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        _keysFor = null;
        _drawnFrom.Clear();
        foreach (var view in _views.Values)
        {
            view.HeroFrom = null;
            view.Transcript.Invalidate();
        }

        if (_sheetName == SettingsScreen)
        {
            _settings.Reload();
        }
        else if (_sheetName == HelpScreen)
        {
            _helpPage.Redraw();
        }
        else if (_sheetName == TabsScreen)
        {
            _tabsPage.Reload();
        }

        Redraw();
    });

    // ---- drawing -----------------------------------------------------------------------------

    /// <summary>Whether a part must be built again: what it is drawn from, as a line, differs from the last time.</summary>
    private bool NeedsDrawing(string part, string from)
    {
        if (_drawnFrom.TryGetValue(part, out var last) && last == from)
        {
            return false;
        }

        _drawnFrom[part] = from;
        return true;
    }

    private static long Id(object item) => Identity.Of(item);

    private void QueueRedraw()
    {
        // One redraw per burst of changes: a streaming answer raises Changed for every piece.
        if (Interlocked.Exchange(ref _redrawQueued, 1) == 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _redrawQueued, 0);
                Redraw();
            });
        }
    }

    /// <summary>The tabs and the bar's counts alone, once per burst: a tab behind the one in front changed, here or in another window.</summary>
    private void QueueStrip()
    {
        if (Interlocked.Exchange(ref _stripQueued, 1) == 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _stripQueued, 0);
                DrawTabs();
                DrawActivity();
                if (_sheetName == TabsScreen)
                {
                    _tabsPage.Reload();
                }
            });
        }
    }

    private void Redraw()
    {
        ApplyAppearance();
        ShowDraft();
        Messages.Render(Model.Transcript);
        var spoken = Model.Transcript.Any(l => l.Role is Role.Person or Role.Agent);
        DrawHero();
        Hero.IsVisible = !spoken;
        ChatColumn.MinHeight = spoken ? 0 : ChatScroll.Bounds.Height;
        if (spoken && _tab.FollowTail)
        {
            ChatScroll.ScrollToEnd();
        }

        // The window's name is the chat in front's: a dot while it answers, "Private" in words.
        Title = $"{(Model.Busy ? "● " : string.Empty)}{(Model.IsPrivate ? "Private · " : string.Empty)}{TabName(Model)} · DipTick";
        DrawKeys();
        DrawTabs();
        DrawActivity();
        DrawWhere();
        DrawMeters();
        DrawStatus();
        DrawPlanLine();
        DrawAttachRow();
        DrawRail();

        // The list of tabs says what the tab in front is doing too: its title as its chat opens, its answer.
        if (_sheetName == TabsScreen)
        {
            _tabsPage.Reload();
        }
    }

    /// <summary>
    /// The right end of the F-key bar: what runs across DipTick and what waits for the person, each
    /// one click from what it names. Left to right, the most pressing last, for a narrow window keeps
    /// the last and drops the first: the answers in other tabs and windows, this tab's answer, this
    /// tab's question, the tabs where a question waits. Nothing shows when nothing runs.
    /// </summary>
    private void DrawActivity()
    {
        var elsewhere = _app.AllTabs.Count(x => x.Tab.Busy && !ReferenceEquals(x.Tab, Model));
        var behind = _app.AllTabs.Where(x => x.Tab.Waiting && !ReferenceEquals(x.Tab, Model)).ToList();
        var from = string.Create(CultureInfo.InvariantCulture, $"{Id(Model)}:{Model.Busy}:{elsewhere}:{Prompt.Open}:{string.Join(',', behind.Select(x => $"{Id(x.Tab)}{x.Tab.WaitingForYes}"))}");
        if (!NeedsDrawing("activity", from))
        {
            return;
        }

        var activity = _keyBar.Activity;
        activity.Children.Clear();
        if (elsewhere > 0)
        {
            var others = new Button { Classes = { "chip" }, Content = string.Create(CultureInfo.InvariantCulture, $"{elsewhere} more answering") };
            ToolTip.SetTip(others, "Answers coming in other tabs. One click lists every tab (F2).");
            others.Click += (_, _) => OpenSheet(TabsScreen);
            activity.Children.Add(others);
        }

        if (Model.Busy)
        {
            var running = new Button { Classes = { "chip" }, Content = "answering  ■" };
            ToolTip.SetTip(running, "An answer is coming in this tab. One click stops it (F8, or Esc, on the chat).");
            running.Click += (_, _) => Model.Stop();
            activity.Children.Add(running);
        }

        if (Prompt.Open)
        {
            var waiting = new Button { Classes = { "chip" }, Content = "1 question waiting" };
            ToolTip.SetTip(waiting, "A question waits for you above the composer. One click goes to it.");
            waiting.Click += (_, _) =>
            {
                CloseSheet();
                Prompt.TakeFocus();
            };
            activity.Children.Add(waiting);
        }

        if (behind.Count > 0)
        {
            // "need your yes" when every question behind is an approval; any other question waits for an answer.
            var words = (behind.Count, behind.TrueForAll(x => x.Tab.WaitingForYes)) switch
            {
                (1, true) => "1 tab needs your yes",
                (1, false) => "1 tab is waiting for you",
                (_, true) => string.Create(CultureInfo.InvariantCulture, $"{behind.Count} tabs need your yes"),
                _ => string.Create(CultureInfo.InvariantCulture, $"{behind.Count} tabs are waiting for you"),
            };
            var yes = new Button { Classes = { "chip" }, Content = words };
            ToolTip.SetTip(yes, "A question waits in another tab. One click goes to it.");
            yes.Click += (_, _) => GoTo(behind[0].Window, behind[0].Tab);
            activity.Children.Add(yes);
        }
    }

    /// <summary>
    /// The chat's header. First line: the chat and its table, each agent
    /// a chip (its colour, name and model; one click opens its menu), then + Add agent. Second line: who
    /// answers a message with no @ (the answer chip), What they're told, and how answers are let
    /// through (the permissions chip); the chat's meters sit at its right.
    /// </summary>
    private void DrawWhere()
    {
        var table = Model.Table;
        var seats = string.Join(',', table.Select(s => $"{s.Name}/{s.Agent}/{s.Model}/{s.Color}/{s.SetUp}/{Model.TakesTools(s)}"));
        if (!NeedsDrawing("where", string.Create(CultureInfo.InvariantCulture, $"{Id(Model)}:{Model.IsPrivate}:{Id(Model.Config)}:{Model.Mode}:{Model.AnswerMode}:{Model.PermissionsWords}:{Model.ResearchWords}:{seats}")))
        {
            return;
        }

        Where.Children.Clear();
        Where.Children.Add(new TextBlock { Text = ChatScreen, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (Model.IsPrivate)
        {
            // Said in words, never by colour alone; the whole promise on the pointer.
            var words = new TextBlock { Text = "· Private: nothing kept", Foreground = Palette.Warning, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(words, Model.PrivateNotice());
            Where.Children.Add(words);
        }

        Where.Children.Add(new TextBlock { Text = "·", Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center });
        if (table.Count == 0)
        {
            Where.Children.Add(new TextBlock { Text = Model.SetUpAgents.Count == 0 ? "no model yet" : "nobody at this table", Foreground = Palette.Warning, VerticalAlignment = VerticalAlignment.Center });
        }

        foreach (var chip in table)
        {
            Where.Children.Add(SeatChip(chip));
        }

        if (Model.SetUpAgents.Count > 0 || table.Count > 0)
        {
            var add = new Button { Classes = { "chip" }, Content = "+ Add agent" };
            ToolTip.SetTip(add, "Seat another agent at this chat: every agent here answers you together, and @name asks one (F4)");
            add.Click += (_, _) =>
            {
                var menu = Menus.Of([.. AddAgentItems()]);
                add.ContextMenu = menu;
                menu.Open(add);
            };
            Where.Children.Add(add);
        }

        Answering.Children.Clear();
        var answer = new Button { Classes = { "chip" }, Content = Model.AnswerModeWords + "  ▾" };
        ToolTip.SetTip(answer, "Who answers when you name nobody: pick one. @name asks one agent, whatever this says.");
        answer.Click += (_, _) =>
        {
            var menu = Menus.Of([.. AnswerItems()]);
            answer.ContextMenu = menu;
            menu.Open(answer);
        };
        Answering.Children.Add(answer);

        var told = new Button { Classes = { "chip" }, Content = "What they're told" };
        ToolTip.SetTip(told, "Exactly what each agent at this table is sent before the chat, and this chat's own brief");
        told.Click += (_, _) => ToggleSheet(ToldScreen);
        Answering.Children.Add(told);

        // The Research chip: whether agents here may search and fetch; one click switches it.
        var researchState = Model.Research;
        var research = new Button { Classes = { "chip" }, Content = Model.ResearchWords + "  ▾" };
        ToolTip.SetTip(research, researchState switch
        {
            Models.Research.ResearchState.On => "Agents in this chat may search the web and fetch pages, each through your permissions; their sources show under each answer.",
            Models.Research.ResearchState.Off => "No agent in this chat searches or fetches until you switch research on.",
            _ => "No search service is set up, and no agent here has its provider's own search. Fetching a page you name still works.",
        });
        research.Click += (_, _) =>
        {
            var model = Model;
            var menu = Menus.Of(
                Menus.Item(researchState == Models.Research.ResearchState.Off ? "Research on" : "✓ Research on", () => OffThread(() => model.SetResearchAsync(true))),
                Menus.Item(researchState == Models.Research.ResearchState.Off ? "✓ Research off" : "Research off", () => OffThread(() => model.SetResearchAsync(false))),
                Menus.Line(),
                Menus.Item("Set up research, in Settings", () =>
                {
                    OpenSheet(SettingsScreen);
                    _settings.Show("Tools");
                }));
            research.ContextMenu = menu;
            menu.Open(research);
        };
        Answering.Children.Add(research);

        // The permissions chip: the settings' preset and this chat's own rules, then how
        // answers are let through. One click opens the three ways and this chat's permissions.
        var mode = new Button { Classes = { "chip" }, Content = $"{Model.PermissionsWords} · {ModeWords(Model.Mode)}  ▾" };
        ToolTip.SetTip(mode, "What agents may do here: the settings' preset, and this chat's own rules; then how answers are let through. Shift+Tab steps through those.");
        mode.Click += (_, _) =>
        {
            var model = Model;
            var modes = Menus.Of(
                ModeItem(ChatMode.Ask),
                ModeItem(ChatMode.Allow),
                ModeItem(ChatMode.DryRun),
                Menus.Line(),
                Menus.Item("This chat's permissions…", () => OffThread(model.ChatRulesAsync)),
                Menus.Item("All permissions, in Settings", () =>
                {
                    OpenSheet(SettingsScreen);
                    _settings.Show("Permissions");
                }));
            mode.ContextMenu = modes;
            modes.Open(mode);
        };
        Answering.Children.Add(mode);
    }

    /// <summary>An agent at the table as a chip: its colour, name and model; one click opens what can be done with it here.</summary>
    private Button SeatChip(TableChip chip)
    {
        var face = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        face.Children.Add(new TextBlock { Text = "●", Foreground = Palette.Seat(chip.Color), FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
        face.Children.Add(new TextBlock { Text = chip.Name, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        face.Children.Add(new TextBlock { Text = chip.Model.Length == 0 ? "no model" : chip.Model, Foreground = chip.SetUp ? Palette.Dim : Palette.Warning, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        if (Model.TakesTools(chip) == false)
        {
            // Said in words: this agent answers, but is told of no tools.
            face.Children.Add(new TextBlock { Text = "· no tools", Foreground = Palette.Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        }

        var button = new Button { Classes = { "chip" }, Content = face };
        ToolTip.SetTip(button, $"{chip.Name}: {chip.Provider} {chip.Model}{(chip.SetUp ? string.Empty : ", not set up any more")}. One click: another model here, what it is told, or take it from this chat.");
        button.Click += (_, _) =>
        {
            var menu = Menus.Of([.. SeatItems(chip)]);
            button.ContextMenu = menu;
            menu.Open(button);
        };
        return button;
    }

    /// <summary>What can be done with one agent at this table: another model here, what it is told, asking it the last question, taking it away, its settings.</summary>
    private List<Control> SeatItems(TableChip chip)
    {
        var model = Model;
        return
        [
            Menus.Item($"Another model for {chip.Name} in this chat…", () => OffThread(() => model.PickSeatModelAsync(chip.Name))),
            Menus.Item($"What {chip.Name} is told", () => ShowTold(chip.Name)),
            Menus.Item($"Ask {chip.Name} the last question", () => OffThread(() => model.RetryAsync(null, chip.Name))),
            Menus.Line(),
            Menus.Item($"Take {chip.Name} from this chat", () => OffThread(() => model.RemoveSeatAsync(chip.Name))),
            Menus.Line(),
            Menus.Item($"{chip.Agent}'s settings", () => ShowModelSettings(chip.Agent)),
        ];
    }

    /// <summary>The agents set up that can sit here, each one click away; one already here, again with another model; then setting up another.</summary>
    private List<Control> AddAgentItems()
    {
        var model = Model;
        var table = model.Table;
        var items = new List<Control>();
        foreach (var agent in model.SetUpAgents)
        {
            items.Add(table.Any(s => s.Agent == agent.Agent)
                ? Menus.Item($"{agent.Agent} again, with another model…", () => OffThread(() => model.AddSeatWithModelAsync(agent.Agent)))
                : Menus.Item($"{agent.Agent} · {agent.Provider} {agent.Model}", () => OffThread(() => model.AddSeatAsync(agent.Agent))));
        }

        if (items.Count > 0)
        {
            items.Add(Menus.Line());
        }

        items.Add(Menus.Item("Set up another agent…", StartAddModel));
        return items;
    }

    /// <summary>Who answers a message with no @: each mode this table can run, the one in force ticked; any other, typed.</summary>
    private List<Control> AnswerItems()
    {
        var model = Model;
        var current = model.AnswerMode;
        var items = new List<Control>();
        foreach (var (mode, words) in model.ModeChoices)
        {
            var item = Menus.Item(words, () => OffThread(() => model.SetAnswerModeAsync(mode)));
            item.ToggleType = MenuItemToggleType.Radio;
            item.IsChecked = mode == current;
            items.Add(item);
        }

        items.Add(Menus.Line());
        items.Add(Menus.Item("Another mode…", () => OffThread(model.AskAnswerModeAsync)));
        return items;
    }

    /// <summary>
    /// F4 Agents: the table in one menu, from the keyboard as from the mouse: each agent and what can be
    /// done with it, adding one, who answers, and what they are told.
    /// </summary>
    private void ShowTableMenu()
    {
        CloseSheet();
        var items = new List<Control>();
        foreach (var chip in Model.Table)
        {
            var seat = new MenuItem { Header = $"{chip.Name} · {chip.Model}" };
            foreach (var item in SeatItems(chip))
            {
                seat.Items.Add(item);
            }

            items.Add(seat);
        }

        var add = new MenuItem { Header = "Add agent" };
        foreach (var item in AddAgentItems())
        {
            add.Items.Add(item);
        }

        var answering = new MenuItem { Header = $"Who answers: {Model.AnswerModeWords}" };
        foreach (var item in AnswerItems())
        {
            answering.Items.Add(item);
        }

        items.Add(Menus.Line());
        items.Add(add);
        items.Add(answering);
        items.Add(Menus.Item("What they're told", () => ToggleSheet(ToldScreen)));
        var menu = Menus.Of([.. items]);
        menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
        menu.Open(Where);
    }

    /// <summary>Settings, Models, Add a model: an agent set up from the table, in the card above the composer.</summary>
    private void StartAddModel()
    {
        OpenSheet(SettingsScreen);
        _settings.Show("Models");
        _settings.StartAddModel();
    }

    /// <summary>What they're told, with one agent's text in view.</summary>
    private void ShowTold(string seat)
    {
        if (_sheetName != ToldScreen)
        {
            OpenSheet(ToldScreen);
        }

        _toldPage.Reload(seat);
    }

    /// <summary>
    /// The files waiting to go with the next message, over where you type: each
    /// a chip with its name, its size and who at the table will not see it, the rest on the pointer; ✕
    /// takes it off. Built again only when the files change.
    /// </summary>
    private void DrawAttachRow()
    {
        var pending = Model.Pending;
        if (!NeedsDrawing("attach", $"{Id(Model)}|{string.Join("|", pending.Select(p => p.Reference))}"))
        {
            return;
        }

        AttachRow.Children.Clear();
        AttachRow.IsVisible = pending.Count > 0;
        for (var i = 0; i < pending.Count; i++)
        {
            var file = pending[i];
            var index = i;
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (file.Image is { } image)
            {
                try
                {
                    using var stream = new MemoryStream(image);
                    chip.Children.Add(new Image { Source = Avalonia.Media.Imaging.Bitmap.DecodeToHeight(stream, 40, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality), Height = 20, VerticalAlignment = VerticalAlignment.Center });
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or NotSupportedException)
                {
                    // An image the window cannot draw is still sent: the chip shows its name alone.
                }
            }

            chip.Children.Add(new TextBlock { Text = Models.SafeText.Line(ChatViewModel.ChipWords(file)), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = file.NotSent.Count > 0 ? Palette.Warning : Palette.Ink });
            var off = new Button { Content = "✕", Classes = { "tool" }, Padding = new Thickness(4, 0) };
            ToolTip.SetTip(off, $"Take {file.Name} off the message");
            off.Click += (_, _) => Model.Detach(index);
            chip.Children.Add(off);
            var box = new Border { Child = chip, Classes = { "chip" }, Padding = new Thickness(8, 2, 2, 2), Margin = new Thickness(0, 0, 6, 4) };
            ToolTip.SetTip(box, Models.SafeText.Block(ChatViewModel.ChipTip(file)));
            AttachRow.Children.Add(box);
        }
    }

    /// <summary>F7, Attach: Windows' own open dialog, several files at once; each is read and checked as it is attached.</summary>
    private async Task PickFilesAsync()
    {
        if (!StorageProvider.CanOpen)
        {
            await Model.Say(Role.System, "This window cannot open a file dialog here; '/attach <file>' attaches one.").ConfigureAwait(true);
            return;
        }

        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach to your next message",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Images, PDFs and text files") { Patterns = [.. Models.Files.Attachments.Extensions.Select(e => "*" + e)] },
                FilePickerFileTypes.All,
            ],
        }).ConfigureAwait(true);
        var paths = new List<string>();
        foreach (var file in picked)
        {
            using (file)
            {
                if (file.TryGetLocalPath() is { } path)
                {
                    paths.Add(path);
                }
            }
        }

        AttachPaths(paths);
        Input.Focus();
    }

    /// <summary>Files attached to the next message, off the window's thread; what stood in the way of one is said in the chat.</summary>
    private void AttachPaths(List<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        var model = Model;
        OffThread(async () =>
        {
            foreach (var path in paths)
            {
                if (await model.AttachAsync(path).ConfigureAwait(false) is { } problem)
                {
                    await model.Say(Role.System, problem).ConfigureAwait(false);
                }
            }
        });
    }

    /// <summary>Something dragged over the chat: a copy is offered when it carries files.</summary>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Files dropped on the chat: each attached to the next message, as Attach attaches it.</summary>
    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is not { Length: > 0 } items)
        {
            return;
        }

        e.Handled = true;
        AttachPaths([.. items.Select(i => i.TryGetLocalPath()).OfType<string>()]);
    }

    /// <summary>
    /// A paste into the composer: text pastes as text, as it always has. A clipboard with no text but files
    /// (copied in Explorer) or an image (a screenshot) attaches them instead; a pasted image is held in
    /// memory until it is sent, never written anywhere.
    /// </summary>
    private void OnPasting(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard)
        {
            Report(PasteFilesAsync(clipboard, Model));
        }
    }

    private async Task PasteFilesAsync(IClipboard clipboard, ChatViewModel model)
    {
        if (await clipboard.TryGetTextAsync().ConfigureAwait(true) is { Length: > 0 })
        {
            return;
        }

        if (await clipboard.TryGetFilesAsync().ConfigureAwait(true) is { Length: > 0 } items)
        {
            AttachPaths([.. items.Select(i => i.TryGetLocalPath()).OfType<string>()]);
            return;
        }

        using var bitmap = await clipboard.TryGetBitmapAsync().ConfigureAwait(true);
        if (bitmap is null)
        {
            return;
        }

        byte[] png;
        using (var stream = new MemoryStream())
        {
            bitmap.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            png = stream.ToArray();
        }

        OffThread(async () =>
        {
            if (await model.AttachPastedAsync(png).ConfigureAwait(false) is { } problem)
            {
                await model.Say(Role.System, problem).ConfigureAwait(false);
            }
        });
    }

    /// <summary>The plan line over the composer: who will answer what is typed, or why it will not go; hidden while nothing is typed.</summary>
    private void DrawPlanLine()
    {
        var plan = Model.PlanFor(Input.Text ?? string.Empty);
        PlanLine.IsVisible = plan is not null;
        if (plan is { } line)
        {
            PlanLine.Text = line.Text;
            PlanLine.Foreground = line.Problem ? Palette.Warning : Palette.Dim;
        }
    }

    private MenuItem ModeItem(ChatMode mode)
    {
        var item = Menus.Item(ModeWords(mode), () => OffThread(() => Model.SetModeAsync(mode)));
        item.ToggleType = MenuItemToggleType.Radio;
        item.IsChecked = Model.Mode == mode;
        return item;
    }

    private static string ModeWords(ChatMode mode) => mode switch
    {
        ChatMode.Ask => "Ask before each answer",
        ChatMode.Allow => "Answers freely",
        ChatMode.DryRun => "Dry run",
        _ => "Custom permissions",
    };

    /// <summary>
    /// The chat's own meters, on the right of its header: its turns, its tokens and how long it has
    /// been open. Only while the chat is the screen: a sheet hides them.
    /// </summary>
    private void DrawMeters()
    {
        Meters.Children.Clear();
        Meters.IsVisible = _sheetName is null;
        if (_sheetName is not null)
        {
            return;
        }

        var turns = Model.Transcript.Count(l => l.Role == Role.Person);
        var tokens = Model.ApproxTokens;
        var shown = tokens >= 1000 ? $"≈{(tokens / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)}k tokens" : $"≈{tokens.ToString(CultureInfo.InvariantCulture)} tokens";

        // What this chat has cost comes first, so it is the last to give way.
        if (Model.ChatCost is { } cost)
        {
            Meters.Children.Add(new TextBlock { Text = cost, Foreground = Palette.Ink, FontSize = 12 });
        }

        Meters.Children.Add(new TextBlock { Text = turns == 1 ? "1 turn" : $"{turns.ToString(CultureInfo.InvariantCulture)} turns", Foreground = Palette.Dim, FontSize = 12 });
        Meters.Children.Add(new TextBlock { Text = shown, Foreground = Palette.Dim, FontSize = 12 });
        Meters.Children.Add(new TextBlock { Text = _clock.Elapsed.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture), Foreground = Palette.Dim, FontSize = 12 });
    }

    private void DrawStatus()
    {
        StatusLeft.Children.Clear();
        var state = Model.Working ?? (Model.Busy ? "answering…" : "ready");
        StatusLeft.Children.Add(new TextBlock { Text = state, Foreground = state == "ready" ? Palette.Accent : Palette.Warning, FontSize = 12 });
        foreach (var part in new[] { Model.Who, ModeWords(Model.Mode) })
        {
            StatusLeft.Children.Add(new TextBlock { Text = "│", Foreground = Palette.Hairline, FontSize = 12 });
            StatusLeft.Children.Add(new TextBlock { Text = part, Foreground = Palette.Dim, FontSize = 12 });
        }

        if (Model.Flags.Yes)
        {
            StatusLeft.Children.Add(new TextBlock { Text = "│ auto-yes", Foreground = Palette.Warning, FontSize = 12 });
        }

        // How many agents answer here, and today's cost across every chat where prices are set.
        var table = Model.Table.Count;
        StatusLeft.Children.Add(new TextBlock { Text = "│", Foreground = Palette.Hairline, FontSize = 12 });
        StatusLeft.Children.Add(new TextBlock { Text = table == 1 ? "1 agent" : $"{table.ToString(CultureInfo.InvariantCulture)} agents", Foreground = Palette.Dim, FontSize = 12 });
        if (Model.TodayCost is { } today)
        {
            StatusLeft.Children.Add(new TextBlock { Text = "│", Foreground = Palette.Hairline, FontSize = 12 });
            StatusLeft.Children.Add(new TextBlock { Text = today, Foreground = Palette.Dim, FontSize = 12 });
        }
    }
}
