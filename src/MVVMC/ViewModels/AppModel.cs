using System.Globalization;
using DipTick.Models;
using DipTick.Models.Chat;

namespace DipTick.ViewModels;

/// <summary>How a tab's chat starts: a kept chat to open (null for a new one), whether it is private, and whether it is the run's first.</summary>
internal sealed record TabStart(string? Session, bool Private, bool First);

/// <summary>One of DipTick's windows: its tabs, left to right, and the one in front.</summary>
internal sealed class AppWindow
{
    internal AppWindow(int number) => Number = number;

    /// <summary>The window's number, in the order the windows opened: "Window 2" in the list of tabs.</summary>
    public int Number { get; }

    public IReadOnlyList<ChatViewModel> Tabs => Items;

    /// <summary>The tab in front, by its place.</summary>
    public int Current { get; internal set; }

    public ChatViewModel CurrentTab => Items[Math.Clamp(Current, 0, Items.Count - 1)];

    internal List<ChatViewModel> Items { get; } = [];
}

/// <summary>
/// DipTick's windows and their tabs, which hold several chats at once, as a web browser's tabs hold
/// pages. Each tab is one chat, each chat its own <see cref="ChatViewModel"/>, which streams, asks
/// and answers on its own. This opens and closes tabs and windows, keeps the chats of closed tabs to
/// reopen, finds the tab a chat is open in (two tabs never hold one chat), brings back the windows
/// and tabs left open, and quits all of it at once. It draws nothing: each window draws its own.
/// </summary>
internal sealed class AppModel
{
    /// <summary>How many closed tabs Ctrl+Shift+T can bring back, the last closed first.</summary>
    private const int MostClosed = 25;

    private readonly Func<TabStart, ChatViewModel> _make;
    private readonly List<AppWindow> _windows = [];

    /// <summary>The kept chats of closed tabs, the last closed last; their drafts stay in the tabs' draft book.</summary>
    private readonly List<string> _closed = [];
    private readonly Lock _lock = new();
    private int _numbers;
    private int _quitting;
    private int _askingQuit;

    public AppModel(Func<TabStart, ChatViewModel> make, UiState? state = null)
    {
        ArgumentNullException.ThrowIfNull(make);
        _make = make;
        UiState = state;
    }

    /// <summary>A window opened here: the screen makes one for it.</summary>
    public event Action<AppWindow>? WindowOpened;

    /// <summary>A window's last tab closed, or the window was closed: the screen closes it.</summary>
    public event Action<AppWindow>? WindowClosed;

    /// <summary>A window should come to the front: a tab in it was chosen from another.</summary>
    public event Action<AppWindow>? ActivateRequested;

    /// <summary>Tabs opened, closed, moved or chosen. May come from any thread.</summary>
    public event Action? Changed;

    /// <summary>Something in one tab changed: its title, whether it answers, whether a question waits. May come from any thread.</summary>
    public event Action<ChatViewModel>? TabChanged;

    /// <summary>What the windows remember between runs; null when nothing is remembered.</summary>
    public UiState? UiState { get; }

    public IReadOnlyList<AppWindow> Windows => _windows;

    /// <summary>The window in front, as the screens say; questions for all of DipTick are asked there.</summary>
    public AppWindow? Active { get; set; }

    /// <summary>Set by the screens: how far down a tab is scrolled, 0 to 1, for the next run.</summary>
    public Func<ChatViewModel, double>? ScrollOf { get; set; }

    public bool Quitting => Volatile.Read(ref _quitting) == 1;

    /// <summary>Every tab in every window, the first window's first.</summary>
    public IEnumerable<(AppWindow Window, ChatViewModel Tab)> AllTabs => [.. _windows.SelectMany(w => w.Items.Select(t => (w, t)))];

    /// <summary>How many tabs are answering now.</summary>
    public int Busy => AllTabs.Count(x => x.Tab.Busy);

    /// <summary>How many tabs have a question waiting for the person.</summary>
    public int Waiting => AllTabs.Count(x => x.Tab.Waiting);

    /// <summary>How many closed tabs Ctrl+Shift+T can still bring back.</summary>
    public int ClosedCount
    {
        get
        {
            lock (_lock)
            {
                return _closed.Count;
            }
        }
    }

