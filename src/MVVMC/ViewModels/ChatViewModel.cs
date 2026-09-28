using System.Globalization;
using System.Text;
using System.Text.Json;
using DipTick.Models;
using DipTick.Models.Chat;
using DipTick.Models.Chat.Store;
using DipTick.Models.Files;
using DipTick.Models.Harness;
using DipTick.Models.Keys;

namespace DipTick.ViewModels;

/// <summary>What a transcript row is: words said, an answer's head (its agent's name), or a note under an answer (a question it asked, what it used).</summary>
internal enum RowPart
{
    Text,
    Head,
    Note,
}

/// <summary>
/// One screen row of the transcript: who said it, whether it starts their line, its text, and for an
/// agent's answer its colour and which part of the answer the row is.
/// </summary>
internal sealed record TranscriptRow(Role Role, bool First, string Text, string Color = "", RowPart Part = RowPart.Text);

/// <summary>
/// How answers are gated this session: each one waits for a yes, or comes freely, or the session
/// is a dry run; Custom when the permission lists name <c>model.call</c> through a group or
/// <c>*</c>, which no single switch should touch.
/// </summary>
internal enum ChatMode
{
    Ask,
    Allow,
    DryRun,
    Custom,
}

/// <summary>
/// Everything <c>diptick ui</c> shows and does, with no screen code in it: the transcript, the
/// input history, the slash commands, the status line. The view draws this and sends it keys;
/// the same model could sit behind any other screen.
/// </summary>
/// <remarks>
/// Slash commands never do more than the verbs do. <c>/permissions</c> is <c>diptick policy
/// show</c>, <c>/audit</c> is <c>diptick audit</c>; there is nothing a person can do here that the
/// command line cannot. Talking is <c>diptick chat say --events</c>, run in this process, its
/// events drawn as they come; the session list is <c>chat list</c>, opening one is
/// <c>chat show</c>, naming it <c>chat title</c>, writing it out <c>chat export</c>, closing and
/// deleting it <c>chat close</c> and <c>chat delete</c>. This model keeps no store of its own.
/// Setup and the model picker go through the same model code their verbs use, and ask through
/// the screen's <see cref="Asker"/>. Settings is the one place a setting changes:
/// setup opens by itself once, then from Settings, You.
/// </remarks>
internal sealed partial class ChatViewModel
{
    private const int MostHistory = 500;

    /// <summary>Commands that take a word after them: Tab leaves a space for it.</summary>
    private static readonly HashSet<string> TakesArgument = new(StringComparer.OrdinalIgnoreCase) { "/dry-run", "/yes", "/audit", "/model", "/export", "/rename", "/open", "/copy", "/seats", "/mode", "/retry", "/brief", "/cost", "/calc", "/allow", "/ask", "/deny", "/research", "/sources", "/attach", "/detach", "/files", "/save", "/image", "/video", "/connectors", "/connector", "/skills", "/skill" };

    /// <summary>
    /// How long quitting waits for the answer that runs to end, and then for each local runtime to
    /// let go of a model, before it closes anyway: a hung runtime never holds the quit.
    /// </summary>
    private static readonly TimeSpan QuitWait = TimeSpan.FromSeconds(5);

    private readonly List<ChatLine> _transcript = [];

    /// <summary>Guards <see cref="_transcript"/>: worker threads write it as answers stream, while every tab's screen reads it to draw.</summary>
    private readonly Lock _lines = new();
    private readonly List<string> _history = [];
    private readonly SortedDictionary<string, (string Help, Func<string, Task> Run)> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    /// <summary>The kept chats' unsent drafts: this chat's own book, or the one every tab shares (<see cref="Drafts"/>).</summary>
    private DraftBook _book = new();

    /// <summary>Whether <see cref="_book"/> is shared by the tabs, so what the last run kept is theirs to hand out.</summary>
    private bool _sharedBook;

    /// <summary>The draft of the chat nobody has spoken in yet, or of the private chat on screen: this chat's alone, never in the book.</summary>
    private string _ownDraft = string.Empty;

    /// <summary>The models that answered in this run, by the section that names them: what quitting lets go of.</summary>
    private readonly HashSet<(string Section, string Model)> _answeredBy = [];

    /// <summary>The paste files this run made (<see cref="KeepPaste"/>): what quitting sweeps.</summary>
    private readonly List<string> _pastesMade = [];

    /// <summary>The last paste's number; -1 until the first, which counts on from the kept drafts.</summary>
    private int _pasteNumber = -1;
    private readonly UiState? _uiState;
    private IChatEngine _engine;
    private int _historyAt;
    private CancellationTokenSource? _turn;
    private Task? _running;
    private string? _sessionId;
    private string _title = string.Empty;
    private IReadOnlyList<SessionInfo>? _sessions;
    private int _askingQuit;
    private int _quitting;

    /// <summary>Whether the chat on screen is private: in this process's memory only, never listed, never drafted, never exported.</summary>
    private bool _private;

    /// <summary>The private chats this model has made, by id: their drafts are never kept.</summary>
    private readonly HashSet<string> _privateIds = new(StringComparer.Ordinal);

    public ChatViewModel(MasterConfig config, RunFlags flags, IChatEngine engine)
    {
        ArgumentNullException.ThrowIfNull(config);
        Config = config;
        Flags = flags;
        _engine = engine;
        Gate = new Gate(config.Permissions, flags, AuditLog.ForThisUser(), Who, TextWriter.Null, AskPerson);
        Composer = new ComposerViewModel(() => [.. _commands.Select(c => new CommandEntry(c.Key, c.Value.Help, TakesArgument.Contains(c.Key)))]);

        Func<string, Task> quit = _ => QuitAsync(ask: true);

        _commands["/help"] = ("the commands and keys (also F1)", _ => Say(Role.System, HelpText()));
        _commands["/quit"] = ("leave DipTick; it asks first while an answer runs (also F10, Ctrl+Q, or exit typed alone)", quit);
        _commands["/exit"] = ("leave DipTick (same as /quit)", quit);
        _commands["/stop"] = ("stop the answer in progress (also Esc)", _ => { Stop(); return Task.CompletedTask; });

        // Chats: each one a diptick chat verb, on the chat on screen.
        _commands["/new"] = ("a new chat; this one stays in the list (also Ctrl+T)", _ => { NewChat(); return Task.CompletedTask; });
        _commands["/private"] = ("a private chat: nothing of it is kept, and it says who sees what you send (also Ctrl+Shift+N)", _ => { NewChat(isPrivate: true); return Task.CompletedTask; });
        _commands["/window"] = ("the window, opened beside the terminal screen, which goes on working (there, also F2)", _ => OpenWindowAsync());
        _commands["/pin"] = ("/pin [off]: keep this chat at the top of the list, or let it go back (diptick chat pin)", PinAsync);
        _commands["/open"] = ("/open <chat>: open a kept chat by its id, or last; alone, list them (diptick chat list)", OpenAsync);
        _commands["/rename"] = ("/rename <title>: name this chat (diptick chat title)", RenameAsync);
        _commands["/export"] = ("/export <file>: write this chat to a text file (diptick chat export)", ExportAsync);
        _commands["/copy"] = ("/copy [n|all]: the last answer, the n-th from the end, or the whole chat, to the clipboard", CopyAsync);
        _commands["/close"] = ("close this chat: it leaves the list and is kept (diptick chat close)", _ => CloseAsync());
        _commands["/delete"] = ("delete this chat for good, after your yes (diptick chat delete)", _ => DeleteAsync());

        // The chat's table: each a diptick chat verb, on the chat on screen.
        _commands["/seats"] = ("/seats [add <agent> [<model>] | remove <name> | model <name> <model>]: who sits at this chat (diptick chat seats; also F4)", SeatsAsync);
        _commands["/mode"] = ("/mode [<mode>]: who answers when you name nobody: all, solo <name>, pick, relay <a> <b>, review <a> <b>, stack <a> <b> [turns] (diptick chat mode)", ModeAsync);
        _commands["/retry"] = ("/retry [<name>]: the last question asked again, of the same agent or another (diptick chat retry; also F5)", RetryCommandAsync);
        _commands["/brief"] = ("/brief [<words>]: exactly what each agent is told; with words, this chat's own brief (diptick chat brief)", BriefAsync);
        _commands["/cost"] = ("/cost [today]: what this chat, or today, has cost where prices are set (diptick chat cost)", CostAsync);
        _commands["/pass"] = ("let the agents go on with no new words, as a debate does (diptick chat pass)", _ => PassAsync());

        // Tools and this chat's permissions.
        _commands["/tools"] = ("DipTick's own tools, which agents may call too, and the time now (diptick tools)", ToolsAsync);
        _commands["/calc"] = ("/calc <arithmetic>: exact decimal arithmetic, like 17% of 2345.60 (diptick tools calc)", CalcAsync);
        _commands["/allow"] = ("/allow <capability>: allowed in this chat, where the settings ask; never loosens a never (diptick policy set --session)", arg => ChatRuleCommandAsync("allow", arg));
        _commands["/ask"] = ("/ask <capability>: ask first in this chat (diptick policy set --session)", arg => ChatRuleCommandAsync("ask", arg));
        _commands["/deny"] = ("/deny <capability>: never in this chat (diptick policy set --session)", arg => ChatRuleCommandAsync("deny", arg));
        _commands["/rules"] = ("this chat's own permissions: see and change them (diptick policy chat)", _ => ChatRulesAsync());

        // Research.
        _commands["/research"] = ("/research [on|off]: whether agents in this chat may search and fetch (diptick chat research)", ResearchCommandAsync);
        _commands["/sources"] = ("/sources [n]: what this chat's answers were given to read, numbered; n for one answer's (diptick chat sources)", SourcesCommandAsync);

        // Files, images and video.
        _commands["/attach"] = ("/attach <file>: an image, a PDF or a text file for your next message; alone, what is attached (also F7)", AttachCommandAsync);
        _commands["/detach"] = ("/detach [n|all]: a file taken off your next message", DetachCommandAsync);
        _commands["/files"] = ("/files [n]: this chat's files, or line n's (diptick chat files)", FilesCommandAsync);
        _commands["/save"] = ("/save <n> <file>: line n's words to a file; --block <k> for one of its code blocks, --file <id> for a file of the chat (diptick chat save)", SaveCommandAsync);
        _commands["/image"] = ("/image <words>: an image made from your words, kept in this chat; it costs money on your key (diptick image)", ImageCommandAsync);
        _commands["/video"] = ("/video <words>: a short video made by Veo, kept in this chat; it costs money on your key (diptick video)", VideoCommandAsync);

        // Connectors.
        _commands["/connectors"] = ("the connectors, and which are on in this chat (diptick chat connectors)", _ => ConnectorsCommandAsync());
        _commands["/connector"] = ("/connector on|off <name>: a connector on or off in this chat, so its tools are offered here or not (diptick chat connectors)", ConnectorCommandAsync);

        // Skills.
        _commands["/skills"] = ("the skills, and which are on in this chat (diptick chat skills)", _ => SkillsCommandAsync());
        _commands["/skill"] = ("/skill <name>: an approved skill on in this chat; /skill off <name> takes it out (diptick chat skills)", SkillCommandAsync);

        // Settings are changed in one place. The model is picked here too.
        _commands["/settings"] = ("every setting: its value, what it does, its default; change any (also Ctrl+,)", _ => OpenSettingsAsync());
        _commands["/model"] = ("pick the model from the provider's list (/model <name> sets it directly)", Model);

        // Switches for this window only; nothing is saved.
        _commands["/dry-run"] = ("/dry-run on|off: report changes instead of making them", arg => Toggle(arg, on => Flags = Flags with { DryRun = on }, () => Flags.DryRun, "dry run"));
        _commands["/yes"] = ("/yes on|off: answer your own confirmations", arg => Toggle(arg, on => Flags = Flags with { Yes = on }, () => Flags.Yes, "auto-yes"));

        // Each of these is a diptick verb, run in this process with this session's flags.
        _commands["/status"] = ("who, which config, and this session's switches", _ => Status_());
        _commands["/permissions"] = ("what agents may do (diptick policy show)", _ => Verb("policy", "show"));
        _commands["/audit"] = ("/audit [n]: the last decisions (diptick audit)", arg => arg.Length == 0 ? Verb("audit", "15") : Verb("audit", arg));
        _commands["/doctor"] = ("check everything that can be checked (diptick doctor)", _ => Verb("doctor"));
        _commands["/whoami"] = ("who is signed in (diptick whoami)", _ => Verb("whoami"));
        _commands["/version"] = ("the version (diptick version)", _ => Verb("version"));
    }

