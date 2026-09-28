using System.Globalization;
using DipTick.Models.Chat;
using DipTick.Models.Harness;

namespace DipTick.Models;

/// <summary>What sort of value a setting takes, which decides how it is asked for.</summary>
internal enum SettingKind
{
    Text,
    Choice,
    Number,
    YesNo,
    List,
    Money,
    Provider,
    Model,
    Address,
    Variable,
    Password,
}

/// <summary>
/// One setting as a person sees it: where it sits, what it is called, what it does, what it takes,
/// and its default. <see cref="Name"/> is the dotted name; a seat's keys are written
/// <c>&lt;seat&gt;.key</c>.
/// </summary>
internal sealed record SettingInfo(
    string Name,
    string Section,
    string Label,
    string Help,
    SettingKind Kind,
    string Default,
    IReadOnlyList<string>? Choices = null,
    int Min = 0,
    int Max = 0)
{
    /// <summary>What it takes, in words: the choices, the range, or the kind.</summary>
    public string Takes => Kind switch
    {
        SettingKind.Choice => string.Join(", ", Choices ?? []),
        SettingKind.Provider => "a provider's id: 'diptick providers' lists them, or openai-compatible",
        SettingKind.Number => string.Create(CultureInfo.InvariantCulture, $"a whole number from {Min} to {Max}"),
        SettingKind.YesNo => "true or false",
        SettingKind.List => "a comma-separated list",
        SettingKind.Money => "an amount in money.currency, like 0.25",
        SettingKind.Model => "a model name; 'diptick models' or /model lists them",
        SettingKind.Address => "an https URL, or http to this PC",
        SettingKind.Variable => "an environment variable's name, not its value",
        SettingKind.Password => "set with 'diptick config password'",
        _ => "text",
    };
}

/// <summary>
/// Every setting DipTick has, as a person sees it. The settings screen and <c>diptick settings</c>
/// draw from it; it holds every setting <see cref="MasterConfig"/> reads, and no other.
/// </summary>
internal static class SettingsCatalogue
{
    public const string SeatPlaceholder = "<seat>";

    /// <summary>The section that lists DipTick's own pages (Settings, Help): no settings, one row a page.</summary>
    public const string HelpSection = "Help";

    /// <summary>The section the screens do not show: see <c>ViewModels.SettingsViewModel.Sections</c>.</summary>
    public const string HiddenSection = "Database";

    /// <summary>
    /// The sections in the order they are shown, each with a note when it does not act, or acts in
    /// one place only. Settings is the one place a setting changes.
    /// </summary>
    public static IReadOnlyList<(string Name, string Note)> Sections { get; } =
    [
        ("You", string.Empty),
        ("Models", string.Empty),
        ("Chats", "A new chat seats the model that answers you and the agents named here, in this mode; each chat then keeps its own table and mode (F4)."),
        ("Money", "Prices are per million tokens, as each provider's price page says; DipTick never looks them up. Every cap is off until you set one."),
        ("Permissions", "What agents may do, capability by capability: allowed, ask me first, or never. A chat can tighten any of it, and turn an ask into an allow for itself; nothing loosens a never. Every decision is in Activity."),
        ("Tools", "DipTick's own tools, for you and for agents: the clock's other time zones, the search service agents use, where the files agents make go, and who makes images and video."),
        ("Connectors", "Services and programs agents may use (MCP): the ones DipTick knows, and yours. Each is signed in in your browser, its tools each Allowed, Ask me first or Never, and on only in the chats you put it in. A program on this PC runs only after your yes to exactly what it runs."),
        ("Skills", "Instructions agents follow when they need them, in the open Agent Skills format: added from a folder or a zip, shown to you whole, and used only after your yes and only in the chats you put them in. A skill's scripts run only in DipTick's sandbox, each run asked."),
        ("Appearance", "These change the window. The terminal screen (diptick tui) keeps the terminal's own look."),
        ("Database", "A database of your own, for a tool that works with databases. No tool uses one, so these settings are only read and checked."),
        ("Command line", string.Empty),
        ("Data", "Where DipTick keeps things. One click opens the folder; What is this? says what each is for."),
        (HelpSection, "DipTick's official pages, the same as 'diptick docs'. One click opens one in Help."),
    ];