    /// <summary>
    /// The windows left open at the last quit, each with its tabs, their drafts and where they were
    /// scrolled (never a private tab); or, with nothing remembered or <paramref name="restore"/>
    /// false, one window with the tab <paramref name="first"/> describes, which takes the last run's
    /// new chat's draft.
    /// </summary>
    public void Start(TabStart first, bool restore)
    {
        ArgumentNullException.ThrowIfNull(first);
        var layout = restore && UiState is { } state ? state.Windows : [];
        if (layout.Count == 0)
        {
            OpenWindow(_make(first));
            return;
        }

        var isFirst = true;
        foreach (var saved in layout)
        {
            var window = new AppWindow(++_numbers);
            _windows.Add(window);
            foreach (var tab in saved.Tabs)
            {
                var model = _make(new TabStart(tab.Session, Private: false, isFirst));
                if (isFirst)
                {
                    // Each tab brought back has its own draft, the new chats' among them.
                    _ = model.Drafts.TakeNewChat();
                    isFirst = false;
                }

                model.SeedDraft(tab.Session, tab.Draft);
                model.StartScroll = tab.Scroll;
                Adopt(window, model, window.Items.Count);
            }

            window.Current = Math.Clamp(saved.Current, 0, window.Items.Count - 1);
            Active ??= window;
            WindowOpened?.Invoke(window);
        }

        Changed?.Invoke();
    }

    /// <summary>Ctrl+N: a new window with one tab, <paramref name="tab"/> or a new chat.</summary>
    public AppWindow OpenWindow(ChatViewModel? tab = null)
    {
        var window = new AppWindow(++_numbers);
        _windows.Add(window);
        Adopt(window, tab ?? _make(new TabStart(null, Private: false, First: false)), 0);
        Active = window;
        WindowOpened?.Invoke(window);
        Changed?.Invoke();
        return window;
    }

    /// <summary>Ctrl+T, and Ctrl+Shift+N for a private one: a new chat in a new tab, right of the one in front, and in front itself.</summary>
    public ChatViewModel NewTab(AppWindow window, bool isPrivate = false)
    {
        ArgumentNullException.ThrowIfNull(window);
        var tab = _make(new TabStart(null, isPrivate, First: false));
        Insert(window, tab, window.Current + 1);
        return tab;
    }

    /// <summary>A kept chat in a new tab, right of the one in front; the tab it is already open in, when it is.</summary>
    public ChatViewModel OpenInNewTab(AppWindow window, string session)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(session);
        if (Find(session) is { } open)
        {
            Select(open.Window, open.Tab);
            return open.Tab;
        }

