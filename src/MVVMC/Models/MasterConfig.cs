using System.Globalization;
using System.Text.Json;
using DipTick.Models.Harness;

namespace DipTick.Models;

/// <summary>
/// DipTick's settings, from <c>diptick.master.json</c>: each value and where it came from.
/// </summary>
/// <remarks>
/// <para>
/// A bad value never breaks anything. A missing file is every default. A syntax error
/// anywhere means every default. A value of the wrong type or outside its limits is ignored
/// and its default used. An unknown key is reported and changes nothing. Each of those lands
/// in <see cref="Problems"/>; loading never throws for anything the file contains.
/// </para>
/// <para>
/// The file never holds a secret in the clear. The password is an Argon2id hash
/// (<see cref="PasswordHash"/>) written by <c>diptick config password</c>; a plain
/// <c>user.password</c> is reported and never read. Database and model keys stay in environment
/// variables the file names, and a connection string carrying a password is refused.
/// </para>
/// </remarks>
internal sealed partial class MasterConfig
{
    public const string FileName = "diptick.master.json";
    public const string PathVariable = "DIPTICK_CONFIG";
    public const int CurrentSchema = 1;

    private const int MinTimeout = 1;
    private const int MaxTimeout = 600;

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 16,
    };

    private static readonly string[] Formats = ["text", "json", "none"];
    private static readonly string[] Verbosities = ["quiet", "normal", "verbose", "debug"];
    private static readonly string[] Colors = ["auto", "always", "never"];

    /// <summary>The window's themes: its own two, or "system", which follows Windows as it changes.</summary>
    public static IReadOnlyList<string> Themes { get; } = ["dark", "light", "system"];

    /// <summary>How large the window draws everything, smallest first.</summary>
    public static IReadOnlyList<string> TextSizes { get; } = ["small", "normal", "large", "larger"];

    public static IReadOnlyList<string> Densities { get; } = ["comfortable", "compact"];

    private readonly List<string> _problems = [];
    private readonly List<string> _notices = [];
    private readonly List<Setting> _settings = [];
    private int _duplicated;
    private bool _sectionMissingButBroken;

    /// <summary>Whether the permissions section was read whole, so the chats' own folder may join its folders.</summary>
    private bool _permissionsTrusted;

    /// <summary>The folders <c>permissions.roots</c> names in the file, without the chats' own folder that DipTick adds; what a change to the list writes back.</summary>
    public IReadOnlyList<string> FileRoots { get; private set; } = [];

    private MasterConfig(string? path) => Path = path;

    /// <summary>The file read, or null when none was found and every value is a default.</summary>
    public string? Path { get; }

    public IReadOnlyList<string> Problems => _problems;

    /// <summary>
    /// Things worth saying that break nothing: a section still under its former name. Unlike
    /// <see cref="Problems"/>, they never fail <c>diptick config check</c>; <c>diptick doctor</c> names them.
    /// </summary>
    public IReadOnlyList<string> Notices => _notices;

    /// <summary>
    /// True when the main model's settings were read from a section still called <c>ai</c>, its
    /// former name. The first change to a <c>main.*</c> key renames the section in the file.
    /// </summary>
    public bool LegacyMain { get; private set; }

    public IReadOnlyList<Setting> Settings => _settings;

    public string UserName { get; private set; } = string.Empty;

    /// <summary><c>user.brief</c>: the person's own instructions for every agent in every chat, labelled as theirs.</summary>
    public string UserBrief { get; private set; } = string.Empty;

    /// <summary>The stored Argon2id hash, or empty when no password is set.</summary>
    public string UserPasswordHash { get; private set; } = string.Empty;

    public DatabaseKind DbKind { get; private set; } = DatabaseKinds.Default;

    public string DbConnection { get; private set; } = DatabaseKinds.DefaultConnection;

    public string DbSecretVariable { get; private set; } = string.Empty;

    /// <summary>What agents may do. Anything not allowed here is denied.</summary>
    public Permissions Permissions { get; private set; } = Permissions.Closed;

    public string MainProvider { get; private set; } = string.Empty;

    public string MainModel { get; private set; } = string.Empty;

    public string MainEndpoint { get; private set; } = string.Empty;

    public string MainKeyVariable { get; private set; } = string.Empty;

    public int MainTimeoutSeconds { get; private set; } = 120;

    /// <summary>
    /// <c>main.workspace</c>: the Anthropic workspace the key acts in, for Anthropic's API when a key
    /// can act in more than one, and for Claude Platform on AWS, which needs one on every call.
    /// </summary>
    public string MainWorkspace { get; private set; } = string.Empty;

    /// <summary>The model that answers you, as an agent at a table: the <c>main</c> section, under the name <c>main</c>.</summary>
    public SeatConfig MainAgent { get; private set; } =
        new(MainAgentName, MainAgentName, string.Empty, string.Empty, string.Empty, string.Empty, 120, 4000, 128000, null, null, MainColor, ["model.call"], [], string.Empty, string.Empty, string.Empty);

    /// <summary>The name the model that answers you sits under at a table, and in the store.</summary>
    public const string MainAgentName = "main";

    /// <summary>The main model's colour at a table: the accent, which no seat takes.</summary>
    public const string MainColor = "green";

    /// <summary>The most tokens any one answer may be given: long answers are a power user's to ask for.</summary>
    public const int MaxAnswerCeiling = 128000;

    /// <summary><c>quit.unloadModels</c>: when DipTick quits, the local models it used are unloaded (Ollama).</summary>
    public bool QuitUnloadModels { get; private set; } = true;

    public string OutputFormat { get; private set; } = "text";

    /// <summary><c>cli.dryRun</c>. A bad value reads as true: a typo must not turn a preview into a real run.</summary>
    public bool CliDryRun { get; private set; }

    public bool CliYes { get; private set; }

    /// <summary><c>cli.interactive</c>: null is "auto", ask only when stdin is a keyboard.</summary>
    public bool? CliInteractive { get; private set; }

    public string CliVerbosity { get; private set; } = "normal";

    public string CliColor { get; private set; } = "auto";

    public int CliTimeoutSeconds { get; private set; } = 60;

    /// <summary><c>appearance.theme</c>: dark, light, or system (follow Windows).</summary>
    public string AppearanceTheme { get; private set; } = "dark";

    /// <summary><c>appearance.textSize</c>: small, normal, large or larger.</summary>
    public string AppearanceTextSize { get; private set; } = "normal";

    /// <summary><c>appearance.density</c>: comfortable or compact.</summary>
    public string AppearanceDensity { get; private set; } = "comfortable";

    /// <summary>
    /// Where the config is looked for, first found wins: <c>--config</c>, <c>DIPTICK_CONFIG</c>,
    /// the per-user file (<see cref="UserConfigPath"/>), the file beside diptick.exe.
    /// </summary>
    /// <remarks>
    /// Never the current folder or any folder above it. A config found there could have been put
    /// there by anything that can write to a project, an agent included, and whoever writes the
    /// config writes the permissions.
    /// </remarks>
    public static string? Find() => Find(explicitPath: null);

    /// <summary>The per-user config: %APPDATA%\DipTick\diptick.master.json.</summary>
    public static string UserConfigPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        "DipTick",
        FileName);

    /// <summary>
    /// As <see cref="Find()"/>, but a path given with <c>--config</c> comes first. The word
    /// <c>none</c> means no file at all: every value is a default.
    /// </summary>
    public static string? Find(string? explicitPath)
    {
        if (explicitPath is not null)
        {
            return explicitPath.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : System.IO.Path.GetFullPath(explicitPath);
        }

        var named = Environment.GetEnvironmentVariable(PathVariable);
        if (!string.IsNullOrWhiteSpace(named))
        {
            return System.IO.Path.GetFullPath(named);
        }

        if (File.Exists(UserConfigPath))
        {
            return UserConfigPath;
        }

        var beside = System.IO.Path.Combine(AppContext.BaseDirectory, FileName);
        return File.Exists(beside) ? beside : null;
    }

    public static MasterConfig Load(string? path)
    {
        var config = new MasterConfig(path);
        if (path is null)
        {
            config.ReadAll(root: default);
            return config;
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            config._problems.Add($"{path}: cannot be read ({ex.Message}); every default used.");
            config.ReadAll(root: default);
            return config;
        }

        return config.Parse(text, path);
    }

    /// <summary>
    /// Settings read from text rather than a file: the starter inside diptick.exe, before setup has
    /// written it anywhere. <paramref name="path"/> is where it will live, which the permissions
    /// protect as they protect the file itself.
    /// </summary>
    public static MasterConfig FromText(string text, string? path)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new MasterConfig(path).Parse(text, path ?? "(text)");
    }

    /// <summary>Why this cannot be a model's endpoint, or null when it can: the rule main.endpoint is held to.</summary>
    public static string? EndpointProblem(string text) => CheckEndpoint(text);

    /// <summary>Why this cannot be a seat's id, or null when it can.</summary>
    public static string? SeatIdProblem(string text) => CheckSeatId(text);

    private MasterConfig Parse(string text, string path)
    {
        try
        {
            using var document = JsonDocument.Parse(text, ReadOptions);
            ReadAll(document.RootElement);
        }
        catch (JsonException ex)
        {
            var line = ex.LineNumber is { } n ? (n + 1).ToString(CultureInfo.InvariantCulture) : "?";
            _problems.Add($"{path}: not valid JSON at line {line}; every default used.");
            _settings.Clear();
            ReadAll(root: default);
        }

        return this;
    }

    private void ReadAll(JsonElement root)
    {
        var sections = TopLevel(root);

        var user = Section(sections, "user");
        UserName = Text(user, "user.name", "name", UserName, check: null);
        UserBrief = Text(user, "user.brief", "brief", UserBrief, t => t.Length <= 2000 ? null : "is longer than 2000 characters");
        UserPasswordHash = Text(user, "user.passwordHash", "passwordHash", UserPasswordHash, CheckHash);
        if (user.Remove("password"))
        {
            _problems.Add("'user.password' is never read: a password is not kept in the clear. Remove it and run 'diptick config password'.");
        }

        Unknown(user, "user");

        var db = Section(sections, "db");
        var kind = Text(db, "db.kind", "kind", DatabaseKinds.Name(DbKind), CheckKind);
        DbKind = DatabaseKinds.Parse(kind) ?? DatabaseKinds.Default;
        DbConnection = Text(db, "db.connection", "connection", DbConnection, CheckConnection);
        DbSecretVariable = Text(db, "db.secretVariable", "secretVariable", DbSecretVariable, CheckVariableName);
        Unknown(db, "db");

        // Fail closed. A bad entry dropped from allow, roots or hosts only narrows what agents may
        // do; one dropped from deny or ask would widen it, as would a duplicated key silently
        // replacing a list. Either of those, and nothing is allowed until the file is fixed.
        var problemsBefore = _problems.Count;
        var duplicatedBefore = _duplicated;
        var permissions = Section(sections, "permissions");
        var allow = List(permissions, "permissions.allow", "allow", CheckRule);
        var ask = List(permissions, "permissions.ask", "ask", CheckRule);
        var askBad = _problems.Count > problemsBefore;
        var deny = List(permissions, "permissions.deny", "deny", CheckRule);
        var denyBad = _problems.Count > problemsBefore;
        var roots = List(permissions, "permissions.roots", "roots", CheckRoot);
        var hosts = List(permissions, "permissions.hosts", "hosts", CheckHost);
        Unknown(permissions, "permissions");
        // DipTick's data, and the folder the program's own libraries are unpacked into.
        List<string> guarded = [AuditLog.DataFolder(), Unpacked.Folder()];
        if (Path is not null)
        {
            guarded.Add(Path);

            // What connectors run and where they connect is as much the person's as the settings are.
            if (Connectors.ConnectorFile.PathFor(this) is { } connectors)
            {
                guarded.Add(connectors);
            }

            // So are the skills, and which of them the person approved.
            if (Skills.SkillStore.FolderFor(this) is { } skills && Skills.SkillStore.LockFor(this) is { } approvals)
            {
                guarded.Add(skills);
                guarded.Add(approvals);
            }
        }

        if (askBad || denyBad || _duplicated != duplicatedBefore || _sectionMissingButBroken)
        {
            _problems.Add("The permissions section cannot be trusted in full, so every capability is denied until it is fixed.");
            Permissions = Permissions.Closed with { Protected = guarded };
        }
        else
        {
            Permissions = new Permissions(allow, ask, deny, roots, hosts, guarded);
            _permissionsTrusted = true;
        }

        FileRoots = roots;

        // The main model: the one that answers when a chat has no table of its own. Its section's
        // former name is "ai"; a file that still says so is read as it stands.
        var hasMain = sections.ContainsKey("main");
        var hasFormer = sections.ContainsKey("ai");
        var main = Section(sections, hasMain ? "main" : "ai");
        if (hasMain && hasFormer)
        {
            Section(sections, "ai");
            _notices.Add("'ai' is the former name of 'main'; 'main' is read and 'ai' is ignored. Remove the 'ai' section.");
        }
        else if (hasFormer)
        {
            LegacyMain = true;
            _notices.Add("'ai' is the former name of 'main' and is read as 'main'. The next change to a main.* setting renames it in the file.");
        }

        MainProvider = Text(main, "main.provider", "provider", MainProvider, global::DipTick.Models.Chat.Engines.ProviderProblem);
        MainModel = Text(main, "main.model", "model", MainModel, check: null);
        MainEndpoint = Text(main, "main.endpoint", "endpoint", MainEndpoint, CheckEndpoint);
        MainEndpoint = CheckDestination("main", MainProvider, MainEndpoint);
        MainKeyVariable = Text(main, "main.keyVariable", "keyVariable", MainKeyVariable, CheckVariableName);
        MainTimeoutSeconds = Number(main, "main.timeoutSeconds", "timeoutSeconds", MainTimeoutSeconds, MinTimeout, MaxTimeout);
        MainWorkspace = Text(main, "main.workspace", "workspace", MainWorkspace, CheckWorkspace);
        if (MainWorkspace.Length > 0 && !TakesWorkspace(MainProvider))
        {
            _problems.Add($"'main.workspace' applies to Claude through Anthropic's API or Claude Platform on AWS only; ignored for {(MainProvider.Length == 0 ? "no provider" : MainProvider)}.");
            MainWorkspace = string.Empty;
        }

        // As an agent at a table: its answer's cap, how much of the chat it
        // is sent, its prices, and the person's instructions for it.
        var mainMaxAnswer = Number(main, "main.maxAnswerTokens", "maxAnswerTokens", 4000, 1, MaxAnswerCeiling);
        var mainContext = Number(main, "main.contextTokens", "contextTokens", 128000, 1000, 1_000_000);
        var mainPriceIn = Money(main, "main.priceIn", "priceIn", null, allowNull: true);
        var mainPriceOut = Money(main, "main.priceOut", "priceOut", null, allowNull: true);
        var mainBrief = Text(main, "main.brief", "brief", string.Empty, t => t.Length <= 2000 ? null : "is longer than 2000 characters");
        var mainTools = Text(main, "main.tools", "tools", "auto", CheckTools);

        // Its lane is everything the permissions allow: the permissions decide what any agent may do,
        // and ask before anything consequential.
        MainAgent = new SeatConfig(
            MainAgentName, MainAgentName, MainProvider, MainModel, MainEndpoint, MainKeyVariable, MainTimeoutSeconds,
            mainMaxAnswer, mainContext, mainPriceIn, mainPriceOut, MainColor, ["*"], [], mainBrief, string.Empty, MainWorkspace)
        {
            Tools = mainTools,
        };

        Unknown(main, hasMain || !hasFormer ? "main" : "ai");

        var quit = Section(sections, "quit");
        QuitUnloadModels = Bool(quit, "quit.unloadModels", "unloadModels", QuitUnloadModels);
        Unknown(quit, "quit");

        var output = Section(sections, "output");
        var format = Text(output, "output.format", "format", OutputFormat, CheckFormat);
        OutputFormat = Array.Find(Formats, f => string.Equals(f, format, StringComparison.OrdinalIgnoreCase)) ?? OutputFormat;
        Unknown(output, "output");

        var cli = Section(sections, "cli");
        CliDryRun = DryRunValue(cli);
        CliYes = Bool(cli, "cli.yes", "yes", CliYes);
        CliInteractive = InteractiveValue(cli);
        var verbosity = Text(cli, "cli.verbosity", "verbosity", CliVerbosity, t => Array.Exists(Verbosities, v => string.Equals(v, t, StringComparison.OrdinalIgnoreCase)) ? null : $"must be one of: {string.Join(", ", Verbosities)}");
        CliVerbosity = Array.Find(Verbosities, v => string.Equals(v, verbosity, StringComparison.OrdinalIgnoreCase)) ?? CliVerbosity;
        var color = Text(cli, "cli.color", "color", CliColor, t => Array.Exists(Colors, c => string.Equals(c, t, StringComparison.OrdinalIgnoreCase)) ? null : $"must be one of: {string.Join(", ", Colors)}");
        CliColor = Array.Find(Colors, c => string.Equals(c, color, StringComparison.OrdinalIgnoreCase)) ?? CliColor;
        CliTimeoutSeconds = Number(cli, "cli.timeoutSeconds", "timeoutSeconds", CliTimeoutSeconds, MinTimeout, MaxTimeout);
        Unknown(cli, "cli");

        // How the window looks (Settings, Appearance). The terminal screen keeps the terminal's own.
        var appearance = Section(sections, "appearance");
        AppearanceTheme = Choice(appearance, "appearance.theme", "theme", AppearanceTheme, Themes);
        AppearanceTextSize = Choice(appearance, "appearance.textSize", "textSize", AppearanceTextSize, TextSizes);
        AppearanceDensity = Choice(appearance, "appearance.density", "density", AppearanceDensity, Densities);
        Unknown(appearance, "appearance");

        ReadChat(sections);
        Unknown(sections, string.Empty);
    }

    private Dictionary<string, JsonElement> TopLevel(JsonElement root)
    {
        var sections = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind == JsonValueKind.Undefined)
        {
            return sections;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            _problems.Add("The file is not a JSON object; every default used.");
            return sections;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (!sections.TryAdd(property.Name, property.Value))
            {
                Duplicate(property.Name);
                if (string.Equals(property.Name, "permissions", StringComparison.OrdinalIgnoreCase))
                {
                    _sectionMissingButBroken = true;
                }
            }
        }

        if (sections.Remove("schema", out var schema))
        {
            if (schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var number) || number < 1)
            {
                _problems.Add("'schema' is not a positive whole number; read as schema 1.");
            }
            else if (number > CurrentSchema)
            {
                _problems.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Schema {number} is newer than this build understands ({CurrentSchema}); known keys read, others reported."));
            }
        }

        return sections;
    }

    private Dictionary<string, JsonElement> Section(Dictionary<string, JsonElement> sections, string name)
    {
        var keys = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (!sections.Remove(name, out var section))
        {
            return keys;
        }

        if (section.ValueKind != JsonValueKind.Object)
        {
            _problems.Add($"'{name}' is not an object; its defaults used.");
            if (name == "permissions")
            {
                _sectionMissingButBroken = true;
            }

            return keys;
        }

        foreach (var property in section.EnumerateObject())
        {
            if (!keys.TryAdd(property.Name, property.Value))
            {
                Duplicate($"{name}.{property.Name}");
            }
        }

        return keys;
    }

    /// <summary>
    /// A key given twice. JSON leaves the meaning open and readers differ, so the file does not
    /// get to mean two things: it is a problem, and in the permissions section it closes them.
    /// </summary>
    private void Duplicate(string name)
    {
        _duplicated++;
        _problems.Add($"'{name}' appears twice; the second is ignored.");
    }

    private void Unknown(Dictionary<string, JsonElement> keys, string section)
    {
        foreach (var key in keys.Keys)
        {
            _problems.Add(section.Length == 0 ? $"Unknown key '{key}' ignored." : $"Unknown key '{section}.{key}' ignored.");
        }
    }

    private string Text(Dictionary<string, JsonElement> keys, string name, string key, string fallback, Func<string, string?>? check)
    {
        if (!keys.Remove(key, out var value))
        {
            return Record(name, fallback, fromDefault: true);
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            _problems.Add($"'{name}' is not text; default used.");
            return Record(name, fallback, fromDefault: true);
        }

        var text = value.GetString()!.Trim();
        if (text.Length > 0 && check?.Invoke(text) is { } why)
        {
            _problems.Add($"'{name}' {why}; default used.");
            return Record(name, fallback, fromDefault: true);
        }

        return Record(name, text, fromDefault: false);
    }

    /// <summary>One of <paramref name="choices"/>, as the list spells it; anything else is reported and the default used.</summary>
    private string Choice(Dictionary<string, JsonElement> keys, string name, string key, string fallback, IReadOnlyList<string> choices)
    {
        var text = Text(keys, name, key, fallback, t => choices.Contains(t, StringComparer.OrdinalIgnoreCase) ? null : $"must be one of: {string.Join(", ", choices)}");
        return choices.FirstOrDefault(c => string.Equals(c, text, StringComparison.OrdinalIgnoreCase)) ?? fallback;
    }

    /// <summary>
    /// A list of strings. One bad entry drops that entry, not the list: dropping the whole of
    /// a deny list over one typo would widen what agents may do.
    /// </summary>
    private List<string> List(Dictionary<string, JsonElement> keys, string name, string key, Func<string, string?> check, IReadOnlyList<string>? fallback = null)
    {
        var list = new List<string>();
        var shown = fallback is null || fallback.Count == 0 ? "(none)" : string.Join(", ", fallback);
        if (!keys.Remove(key, out var value))
        {
            Record(name, shown, fromDefault: true);
            return fallback is null ? list : [.. fallback];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            _problems.Add($"'{name}' is not a list; {(fallback is null ? "none" : "the default")} used.");
            Record(name, shown, fromDefault: true);
            return fallback is null ? list : [.. fallback];
        }

        foreach (var item in value.EnumerateArray())
        {
            var text = item.ValueKind == JsonValueKind.String ? item.GetString()!.Trim() : null;
            var why = text is null ? "is not text" : text.Length == 0 ? "is empty" : check(text);
            if (why is not null)
            {
                _problems.Add($"An entry in '{name}' {why}; that entry ignored.");
                continue;
            }

            list.Add(text!);
        }

        Record(name, list.Count == 0 ? "(none)" : string.Join(", ", list), fromDefault: false);
        return list;
    }

    private bool Bool(Dictionary<string, JsonElement> keys, string name, string key, bool fallback)
    {
        if (keys.Remove(key, out var value))
        {
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                var b = value.ValueKind == JsonValueKind.True;
                Record(name, b ? "true" : "false", fromDefault: false);
                return b;
            }

            _problems.Add($"'{name}' must be true or false; default used.");
        }

        Record(name, fallback ? "true" : "false", fromDefault: true);
        return fallback;
    }

    private bool DryRunValue(Dictionary<string, JsonElement> keys)
    {
        const string name = "cli.dryRun";
        if (!keys.Remove("dryRun", out var value))
        {
            Record(name, "false", fromDefault: true);
            return false;
        }

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            var on = value.ValueKind == JsonValueKind.True;
            Record(name, on ? "true" : "false", fromDefault: false);
            return on;
        }

        _problems.Add($"'{name}' must be true or false; read as true, so nothing changes by mistake.");
        Record(name, "true", fromDefault: false);
        return true;
    }

    private bool? InteractiveValue(Dictionary<string, JsonElement> keys)
    {
        const string name = "cli.interactive";
        if (!keys.Remove("interactive", out var value))
        {
            Record(name, "auto", fromDefault: true);
            return null;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.True:
            case JsonValueKind.False:
                var on = value.ValueKind == JsonValueKind.True;
                Record(name, on ? "true" : "false", fromDefault: false);
                return on;
            case JsonValueKind.String when string.Equals(value.GetString(), "auto", StringComparison.OrdinalIgnoreCase):
                Record(name, "auto", fromDefault: false);
                return null;
            default:
                _problems.Add($"'{name}' must be \"auto\", true or false; default used.");
                Record(name, "auto", fromDefault: true);
                return null;
        }
    }

    private int Number(Dictionary<string, JsonElement> keys, string name, string key, int fallback, int min, int max)
    {
        if (keys.Remove(key, out var value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= min && number <= max)
            {
                Record(name, number.ToString(CultureInfo.InvariantCulture), fromDefault: false);
                return number;
            }

            _problems.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"'{name}' must be a whole number from {min} to {max}; default used."));
        }

        Record(name, fallback.ToString(CultureInfo.InvariantCulture), fromDefault: true);
        return fallback;
    }

    private string Record(string name, string value, bool fromDefault)
    {
        // The hash is not a secret in the way a password is, but it is an offline-guessing
        // target, so nothing that prints settings ever shows it.
        var shown = name == "user.passwordHash" && value.Length > 0 ? "(set)" : value;
        _settings.Add(new Setting(name, shown, fromDefault));
        return value;
    }

    private static string? CheckRule(string text) =>
        Capabilities.IsRule(text) ? null : $"'{text}' is not a capability; known: {string.Join(", ", Capabilities.Names)}, a group like fs.*, *, or one narrowed to a target like mcp.call:clickup/*";

    private static string? CheckRoot(string text) =>
        System.IO.Path.IsPathFullyQualified(text) && !text.StartsWith(@"\\", StringComparison.Ordinal)
            ? null
            : $"'{text}' is not an absolute local folder";

    /// <summary>
    /// <c>example.com</c>, <c>*.example.com</c>, optionally <c>:port</c> (443 when none). ASCII
    /// only: an international name is written as its punycode (xn--…), which is what a URL's host
    /// becomes before it is compared, so an entry can never look like one host and match another.
    /// </summary>
    private static string? CheckHost(string text)
    {
        // Any public site: never this PC or a private network, which Policy refuses by name and by address.
        if (text == "*")
        {
            return null;
        }

        var (name, port) = Harness.Policy.SplitHost(text);
        var host = name.StartsWith("*.", StringComparison.Ordinal) ? name[2..] : name;
        if (!text.All(char.IsAscii))
        {
            return $"'{text}' has non-ASCII letters; write the host in punycode (xn--...)";
        }

        return host.Contains('.', StringComparison.Ordinal) && !host.EndsWith('.') && Uri.CheckHostName(host) == UriHostNameType.Dns && port is > 0 and <= 65535
            ? null
            : $"'{text}' is not a host like example.com, *.example.com or example.com:8443";
    }

    private static string? CheckHash(string text) =>
        PasswordHash.IsWellFormed(text) ? null : "is not a hash this build wrote; run 'diptick config password'";

    private static string? CheckKind(string text) =>
        DatabaseKinds.Parse(text) is null ? $"must be one of: {string.Join(", ", DatabaseKinds.Names)}" : null;

    private static string? CheckFormat(string text) =>
        Array.Exists(Formats, f => string.Equals(f, text, StringComparison.OrdinalIgnoreCase))
            ? null
            : $"must be one of: {string.Join(", ", Formats)}";

    private static string? CheckVariableName(string text) =>
        text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && !char.IsAsciiDigit(text[0])
            ? null
            : "must be an environment variable's name (letters, digits, underscore), not a value";

    /// <summary>
    /// A section's endpoint as it may be used with its provider: a provider signed in to Google takes
    /// only a Google address, so its sign-in cannot be sent anywhere else. Anything else is a problem,
    /// and the endpoint is left out.
    /// </summary>
    private string CheckDestination(string section, string provider, string endpoint)
    {
        if (global::DipTick.Models.Chat.Engines.EndpointProblem(provider, endpoint) is { } why)
        {
            _problems.Add($"'{section}.endpoint' {why}; it is ignored.");
            return string.Empty;
        }

        return endpoint;
    }

    /// <summary>
    /// Whether a provider's calls may name an Anthropic workspace: Anthropic's own API, for a key
    /// that can act in more than one, and Claude Platform on AWS, which needs one on every call.
    /// </summary>
    internal static bool TakesWorkspace(string provider) =>
        Providers.ProviderCatalogue.Find(provider) is { Flavor: Providers.AnthropicFlavor.Api or Providers.AnthropicFlavor.Aws };

    /// <summary>
    /// Claude Platform on AWS: "Workspace IDs use the format wrkspc_ followed by an alphanumeric
    /// identifier" (platform.claude.com, "Claude Platform on AWS"), as Anthropic's own workspaces do.
    /// </summary>
    private static string? CheckWorkspace(string text) =>
        text.StartsWith("wrkspc_", StringComparison.Ordinal) && text.Length > 7 && text.Skip(7).All(char.IsAsciiLetterOrDigit)
            ? null
            : "must be a workspace ID: wrkspc_ and letters or digits";

    private static string? CheckEndpoint(string text) =>
        !Uri.TryCreate(text, UriKind.Absolute, out var uri) || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            ? "must be an https URL, or http to this machine only"
            : uri.UserInfo.Length > 0 || uri.Query.Length > 0
                ? "must not carry credentials or a query; keys go in the variable main.keyVariable names"
                : null;

    private static readonly string[] SecretNames =
    [
        "password", "pwd", "passwd", "pass", "passphrase", "secret", "clientsecret", "token", "accesstoken",
        "apikey", "key", "accesskey", "accountkey", "sharedaccesskey", "sharedaccesssignature", "sig", "credential", "credentials",
    ];

    private static bool LooksSecret(string key)
    {
        var bare = new string(key.Where(char.IsAsciiLetterOrDigit).ToArray());
        return SecretNames.Any(s => string.Equals(s, bare, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// No secret in a connection string, however it is spelled: any key that names one
    /// (Password, PWD, "Pass Word", AccountKey, token, ...), in <c>key=value;</c> pairs or in a
    /// URL's query, and no credentials in a URL at all.
    /// </summary>
    private static string? CheckConnection(string text)
    {
        const string Where = "; put it in the variable 'db.secretVariable' names instead";
        foreach (var part in text.Split(';', '&', '?'))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && LooksSecret(part[..eq]))
            {
                return $"carries a secret ('{part[..eq].Trim()}'){Where}";
            }
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo)
            ? $"carries credentials in its URL{Where}"
            : null;
    }
}

/// <summary>One setting as read: its dotted name, its value as text, and whether it is a default.</summary>
internal sealed record Setting(string Name, string Value, bool FromDefault);