    private static readonly string[] DestinationSuffixes = [".endpoint", ".keyVariable", ".lane", ".ask", ".workspace"];

    private static readonly string[] Providers = [.. Engines.Known.Keys];

    private static readonly List<SettingInfo> Fixed =
    [
        new("user.name", "You", "Your name", "Shown on your lines in the chat, recorded in the audit log as who said yes to each decision, and told to the agents in each chat, so they know whom they answer. Empty: agents are told \"the person\", and your lines and the log show your Windows user name.", SettingKind.Text, string.Empty),
        new("user.passwordHash", "You", "Password", "Asked before the chat screen opens. Only an Argon2id hash of it is kept, never the password.", SettingKind.Password, string.Empty),
        new("user.brief", "You", "Your instructions for every chat", "Told to every agent in every chat, marked as yours: how you like answers, what you work on. Up to 2000 characters. 'diptick chat brief' shows exactly what each agent is told.", SettingKind.Text, string.Empty),

        new("main.provider", "Models", "Provider", "Who serves the model that answers you: one of the providers DipTick knows ('diptick providers' lists them all, on this PC, makers, hosts, clouds and gateways), or another server with the OpenAI chat API (openai-compatible).", SettingKind.Provider, string.Empty, Providers),
        new("main.model", "Models", "Model", "Which of the provider's models answers you. The list comes from the provider itself.", SettingKind.Model, string.Empty),
        new("main.endpoint", "Models", "Address", "Where the chat is sent. Empty uses the provider's own address; a cloud account, a deployment or another server needs one.", SettingKind.Address, string.Empty),
        new("main.keyVariable", "Models", "Key variable", "The name of an environment variable holding the key, for scripts. Empty uses the key saved by setup or 'diptick key set'.", SettingKind.Variable, string.Empty),
        new("main.timeoutSeconds", "Models", "Seconds per answer", "How long to wait for one answer before giving up.", SettingKind.Number, "120", Min: 1, Max: 600),
        new("main.workspace", "Models", "Workspace", "The Anthropic workspace the key acts in (wrkspc_...): Claude Platform on AWS needs one; an Anthropic key that acts in several may name one. Empty for none.", SettingKind.Text, string.Empty),
        new("main.maxAnswerTokens", "Models", "Longest answer", "The most tokens one answer from this model may take; its provider stops it there. On a model that thinks, the thinking counts too.", SettingKind.Number, "4000", Min: 1, Max: MasterConfig.MaxAnswerCeiling),
        new("main.contextTokens", "Models", "Chat sent", "How much of a long chat this model is sent, in tokens. Older rounds are left out whole, never the first thing you said, and the model is told how many.", SettingKind.Number, "128000", Min: 1000, Max: 1_000_000),
        new("main.priceIn", "Models", "Price in", "What its provider charges per million tokens read, from the provider's price page; unknown when not set. Used to show and cap what answers cost.", SettingKind.Money, "unknown"),
        new("main.priceOut", "Models", "Price out", "What its provider charges per million tokens written.", SettingKind.Money, "unknown"),
        new("main.brief", "Models", "Its instructions", "Told to this model in every chat, marked as yours, up to 2000 characters.", SettingKind.Text, string.Empty),
        new("main.tools", "Models", "Tools", "Whether it is offered tools: auto (as its provider and model say; Ollama is asked), on, or off. Each tool still goes through your permissions.", SettingKind.Choice, "auto", ["auto", "on", "off"]),
        new("quit.unloadModels", "Models", "Unload local models on quit", "When DipTick quits, it asks Ollama on this PC to unload the models DipTick used, so they stop holding memory. No leaves them loaded.", SettingKind.YesNo, "true"),
        new("providers.googleMachineAccount", "Models", "Google Cloud machine's own account", "On a Google Compute Engine machine with no Google Cloud sign-in file, sign in as the machine's own account, through its metadata server. Asked only when this is on and the machine's firmware says it is Google's; its tokens go only to Google.", SettingKind.YesNo, "false"),

        new("permissions.allow", "Permissions", "Allowed", "What agents may do without asking. Capabilities: model.call, fs.read, fs.write, fs.delete, net.fetch, net.probe, web.search, media.make, mcp.call, db.read, db.write, shell.run; a group like fs.*; *; or one narrowed to a target, like mcp.call:clickup/*.", SettingKind.List, "(none)"),
        new("permissions.ask", "Permissions", "Ask me first", "What agents may do only after your yes, each time.", SettingKind.List, "(none)"),
        new("permissions.deny", "Permissions", "Never", "What agents may never do. Never beats ask me first, which beats allowed; anything no list names is denied.", SettingKind.List, "(none)"),
        new("permissions.roots", "Permissions", "Folders", "The only folders the file capabilities may touch, as full paths. None: no file access at all.", SettingKind.List, "(none)"),
        new("permissions.hosts", "Permissions", "Web hosts", "The only hosts net.fetch may reach, like example.com or *.example.com. None: no web access.", SettingKind.List, "(none)"),

        new("chat.seats", "Chats", "New chats seat", "The agents a new chat seats beside the model that answers you, in speaking order; four at a table in all. Add a model, under Models, sets one up; F4 changes a chat's own table.", SettingKind.List, "(none)"),
        new("chat.mode", "Chats", "New chats' mode", "Who answers when you address nobody: all (every agent at once), solo <name>, pick (only whom you @name), relay <a> <b>, review <builder> <reviewer> [revise], or stack <a> <b> [turns] (a debate). Each chat keeps its own.", SettingKind.Text, "all"),
        new("chat.maxAiTurns", "Chats", "Turns a round", "The most agent answers one thing you say can bring. A plan longer than this is cut, and says so; never more than 8.", SettingKind.Number, "4", Min: 1, Max: RoundLimits.TurnCeiling),
        new("chat.maxRoundSeconds", "Chats", "Seconds a round", "How long one round may run before its answers are cut.", SettingKind.Number, "300", Min: 30, Max: 1800),
        new("chat.maxRoundTokens", "Chats", "Tokens a round", "The most tokens all of a round's answers may write together; a later answer gets what is left.", SettingKind.Number, "16000", Min: 256, Max: MasterConfig.MaxAnswerCeiling * 4),
        new("chat.maxQuotedChars", "Chats", "Quoted from each answer", "How much of one agent's answer another agent is shown, in characters; the rest is marked as left out.", SettingKind.Number, "16000", Min: 1000, Max: 65536),
        new("chat.save", "Chats", "Keep sessions", "Keep every chat in sessions.db, in DipTick's data folder, so it can be opened again. No: a session lives only while its window is open.", SettingKind.YesNo, "true"),
        new("audit.privateDays", "Chats", "Private decisions kept for", "A private chat keeps nothing, but what an agent decided in it is recorded, without words, in a log of its own for each day (audit-private, in DipTick's data folder), so it can still be checked. A day's log is deleted whole after this many days: 15 to 30.", SettingKind.Number, "30", Min: 15, Max: 30),
        new("chat.pasteLines", "Chats", "Paste to a file from", "In the terminal screen, a paste of this many lines or more is kept in a text file in DipTick's data folder (pastes), and the composer shows the file's name; sending puts the whole text in the message. The file goes when the screen closes, unless a kept draft names it. 0 keeps every paste in the composer.", SettingKind.Number, "5", Min: 0, Max: 10000),

        new("tools.zones", "Tools", "Other time zones", "The clock's other places, as time zone ids (Windows' own, like Tokyo Standard Time, or IANA, like Asia/Tokyo). The Tools sheet shows each beside your own.", SettingKind.List, "(none)"),
        new("research.search", "Tools", "Search service", "What agents search the web through: none, brave (the Brave Search API, with your own key) or searxng (a SearXNG you run). DipTick has no search of its own and no key of its own.", SettingKind.Choice, "none", ["none", "brave", "searxng"]),
        new("research.searxngUrl", "Tools", "Your SearXNG", "Its address, like https://search.example.org; its JSON output must be on in its settings.yml, or it answers 403.", SettingKind.Address, string.Empty),
        new("research.keyVariable", "Tools", "Brave key variable", "An environment variable holding your Brave Search API key, for scripts. Empty uses the key saved with 'diptick key set research'.", SettingKind.Variable, string.Empty),
        new("research.braveUrl", "Tools", "Brave's address", "Where Brave is reached. Empty is Brave's own, https://api.search.brave.com; a key saved for Brave is never sent anywhere else.", SettingKind.Address, string.Empty),
        new("files.folder", "Tools", "Where agents' files go", "The folder the files agents make are written to, one folder per chat inside it; agents may always touch it, and writing there asks as fs.write does. Empty is Documents\\DipTick. Not a drive's root or your profile's folder.", SettingKind.Text, string.Empty),
        new("media.images", "Tools", "Images from", "Who makes images, for /image and agents' make_image: auto (OpenAI if DipTick has its key, else Google), openai (its Images API), google (the Gemini API's image models), nscale, replicate, fal, wavespeed (each with the model you name), comfyui (your workflow), sdcpp (stable-diffusion.cpp's sd-server), or none. A hosted one costs money on your key.", SettingKind.Choice, "auto", ["auto", .. Media.ImageMaker.Providers, "none"]),
        new("media.imageModel", "Tools", "Image model", "Empty uses gpt-image-2.5-flare at OpenAI, gemini-3.1-flash-image at Google. Nscale, Replicate (owner/name), fal (like fal-ai/flux/schnell) and WaveSpeedAI (like wavespeed-ai/flux-dev) need one named here.", SettingKind.Text, string.Empty),
        new("media.imageInput", "Tools", "Image model's own fields", "For Replicate, fal and WaveSpeedAI: the model's own fields as a JSON object, sent beside your words, like {\"aspect_ratio\": \"16:9\"}. Each model takes its own; its page on the provider lists them. No secret in it.", SettingKind.Text, string.Empty),
        new("media.imageAddress", "Tools", "Image server's address", "Where the images provider is, when not at its own address: a ComfyUI or sd-server of yours on another port, say. https, or http to this PC.", SettingKind.Address, string.Empty),
        new("media.comfyImageWorkflow", "Tools", "ComfyUI image workflow", "The full path of a workflow saved with ComfyUI's File > Export (API). DipTick puts your words where it says {{prompt}}, and fills {{width}}, {{height}} and {{seed}}.", SettingKind.Text, string.Empty),
        new("media.video", "Tools", "Video from", "Who makes video, for /video and agents' make_video: auto (Google if DipTick has its key), google (Veo, through the Gemini API), replicate, fal, wavespeed (each with the model you name), comfyui (your workflow), sdcpp (sd-server), or none. A hosted one costs money on your key.", SettingKind.Choice, "auto", ["auto", .. Media.VideoMaker.Providers, "none"]),
        new("media.videoModel", "Tools", "Video model", "Empty uses veo-3.1-generate-preview; veo-3.1-fast-generate-preview and veo-3.1-lite-generate-preview are quicker and cheaper. Replicate, fal and WaveSpeedAI need one named here.", SettingKind.Text, string.Empty),
        new("media.videoInput", "Tools", "Video model's own fields", "For Replicate, fal, WaveSpeedAI and sd-server: the model's own fields as a JSON object, sent beside your words, like {\"duration\": 5}.", SettingKind.Text, string.Empty),
        new("media.videoAddress", "Tools", "Video server's address", "Where the video provider is, when not at its own address. https, or http to this PC.", SettingKind.Address, string.Empty),
        new("media.comfyVideoWorkflow", "Tools", "ComfyUI video workflow", "The full path of a video workflow exported in API format; {{prompt}}, {{width}}, {{height}}, {{seed}} and {{seconds}} are filled.", SettingKind.Text, string.Empty),
        new("media.speech", "Tools", "Speech to text", "What turns recordings into text, for 'diptick transcribe' and agents' transcribe: whispercpp (whisper.cpp's whisper-server on this PC), or none.", SettingKind.Choice, "none", ["none", .. Media.SpeechMaker.Providers]),
        new("media.speechAddress", "Tools", "Speech server's address", "Where whisper-server is, when not at http://127.0.0.1:8080. https, or http to this PC.", SettingKind.Address, string.Empty),

        new("money.showCost", "Money", "Show what answers cost", "Each answer shows its tokens and, where its agent's prices are set, its cost; the chat and the day show their totals.", SettingKind.YesNo, "true"),
        new("money.currency", "Money", "Currency", "The currency your prices are in, as three letters: USD, EUR, GBP. Only a label: DipTick converts nothing.", SettingKind.Text, "USD"),
        new("money.capChat", "Money", "Most a chat may spend", "Off, or an amount: an answer that could take a chat past it is not asked for, and the chat says so. Only answers with prices set count.", SettingKind.Money, "off"),
        new("money.capDay", "Money", "Most a day may spend", "Off, or an amount for all chats together, today.", SettingKind.Money, "off"),
        new("money.capMonth", "Money", "Most a month may spend", "Off, or an amount for all chats together, this month.", SettingKind.Money, "off"),

        new("appearance.theme", "Appearance", "Theme", "dark or light, or system to follow Windows: the window changes when Windows does.", SettingKind.Choice, "dark", [.. MasterConfig.Themes]),
        new("appearance.textSize", "Appearance", "Text size", "How large the window draws everything, text and all: small, normal, large or larger.", SettingKind.Choice, "normal", [.. MasterConfig.TextSizes]),
        new("appearance.density", "Appearance", "Density", "comfortable spaces the messages and rows out; compact fits more on the screen.", SettingKind.Choice, "comfortable", [.. MasterConfig.Densities]),

        new("db.kind", "Database", "Kind", "sqlite (one file, the default), postgresql, sqlserver, duckdb, mariadb or neo4j: six that a person and an agent can both work in.", SettingKind.Choice, "sqlite", [.. DatabaseKinds.Names]),
        new("db.connection", "Database", "Connection", "A file path for sqlite or duckdb; a connection string or URL for the rest. Never a password in it.", SettingKind.Text, DatabaseKinds.DefaultConnection),
        new("db.secretVariable", "Database", "Password variable", "The name of an environment variable holding the database password, if it needs one.", SettingKind.Variable, string.Empty),

        new("output.format", "Command line", "Output", "text for people, json for programs, none for the exit code alone.", SettingKind.Choice, "text", ["text", "json", "none"]),
        new("cli.dryRun", "Command line", "Dry run", "Change nothing; say what would change. A bad value here reads as on, so a typo never makes a real change.", SettingKind.YesNo, "false"),
        new("cli.yes", "Command line", "Answer yes", "Answer yes to your own confirmations. It never answers an agent's request to write or delete.", SettingKind.YesNo, "false"),
        new("cli.interactive", "Command line", "Ask questions", "auto asks only at a keyboard; true or false forces it.", SettingKind.Choice, "auto", ["auto", "true", "false"]),
        new("cli.verbosity", "Command line", "Detail", "How much the commands say.", SettingKind.Choice, "normal", ["quiet", "normal", "verbose", "debug"]),
        new("cli.color", "Command line", "Colour", "auto uses colour at a terminal and honours NO_COLOR.", SettingKind.Choice, "auto", ["auto", "always", "never"]),
        new("cli.timeoutSeconds", "Command line", "Seconds per network call", "How long a database or network call may take. Answers use their own setting.", SettingKind.Number, "60", Min: 1, Max: 600),
    ];

