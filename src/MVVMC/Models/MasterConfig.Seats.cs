using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DipTick.Models.Chat;
using DipTick.Models.Harness;

namespace DipTick.Models;

/// <summary>The <c>chat</c> section, and one section per seat.</summary>
internal sealed partial class MasterConfig
{
    /// <summary>At most four agents at the table: with four, a blind step and a review still fit in one 8-turn round.</summary>
    public const int MaxSeats = 4;

    /// <summary>
    /// Section names and words a seat id may not take: they already mean something in a chat or in
    /// the file. "ai" stays reserved as the main model's former name.
    /// </summary>
    private static readonly string[] ReservedIds =
    [
        "schema", "user", "db", "permissions", "main", "ai", "quit", "output", "cli", "appearance", "chat",
        "audit", "research", "files", "media", "money", "tools", "connectors", "skills", "providers",
        "team", "all", "both", "seam", "work", "person", "you",
    ];

    private static readonly string[] SeatColors = ["cyan", "magenta", "blue", "white"];
    private static readonly string[] Efforts = ["low", "medium", "high"];
    public ChatSettings Chat { get; private set; } = new([], "all", Save: true);

    /// <summary>What answers cost is shown, and capped only where the person set a cap.</summary>
    public MoneySettings Spending { get; private set; } = MoneySettings.Default;

    /// <summary>The days a private chat's day of decisions is kept before its file is deleted whole: <c>audit.privateDays</c>, 15 to 30.</summary>
    public int AuditPrivateDays { get; private set; } = AuditLog.DefaultPrivateDays;

    /// <summary>The clock's other time zones (<c>tools.zones</c>), as ids this PC knows.</summary>
    public IReadOnlyList<string> ToolZones { get; private set; } = [];

    /// <summary>What agents search the web through (<c>research.search</c>): none, brave or searxng.</summary>
    public string ResearchSearch { get; private set; } = "none";

    /// <summary>The SearXNG the person runs (<c>research.searxngUrl</c>), or empty.</summary>
    public string ResearchSearxngUrl { get; private set; } = string.Empty;

    /// <summary>Where Brave is reached (<c>research.braveUrl</c>); empty for Brave's own address. A key saved for Brave's host never goes anywhere else.</summary>
    public string ResearchBraveUrl { get; private set; } = string.Empty;

    /// <summary>An environment variable holding Brave's key (<c>research.keyVariable</c>); empty for the key saved for Brave's host.</summary>
    public string ResearchKeyVariable { get; private set; } = string.Empty;

    /// <summary>Where the files agents make go (<c>files.folder</c>); empty for Documents\DipTick.</summary>
    public string FilesFolder { get; private set; } = string.Empty;

    /// <summary>Who makes images (<c>media.images</c>): auto, none, or one of <see cref="Media.ImageMaker.Providers"/>.</summary>
    public string MediaImages { get; private set; } = "auto";

    /// <summary>Where the images provider is reached when not at its own address (<c>media.imageAddress</c>): a server of yours; empty for its own.</summary>
    public string MediaImageAddress { get; private set; } = string.Empty;

    /// <summary>The image model's own fields, as a JSON object (<c>media.imageInput</c>), for Replicate, fal and WaveSpeedAI; empty for none.</summary>
    public string MediaImageInput { get; private set; } = string.Empty;

    /// <summary>The ComfyUI workflow that makes images (<c>media.comfyImageWorkflow</c>), exported in API format; empty for none.</summary>
    public string MediaComfyImageWorkflow { get; private set; } = string.Empty;

    /// <summary>Where the video provider is reached when not at its own address (<c>media.videoAddress</c>).</summary>
    public string MediaVideoAddress { get; private set; } = string.Empty;

    /// <summary>The video model's own fields, as a JSON object (<c>media.videoInput</c>).</summary>
    public string MediaVideoInput { get; private set; } = string.Empty;

    /// <summary>The ComfyUI workflow that makes video (<c>media.comfyVideoWorkflow</c>).</summary>
    public string MediaComfyVideoWorkflow { get; private set; } = string.Empty;

    /// <summary>What turns speech into text (<c>media.speech</c>): none or whispercpp.</summary>
    public string MediaSpeech { get; private set; } = "none";

    /// <summary>Where the speech server is when not at its own address (<c>media.speechAddress</c>).</summary>
    public string MediaSpeechAddress { get; private set; } = string.Empty;