    /// <summary>Raised on any change the screen should redraw for. May come from any thread.</summary>
    public event Action? Changed;

    /// <summary>Raised once, when quitting has stopped everything: the screen closes. May come from any thread.</summary>
    public event Action? QuitRequested;

    public MasterConfig Config { get; private set; }

    /// <summary>Where setup and /settings save, or null when nothing can be saved (--config none).</summary>
    public string? SettingsPath { get; init; }

    /// <summary>True when no model is set up: the screen opens setup on top of itself at once.</summary>
    public bool SetupOnStart { get; init; }

    /// <summary>A kept session to open first (an id, or "last"), or null for a new one.</summary>
    public string? OpenOnStart { get; init; }

    /// <summary>
    /// Set by the terminal screen: starts the window as a program of its own, on the kept session
    /// given, or as it was last left for none; null once it has started, or why it could not. Null
    /// in the window, which has nothing to open.
    /// </summary>
    public Func<string?, string?>? OpenWindow { get; set; }

    /// <summary>The kept session on screen, or null while a new chat has not been spoken in.</summary>
    public string? SessionId => _sessionId;

    /// <summary>Whether the chat on screen is private: kept in this process's memory only, and gone when it closes.</summary>
    public bool IsPrivate => _private;

    /// <summary>
    /// Set by the app when this chat sits in a tab: quitting is the app's, across every tab and
    /// window, so /quit, exit and Ctrl+C from any tab quit it all.
    /// </summary>
    public Func<bool, Task>? QuitHook { get; set; }

    /// <summary>Set by the screen: whether a question waits for the person in this chat, so a tab behind others can say so.</summary>
    public bool Waiting { get; set; }

    /// <summary>Set by the screen: whether the question waiting is an approval, whose tab says "needs your yes" rather than "waiting for you".</summary>
    public bool WaitingForYes { get; set; }

    /// <summary>Set by the app for a tab brought back from the last run: how far down it was scrolled, 0 to 1; the screen reads it once.</summary>
    public double? StartScroll { get; set; }

    /// <summary>A draft brought back for a tab: the kept chat's, or, with <paramref name="session"/> null, the new chat's.</summary>
    public void SeedDraft(string? session, string draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Length == 0)
        {
            return;
        }

        if (session is not null)
        {
            _book.Set(session, draft);
            return;
        }