        var tab = _make(new TabStart(session, Private: false, First: false));
        Insert(window, tab, window.Current + 1);
        return tab;
    }

    /// <summary>A kept chat in a new window; the tab it is already open in, when it is.</summary>
    public ChatViewModel OpenInNewWindow(string session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (Find(session) is { } open)
        {
            Select(open.Window, open.Tab);
            return open.Tab;
        }

        var tab = _make(new TabStart(session, Private: false, First: false));
        OpenWindow(tab);
        return tab;
    }

    /// <summary>
    /// Ctrl+W, Ctrl+F4, a tab's ✕: the tab closes and its kept chat stays kept, for Ctrl+Shift+T; a
    /// private tab's chat goes for good. The last tab closes its window. False, and nothing closed,
    /// when it is the last tab of the last window: closing that is quitting, which the caller asks.
    /// </summary>
    public bool CloseTab(AppWindow window, ChatViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(tab);
        var at = window.Items.IndexOf(tab);
        if (at < 0)
        {
            return true;
        }

        if (window.Items.Count == 1 && _windows.Count == 1)
        {
            return false;
        }

        Retire(tab);
        window.Items.RemoveAt(at);
        if (window.Items.Count == 0)
        {
            _windows.Remove(window);
            WindowClosed?.Invoke(window);
        }
        else if (at < window.Current || (at == window.Current && at == window.Items.Count))
        {
            // As a browser does: the tab to the right of a closed one comes in front, or, at the end, the one to its left.
            window.Current--;
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// A window's ✕ with another window open: its tabs close as Ctrl+W closes them. False, and
    /// nothing closed, when it is the last window: closing that is quitting.
    /// </summary>
    public bool CloseWindow(AppWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!_windows.Contains(window))
        {
            return true;
        }

        if (_windows.Count == 1)
        {
            return false;
        }

        foreach (var tab in window.Items)
        {
            Retire(tab);
        }

        window.Items.Clear();
        _windows.Remove(window);
        if (ReferenceEquals(Active, window))
        {
            Active = _windows[^1];
        }

        WindowClosed?.Invoke(window);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Ctrl+Shift+T: the last closed tab's chat back in a tab, with its draft; the tab it is open in, when it is. Null when none is left.</summary>
    public ChatViewModel? ReopenClosed(AppWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        string last;
        lock (_lock)
        {
            if (_closed.Count == 0)
            {
                return null;
            }

            last = _closed[^1];
            _closed.RemoveAt(_closed.Count - 1);
        }

        if (Find(last) is { } open)
        {
            Select(open.Window, open.Tab);
            return open.Tab;
        }

        var tab = _make(new TabStart(last, Private: false, First: false));
        Insert(window, tab, window.Current + 1);
        return tab;
    }

    /// <summary>A tab comes in front in its window, and the window to the front when it is not.</summary>
    public void Select(AppWindow window, ChatViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(window);
        var at = window.Items.IndexOf(tab);
        if (at < 0)
        {
            return;
        }

        window.Current = at;
        if (!ReferenceEquals(Active, window))
        {
            ActivateRequested?.Invoke(window);
        }

        Changed?.Invoke();
    }

    /// <summary>Ctrl+Tab and Ctrl+Shift+Tab, Ctrl+PgDn and Ctrl+PgUp: the next or the previous tab, round the end.</summary>
    public void Step(AppWindow window, int step)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.Items.Count > 1)
        {
            window.Current = ((window.Current + step) % window.Items.Count + window.Items.Count) % window.Items.Count;
            Changed?.Invoke();
        }
    }

    /// <summary>Ctrl+1 to Ctrl+8: that tab; Ctrl+9: the last. Nothing past the end.</summary>
    public void Go(AppWindow window, int number)
    {
        ArgumentNullException.ThrowIfNull(window);
        var at = number == 9 ? window.Items.Count - 1 : number - 1;
        if (at >= 0 && at < window.Items.Count)
        {
            window.Current = at;
            Changed?.Invoke();
        }
    }

    /// <summary>Ctrl+Shift+PgUp and Ctrl+Shift+PgDn: the tab in front moves one place left or right.</summary>
    public void Move(AppWindow window, int step)
    {
        ArgumentNullException.ThrowIfNull(window);
        var from = window.Current;
        var to = from + step;
        if (to < 0 || to >= window.Items.Count)
        {
            return;
        }

        (window.Items[from], window.Items[to]) = (window.Items[to], window.Items[from]);
        window.Current = to;
        Changed?.Invoke();
    }

    /// <summary>Move to new window: the tab leaves its window, which closes when it was the last, and opens a window of its own.</summary>
    public void MoveToNewWindow(AppWindow window, ChatViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(tab);
        var at = window.Items.IndexOf(tab);
        if (at < 0 || (window.Items.Count == 1 && _windows.Count == 1))
        {
            return;
        }

        window.Items.RemoveAt(at);
        if (window.Items.Count == 0)
        {
            _windows.Remove(window);
            WindowClosed?.Invoke(window);
        }
        else
        {
            window.Current = Math.Clamp(window.Current > at ? window.Current - 1 : window.Current, 0, window.Items.Count - 1);
        }

        var moved = new AppWindow(++_numbers);
        _windows.Add(moved);
        moved.Items.Add(tab);
        Active = moved;
        WindowOpened?.Invoke(moved);
        Changed?.Invoke();
    }

    /// <summary>The window and tab a kept chat is open in, or null: two tabs never hold one chat.</summary>
    public (AppWindow Window, ChatViewModel Tab)? Find(string session)
    {
        ArgumentNullException.ThrowIfNull(session);
        foreach (var (window, tab) in AllTabs)
        {
            if (!tab.IsPrivate && tab.SessionId == session)
            {
                return (window, tab);
            }
        }

        return null;
    }

    /// <summary>Says a tab changed where the screens do not hear it themselves: a question now waits in it, or no longer.</summary>
    public void Notify(ChatViewModel tab) => TabChanged?.Invoke(tab);

    /// <summary>
    /// Quits DipTick, every window and tab: with
    /// <paramref name="ask"/>, and only while something answers, it asks once, in the window in front;
    /// then every answer stops and is waited for while it keeps what it said, each local model that
    /// answered is let go once, the drafts and the tabs left open are kept for the next run (never a
    /// private one), and every window closes. Runs on a worker thread: the question and the waits block.
    /// </summary>
    public async Task QuitAsync(bool ask)
    {
        if (Quitting)
        {
            return;
        }

        var tabs = AllTabs.Select(x => x.Tab).ToList();
        var busy = tabs.Count(t => t.Busy);
        if (ask && busy > 0 && ((Active ?? _windows.FirstOrDefault())?.CurrentTab.Asker ?? tabs.FirstOrDefault()?.Asker) is { } asker)
        {
            // One question at a time: a second F10 while it is open adds nothing.
            if (Interlocked.CompareExchange(ref _askingQuit, 1, 0) != 0)
            {
                return;
            }

            try
            {
                var question = busy == 1
                    ? "An answer is still coming. Quit and stop it?"
                    : string.Create(CultureInfo.InvariantCulture, $"{busy} answers are still coming. Quit and stop them?");
                var sure = asker.Choose(
                    new Question("Quit", question, "What each has said so far is kept.", Now: true),
                    [new Choice("Quit", "stop the answers and close DipTick"), new Choice("Stay", "let them finish")],
                    0);
                if (sure.Reply != Reply.Answered || sure.Value != 0)
                {
                    return;
                }
            }
            finally
            {
                Volatile.Write(ref _askingQuit, 0);
            }
        }

        if (Interlocked.Exchange(ref _quitting, 1) == 1)
        {
            return;
        }

        Changed?.Invoke();
        await Task.WhenAll(tabs.Select(t => t.StopForQuitAsync())).ConfigureAwait(false);

        // The connectors' connections close, and the programs they ran end with their jobs.
        await Models.Connectors.ConnectorHub.CloseAllAsync().ConfigureAwait(false);
        if (tabs.Count > 0 && tabs[0].Config.QuitUnloadModels)
        {
            await tabs[0].UnloadAsync([.. tabs.SelectMany(t => t.AnsweredBy())]).ConfigureAwait(false);
        }

        // The drafts and the tabs left open, for the next run; the paste files no draft names go.
        var drafts = Drafts(tabs);
        var layout = Layout();
        var kept = UiState?.KeepQuit(drafts, layout) is null
            ? [.. drafts.Values, .. layout.SelectMany(w => w.Tabs.Select(t => t.Draft))]
            : Array.Empty<string>();
        _ = Pastes.Sweep([.. tabs.SelectMany(t => t.PastesToSweep())], kept);
        foreach (var tab in tabs)
        {
            tab.Close();
        }
    }

    /// <summary>
    /// The windows and tabs as they stand, for the next run: never a private tab. When a private tab
    /// is in front, the kept tab to its left comes back in front.
    /// </summary>
    public IReadOnlyList<WindowLayout> Layout() =>
    [
        .. _windows.Select(w =>
        {
            var tabs = w.Items.Where(t => !t.IsPrivate).Select(t => new TabLayout(t.SessionId, t.Draft, ScrollOf?.Invoke(t) ?? 1)).ToList();
            var keptUpToFront = w.Items.Take(w.Current + 1).Count(t => !t.IsPrivate);
            return new WindowLayout(tabs, Math.Max(0, keptUpToFront - 1));
        }).Where(w => w.Tabs.Count > 0),
    ];

    /// <summary>
    /// The drafts to keep: the kept chats' still in the list, from the tabs' one book, and the chat
    /// nobody has spoken in yet of the window in front; each tab's own is in the layout besides. Never
    /// a private chat's.
    /// </summary>
    private Dictionary<string, string> Drafts(List<ChatViewModel> tabs)
    {
        var drafts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tab in tabs)
        {
            foreach (var (chat, text) in tab.DraftsToKeep().Where(d => d.Key.Length > 0))
            {
                drafts[chat] = text;
            }
        }

        if ((Active ?? _windows.FirstOrDefault())?.CurrentTab is { IsPrivate: false, SessionId: null, Draft.Length: > 0 } front)
        {
            drafts[string.Empty] = front.Draft;
        }
        else if (tabs.Count > 0 && tabs[0].Drafts.NewChatWaiting is { Length: > 0 } waiting)
        {
            // The last run's new chat's draft, never taken this run (every tab was private): kept as it came.
            drafts[string.Empty] = waiting;
        }

        return drafts;
    }

    private void Insert(AppWindow window, ChatViewModel tab, int at)
    {
        Adopt(window, tab, Math.Clamp(at, 0, window.Items.Count));
        window.Current = window.Items.IndexOf(tab);
        Changed?.Invoke();
    }

    /// <summary>A tab joins a window: quitting from it is the app's, and the last run's new chat's draft goes to the first kept tab that comes.</summary>
    private void Adopt(AppWindow window, ChatViewModel tab, int at)
    {
        tab.QuitHook = QuitAsync;
        tab.Changed += () => TabChanged?.Invoke(tab);
        if (!tab.IsPrivate)
        {
            tab.SeedDraft(null, tab.Drafts.TakeNewChat());
        }

        window.Items.Insert(at, tab);
    }

    /// <summary>A tab closes: its answer stops, its kept chat stays for Ctrl+Shift+T with its draft in the book, and a private chat goes for good.</summary>
    private void Retire(ChatViewModel tab)
    {
        tab.Stop();
        tab.QuitHook = null;
        if (tab.IsPrivate)
        {
            tab.NewChat();
            return;
        }

        if (tab.SessionId is { } session)
        {
            lock (_lock)
            {
                _closed.Remove(session);
                _closed.Add(session);
                if (_closed.Count > MostClosed)
                {
                    _closed.RemoveAt(0);
                }
            }
        }
    }
}