    private static readonly List<SettingInfo> SeatKeys =
    [
        new($"{SeatPlaceholder}.name", "Models", "Name", "Shown on screen and matched by @; the seat's id when empty.", SettingKind.Text, SeatPlaceholder),
        new($"{SeatPlaceholder}.provider", "Models", "Provider", "Who serves this seat's model.", SettingKind.Provider, string.Empty, Providers),
        new($"{SeatPlaceholder}.model", "Models", "Model", "Which of the provider's models sits here.", SettingKind.Model, string.Empty),
        new($"{SeatPlaceholder}.endpoint", "Models", "Address", "Where this seat's conversation is sent. Empty uses the provider's own address.", SettingKind.Address, string.Empty),
        new($"{SeatPlaceholder}.keyVariable", "Models", "Key variable", "An environment variable holding this seat's key. Empty uses the key saved for its host.", SettingKind.Variable, string.Empty),
        new($"{SeatPlaceholder}.timeoutSeconds", "Models", "Seconds per answer", "How long to wait for one of this seat's answers.", SettingKind.Number, "120", Min: 1, Max: 600),
        new($"{SeatPlaceholder}.maxAnswerTokens", "Models", "Longest answer", "The most one answer may be, in tokens.", SettingKind.Number, "1500", Min: 1, Max: MasterConfig.MaxAnswerCeiling),
        new($"{SeatPlaceholder}.contextTokens", "Models", "Context", "How much of the conversation this seat is sent, in tokens.", SettingKind.Number, "16000", Min: 1000, Max: 1_000_000),
        new($"{SeatPlaceholder}.priceIn", "Models", "Price in", "What the provider charges per million tokens read, from its price page; unknown when not set. Used to show and cap what answers cost.", SettingKind.Money, "unknown"),
        new($"{SeatPlaceholder}.priceOut", "Models", "Price out", "What the provider charges per million tokens written.", SettingKind.Money, "unknown"),
        new($"{SeatPlaceholder}.color", "Models", "Colour", "The colour of this seat's lines.", SettingKind.Choice, "white", ["cyan", "magenta", "blue", "white"]),
        new($"{SeatPlaceholder}.lane", "Models", "Lane", "What this seat may ask the harness for at all. It only narrows the permissions; it never widens them. Unset, it is everything they allow.", SettingKind.List, "*"),
        new($"{SeatPlaceholder}.tools", "Models", "Tools", "Whether this seat is offered tools: auto (as its provider and model say), on, or off.", SettingKind.Choice, "auto", ["auto", "on", "off"]),
        new($"{SeatPlaceholder}.ask", "Models", "Always ask", "What needs your yes for this seat even where the permissions allow it.", SettingKind.List, "(none)"),
        new($"{SeatPlaceholder}.brief", "Models", "Brief", "A short note to this seat about its part, up to 2000 characters.", SettingKind.Text, string.Empty),
        new($"{SeatPlaceholder}.effort", "Models", "Effort", "How hard a Claude seat thinks: low, medium or high; empty for the provider's default. Claude seats only.", SettingKind.Choice, string.Empty, ["", "low", "medium", "high"]),
        new($"{SeatPlaceholder}.workspace", "Models", "Workspace", "The Anthropic workspace this seat's key acts in (wrkspc_...): Claude Platform on AWS needs one; an Anthropic key that acts in several may name one.", SettingKind.Text, string.Empty),
    ];