        lock (_lock)
        {
            _ownDraft = draft;
        }
    }

    /// <summary>
    /// The kept chats' unsent drafts. The tabs of a window share one book, set by whoever makes them
    /// (<see cref="AppModel"/>), which hands the last run's new chat's draft to one tab only. A chat on
    /// its own has a book of its own, from what <see cref="UiState"/> kept, and takes that draft itself.
    /// </summary>
    internal DraftBook Drafts
    {
        get => _book;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _book = value;
            _sharedBook = true;
            _ownDraft = string.Empty;
        }
    }

    /// <summary>What the session is called: its title, else its first words, else "New chat".</summary>
    public string SessionTitle
    {
        get
        {
            if (_title.Length > 0)
            {
                return _title;
            }

            string? first;
            lock (_lines)
            {
                first = _transcript.FirstOrDefault(l => l.Role == Role.Person)?.Text;
            }

            return string.IsNullOrWhiteSpace(first) ? "New chat" : ChatStores.TitleFrom(first);
        }
    }

    public RunFlags Flags { get; private set; }

    public Gate Gate { get; private set; }

    /// <summary>The input line's state: the command list, the draft, pastes.</summary>
    public ComposerViewModel Composer { get; }

    /// <summary>Set by the view: asks the person in the screen's dialogs. Called from worker threads only.</summary>
    public IAsker? Asker { get; set; }

    /// <summary>Set by the view: shows the settings screen and returns when it is closed.</summary>
    public Func<Task>? SettingsHook { get; set; }

    /// <summary>Set by the view: opens Help at a page, by the page's id (Settings, Help; Data's "What is this?").</summary>
    public Action<string>? DocsHook { get; set; }

    /// <summary>
    /// Set by the window: Windows' save dialog, with a title that says what the file holds, a
    /// suggested name, what kind of file it is, and its extension; the path picked, or null. The
    /// terminal screen has none, and asks for the path instead.
    /// </summary>
    public Func<string, string, string, string, Task<string?>>? SaveHook { get; set; }

    /// <summary>Set by the view: asks the person, on the screen, and returns their answer.</summary>
    public Func<string, bool?>? ConfirmHook { get; set; }

    /// <summary>
    /// Set by the view: an agent's request, asked with three answers, Allow once, Allow for this chat and
    /// Deny. Without it, an approval is <see cref="ConfirmHook"/>'s yes or no.
    /// </summary>
    public Func<string, Approval?>? ApproveHook { get; set; }

    /// <summary>
    /// Set by the view: puts text on the clipboard, and says whether it could. Only ever run for
    /// something the person asked to copy.
    /// </summary>
    public Func<string, Task<bool>>? ClipboardHook { get; set; }

    /// <summary>
    /// What the window remembers between runs: that setup has opened by itself once, and the drafts
    /// left unsent at the last quit, which come back here. Null when nothing is remembered.
    /// </summary>
    public UiState? UiState
    {
        get => _uiState;
        init
        {
            _uiState = value;
            if (!_sharedBook)
            {
                _book = new DraftBook(value?.Drafts);
                _ownDraft = _book.TakeNewChat();
            }
        }
    }

    /// <summary>
    /// What is typed and not sent in the chat on screen. The screen writes it as the person types
    /// and reads it when the chat on screen changes; it follows a new chat to the id its first
    /// answer gives it, and quitting keeps it for the next run.
    /// </summary>
    public string Draft
    {
        get
        {
            lock (_lock)
            {
                return _sessionId is null || _private ? _ownDraft : _book.Get(_sessionId);
            }
        }

        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_lock)
            {
                if (_sessionId is null || _private)
                {
                    _ownDraft = value;
                }
                else
                {
                    _book.Set(_sessionId, value);
                }
            }
        }
    }

    /// <summary>True once quitting has begun: nothing new is started.</summary>
    public bool Quitting => Volatile.Read(ref _quitting) == 1;

    /// <summary>The chat's lines as they stand now: a copy, so a screen can draw it while an answer still streams into the chat.</summary>
    public IReadOnlyList<ChatLine> Transcript
    {
        get
        {
            lock (_lines)
            {
                return [.. _transcript];
            }
        }
    }

    public bool Busy => _turn is not null;

    public string Who => Config.UserName.Length > 0 ? Config.UserName : Environment.UserName;

    public IEnumerable<string> CommandNames => _commands.Keys;

    /// <summary>Where pastes are kept: <c>pastes</c> in DipTick's data folder.</summary>
    public static string PasteFolder => Pastes.Folder(AuditLog.DataFolder());

    /// <summary>
    /// Settings, Data, Export all my data: where the zip goes (the window's save dialog, or a typed
    /// path in the terminal), then <c>diptick data export</c>, whose word is said in the chat. Runs on
    /// a worker thread: the questions block.
    /// </summary>
    public async Task ExportAllDataAsync()
    {
        var suggested = string.Create(CultureInfo.InvariantCulture, $"DipTick data {DateTime.Now:yyyy-MM-dd}.zip");
        string? path = null;
        if (SaveHook is { } save)
        {
            path = await save("Export all my data (the file holds your chats as plain text)", suggested, "Zip file", "zip").ConfigureAwait(false);
        }
        else if (Asker is { } asker)
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var typed = asker.Text(new Question("Export all my data", "Where should the zip go?", "It holds your chats as plain text: keep it where you would keep them."), Path.Combine(documents, suggested), text => text.Trim().Length == 0 ? "a file's path" : null);
            path = typed.Reply == Reply.Answered ? typed.Value.Trim() : null;
        }

        if (path is null)
        {
            return;
        }

        var (said, _) = Capture(false, "data", "export", path);
        await Say(Role.System, said).ConfigureAwait(false);
    }

    /// <summary>
    /// A paste of <c>chat.pasteLines</c> lines or more, kept in a file (<see cref="Pastes"/>): the
    /// reference that stands for it in the composer, or null when it goes into the composer as it
    /// is. A file that cannot be written throws, and the screen puts the paste in the composer.
    /// </summary>
    public string? KeepPaste(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var least = Config.Chat.PasteLines;
        if (least <= 0 || Pastes.Lines(text) < least)
        {
            return null;
        }

        lock (_lock)
        {
            if (_pasteNumber < 0)
            {
                _pasteNumber = Pastes.Highest([.. _book.Texts(), _ownDraft]);
            }

            var (reference, path) = Pastes.Keep(PasteFolder, _pasteNumber + 1, text, DateTime.Now);
            _pasteNumber++;
            _pastesMade.Add(path);
            return reference;
        }
    }

    /// <summary>The window's title: who you are talking to, or what to do first.</summary>
    public string Title => Table.Count > 0
        ? $"DipTick · {string.Join(", ", Table.Select(s => s.Name))} · /help"
        : Config.MainProvider.Length == 0
            ? "DipTick · no model yet · /settings"
            : Config.MainModel.Length == 0
                ? $"DipTick · {Engines.DisplayName(Config.MainProvider, Config.MainEndpoint)}, no model yet · /model"
                : $"DipTick · nobody at this chat's table · /seats";

    /// <summary>What is running while the person waits (a key check, a model list), or null.</summary>
    public string? Working { get; set; }

    public string Status
    {
        get
        {
            var table = Table;
            var parts = new List<string>
            {
                Working ?? (Busy ? "answering…" : "ready"),
                Who,
                table.Count switch
                {
                    0 => "nobody at the table",
                    1 => $"{table[0].Name} · {table[0].Model}",
                    _ => $"{table.Count.ToString(CultureInfo.InvariantCulture)} agents: {string.Join(", ", table.Select(s => s.Name))}",
                },
            };
            if (TodayCost is { } today)
            {
                parts.Add(today);
            }

            if (Pending.Count is > 0 and var waiting)
            {
                parts.Add(waiting == 1 ? "1 file attached (/attach lists it)" : $"{waiting.ToString(CultureInfo.InvariantCulture)} files attached (/attach lists them)");
            }
            if (Flags.DryRun)
            {
                parts.Add("DRY RUN");
            }

            if (Flags.Yes)
            {
                parts.Add("auto-yes");
            }

            if (Config.UserPasswordHash.Length == 0)
            {
                parts.Add("no password set");
            }

            return string.Join(" │ ", parts);
        }
    }

    /// <summary>Roughly how much of the conversation an answer carries: four characters to a token.</summary>
    public int ApproxTokens
    {
        get
        {
            lock (_lines)
            {
                return _transcript.Sum(l => l.Text.Length) / 4;
            }
        }
    }

    public ChatMode Mode => Flags.DryRun ? ChatMode.DryRun
        : !PlainPermissions ? ChatMode.Custom
        : Config.Permissions.Ask.Any(r => Capabilities.IsExactly(r, Capability.ModelCall)) ? ChatMode.Ask
        : ChatMode.Allow;

    /// <summary>Whether <c>model.call</c> is named only on its own in the lists, so one switch can move it.</summary>
    private bool PlainPermissions =>
        !Config.Problems.Any(p => p.Contains("permissions section cannot be trusted", StringComparison.Ordinal))
        && SettingsCatalogue.NamesOnlyExactly(Config.Permissions.Allow, Capability.ModelCall)
        && SettingsCatalogue.NamesOnlyExactly(Config.Permissions.Ask, Capability.ModelCall)
        && !Config.Permissions.Deny.Any(r => Capabilities.Matches(r, Capability.ModelCall));

    /// <summary>Shift+Tab: Ask, Allow, Dry run, Ask, each step as <see cref="SetModeAsync"/> takes it.</summary>
    public Task CycleModeAsync() => SetModeAsync(Mode switch
    {
        ChatMode.Ask => ChatMode.Allow,
        ChatMode.Allow => ChatMode.DryRun,
        ChatMode.DryRun => ChatMode.Ask,
        _ => ChatMode.Custom,
    });

    /// <summary>
    /// The mode picked from the mode chip's menu, in one click, or stepped to with Shift+Tab.
    /// Narrowing is free; answering freely asks first, because it lets agents answer with no person
    /// in the loop. Custom permission lists are never touched by the switch.
    /// </summary>
    public async Task SetModeAsync(ChatMode target)
    {
        const string CustomWords = "Your permission lists are custom, so the mode switch leaves them alone. Change them in Settings, Permissions.";
        if (target == ChatMode.Custom)
        {
            await Say(Role.System, CustomWords).ConfigureAwait(false);
            return;
        }

        if (Mode == target)
        {
            return;
        }

        if (target == ChatMode.DryRun)
        {
            SetDryRun(true);
            await Say(Role.System, "Mode: dry run. Anything that would change something is reported instead of done.").ConfigureAwait(false);
            Changed?.Invoke();
            return;
        }

        if (!PlainPermissions)
        {
            var wasDry = Flags.DryRun;
            SetDryRun(false);
            await Say(Role.System, wasDry ? $"Dry run off. {CustomWords}" : CustomWords).ConfigureAwait(false);
            Changed?.Invoke();
            return;
        }

        if (target == ChatMode.Ask)
        {
            SetDryRun(false);
            if (!AsksEachAnswer)
            {
                SetAskEachAnswer(true);
            }

            await Say(Role.System, "Mode: each answer waits for your yes.").ConfigureAwait(false);
            Changed?.Invoke();
            return;
        }

        if (Asker is not { } asker)
        {
            return;
        }

        var sure = asker.YesNo(new Question("Mode", "Let agents answer without asking each time?", "Every answer is still recorded. The mode chip, or Shift+Tab, puts Ask back."), false);
        if (sure.Reply != Reply.Answered || !sure.Value)
        {
            return;
        }

        SetDryRun(false);
        if (AsksEachAnswer)
        {
            SetAskEachAnswer(false);
        }

        await Say(Role.System, "Mode: answers come freely.").ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary>Whether <c>model.call</c> sits in the ask list, so each answer waits for a yes.</summary>
    private bool AsksEachAnswer => Config.Permissions.Ask.Any(r => Capabilities.IsExactly(r, Capability.ModelCall));

    /// <summary>Dry run on or off for this window; the gate reads the flags it was built with, so a new one carries the change.</summary>
    private void SetDryRun(bool on)
    {
        if (Flags.DryRun != on)
        {
            Flags = Flags with { DryRun = on };
            Gate = new Gate(Config.Permissions, Flags, AuditLog.ForThisUser(), Who, TextWriter.Null, AskPerson);
        }
    }

    /// <summary>
    /// Moves <c>model.call</c> between the allow and ask lists, with the yes the person just gave. Two
    /// verbs: it joins its new list first and leaves the old one second, so in between it sits in
    /// both, where ask beats allow, and it is never more allowed than before or after.
    /// </summary>
    public void SetAskEachAnswer(bool ask)
    {
        var p = Config.Permissions;
        var allow = p.Allow.Where(r => !Capabilities.IsExactly(r, Capability.ModelCall)).ToList();
        var askList = p.Ask.Where(r => !Capabilities.IsExactly(r, Capability.ModelCall)).ToList();
        (ask ? askList : allow).Add("model.call");
        var (join, joinList, leave, leaveList) = ask
            ? ("permissions.ask", askList, "permissions.allow", allow)
            : ("permissions.allow", allow, "permissions.ask", askList);
        if (Run(true, "config", "set", join, string.Join(",", joinList)) == Controllers.Exit.Ok)
        {
            Run(true, "config", "set", leave, string.Join(",", leaveList));
        }

        Reload();
    }

    /// <summary>
    /// The kept sessions, newest first, for a list to draw: <c>diptick chat list</c>, read again only
    /// after something changed them.
    /// </summary>
    public IReadOnlyList<SessionInfo> Sessions()
    {
        lock (_lock)
        {
            if (_sessions is { } known)
            {
                return known;
            }
        }

        var (json, _, code) = Json("chat", "list");
        var sessions = code == Controllers.Exit.Ok ? ReadSessions(json) : [];
        lock (_lock)
        {
            _sessions = sessions;
        }

        return sessions;
    }

    /// <summary>
    /// A new chat: nothing is kept until the first thing said in it; the session on screen stays in the
    /// list. A private one (<paramref name="isPrivate"/>) is kept nowhere but this process's memory,
    /// and first says who sees what is sent. A private chat on screen goes when another takes its place.
    /// </summary>
    public void NewChat(bool isPrivate = false)
    {
        Stop();
        LeavePrivate();
        lock (_lock)
        {
            _sessionId = null;
            _title = string.Empty;
            _sessions = null;
            _private = isPrivate;
            _table = null;
            _answerMode = string.Empty;
            _costs = null;
            _chatRules = null;
            _sources = null;
            _connectorsOn = null;
            _skillsOn = null;
        }

        var notice = isPrivate ? PrivateNotice() : null;
        lock (_lines)
        {
            _transcript.Clear();
            if (notice is not null)
            {
                _transcript.Add(new ChatLine(Role.System, notice, DateTimeOffset.Now));
            }
        }

        DropPending();
        Changed?.Invoke();
    }

    /// <summary>
    /// What a private chat promises, said in it before the first send, for every agent at its table:
    /// with them all on this PC, that nothing leaves it; with any elsewhere, who sees what is sent to
    /// which model. Where each answers from is the address its provider has, read
    /// as <see cref="Uri.IsLoopback"/> reads it, as <see cref="ChatStores.MainSeatRecord"/> does.
    /// </summary>
    public string PrivateNotice()
    {
        var table = Table;
        if (table.Count == 0)
        {
            return Config.AnyAgent
                ? "Private: nothing of this chat is kept. Nobody sits at this chat's table yet, so nothing is sent anywhere."
                : "Private: nothing of this chat is kept. No model is set up yet, so nothing is sent anywhere.";
        }

        var elsewhere = new List<string>();
        var addressed = 0;
        foreach (var chip in table)
        {
            if (Models.Chat.Seam.Table.Resolve(Config, new Models.Chat.Seam.TableSeat(chip.Name, chip.Agent, chip.Model)) is not { } agent
                || Engines.Endpoint(agent.Provider, agent.Endpoint) is not { } endpoint)
            {
                continue;
            }

            addressed++;
            if (!endpoint.IsLoopback)
            {
                var model = chip.Model.Length > 0 ? chip.Model : "its model";
                elsewhere.Add($"{Engines.DisplayName(agent.Provider, agent.Endpoint)} sees what you send to {model}");
            }
        }

        if (addressed == 0)
        {
            return table.Count == 1
                ? "Private: nothing of this chat is kept. The model has no address yet, so nothing is sent anywhere."
                : "Private: nothing of this chat is kept. No agent at this table has an address yet, so nothing is sent anywhere.";
        }

        return elsewhere.Count == 0
            ? "Private: nothing of this chat is kept, and nothing leaves this PC."
            : $"Private: nothing of this chat is kept on this PC. {string.Join("; ", elsewhere)}.";
    }

    /// <summary>
    /// The private chat on screen goes: out of this process's memory (<c>chat delete --private</c>),
    /// with its draft. Nothing brings it back. Nothing happens when the chat on screen is not private.
    /// </summary>
    private void LeavePrivate()
    {
        string? gone;
        lock (_lock)
        {
            if (!_private)
            {
                return;
            }

            gone = _sessionId;
            _private = false;
            _ownDraft = string.Empty;

            // Its images go from this screen's memory with it.
            _images.Clear();
            _imageOrder.Clear();
            _imageBytes = 0;
        }

        if (gone is not null)
        {
            _ = Printed("text", ["chat", "delete", "--private", gone, $"--confirm={gone}"]);
        }
    }

    /// <summary>Brings a kept session back on screen: <c>diptick chat show</c>. Returns what stood in the way, or null.</summary>
    public string? OpenSession(string idOrLast)
    {
        ArgumentNullException.ThrowIfNull(idOrLast);
        Stop();
        var (json, error, code) = Json("chat", "show", $"--session={idOrLast}");
        if (code == Controllers.Exit.Ok)
        {
            LeavePrivate();
        }

        if (code != Controllers.Exit.Ok)
        {
            return Plain(error) is { Length: > 0 } said ? said : $"No session {idOrLast}.";
        }

        List<ChatLine> lines;
        string id, title;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            id = root.GetProperty("session").GetString() ?? idOrLast;
            title = root.GetProperty("title").GetString() ?? string.Empty;
            lines = LinesOf(root);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return $"'diptick chat show' printed something this screen could not read: {ex.Message}";
        }

        lock (_lock)
        {
            _sessionId = id;
            _title = title;
            _sessions = null;
            _table = null;
            _answerMode = string.Empty;
            _costs = null;
            _chatRules = null;
            _sources = null;
            _connectorsOn = null;
            _skillsOn = null;
        }

        lock (_lines)
        {
            _transcript.Clear();
            _transcript.AddRange(lines);
        }

        DropPending();
        ReadFiles();
        Changed?.Invoke();
        return null;
    }

    /// <summary>
    /// The first thing the screen does: one line of welcome, the session asked for, and setup when
    /// no model is set up and setup has never opened by itself before. Run off the screen's thread.
    /// </summary>
    public async Task StartAsync()
    {
        // Pastes a screen left when it ended without closing, that no kept draft names.
        lock (_lock)
        {
            _ = Pastes.SweepLeftOvers(PasteFolder, [.. _book.Texts(), _ownDraft], DateTime.UtcNow);
        }

        await Say(Role.System, "Type to talk. / shows the commands; F1 opens Help.").ConfigureAwait(false);
        if (OpenOnStart is { } want && OpenSession(want) is { } problem)
        {
            await Say(Role.System, problem).ConfigureAwait(false);
        }

        if (SetupOnStart)
        {
            // Once: from now on setup opens only from Settings, You, Run setup again.
            if (UiState?.MarkSetupShown() is { } unsaved)
            {
                await Say(Role.System, unsaved).ConfigureAwait(false);
            }

            await Say(Role.System, "No model is set up yet, so setup opens now, this once. Esc leaves it at any step; Settings, You, Run setup again brings it back.").ConfigureAwait(false);
            await RunSetupAsync(full: false).ConfigureAwait(false);
        }
    }

    public async Task SubmitAsync(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var text = input.Trim();
        if ((text.Length == 0 && Pending.Count == 0) || Quitting)
        {
            return;
        }

        // exit, typed alone, leaves, as on every command line DipTick has; it works while an answer runs.
        if (IsExitWord(text))
        {
            await QuitAsync(ask: true).ConfigureAwait(false);
            return;
        }

        if (text.Length > 0)
        {
            Remember(text);
        }

        // A command is one line starting with /. A draft of several lines is always words to the model.
        if (text.StartsWith('/') && !text.Contains('\n', StringComparison.Ordinal))
        {
            var space = text.IndexOf(' ', StringComparison.Ordinal);
            var name = space < 0 ? text : text[..space];
            var arg = space < 0 ? string.Empty : text[(space + 1)..].Trim();
            if (_commands.TryGetValue(name, out var command))
            {
                await command.Run(arg).ConfigureAwait(false);
            }
            else
            {
                await Say(Role.System, $"No command {name}. /help lists them.").ConfigureAwait(false);
            }

            return;
        }

        if (Busy)
        {
            await Say(Role.System, "Still answering. Esc or /stop first.").ConfigureAwait(false);
            return;
        }

        // A paste kept in a file goes in whole; the chat on screen keeps its short reference.
        var (said, missing) = Pastes.Expand(text, PasteFolder);
        if (missing.Count > 0)
        {
            await Say(Role.System, $"{string.Join(", ", missing)} {(missing.Count == 1 ? "is" : "are")} gone from {PasteFolder}, so nothing was sent. Paste it again, or take its reference out; ↑ brings the message back.").ConfigureAwait(false);
            return;
        }

        // The files waiting go with it, shown on the person's line at once; when nothing is kept, they wait again.
        var files = TakePending();
        var shown = text.Length > 0 ? text : Attachments.Wordless(files.Select(f => f.Name));
        Add(new ChatLine(Role.Person, shown, DateTimeOffset.Now) { Files = [.. files.Select(Shown)] });
        Changed?.Invoke();
        var kept = await AnswerAsync(said, files).ConfigureAwait(false);
        await AfterSendAsync(files, kept).ConfigureAwait(false);
    }

    /// <summary><c>exit</c> or <c>quit</c> on its own: the word that leaves wherever DipTick asks or listens.</summary>
    public static bool IsExitWord(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var word = text.Trim();
        return word.Equals("exit", StringComparison.OrdinalIgnoreCase) || word.Equals("quit", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Lines as text, each with its author's mark, the way <c>diptick chat show</c> prints them.</summary>
    public string AsText(IEnumerable<ChatLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var text = new StringBuilder();
        foreach (var line in lines)
        {
            var mark = line.Role switch { Role.Person => $"{Who} ›", Role.Agent => $"{(line.Seat.Length > 0 ? line.Seat : ChatStores.MainSeat)} ┊", _ => "·" };
            text.Append(mark).Append(' ').Append(SafeText.Block(line.Text).Replace("\n", "\n    ", StringComparison.Ordinal)).Append('\n');
        }

        return text.ToString().TrimEnd('\n');
    }

    /// <summary>A kept chat as text: the chat on screen as it stands, any other as <c>diptick chat show</c> prints it.</summary>
    public string SessionText(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (id == _sessionId)
        {
            return AsText(Transcript);
        }

        var (output, _, code) = TextOut("chat", "show", $"--session={id}");
        return code == Controllers.Exit.Ok ? output.TrimEnd() : string.Empty;
    }

    /// <summary>Names a kept chat: <c>diptick chat title</c>. Runs on a worker thread.</summary>
    public void RenameSession(string id, string title)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(title);
        var named = IsPrivateId(id)
            ? Run(yes: false, "chat", "title", "--private", $"--session={id}", "--", title)
            : Run(yes: false, "chat", "title", $"--session={id}", "--", title);
        if (named == Controllers.Exit.Ok)
        {
            lock (_lock)
            {
                if (id == _sessionId)
                {
                    _title = ChatStores.TitleFrom(title);
                }

                _sessions = null;
            }

            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Asks for the new name of a kept chat on the screen, then names it. Runs on a worker thread,
    /// because the question waits for the person.
    /// </summary>
    public void AskRenameSession(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (Asker is not { } asker)
        {
            return;
        }

        var now = Sessions().FirstOrDefault(s => s.Id == id)?.Title ?? string.Empty;
        var typed = asker.Text(new Question("Rename chat", "What should this chat be called?", "It is how the list on the left shows it."), now, t => t.Trim().Length == 0 ? "a name needs some words" : SafeText.HasControls(t) ? "no control characters" : null);
        if (typed.Reply == Reply.Answered && typed.Value.Trim() != now)
        {
            RenameSession(id, typed.Value.Trim());
        }
    }

    /// <summary>Writes a kept chat to a file: <c>diptick chat export</c>, which asks before it overwrites. Runs on a worker thread.</summary>
    public void ExportSession(string id, string path)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(path);
        if (IsPrivateId(id))
        {
            _ = Say(Role.System, PrivateNotWritten);
            return;
        }

        Run(yes: false, "chat", "export", $"--session={id}", "--", path);
    }

    /// <summary>What DipTick says when asked to write a private chat to a file.</summary>
    private const string PrivateNotWritten = "A private chat is never written to a file by DipTick. What you copy from it yourself is yours to keep.";

    /// <summary>Whether a chat is private: the one on screen, or one this model made.</summary>
    private bool IsPrivateId(string id)
    {
        lock (_lock)
        {
            return (_private && id == _sessionId) || _privateIds.Contains(id);
        }
    }

    /// <summary>
    /// Closes a kept chat: <c>diptick chat close</c>. It leaves the list and stays kept; closing the
    /// chat on screen starts a new one. Runs on a worker thread.
    /// </summary>
    public void CloseSession(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (IsPrivateId(id))
        {
            NewChat();
            return;
        }

        var (text, code) = Capture(yes: false, "chat", "close", $"--session={id}");
        if (code == Controllers.Exit.Ok && id == _sessionId)
        {
            NewChat();
        }

        lock (_lock)
        {
            _sessions = null;
        }

        _ = Say(Role.System, text.Length == 0 ? "(nothing printed)" : text);
    }

    /// <summary>
    /// Deletes a kept chat for good, after the person's yes on the screen: <c>diptick chat delete
    /// &lt;id&gt; --confirm=&lt;id&gt;</c>. Deny is where the answer starts. Runs on a worker thread.
    /// </summary>
    public void DeleteSession(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (IsPrivateId(id))
        {
            NewChat();
            return;
        }

        if (Asker is not { } asker)
        {
            _ = Say(Role.System, $"'diptick chat delete {id} --confirm={id}' deletes it from a terminal.");
            return;
        }

        var title = Sessions().FirstOrDefault(s => s.Id == id)?.Title is { Length: > 0 } named ? named : id == _sessionId ? SessionTitle : id;
        var sure = asker.YesNo(new Question("Delete chat", $"Delete '{title}' for good?", "Its lines leave sessions.db and cannot be brought back. Close takes a chat off the list and keeps it."), false);
        if (sure.Reply != Reply.Answered || !sure.Value)
        {
            return;
        }

        var (text, code) = Capture(yes: false, "chat", "delete", id, $"--confirm={id}");
        if (code == Controllers.Exit.Ok && id == _sessionId)
        {
            NewChat();
        }

        lock (_lock)
        {
            _sessions = null;
            if (code == Controllers.Exit.Ok)
            {
                _book.Remove(id);
            }
        }

        _ = Say(Role.System, text.Length == 0 ? "(nothing printed)" : text);
    }

    /// <summary>The previous (-1) or next (+1) line typed, or null past either end.</summary>
    public string? Recall(int step)
    {
        // The screen's thread reads what a send, on a worker, remembers.
        lock (_lock)
        {
            if (_history.Count == 0)
            {
                return null;
            }

            _historyAt = Math.Clamp(_historyAt + step, 0, _history.Count);
            return _historyAt == _history.Count ? string.Empty : _history[_historyAt];
        }
    }

    public void Stop() => _turn?.Cancel();

    /// <summary>
    /// Quits: the answer that runs is stopped, and waited for while it
    /// keeps what it said; the local models that answered in this run are let go
    /// (<c>quit.unloadModels</c>, <c>diptick models unload</c>); the unsent drafts are kept for the
    /// next run; then <see cref="QuitRequested"/> tells the screen to close. With
    /// <paramref name="ask"/> it asks first, and only while an answer runs: Quit on Enter, Stay on
    /// Esc. Esc three times never asks. Runs on a worker thread: the question and the waits block.
    /// </summary>
    public async Task QuitAsync(bool ask)
    {
        if (Quitting)
        {
            return;
        }

        if (QuitHook is { } app)
        {
            await app(ask).ConfigureAwait(false);
            return;
        }

        if (ask && Busy && Asker is { } asker)
        {
            // One question at a time: a second F10 while it is open adds nothing.
            if (Interlocked.CompareExchange(ref _askingQuit, 1, 0) != 0)
            {
                return;
            }

            try
            {
                var sure = asker.Choose(
                    new Question("Quit", "An answer is still coming. Quit and stop it?", "What it has said so far is kept.", Now: true),
                    [new Choice("Quit", "stop the answer and close DipTick"), new Choice("Stay", "let the answer finish")],
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

        Working = "quitting: stopping everything…";
        Changed?.Invoke();
        Stop();
        if (_running is { } running)
        {
            await Task.WhenAny(running, Task.Delay(QuitWait)).ConfigureAwait(false);
        }

        await Models.Connectors.ConnectorHub.CloseAllAsync().ConfigureAwait(false);
        if (Config.QuitUnloadModels)
        {
            await UnloadModelsAsync().ConfigureAwait(false);
        }

        // The paste files this run made go, but for those a kept draft still names.
        _ = Pastes.Sweep(PastesMade(), KeepDrafts());
        QuitRequested?.Invoke();
    }

    /// <summary>For the app's quit: nothing new starts, and the answer that runs is stopped and waited for while it keeps what it said.</summary>
    internal async Task StopForQuitAsync()
    {
        Volatile.Write(ref _quitting, 1);
        Working = "quitting: stopping everything…";
        Changed?.Invoke();
        Stop();
        if (_running is { } running)
        {
            await Task.WhenAny(running, Task.Delay(QuitWait)).ConfigureAwait(false);
        }
    }

    /// <summary>For the app's quit: the models that answered in this chat, by the section that names them.</summary>
    internal IReadOnlyList<(string Section, string Model)> AnsweredBy()
    {
        lock (_lock)
        {
            return [.. _answeredBy];
        }
    }

    /// <summary>For the app's quit: lets go of <paramref name="used"/> (<c>diptick models unload</c>), each once.</summary>
    internal async Task UnloadAsync(IEnumerable<(string Section, string Model)> used)
    {
        foreach (var (section, model) in used.Distinct())
        {
            using var limit = new CancellationTokenSource(QuitWait);
            string[] args = ["models", "unload", "--for", section, "--model", model];
            await Task.Run(() => RunVerb(args, TextWriter.Null, TextWriter.Null, yes: true, "text", limit.Token, dryRun: false)).ConfigureAwait(false);
        }
    }

    /// <summary>For the app's quit: the paste files this chat made.</summary>
    internal IReadOnlyList<string> PastesToSweep() => PastesMade();

    /// <summary>For the app's quit: the drafts to keep, as <see cref="KeepDrafts"/> chooses them, without keeping them.</summary>
    internal Dictionary<string, string> DraftsToKeep() => DraftsWorthKeeping();

    /// <summary>For the app's quit: the screen closes.</summary>
    internal void Close() => QuitRequested?.Invoke();

    private string[] PastesMade()
    {
        lock (_lock)
        {
            return [.. _pastesMade];
        }
    }

    /// <summary>
    /// Lets go of each model that answered in this run: <c>diptick models unload</c>, which asks
    /// Ollama and says of any other runtime that it holds its own models. The person's quit is the
    /// yes for it, and a dry run does not hold it: the dry run was this chat's, and the chat is over.
    /// </summary>
    private async Task UnloadModelsAsync()
    {
        (string Section, string Model)[] used;
        lock (_lock)
        {
            used = [.. _answeredBy];
        }

        foreach (var (section, model) in used)
        {
            using var limit = new CancellationTokenSource(QuitWait);
            string[] args = ["models", "unload", "--for", section, "--model", model];
            await Task.Run(() => RunVerb(args, TextWriter.Null, TextWriter.Null, yes: true, "text", limit.Token, dryRun: false)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Keeps the unsent drafts for the next run: those of the chat nobody has spoken in and of the
    /// chats still in the list. A chat that left the list takes its draft with it. Returns the
    /// drafts kept; none when nothing keeps them.
    /// </summary>
    private string[] KeepDrafts()
    {
        if (_uiState is not { } state)
        {
            return [];
        }

        var drafts = DraftsWorthKeeping();

        // Nobody is left to read a failure: the drafts are only kept when they can be.
        return state.KeepDrafts(drafts) is null ? [.. drafts.Values] : [];
    }

    /// <summary>
    /// The drafts worth keeping: the new chat's and those of the chats still in the list; a chat that
    /// left the list takes its draft with it, and a private chat's is never kept.
    /// </summary>
    private Dictionary<string, string> DraftsWorthKeeping()
    {
        var listed = Sessions().Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        lock (_lock)
        {
            var drafts = _book.All()
                .Where(d => !_privateIds.Contains(d.Key) && (d.Key == _sessionId || listed.Contains(d.Key)))
                .ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);

            // The new chat's: this chat's own, or, when there is none to keep (a private chat on screen), the last run's, still waiting.
            drafts[string.Empty] = _private || _ownDraft.Length == 0 ? _book.NewChatWaiting : _ownDraft;
            return drafts;
        }
    }

    /// <summary>
    /// What the transcript pane shows: one row per screen row, wrapped to <paramref name="width"/>
    /// less the mark, each carrying who said it and whether it starts the line (where the mark goes).
    /// A blank row follows each line.
    /// </summary>
    /// <param name="find">Only the lines that hold these words (Find, F3), or null for every line.</param>
    public List<TranscriptRow> RenderRows(int width, string? find = null)
    {
        const int MarkWidth = 3;
        var room = Math.Max(10, width - MarkWidth - 1);
        var rows = new List<TranscriptRow>();
        foreach (var line in Transcript)
        {
            if (find is { Length: > 0 } && !line.Text.Contains(find, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A tool call, folded to its one line: the agent and what the tool did (/copy all has the rest).
            if (line.Role == Role.Tool)
            {
                var ended = line.Status == MessageStatus.Done ? string.Empty : $" · {line.Status}";
                var head = $"{(line.Seat.Length > 0 ? line.Seat + " · " : string.Empty)}{SafeText.Line(line.Text.Split('\n')[0])}{ended}";
                rows.Add(new TranscriptRow(Role.Tool, true, head.Length <= room ? head : head[..Math.Max(1, room - 1)] + "…", line.Color, RowPart.Note));
                rows.Add(new TranscriptRow(Role.Tool, false, string.Empty));
                continue;
            }

            // An agent's answer under its name, in its colour: the head takes the line's mark.
            var first = true;
            if (line.Role == Role.Agent && line.Seat.Length > 0)
            {
                var ended = EndOf(line);
                var head = $"{line.Seat}{(line.Model.Length > 0 ? " · " + line.Model : string.Empty)}{(ended.Length > 0 ? " · " + ended : string.Empty)}";
                rows.Add(new TranscriptRow(line.Role, true, SafeText.Line(head), line.Color, RowPart.Head));
                first = false;
            }

            // What any voice says is shown, never obeyed by the terminal (SafeText); a finished answer's
            // citation to a number the chat has no source for is marked.
            var said = line.Role == Role.Agent && line.Status != MessageStatus.Streaming ? Marked(line.Text) : line.Text;
            Wrapped(rows, line, SafeText.Block(said.Length == 0 && line.Seat.Length > 0 ? "…" : said), room, first, RowPart.Text);
            foreach (var act in line.Acts)
            {
                Wrapped(rows, line, "→ " + SafeText.Line(act), room, false, RowPart.Note);
            }

            // The files that came with the line: name, type and size, and its number for /save --file.
            foreach (var file in line.Files)
            {
                var number = file.Id > 0 ? string.Create(CultureInfo.InvariantCulture, $" · file {file.Id}") : string.Empty;
                var entry = SafeText.Line($"▪ {file.Name} · {file.Type} · {Models.Files.AgentFolders.Size(file.Size)}{number}{(file.Path is { } path ? " · " + path : string.Empty)}");
                rows.Add(new TranscriptRow(line.Role, false, entry.Length <= room ? entry : entry[..Math.Max(1, room - 1)] + "…", line.Color, RowPart.Note));
            }

            // The sources the answer served, by number: title and host (/sources has the addresses).
            if (line.Role == Role.Agent && line.Seq is { } seq)
            {
                foreach (var source in SourcesOf(seq))
                {
                    var host = Uri.TryCreate(source.Address, UriKind.Absolute, out var uri) ? uri.Host : source.Address;
                    var entry = SafeText.Line(string.Create(CultureInfo.InvariantCulture, $"[{source.Id}] {(source.Title.Length > 0 ? source.Title : source.Address)} · {host}"));
                    rows.Add(new TranscriptRow(line.Role, false, entry.Length <= room ? entry : entry[..Math.Max(1, room - 1)] + "…", line.Color, RowPart.Note));
                }
            }

            if (UsageOf(line) is { Length: > 0 } usage)
            {
                rows.Add(new TranscriptRow(line.Role, false, usage, string.Empty, RowPart.Note));
            }

            rows.Add(new TranscriptRow(line.Role, false, string.Empty));
        }

        return rows;
    }

    /// <summary>Text as rows no wider than <paramref name="room"/>, broken at spaces, each paragraph on its own rows.</summary>
    private static void Wrapped(List<TranscriptRow> rows, ChatLine line, string text, int room, bool first, RowPart part)
    {
        foreach (var paragraph in text.Split('\n'))
        {
            var rest = paragraph;
            do
            {
                var take = rest.Length <= room ? rest.Length : Math.Max(rest.LastIndexOf(' ', room), 1);
                rows.Add(new TranscriptRow(line.Role, first, rest[..take].TrimEnd(), part == RowPart.Note ? line.Color : string.Empty, part));
                rest = rest[take..].TrimStart();
                first = false;
            }
            while (rest.Length > 0);
        }
    }

    /// <summary>Reads the settings again after something changed them, and rebuilds the gate and the engine from them.</summary>
    public void Reload()
    {
        if ((Config.Path ?? SettingsPath) is { } path)
        {
            Config = MasterConfig.Load(path);
            Models.Providers.GoogleSignIn.UseMachineAccount = Config.GoogleMachineAccount;
            Gate = new Gate(Config.Permissions, Flags, AuditLog.ForThisUser(), Who, TextWriter.Null, AskPerson);
            (_engine as IDisposable)?.Dispose();
            _engine = Engines.Create(Config);
            lock (_lock)
            {
                // A new chat's table comes from the settings, and every agent's colour and prices do.
                _table = null;
                _answerMode = string.Empty;
                _costs = null;
                _chatRules = null;
                _sources = null;
                _connectorsOn = null;
                _skillsOn = null;
            }

            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Runs a diptick verb in this process and puts what it printed in the transcript. It never
    /// prompts (--no-input: the screen owns the keyboard), and it carries this session's config,
    /// dry run and auto-yes, so a verb here does exactly what it does on the command line.
    /// <paramref name="yes"/> is a yes the person has just given in a dialog, for this one verb.
    /// </summary>
    /// <returns>The verb's exit code.</returns>
    public int Run(bool yes, params string[] args)
    {
        var (text, code) = Capture(yes, args);
        if (code != 0)
        {
            text += string.Create(CultureInfo.InvariantCulture, $"\n(exit {code})");
        }

        _ = Say(Role.System, text.Length == 0 ? "(nothing printed)" : text);
        return code;
    }

    /// <summary>
    /// As <see cref="Run"/>, but what the verb printed comes back instead of going to the transcript. A
    /// question the verb asks comes to the screen with what the verb said before it, as a terminal shows it
    /// above the question: a yes is given to what it is about (the program a connector runs and its sandbox,
    /// a skill as it is approved), not to a line whose subject is still held back. What went with one
    /// question does not go again with the next.
    /// </summary>
    public (string Text, int Code) Capture(bool yes, params string[] args)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var shown = 0;
        bool? Ask(string question)
        {
            var said = output.ToString();
            var before = Plain(said[shown..]);
            shown = said.Length;
            return AskPerson(before.Length > 0 ? $"{before}\n\n{question}" : question);
        }

        var code = RunVerb(args, output, output, yes, "text", CancellationToken.None, ask: Ask);
        return (output.ToString().TrimEnd(), code);
    }

    /// <summary>A line on the screen: DipTick's, or the person's as they send it. The verbs keep what is kept.</summary>
    public Task Say(Role role, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Add(new ChatLine(role, text, DateTimeOffset.Now));
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>A line at the end of the chat.</summary>
    private void Add(ChatLine line)
    {
        lock (_lines)
        {
            _transcript.Add(line);
        }
    }

    /// <summary>The setup flow's surroundings for this session: its settings, its switches, the real key store and platforms.</summary>
    public SetupContext SetupContext() => new(
        Config,
        SettingsPath ?? Config.Path ?? MasterConfig.UserConfigPath,
        Flags,
        WindowsKeyStore.Instance,
        Environment.GetEnvironmentVariable,
        ModelLister.Instance,
        permissions => new Gate(permissions, Flags, AuditLog.ForThisUser(), Who, TextWriter.Null, AskPerson));

    /// <summary>
    /// A round: <c>diptick chat say --events</c>, run in this process on this session, its events drawn
    /// as they come (<see cref="RoundAsync"/>). The screen's person answers the verb's questions (the
    /// prompt zone), and Esc stops it; everything kept, the verb keeps.
    /// </summary>
    private Task<bool> AnswerAsync(string said, IReadOnlyList<PendingFile> files)
    {
        var args = new List<string> { "chat", "say", $"--session={_sessionId ?? "new"}", "--events" };
        foreach (var file in files)
        {
            args.AddRange(["--attach", file.Reference]);
        }

        args.AddRange(["--", said]);
        return RoundAsync(Private([.. args]));
    }

    /// <summary>Where a line is now, by reference, or -1 when the transcript no longer holds it (a new chat, or another opened). Called with <see cref="_lines"/> held.</summary>
    private int Find(ChatLine line)
    {
        for (var i = _transcript.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_transcript[i], line))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Runs a verb in this process as the command line would, with this session's config, dry run
    /// and auto-yes. Nothing reads the console (--no-input): the verb's questions come to the screen's
    /// prompt instead, and <paramref name="stop"/> is the screen's Esc. The switches come first, so
    /// words after a lone -- in <paramref name="args"/> stay words.
    /// </summary>
    /// <param name="dryRun">The dry run for this one verb, or null for the session's.</param>
    /// <param name="ask">Who the verb's questions go to: the screen's person, unless the caller shows them more.</param>
    private int RunVerb(IReadOnlyList<string> args, TextWriter output, TextWriter error, bool yes, string format, CancellationToken stop, bool? dryRun = null, Func<string, bool?>? ask = null)
    {
        var line = new List<string> { "--no-input", "--no-color", "--output", format };
        line.Add(dryRun ?? Flags.DryRun ? "--dry-run" : "--no-dry-run");
        line.Add(yes || Flags.Yes ? "--yes" : "--no-yes");
        if ((Config.Path ?? SettingsPath) is { } path && File.Exists(path))
        {
            line.Add($"--config={path}");
        }
        else
        {
            line.Add("--no-config");
        }

        line.AddRange(args);
        return Controllers.CommandController.Run(line, output, error, ask ?? AskPerson, ApproveHook, stop);
    }

    /// <summary>A verb run for its JSON: what it printed on stdout, what it said on stderr, and its exit code.</summary>
    private (string Json, string Error, int Code) Json(params string[] args) => Printed("json", args);

    /// <summary>A verb run for its text: what it printed on stdout alone, what it said on stderr, and its exit code.</summary>
    private (string Text, string Error, int Code) TextOut(params string[] args) => Printed("text", args);

    private (string Out, string Error, int Code) Printed(string format, string[] args)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var code = RunVerb(args, output, error, yes: false, format, CancellationToken.None);
        return (output.ToString(), error.ToString(), code);
    }

    /// <summary>What a verb said on stderr, for the screen: its "diptick: " prefixes dropped.</summary>
    private static string Plain(string said) =>
        string.Join('\n', said.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.StartsWith("diptick: ", StringComparison.Ordinal) ? l["diptick: ".Length..] : l)).Trim();

    /// <summary>The sessions from <c>chat list --output json</c>.</summary>
    private static List<SessionInfo> ReadSessions(string json)
    {
        var sessions = new List<SessionInfo>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var s in doc.RootElement.GetProperty("sessions").EnumerateArray())
            {
                sessions.Add(new SessionInfo(
                    s.GetProperty("id").GetString() ?? string.Empty,
                    s.GetProperty("title").GetString() ?? string.Empty,
                    s.GetProperty("person").GetString() ?? string.Empty,
                    s.GetProperty("mode").GetString() ?? string.Empty,
                    s.GetProperty("created").GetDateTimeOffset(),
                    s.GetProperty("updated").GetDateTimeOffset(),
                    s.GetProperty("status").GetString() == "open",
                    s.GetProperty("lines").GetInt32(),
                    s.TryGetProperty("busyIn", out var busy) && busy.ValueKind == JsonValueKind.Number ? busy.GetInt32() : null,
                    s.TryGetProperty("pinned", out var pinned) && pinned.ValueKind == JsonValueKind.True));
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // A list that cannot be read is shown as no list; 'diptick chat list' says more.
        }

        return sessions;
    }

    /// <summary>Takes what a verb writes with --events and hands on each whole line as it ends.</summary>
    private sealed class EventLines(Action<string> line) : TextWriter
    {
        private readonly StringBuilder _pending = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                Hand();
            }
            else if (value != '\r')
            {
                _pending.Append(value);
            }
        }

        public override void Write(string? value)
        {
            foreach (var c in value ?? string.Empty)
            {
                Write(c);
            }
        }

        public override void Write(char[] buffer, int index, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            for (var i = index; i < index + count; i++)
            {
                Write(buffer[i]);
            }
        }

        /// <summary>A last line that ended without a line feed.</summary>
        public void Finish() => Hand();

        private void Hand()
        {
            if (_pending.Length > 0)
            {
                var text = _pending.ToString();
                _pending.Clear();
                line(text);
            }
        }
    }

    /// <summary>/rename: <c>diptick chat title</c> on the chat on screen.</summary>
    private async Task RenameAsync(string arg)
    {
        if (arg.Length == 0)
        {
            await Say(Role.System, "Say what to call it: /rename <title>").ConfigureAwait(false);
            return;
        }

        if (_sessionId is not { } id)
        {
            await Say(Role.System, "Nothing has been said in this chat yet, so there is no chat to name. Say something first.").ConfigureAwait(false);
            return;
        }

        RenameSession(id, arg);
    }

    /// <summary>/open: a kept chat by its id (or last), or, alone, the list: <c>diptick chat list</c>.</summary>
    private async Task OpenAsync(string arg)
    {
        if (arg.Length == 0)
        {
            await Verb("chat", "list").ConfigureAwait(false);
            await Say(Role.System, "'/open <id>' opens one of these, as a click in the list on the left does.").ConfigureAwait(false);
            return;
        }

        if (OpenSession(arg.Trim('"')) is { } problem)
        {
            await Say(Role.System, problem).ConfigureAwait(false);
        }
    }

    /// <summary>/close: the chat on screen leaves the list and stays kept.</summary>
    private async Task CloseAsync()
    {
        if (_sessionId is not { } id)
        {
            await Say(Role.System, "Nothing has been kept in this chat yet, so there is nothing to close.").ConfigureAwait(false);
            return;
        }

        CloseSession(id);
    }

    /// <summary>/delete: the chat on screen, for good, after a yes.</summary>
    private async Task DeleteAsync()
    {
        if (_sessionId is not { } id)
        {
            await Say(Role.System, "Nothing has been kept in this chat yet, so there is nothing to delete.").ConfigureAwait(false);
            return;
        }

        DeleteSession(id);
    }

    /// <summary>
    /// /copy: the last answer as it reads, /copy n the n-th answer from the end, /copy all the whole
    /// chat with its marks. Only ever what the person asked for.
    /// </summary>
    private async Task CopyAsync(string arg)
    {
        string text;
        string what;
        if (arg.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            text = AsText(Transcript);
            what = "the whole chat";
        }
        else
        {
            var n = 1;
            if (arg.Length > 0 && (!int.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out n) || n < 1))
            {
                await Say(Role.System, "Say which: /copy for the last answer, /copy 2 for the one before it, /copy all for the whole chat.").ConfigureAwait(false);
                return;
            }

            var answers = Transcript.Where(l => l.Role == Role.Agent && l.Text.Length > 0).ToList();
            if (answers.Count < n)
            {
                await Say(Role.System, answers.Count == 0 ? "There is no answer to copy yet." : $"There {(answers.Count == 1 ? "is one answer" : $"are {answers.Count.ToString(CultureInfo.InvariantCulture)} answers")} so far.").ConfigureAwait(false);
                return;
            }

            text = MarkdownText.Plain(answers[^n].Text);
            what = n == 1 ? "the last answer" : $"answer {n.ToString(CultureInfo.InvariantCulture)} from the end";
        }

        if (ClipboardHook is not { } clipboard || !await clipboard(text).ConfigureAwait(false))
        {
            await Say(Role.System, "The clipboard could not be reached from this screen; nothing was copied.").ConfigureAwait(false);
            return;
        }

        await Say(Role.System, $"Copied {what} ({text.Length.ToString(CultureInfo.InvariantCulture)} characters).").ConfigureAwait(false);
    }

    private void Remember(string text)
    {
        lock (_lock)
        {
            if (_history.Count == 0 || _history[^1] != text)
            {
                _history.Add(text);
                if (_history.Count > MostHistory)
                {
                    _history.RemoveAt(0);
                }
            }

            _historyAt = _history.Count;
        }
    }

    private Task Toggle(string arg, Action<bool> set, Func<bool> get, string what)
    {
        var on = arg.ToUpperInvariant() switch
        {
            "ON" => true,
            "OFF" => false,
            "" => !get(),
            _ => (bool?)null,
        };
        if (on is null)
        {
            return Say(Role.System, $"Say on or off, or nothing to flip it.");
        }

        set(on.Value);

        // The gate reads the flags it was built with; a new one carries the change.
        Gate = new Gate(Config.Permissions, Flags, AuditLog.ForThisUser(), Who, TextWriter.Null, AskPerson);
        return Say(Role.System, $"{what} {(on.Value ? "on" : "off")}.");
    }

    /// <summary>No hook, no yes: a question nobody can see is answered no.</summary>
    private bool? AskPerson(string question) => ConfirmHook?.Invoke(question);

    private string HelpText()
    {
        var text = new StringBuilder("Type to talk. Commands (type / to see them as a list):\n");
        var width = _commands.Keys.Max(k => k.Length);
        foreach (var (name, (help, _)) in _commands)
        {
            text.Append(CultureInfo.InvariantCulture, $"  {name.PadRight(width)}  {help}\n");
        }

        text.Append("Talking to several agents: @name asks one, @a @b asks those, @team asks everyone, @a>b is a chain, \\@ is a plain @; with no @, the chat's mode decides (/mode).\n");
        text.Append("Keys: Enter send · Shift+Enter or Ctrl+J new line · / commands, Tab completes · ↑/↓ history · Esc stops an answer or leaves a question · F4 the table · F5 ask again · F10, Ctrl+Q, /exit or exit typed alone quits, asking first while an answer runs · Esc three times quits at once and stops everything · F1 help");
        return text.ToString();
    }

    private Task Verb(params string[] args)
    {
        Run(yes: false, args);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The window beside the terminal screen (/window, F2): a program of its own, on this chat when
    /// it is kept, while this screen goes on working. In the window itself there is nothing to open.
    /// </summary>
    public Task OpenWindowAsync()
    {
        if (OpenWindow is null)
        {
            return Say(Role.System, "This is the window. Ctrl+N opens another.");
        }

        var kept = _private ? null : _sessionId;
        return Say(Role.System, OpenWindow(kept) ?? (kept is null
            ? "The window is opening; this screen goes on working."
            : "The window is opening on this chat; this screen goes on working."));
    }

    private async Task Status_()
    {
        await Verb("whoami").ConfigureAwait(false);
        var agents = Table.Count == 0 ? "nobody" : string.Join(", ", Table.Select(s => $"{s.Name} ({s.Provider} {s.Model})"));
        var switches = $"dry run {(Flags.DryRun ? "on" : "off")} · auto-yes {(Flags.Yes ? "on" : "off")} · at the table: {agents}";
        await Say(Role.System, switches).ConfigureAwait(false);
    }

    /// <summary>
    /// Setup's questions, on the screen: at first use once, then from Settings, You, Run setup
    /// again. Nothing is saved until the person says so; Esc leaves at any step.
    /// </summary>
    public async Task RunSetupAsync(bool full)
    {
        if (Asker is null)
        {
            await Say(Role.System, "Setup needs the screen's dialogs; 'diptick setup' runs it in a terminal.").ConfigureAwait(false);
            return;
        }

        if (SettingsPath is null && Config.Path is null)
        {
            await Say(Role.System, "--config none names no file, so setup has nowhere to save. Start DipTick without it.").ConfigureAwait(false);
            return;
        }

        var result = new SetupFlow(SetupContext(), Asker).RunWithAgents(full);
        await Say(Role.System, result.Message).ConfigureAwait(false);
        if (result.Outcome == SetupOutcome.Saved)
        {
            Reload();
        }
    }

    private async Task OpenSettingsAsync()
    {
        if (SettingsHook is null)
        {
            await Verb("settings").ConfigureAwait(false);
            return;
        }

        await SettingsHook().ConfigureAwait(false);
    }

    private async Task Model(string arg)
    {
        if (arg.Length > 0)
        {
            await Verb("config", "set", "main.model", arg).ConfigureAwait(false);
            Reload();
            return;
        }

        if (Asker is null)
        {
            await Verb("config", "get", "main.model").ConfigureAwait(false);
            return;
        }

        if (ModelLister.QueryFor("main", Config, out _, out var why) is not { } query)
        {
            await Say(Role.System, $"No list to pick from: {why}").ConfigureAwait(false);
            return;
        }

        IReadOnlyList<ModelInfo> models;
        using (var timeout = Flags.NetTimeoutSeconds == 0 ? new CancellationTokenSource() : new CancellationTokenSource(TimeSpan.FromSeconds(Flags.NetTimeoutSeconds)))
        {
            try
            {
                models = Asker.Busy($"Asking {Engines.DisplayName(Config.MainProvider, Config.MainEndpoint)} for its models…", () => ModelLister.Instance.ListAsync(query, Gate, timeout.Token));
            }
            catch (ChatException ex)
            {
                await Say(Role.System, $"No list to pick from: {ex.Message} '/model <name>' sets a model by name.").ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                await Say(Role.System, $"{query.Endpoint.Host} did not answer in time. '/model <name>' sets a model by name.").ConfigureAwait(false);
                return;
            }
        }

        var picked = Asker.PickModel(new Question("Choose a model", "Which model should answer you?", $"{models.Count} offered by {Engines.DisplayName(Config.MainProvider, Config.MainEndpoint)}."), models, Config.MainModel);
        if (picked.Reply == Reply.Answered && picked.Value != Config.MainModel)
        {
            await Verb("config", "set", "main.model", picked.Value).ConfigureAwait(false);
            Reload();
        }
    }

    /// <summary>
    /// /export: <c>diptick chat export</c> on the session on screen. The verb asks before it
    /// overwrites, through this screen's prompt, and honours a dry run.
    /// </summary>
    private async Task ExportAsync(string arg)
    {
        if (arg.Length == 0)
        {
            await Say(Role.System, "Say where: /export <file>").ConfigureAwait(false);
            return;
        }

        if (_private)
        {
            await Say(Role.System, PrivateNotWritten).ConfigureAwait(false);
            return;
        }

        if (_sessionId is not { } id)
        {
            await Say(Role.System, "Nothing has been kept in this chat yet, so there is nothing to write out.").ConfigureAwait(false);
            return;
        }

        Run(yes: false, "chat", "export", $"--session={id}", "--", arg.Trim('"'));
    }

    /// <summary>/pin: <c>diptick chat pin</c> on the chat on screen; /pin off lets it go back among the rest.</summary>
    private async Task PinAsync(string arg)
    {
        var off = arg.Equals("off", StringComparison.OrdinalIgnoreCase);
        if (!off && arg.Length > 0)
        {
            await Say(Role.System, "/pin keeps this chat at the top of the list; /pin off lets it go back.").ConfigureAwait(false);
            return;
        }

        if (_private)
        {
            await Say(Role.System, "A private chat is never in the list, so it cannot be pinned.").ConfigureAwait(false);
            return;
        }

        if (_sessionId is not { } id)
        {
            await Say(Role.System, "Nothing has been kept in this chat yet, so there is nothing to pin. Say something first.").ConfigureAwait(false);
            return;
        }

        PinSession(id, !off);
    }

    /// <summary>Pins a kept chat, or lets it go: <c>diptick chat pin</c>. Runs on a worker thread.</summary>
    public void PinSession(string id, bool pinned)
    {
        ArgumentNullException.ThrowIfNull(id);
        var (text, _) = pinned ? Capture(yes: false, "chat", "pin", $"--session={id}") : Capture(yes: false, "chat", "pin", $"--session={id}", "--off");
        lock (_lock)
        {
            _sessions = null;
        }

        _ = Say(Role.System, text.Length == 0 ? "(nothing printed)" : text);
    }
}