    /// <summary>The image model (<c>media.imageModel</c>); empty for the provider's default.</summary>
    public string MediaImageModel { get; private set; } = string.Empty;

    /// <summary>Who makes video (<c>media.video</c>): auto, google or none.</summary>
    public string MediaVideo { get; private set; } = "auto";

    /// <summary>The video model (<c>media.videoModel</c>); empty for Veo 3.1.</summary>
    public string MediaVideoModel { get; private set; } = string.Empty;

    /// <summary>
    /// Whether a Google Compute Engine machine's own account may sign in to Google Cloud where no sign-in
    /// file is (<c>providers.googleMachineAccount</c>): off by default.
    /// </summary>
    public bool GoogleMachineAccount { get; private set; }

    /// <summary>
    /// Why a folder cannot hold agents' files, or null: a full path, and not a drive's root or the
    /// profile's own folder, since it becomes one of the folders agents may touch.
    /// </summary>
    private static string? CheckFilesFolder(string text)
    {
        if (text.Length == 0)
        {
            return null;
        }

        if (!System.IO.Path.IsPathFullyQualified(text))
        {
            return "must be a full path, or empty for Documents\\DipTick";
        }

        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(text));
        var profile = System.IO.Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return string.Equals(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetPathRoot(full) ?? string.Empty), full, StringComparison.OrdinalIgnoreCase)
            || string.Equals(full, profile, StringComparison.OrdinalIgnoreCase)
            ? "must be a folder of its own, not a drive's root or your profile's folder: agents may touch what is in it"
            : null;
    }

    /// <summary>Why a model's own fields will not do, or null: a JSON object, at most 4000 characters, holding no secret by its name.</summary>
    private static string? CheckFields(string text)
    {
        if (text.Length > 4000)
        {
            return "must be at most 4000 characters";
        }

        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(text) is not System.Text.Json.Nodes.JsonObject fields ? "must be a JSON object, like {\"aspect_ratio\": \"16:9\"}"
                : fields.Any(f => LooksSecret(f.Key)) ? "must hold no secret: a key goes in Credential Manager with 'diptick key set'"
                : null;
        }
        catch (JsonException)
        {
            return "must be a JSON object, like {\"aspect_ratio\": \"16:9\"}";
        }
    }

    /// <summary>Why a ComfyUI workflow's path will not do, or null: a full path to a .json file.</summary>
    private static string? CheckWorkflow(string text) =>
        System.IO.Path.IsPathFullyQualified(text) && text.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? null : "must be the full path of a .json workflow, exported with File > Export (API)";

    /// <summary>The folder the files agents make go to: <see cref="FilesFolder"/>, or Documents\DipTick.</summary>
    public string AgentFilesFolder => FilesFolder.Length > 0
        ? FilesFolder
        : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DipTick");

    /// <summary>The seats in chat.seats order, only those with a section; empty seats are reported.</summary>
    public IReadOnlyList<SeatConfig> Seats { get; private set; } = [];

    public SeatConfig? Seat(string id) => Seats.FirstOrDefault(s => s.Id == id || s.Name == id);

    /// <summary>Whether any model is set up to answer: the model that answers you, or an agent in a seat section.</summary>
    public bool AnyAgent => MainProvider.Length > 0 || Seats.Any(s => s.Provider.Length > 0);

    private void ReadChat(Dictionary<string, JsonElement> sections)
    {
        var chat = Section(sections, "chat");
        var ids = List(chat, "chat.seats", "seats", CheckSeatId);
        if (ids.Count != ids.Distinct(StringComparer.Ordinal).Count())
        {
            _problems.Add("'chat.seats' names a seat twice; the second is ignored.");
            ids = ids.Distinct(StringComparer.Ordinal).ToList();
        }

        if (ids.Count > MaxSeats)
        {
            _problems.Add(string.Create(CultureInfo.InvariantCulture, $"'chat.seats' names {ids.Count} seats; at most {MaxSeats} sit, so {string.Join(", ", ids.Skip(MaxSeats))} are left out."));
            ids = ids.Take(MaxSeats).ToList();
        }

        var mode = Text(chat, "chat.mode", "mode", "all", t => Models.Chat.Seam.ChatMode.Problem(t));
        if (chat.Remove("confirmOver"))
        {
            // Spending never blocks by default: the caps under money.* took its place.
            _notices.Add("'chat.confirmOver' is no longer read: nothing asks before spending unless a cap under money.* is switched on. Remove it.");
        }

        var save = Bool(chat, "chat.save", "save", true);
        var pasteLines = Number(chat, "chat.pasteLines", "pasteLines", Models.Chat.Pastes.DefaultLines, 0, 10000);
        var defaults = RoundLimits.Default;
        var limits = new RoundLimits(
            Number(chat, "chat.maxAiTurns", "maxAiTurns", defaults.MaxAiTurns, 1, RoundLimits.TurnCeiling),
            Number(chat, "chat.maxRoundSeconds", "maxRoundSeconds", defaults.MaxRoundSeconds, 30, 1800),
            Number(chat, "chat.maxRoundTokens", "maxRoundTokens", defaults.MaxRoundTokens, 256, MaxAnswerCeiling * 4),
            Number(chat, "chat.maxQuotedChars", "maxQuotedChars", defaults.MaxQuotedChars, 1000, 65536));
        Unknown(chat, "chat");
        Chat = new ChatSettings(ids, mode, save, pasteLines, limits);

        var money = Section(sections, "money");
        Spending = new MoneySettings(
            Bool(money, "money.showCost", "showCost", MoneySettings.Default.ShowCost),
            Text(money, "money.currency", "currency", MoneySettings.Default.Currency, t => t.Length == 3 && t.All(char.IsAsciiLetterUpper) ? null : "must be three capital letters, like USD"),
            Money(money, "money.capChat", "capChat", null, allowNull: true, nullWord: "off"),
            Money(money, "money.capDay", "capDay", null, allowNull: true, nullWord: "off"),
            Money(money, "money.capMonth", "capMonth", null, allowNull: true, nullWord: "off"));
        Unknown(money, "money");

        // How long a private chat's decisions are kept: from 15 to 30 days.
        var audit = Section(sections, "audit");
        AuditPrivateDays = Number(audit, "audit.privateDays", "privateDays", AuditLog.DefaultPrivateDays, AuditLog.FewestPrivateDays, AuditLog.MostPrivateDays);
        Unknown(audit, "audit");

        // DipTick's own tools: the clock's other zones, the search service agents use, and where the
        // files agents make go.
        var tools = Section(sections, "tools");
        ToolZones = List(tools, "tools.zones", "zones", t => TimeZoneInfo.TryFindSystemTimeZoneById(t, out _) ? null : "is not a time zone on this PC");
        Unknown(tools, "tools");

        var research = Section(sections, "research");
        ResearchSearch = Text(research, "research.search", "search", "none", t => t is "none" or "brave" or "searxng" ? null : "must be none, brave or searxng");
        ResearchSearxngUrl = Text(research, "research.searxngUrl", "searxngUrl", string.Empty, CheckEndpoint);
        ResearchBraveUrl = Text(research, "research.braveUrl", "braveUrl", string.Empty, CheckEndpoint);
        ResearchKeyVariable = Text(research, "research.keyVariable", "keyVariable", string.Empty, CheckVariableName);
        Unknown(research, "research");

        var files = Section(sections, "files");
        FilesFolder = Text(files, "files.folder", "folder", string.Empty, CheckFilesFolder);
        Unknown(files, "files");

        // The chats' own folders are always among the folders the file capabilities may touch: what
        // agents make goes there, and writing still asks as fs.write does.
        if (_permissionsTrusted && !Permissions.Roots.Any(r => string.Equals(System.IO.Path.GetFullPath(r).TrimEnd('\\'), AgentFilesFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
        {
            Permissions = Permissions with { Roots = [.. Permissions.Roots, AgentFilesFolder] };
        }

        // Images and video from models: who makes them, and with which model.
        var media = Section(sections, "media");
        MediaImages = Text(media, "media.images", "images", "auto", t => t is "auto" or "none" || Media.ImageMaker.Providers.Contains(t, StringComparer.Ordinal) ? null : $"must be auto, none, or one of {string.Join(", ", Media.ImageMaker.Providers)}");
        MediaImageModel = Text(media, "media.imageModel", "imageModel", string.Empty, t => t.Length <= 100 && t.All(c => c is > ' ' and <= '~') ? null : "must be a model's name");
        MediaVideo = Text(media, "media.video", "video", "auto", t => t is "auto" or "none" || Media.VideoMaker.Providers.Contains(t, StringComparer.Ordinal) ? null : $"must be auto, none, or one of {string.Join(", ", Media.VideoMaker.Providers)}");
        MediaVideoModel = Text(media, "media.videoModel", "videoModel", string.Empty, t => t.Length <= 100 && t.All(c => c is > ' ' and <= '~') ? null : "must be a model's name");

        // The providers' own APIs: where a server of yours is, the fields a model takes, a ComfyUI
        // workflow, and what turns speech into text.
        MediaImageAddress = Text(media, "media.imageAddress", "imageAddress", string.Empty, CheckEndpoint);
        MediaImageInput = Text(media, "media.imageInput", "imageInput", string.Empty, CheckFields);
        MediaComfyImageWorkflow = Text(media, "media.comfyImageWorkflow", "comfyImageWorkflow", string.Empty, CheckWorkflow);
        MediaVideoAddress = Text(media, "media.videoAddress", "videoAddress", string.Empty, CheckEndpoint);
        MediaVideoInput = Text(media, "media.videoInput", "videoInput", string.Empty, CheckFields);
        MediaComfyVideoWorkflow = Text(media, "media.comfyVideoWorkflow", "comfyVideoWorkflow", string.Empty, CheckWorkflow);
        MediaSpeech = Text(media, "media.speech", "speech", "none", t => t is "none" || Media.SpeechMaker.Providers.Contains(t, StringComparer.Ordinal) ? null : $"must be none, or one of {string.Join(", ", Media.SpeechMaker.Providers)}");
        MediaSpeechAddress = Text(media, "media.speechAddress", "speechAddress", string.Empty, CheckEndpoint);
        Unknown(media, "media");

        // A Google Compute Engine machine's own account: off unless turned on.
        var providers = Section(sections, "providers");
        GoogleMachineAccount = Bool(providers, "providers.googleMachineAccount", "googleMachineAccount", false);
        Unknown(providers, "providers");

        var seats = new List<SeatConfig>();
        foreach (var id in ids)
        {
            var present = sections.ContainsKey(id);
            var seat = ReadSeat(id, Section(sections, id));
            if (!present)
            {
                _problems.Add($"Seat '{id}' is listed in chat.seats but has no section; it is empty until 'diptick seats add {id}' or 'config set {id}.provider ...'.");
                continue;
            }

            seats.Add(seat);
        }

        // One key, one host: a key sent to two hosts is a key one of them was never meant to see.
        var hostsByKey = seats.Where(s => s.KeyVariable.Length > 0)
            .Select(s => (s.KeyVariable, Host: HostOf(s.Provider, s.Endpoint), Who: s.Id))
            .Append((KeyVariable: MainKeyVariable, Host: HostOf(MainProvider, MainEndpoint), Who: "main"))
            .Where(x => x.KeyVariable.Length > 0 && x.Host.Length > 0)
            .GroupBy(x => x.KeyVariable, StringComparer.Ordinal);
        foreach (var group in hostsByKey)
        {
            var hosts = group.Select(g => g.Host).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (hosts.Count > 1)
            {
                _problems.Add($"The key in {group.Key} would go to {string.Join(" and ", hosts)} ({string.Join(", ", group.Select(g => g.Who))}); one key, one host. Those seats are left empty.");
                seats.RemoveAll(s => s.KeyVariable == group.Key);
            }
        }

        Seats = seats;
    }

    /// <summary>
    /// One seat's section, with a recorded default for every key, so 'config set &lt;id&gt;.model ...'
    /// works on a seat whose section does not exist yet.
    /// </summary>
    private SeatConfig ReadSeat(string id, Dictionary<string, JsonElement> keys)
    {
        var name = Text(keys, $"{id}.name", "name", id, CheckSeatId);
        var provider = Text(keys, $"{id}.provider", "provider", string.Empty, Engines.ProviderProblem);
        var model = Text(keys, $"{id}.model", "model", string.Empty, check: null);
        var endpoint = CheckDestination(id, provider, Text(keys, $"{id}.endpoint", "endpoint", string.Empty, CheckEndpoint));
        var keyVariable = Text(keys, $"{id}.keyVariable", "keyVariable", string.Empty, CheckVariableName);
        var timeout = Number(keys, $"{id}.timeoutSeconds", "timeoutSeconds", 120, MinTimeout, MaxTimeout);
        var maxAnswer = Number(keys, $"{id}.maxAnswerTokens", "maxAnswerTokens", 1500, 1, MaxAnswerCeiling);
        var context = Number(keys, $"{id}.contextTokens", "contextTokens", 16000, 1000, 1_000_000);
        var priceIn = Money(keys, $"{id}.priceIn", "priceIn", null, allowNull: true);
        var priceOut = Money(keys, $"{id}.priceOut", "priceOut", null, allowNull: true);
        var color = Text(keys, $"{id}.color", "color", "white", t => SeatColors.Contains(t, StringComparer.OrdinalIgnoreCase) ? null : $"must be one of: {string.Join(", ", SeatColors)}");
        // The lane narrows what the permissions allow; unset, it is everything they allow.
        var lane = List(keys, $"{id}.lane", "lane", CheckRule, fallback: ["*"]);
        var ask = List(keys, $"{id}.ask", "ask", CheckRule);
        var brief = Text(keys, $"{id}.brief", "brief", string.Empty, t => t.Length <= 2000 ? null : "is longer than 2000 characters");
        var effort = Text(keys, $"{id}.effort", "effort", string.Empty, t => Efforts.Contains(t, StringComparer.OrdinalIgnoreCase) ? null : $"must be one of: {string.Join(", ", Efforts)}, or empty");
        var workspace = Text(keys, $"{id}.workspace", "workspace", string.Empty, CheckWorkspace);
        var tools = Text(keys, $"{id}.tools", "tools", "auto", CheckTools);
        Unknown(keys, id);

        if (effort.Length > 0 && Providers.ProviderCatalogue.Find(provider) is not { Shape: Providers.ProviderShape.Anthropic })
        {
            _problems.Add($"'{id}.effort' applies to Claude seats only; ignored for {provider}.");
            effort = string.Empty;
        }

        if (workspace.Length > 0 && !TakesWorkspace(provider))
        {
            _problems.Add($"'{id}.workspace' applies to Claude through Anthropic's API or Claude Platform on AWS only; ignored for {provider}.");
            workspace = string.Empty;
        }

        return new SeatConfig(id, name, provider, model, endpoint, keyVariable, timeout, maxAnswer, context, priceIn, priceOut, color, lane, ask, brief, effort, workspace) { Tools = tools };
    }

    /// <summary>Whether an agent is offered tools: auto (as its provider and model say), on, or off.</summary>
    private static string? CheckTools(string text) => text is "auto" or "on" or "off" ? null : "must be auto, on or off";

    /// <summary>A price or a cost: a number of at least 0, or null where that is allowed, shown as <paramref name="nullWord"/> (a price unknown, a cap off).</summary>
    private decimal? Money(Dictionary<string, JsonElement> keys, string name, string key, decimal? fallback, bool allowNull, string nullWord = "unknown")
    {
        if (keys.Remove(key, out var value))
        {
            if (allowNull && value.ValueKind == JsonValueKind.Null)
            {
                Record(name, nullWord, fromDefault: false);
                return null;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var amount) && amount >= 0 && amount <= 1_000_000)
            {
                Record(name, amount.ToString(CultureInfo.InvariantCulture), fromDefault: false);
                return amount;
            }

            _problems.Add($"'{name}' must be a number of at least 0{(allowNull ? $", or null for {nullWord}" : string.Empty)}; default used.");
        }

        Record(name, fallback?.ToString(CultureInfo.InvariantCulture) ?? nullWord, fromDefault: true);
        return fallback;
    }

    private static string? CheckSeatId(string text) =>
        !SeatId().IsMatch(text) ? "must be 1-12 of a-z 0-9 -, starting with a letter"
        : ReservedIds.Contains(text, StringComparer.Ordinal) ? $"'{text}' is reserved"
        : null;

    /// <summary>Where a seat's words and key go: its endpoint's host, or its provider's own.</summary>
    private static string HostOf(string provider, string endpoint)
    {
        if (endpoint.Length > 0 && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        return Engines.Known.TryGetValue(provider, out var known) && known is not null ? known.Host : string.Empty;
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{0,11}$", RegexOptions.CultureInvariant)]
    private static partial Regex SeatId();
}