    /// <summary>Every fixed setting, in the order shown.</summary>
    public static IReadOnlyList<SettingInfo> All => Fixed;

    /// <summary>A seat's settings, with <see cref="SeatPlaceholder"/> for its id.</summary>
    public static IReadOnlyList<SettingInfo> SeatTemplate => SeatKeys;

    /// <summary>A seat template with the seat's id in place of the placeholder.</summary>
    public static SettingInfo For(SettingInfo template, string seatId)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template with
        {
            Name = template.Name.Replace(SeatPlaceholder, seatId, StringComparison.Ordinal),
            Default = template.Default.Replace(SeatPlaceholder, seatId, StringComparison.Ordinal),
        };
    }

    /// <summary>
    /// Changes that widen what agents may do or change where the conversation goes need a yes: the
    /// permissions, who sits at the table, and where a seat's words and key are sent, what a seat may
    /// ask for, and which workspace its key acts in; and where agents' searches go, since a search's
    /// words leave the PC.
    /// </summary>
    public static bool NeedsYes(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.StartsWith("permissions.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("research.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("media.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("providers.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "files.folder", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "chat.seats", StringComparison.OrdinalIgnoreCase)
            || DestinationSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether a capability rule list names <paramref name="cap"/> only by its own name, never by a group or *.</summary>
    public static bool NamesOnlyExactly(IEnumerable<string> rules, Capability cap)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return rules.All(r => !Capabilities.Matches(r, cap) || string.Equals(r, Capabilities.Name(cap), StringComparison.OrdinalIgnoreCase));
    }
}
