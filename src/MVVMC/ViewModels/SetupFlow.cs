using System.Text;
using System.Text.Json;
using DipTick.Models;
using DipTick.Models.Chat;
using DipTick.Models.Harness;
using DipTick.Models.Keys;
using DipTick.Models.Providers;

namespace DipTick.ViewModels;

/// <summary>How setup ended.</summary>
internal enum SetupOutcome
{
    Saved,
    DryRun,
    Stopped,
    NoYes,
    Failed,
}

/// <summary>How setup ended, and one line saying so.</summary>
internal sealed record SetupResult(SetupOutcome Outcome, string Message);

/// <summary>
/// What setup works with: the settings as they are, where they will be saved, this run's switches,
/// and the outside world behind interfaces, so each can be replaced.
/// </summary>
/// <param name="GateFor">The gate the key check goes through, for the permissions in force.</param>
/// <param name="Google">The Google Cloud sign-in a Google provider's check proves; Google's own when null.</param>
internal sealed record SetupContext(
    MasterConfig Config,
    string TargetPath,
    RunFlags Flags,
    IKeyStore Keys,
    Func<string, string?> Environment,
    IModelSource Models,
    Func<Permissions, Gate> GateFor,
    IGoogleSignIn? Google = null);

/// <summary>
/// Setup, one question at a time, whatever the screen: your name, your provider (the whole
/// catalogue), what that provider needs (its address or the parts of one, a workspace, a key or a
/// Google sign-in), a check that lists its models, the model; for Full, a password and whether each
/// answer waits for a yes; then a summary and Save.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is written until Save. Every step can go back, and Stop leaves everything as it was.
/// A key typed here is held in memory, checked with the platform, and saved in Windows Credential
/// Manager only at Save. Changing where the conversation goes needs a yes from a person: at a
/// keyboard, the Save question; from a pipe, --yes.
/// </para>
/// <para>
/// The model part (provider, address, key, check, model) also runs on its own, for one section: the
/// <c>main</c> section or a seat, from the settings screen, with the provider already picked when
/// Settings, Models, Add a model picked it.
/// </para>
/// </remarks>
internal sealed class SetupFlow
{
    private readonly SetupContext _context;
    private readonly IAsker _asker;
    private readonly string _section;
    private readonly bool _newSeat;

    // The draft. Nothing in it is saved until Save.
    private bool _full;
    private bool _modelOnly;
    private bool _providerGiven;
    private string _name;
    private string _provider;
    private string _endpoint;
    private string _model;
    private string _workspace;
    private KeyPlan _key;
    private string _variable = string.Empty;
    private string? _newKey;
    private IReadOnlyList<ModelInfo>? _models;
    private string _checkNote = string.Empty;
    private string? _passwordHash;
    private bool? _askEachAnswer;
    private SetupResult? _result;

    /// <param name="section">"main", or a seat's id for that seat's model.</param>
    /// <param name="newSeat">True when <paramref name="section"/> is a seat that does not sit yet: Save seats it.</param>
    public SetupFlow(SetupContext context, IAsker asker, string section = "main", bool newSeat = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _asker = asker;
        _section = section;
        _newSeat = newSeat;
        var config = context.Config;
        _name = config.UserName.Length > 0 ? config.UserName : Environment.UserName;
        if (section == "main")
        {
            (_provider, _endpoint, _model, _workspace) = (config.MainProvider, config.MainEndpoint, config.MainModel, config.MainWorkspace);
        }
        else if (config.Seat(section) is { } seat)
        {
            (_provider, _endpoint, _model, _workspace) = (seat.Provider, seat.Endpoint, seat.Model, seat.Workspace);
        }
        else
        {
            (_provider, _endpoint, _model, _workspace) = (string.Empty, string.Empty, string.Empty, string.Empty);
        }
    }

    private enum KeyPlan
    {
        /// <summary>No key is asked for: the model runs on this PC, or its server takes none, or it signs in to Google.</summary>
        None,

        /// <summary>The environment variable in <see cref="_variable"/>.</summary>
        Variable,

        /// <summary>The key already saved for the host.</summary>
        Stored,

        /// <summary>A key typed now, saved at Save.</summary>
        New,
    }

    /// <summary>The catalogue's entry for the provider picked, or null for none yet.</summary>
    private Provider? Entry => _provider.Length == 0 ? null : ProviderCatalogue.Find(_provider);

    private Uri? EndpointUri => Engines.Endpoint(_provider, _endpoint);

    /// <summary>
    /// Whether a key is asked for: the provider takes one (a Google sign-in is not a key), and its
    /// server is not on this PC, unless it is a runtime here that locks itself (Xinference, TabbyAPI).
    /// </summary>
    private bool NeedsKey => _provider.Length > 0 && EndpointUri is { } uri && Entry is { } entry
        && entry.Key is KeyScheme.Bearer or KeyScheme.KeyHeader or KeyScheme.ApiKeyHeader or KeyScheme.XApiKey
        && (!uri.IsLoopback || entry.Shape == ProviderShape.Anthropic || entry.Kind == ProviderKind.Local);

    /// <summary>Whether the address, or its parts, is asked for: the provider has none of its own.</summary>
    private bool NeedsAddress => Entry is { Given: true };

    /// <summary>Whether a workspace is asked for: Claude Platform on AWS names one on every call.</summary>
    private bool NeedsWorkspace => Entry is { Flavor: AnthropicFlavor.Aws };

    private string PlatformLabel => Engines.DisplayName(_provider, _endpoint);

    /// <summary>The settings as they stand for this section, to tell a changed choice from a kept one.</summary>
    private (string Provider, string Endpoint, string KeyVariable) Saved => _section == "main"
        ? (_context.Config.MainProvider, _context.Config.MainEndpoint, _context.Config.MainKeyVariable)
        : _context.Config.Seat(_section) is { } seat ? (seat.Provider, seat.Endpoint, seat.KeyVariable) : (string.Empty, string.Empty, string.Empty);

    /// <summary>The permissions a check runs under: the file's, or the starter's when the file is not there yet.</summary>
    private Permissions PermissionsNow => File.Exists(_context.TargetPath)
        ? _context.Config.Permissions
        : MasterConfig.FromText(StarterConfig.Text(), _context.TargetPath).Permissions;

    /// <summary>Whether setup may move model.call between allow and ask: it is named only on its own, and never denied.</summary>
    private bool PlainPermissions
    {
        get
        {
            var p = PermissionsNow;
            return !_context.Config.Problems.Any(x => x.Contains("permissions section cannot be trusted", StringComparison.Ordinal))
                && SettingsCatalogue.NamesOnlyExactly(p.Allow, Capability.ModelCall)
                && SettingsCatalogue.NamesOnlyExactly(p.Ask, Capability.ModelCall)
                && !p.Deny.Any(r => Capabilities.Matches(r, Capability.ModelCall));
        }
    }

    /// <summary>The whole catalogue as a list to pick from, in the order it is shown: grouped by kind, those that work now first.</summary>
    internal static IReadOnlyList<PickItem> ProviderItems() =>
    [
        .. ProviderCatalogue.Ordered.Select(p => new PickItem(
            p.Id,
            p.Name,
            ProviderCatalogue.KindWords(p.Kind),
            $"{p.About} · needs {ProviderCatalogue.NeedsWords(p)}",
            p.WorksNow ? string.Empty : p.Needs)),
    ];

    /// <summary>The whole setup: Quick, or Full.</summary>
    public SetupResult Run(bool full)
    {
        _full = full;
        _modelOnly = false;
        return Walk(
        [
            new("How much", () => true, StepMode),
            new("Your name", () => true, StepName),
            new("Your provider", () => true, StepProvider),
            new("Its address", () => NeedsAddress, StepAddress),
            new("Its workspace", () => NeedsWorkspace, StepWorkspace),
            new("Its key", () => NeedsKey, StepKey),
            new("The check", () => true, StepCheck, Automatic: true),
            new("The model", () => true, StepModel),
            new("Password", () => _full, StepPassword),
            new("Approvals", () => _full && PlainPermissions, StepApprovals),
            new("Save", () => true, StepSave),
        ]);
    }

    /// <summary>
    /// The whole setup, then "Add another agent?" until the person says no or a table would be full.
    /// Each yes is a new seat's id, then the model part for it, saved as its own step, so a first chat
    /// starts with every agent set up here.
    /// </summary>
    public SetupResult RunWithAgents(bool full)
    {
        var first = Run(full);
        if (first.Outcome != SetupOutcome.Saved)
        {
            return first;
        }

        var said = new List<string> { first.Message };
        var context = _context;
        while (true)
        {
            // The file as it now is: the agents just saved sit in it.
            context = context with { Config = MasterConfig.Load(context.TargetPath) };
            var config = context.Config;
            var seated = config.Seats.Count + (config.MainProvider.Length > 0 ? 1 : 0);
            if (seated >= MasterConfig.MaxSeats)
            {
                break;
            }

            var more = _asker.YesNo(
                new Question("Setup · Another agent", "Add another agent?", $"Every agent at a chat's table answers you together, and @name asks one. {MasterConfig.MaxSeats} sit at a table at most; Settings, Models, Add a model adds one later."),
                false);
            if (more.Reply != Reply.Answered || !more.Value || AskSeatId(_asker, config, "Setup · Another agent") is not { } id)
            {
                break;
            }

            var added = new SetupFlow(context, _asker, id, newSeat: true).RunModel();
            said.Add(added.Message);
            if (added.Outcome != SetupOutcome.Saved)
            {
                break;
            }
        }

        return new SetupResult(SetupOutcome.Saved, string.Join('\n', said));
    }

    /// <summary>
    /// A new agent's id, asked: what it is called at a table and in @names. Null when the person left.
    /// The one question for setup's "Add another agent?" and Settings' Add a seat and Add a model.
    /// </summary>
    internal static string? AskSeatId(IAsker asker, MasterConfig config, string title)
    {
        ArgumentNullException.ThrowIfNull(asker);
        ArgumentNullException.ThrowIfNull(config);
        var id = asker.Text(
            new Question(title, "Its name: 1 to 12 of a-z, 0-9 and -, starting with a letter.", "You ask it with @name, and the audit log names it so. Two agents cannot share a name."),
            string.Empty,
            text => MasterConfig.SeatIdProblem(text) is { } why ? why : config.Chat.Seats.Contains(text) || config.Seat(text) is not null ? "an agent already has that name" : null);
        return id.Reply == Reply.Answered ? id.Value : null;
    }

    /// <summary>
    /// Only the model part, for this flow's section: provider, what it needs, check, model, save.
    /// With <paramref name="provider"/>, that provider is taken as picked and its question is not asked.
    /// </summary>
    public SetupResult RunModel(string? provider = null)
    {
        _modelOnly = true;
        if (provider is not null)
        {
            TakeProvider(provider);
            _providerGiven = true;
        }

        return Walk(
        [
            new("Your provider", () => !_providerGiven, StepProvider),
            new("Its address", () => NeedsAddress, StepAddress),
            new("Its workspace", () => NeedsWorkspace, StepWorkspace),
            new("Its key", () => NeedsKey, StepKey),
            new("The check", () => true, StepCheck, Automatic: true),
            new("The model", () => true, StepModel),
            new("Save", () => true, StepSave),
        ]);
    }

    private sealed record Step(string Name, Func<bool> Applies, Func<string, Reply> Ask, bool Automatic = false);

    private SetupResult Walk(List<Step> steps)
    {
        var trail = new Stack<int>();
        var i = 0;
        while (i < steps.Count)
        {
            var step = steps[i];
            if (!step.Applies())
            {
                i++;
                continue;
            }

            var reply = step.Ask(Title(steps, step));
            if (_result is not null)
            {
                return _result;
            }

            switch (reply)
            {
                case Reply.Answered:
                    if (!step.Automatic)
                    {
                        trail.Push(i);
                    }

                    i++;
                    break;
                case Reply.Back:
                    // Back from the first question has nowhere to go: it is asked again.
                    i = trail.Count > 0 ? trail.Pop() : i;
                    break;
                default:
                    return new SetupResult(SetupOutcome.Stopped, "Setup stopped; nothing was saved.");
            }
        }

        return _result ?? new SetupResult(SetupOutcome.Stopped, "Setup stopped; nothing was saved.");
    }

    /// <summary>"Setup · 3 of 7 · Its key": where the person is, counted over the steps that apply now.</summary>
    private string Title(List<Step> steps, Step step)
    {
        var asked = steps.Where(s => !s.Automatic && s.Applies()).ToList();
        var at = asked.IndexOf(step);
        var what = _modelOnly ? (_section == "main" ? "Your model" : $"Seat {_section}") : "Setup";
        return at < 0 ? $"{what} · {step.Name}" : $"{what} · {at + 1} of {asked.Count} · {step.Name}";
    }

    private Reply StepMode(string title)
    {
        var answer = _asker.Choose(
            new Question(title, "How much do you want to set up?", "Quick gets you talking to one model. Full also sets a password, and whether each answer waits for your yes."),
            [new Choice("Quick", "one model, about a minute"), new Choice("Full", "Quick, then a password and approvals")],
            _full ? 1 : 0);
        if (answer.Reply == Reply.Answered)
        {
            _full = answer.Value == 1;
        }

        return answer.Reply;
    }

    private Reply StepName(string title)
    {
        var answer = _asker.Text(
            new Question(title, "What should DipTick call you?", "It is shown on your lines in the chat, the agents in each chat are told it, and the audit log records it as who said yes to each decision."),
            _name,
            NameProblem);
        if (answer.Reply == Reply.Answered)
        {
            _name = answer.Value;
        }

        return answer.Reply;
    }

    private Reply StepProvider(string title)
    {
        var answer = _asker.Pick(
            new Question(
                title,
                _section == "main" ? "Which provider should answer you?" : $"Which provider serves seat {_section}?",
                $"Every provider DipTick knows, grouped by kind, those that work now first; type to narrow the list. Each says what it needs; the key, if any, comes on a later step. The list was checked {ProviderCatalogue.Checked:yyyy-MM-dd}."),
            ProviderItems(),
            _provider);
        if (answer.Reply != Reply.Answered)
        {
            return answer.Reply;
        }

        TakeProvider(answer.Value);
        return Reply.Answered;
    }

    /// <summary>A provider picked: a new one starts without the old one's address, model and workspace.</summary>
    private void TakeProvider(string chosen)
    {
        if (!string.Equals(chosen, _provider, StringComparison.OrdinalIgnoreCase))
        {
            _provider = chosen;
            _endpoint = string.Empty;
            _model = string.Empty;
            _workspace = string.Empty;
            _models = null;
        }
    }

    /// <summary>
    /// The address, for a provider that has none of its own: the parts of it one by one (a Google
    /// Cloud project and location, an AWS region, an Azure resource's name), each checked so it can
    /// only fill its own place; or the whole address, for another server.
    /// </summary>
    private Reply StepAddress(string title)
    {
        var entry = Entry!;
        if (entry.Parts.Count == 0)
        {
            var answer = _asker.Text(
                new Question(title, "Where is the server?", entry.Id == ProviderCatalogue.Another
                    ? "Its base address: the part before /chat/completions, like https://llm.example.com/v1. https, or http on this PC only."
                    : $"{entry.Name}'s address: {entry.Give}. https, or http on this PC only."),
                _endpoint,
                t => t.Length == 0 ? "an address is needed" : MasterConfig.EndpointProblem(t));
            if (answer.Reply == Reply.Answered && answer.Value != _endpoint)
            {
                _endpoint = answer.Value;
                _models = null;
            }

            return answer.Reply;
        }

        var values = PartsOf(entry);
        var index = 0;
        while (index < entry.Parts.Count)
        {
            var part = ProviderCatalogue.Part(entry.Parts[index])!;
            var answer = _asker.Text(
                new Question(title, $"{part.Question}?", $"{entry.Name}'s address is built from your answers: {ProviderCatalogue.Sketch(entry, values)}. This one is like {part.Example}."),
                values.GetValueOrDefault(part.Name, string.Empty),
                t => t.Length == 0 ? "it is needed" : part.Problem(t));
            switch (answer.Reply)
            {
                case Reply.Answered:
                    values[part.Name] = answer.Value;
                    index++;
                    break;
                case Reply.Back when index > 0:
                    index--;
                    break;
                default:
                    return answer.Reply;
            }
        }

        if (ProviderCatalogue.Compose(entry, values, out var why) is not { } uri)
        {
            _asker.Tell(title, $"That address will not do: {why}.");
            return Reply.Back;
        }

        if (uri.AbsoluteUri != _endpoint)
        {
            _endpoint = uri.AbsoluteUri;
            _models = null;
        }

        return Reply.Answered;
    }

    /// <summary>The parts of the address set now, as presets: a Google Cloud project and location read back from it.</summary>
    private Dictionary<string, string> PartsOf(Provider entry)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (entry.GoogleSignIn && _endpoint.Length > 0 && Uri.TryCreate(_endpoint, UriKind.Absolute, out var uri) && GoogleCloud.Parse(uri) is { } where)
        {
            values["project"] = where.Project;
            values["location"] = where.Location;
        }
        else if (entry.GoogleSignIn)
        {
            values["location"] = "global";
        }

        return values;
    }

    private Reply StepWorkspace(string title)
    {
        var answer = _asker.Text(
            new Question(title, "Your workspace ID (wrkspc_...)?", "Claude Platform on AWS names a workspace on every call. It is under Workspaces on the AWS Console's Claude Platform on AWS page, or in the Claude Console."),
            _workspace,
            t => t.StartsWith("wrkspc_", StringComparison.Ordinal) && t.Length > 7 && t.Skip(7).All(char.IsAsciiLetterOrDigit) ? null : "wrkspc_ and letters or digits");
        if (answer.Reply == Reply.Answered)
        {
            _workspace = answer.Value;
        }

        return answer.Reply;
    }

    private Reply StepKey(string title)
    {
        var host = ApiKeys.HostOf(EndpointUri!);
        var platform = PlatformLabel;
        _models = null;

        // A variable: the one the settings already name here, else the one the provider documents.
        var saved = Saved;
        var candidate = string.Equals(saved.Provider, _provider, StringComparison.OrdinalIgnoreCase) && saved.KeyVariable.Length > 0
            ? saved.KeyVariable
            : ApiKeys.UsualVariable(_provider);
        if (candidate is not null && !string.IsNullOrEmpty(_context.Environment(candidate)))
        {
            var use = _asker.YesNo(new Question(title, $"{candidate} is set on this PC. Use it for {platform}?", "DipTick reads it at each call and never copies it. Say no to paste a key instead."), _key != KeyPlan.New);
            if (use.Reply != Reply.Answered)
            {
                return use.Reply;
            }

            if (use.Value)
            {
                (_key, _variable, _newKey) = (KeyPlan.Variable, candidate, null);
                return Reply.Answered;
            }
        }

        // A key already saved for this host.
        string? stored = null;
        try
        {
            stored = _context.Keys.Read(host);
        }
        catch (KeyStoreException ex)
        {
            _asker.Tell(title, SafeText.Line(ex.Message));
        }

        if (!string.IsNullOrEmpty(stored))
        {
            var keep = _asker.YesNo(new Question(title, $"A key for {host} is saved in Windows Credential Manager. Keep it?", "Say no to paste a new one."), _key != KeyPlan.New);
            if (keep.Reply != Reply.Answered)
            {
                return keep.Reply;
            }

            if (keep.Value)
            {
                (_key, _variable, _newKey) = (KeyPlan.Stored, string.Empty, null);
                return Reply.Answered;
            }
        }

        // A new key, held in memory until Save.
        var entry = Entry;
        var where = entry?.KeyPage is { } page ? $" Get one at {page.AbsoluteUri}." : entry is { KeyHow.Length: > 0 } ? $" It is {entry.KeyHow}." : string.Empty;
        var typed = _asker.Secret(
            new Question(
                title,
                $"Paste your {platform} key.",
                $"It is not shown as you type. DipTick keeps it in Windows Credential Manager, for this Windows account on this PC, and sends it only to {host}.{where}"),
            k => ApiKeys.Check(k.Trim()));
        if (typed.Reply != Reply.Answered)
        {
            return typed.Reply;
        }

        (_key, _variable, _newKey) = (KeyPlan.New, string.Empty, typed.Value.Trim());
        return Reply.Answered;
    }

    /// <summary>
    /// The check, by where the provider's models come from: its own list, asked with the key (it
    /// costs nothing); the IDs its maker publishes, with nothing sent; or none, when the person types
    /// the name. A Google provider's sign-in is proved here, since that is what can go wrong.
    /// </summary>
    private Reply StepCheck(string title)
    {
        if (!NeedsKey)
        {
            (_key, _variable, _newKey) = (KeyPlan.None, string.Empty, null);
        }

        var entry = Entry;
        var uri = EndpointUri!;
        var platform = PlatformLabel;
        while (true)
        {
            IReadOnlyList<ModelInfo>? models = null;
            string? failure = null;
            var unreachable = false;
            var seconds = _context.Flags.NetTimeoutSeconds;
            using (var timeout = seconds == 0 ? new CancellationTokenSource() : new CancellationTokenSource(TimeSpan.FromSeconds(seconds)))
            {
                try
                {
                    switch (entry?.Models)
                    {
                        case ModelSource.Fixed when entry is not null:
                            _models = ModelLister.Fixed(entry);
                            _checkNote = $"{platform} has no list to ask; these are the models its maker publishes. {(entry.Shape == ProviderShape.Anthropic ? "The model is also a deployment's name where the platform uses one. " : string.Empty)}The key is tried with the first answer.";
                            return Reply.Answered;
                        case ModelSource.Typed when entry is not null:
                            if (entry.GoogleSignIn)
                            {
                                var gate = _context.GateFor(PermissionsNow);
                                if (!gate.Request("setup", Capability.ModelCall, "https://oauth2.googleapis.com/token"))
                                {
                                    throw new ChatException("The harness did not allow the sign-in (model.call). 'diptick audit' says why.");
                                }

                                _ = _asker.Busy("Signing in to Google Cloud.", () => (_context.Google ?? GoogleSignIn.Instance).TokenAsync(timeout.Token));
                            }

                            _models = null;
                            _checkNote = (entry.GoogleSignIn ? "Signed in to Google Cloud. " : string.Empty)
                                + $"{platform} has no list of models to ask; type the model's name exactly as it writes it{(entry.ModelsPage is { } page ? $" ({page.AbsoluteUri} lists them)" : string.Empty)}.";
                            return Reply.Answered;
                    }

                    var query = new ModelQuery(_section, _provider, uri, Finder(uri), NeedsWorkspace ? _workspace : string.Empty);
                    var listGate = _context.GateFor(PermissionsNow);
                    models = _asker.Busy($"Checking with {platform}. This lists its models and costs nothing.", () => _context.Models.ListAsync(query, listGate, timeout.Token));
                }
                catch (ChatException ex)
                {
                    failure = SafeText.Line(ex.Message);
                    unreachable = ex.InnerException is HttpRequestException or Anthropic.Exceptions.AnthropicIOException;
                }
                catch (OperationCanceledException)
                {
                    failure = $"{uri.Host} did not answer within {seconds} seconds.";
                    unreachable = true;
                }
            }

            if (failure is null && models is { Count: > 0 })
            {
                _models = models;
                _checkNote = NeedsKey ? $"The key works: {models.Count} models." : $"{platform} is answering: {models.Count} models.";
                return Reply.Answered;
            }

            var note = failure is null
                ? _provider == "ollama"
                    ? "Ollama has no models yet. In a terminal: ollama pull <name> (ollama.com/library lists them). Then try again."
                    : $"{platform} answered, but offers no models."
                : entry is { Kind: ProviderKind.Local } && unreachable
                    ? $"{platform} is not answering at {ApiKeys.HostOf(uri)}. Start it{(_provider == "ollama" ? " (the Ollama app), or install it from ollama.com" : string.Empty)}, then try again.{(entry.Note.Length > 0 ? " " + entry.Note : string.Empty)}"
                    : failure;
            var choice = _asker.Choose(
                new Question(title, "The check did not work.", note),
                [new Choice("Try again"), new Choice("Go back", "change the key, the address or the provider"), new Choice("Go on without checking", "you type the model's name"), new Choice("Stop", "nothing is saved")],
                0);
            if (choice.Reply != Reply.Answered)
            {
                return choice.Reply;
            }

            switch (choice.Value)
            {
                case 0:
                    continue;
                case 1:
                    return Reply.Back;
                case 2:
                    _models = null;
                    _checkNote = "Not checked. Type the model's name exactly as the provider writes it.";
                    return Reply.Answered;
                default:
                    return Reply.Stop;
            }
        }
    }

    /// <summary>The key finder the check uses: the plan's key, without saving anything.</summary>
    private KeyFinder Finder(Uri uri)
    {
        var entry = Entry;
        var always = entry is { Shape: ProviderShape.Anthropic } || (entry is { Kind: ProviderKind.Local } && entry.NeedsKey);
        var scheme = entry?.Key ?? KeyScheme.Bearer;
        var google = scheme == KeyScheme.GoogleCloud ? _context.Google ?? GoogleSignIn.Instance : null;
        return _key switch
        {
            KeyPlan.Variable => new KeyFinder(_section, _variable, uri, always, _context.Keys, _context.Environment, scheme, google),
            KeyPlan.New => new KeyFinder(_section, string.Empty, uri, always, new MemoryKeyStore(ApiKeys.HostOf(uri), _newKey!), _context.Environment, scheme, google),
            _ => new KeyFinder(_section, string.Empty, uri, always, _context.Keys, _context.Environment, scheme, google),
        };
    }

    private Reply StepModel(string title)
    {
        var question = new Question(title, _section == "main" ? "Which model should answer you?" : $"Which model sits in seat {_section}?", _checkNote);
        var answer = _models is { Count: > 0 } models
            ? _asker.PickModel(question, models, _model)
            : _asker.Text(question, _model, t => t.Length == 0 ? "a model's name is needed" : ModelLister.UsableId(t) ? null : "a model's name has no spaces and only plain characters");
        if (answer.Reply == Reply.Answered)
        {
            if (!ModelLister.UsableId(answer.Value))
            {
                return Reply.Back;
            }

            _model = answer.Value;
        }

        return answer.Reply;
    }

    private Reply StepPassword(string title)
    {
        if (_context.Config.UserPasswordHash.Length > 0)
        {
            var keep = _asker.Choose(new Question(title, "A password is set. Keep it?", "It is asked before the chat screen opens."), [new Choice("Keep it"), new Choice("Change it")], _passwordHash is null ? 0 : 1);
            if (keep.Reply != Reply.Answered)
            {
                return keep.Reply;
            }

            if (keep.Value == 0)
            {
                _passwordHash = null;
                return Reply.Answered;
            }
        }
        else
        {
            var want = _asker.YesNo(new Question(title, "Protect DipTick with a password?", "Without one, anyone at this keyboard can open it. It is asked before the chat screen opens."), _passwordHash is not null);
            if (want.Reply != Reply.Answered)
            {
                return want.Reply;
            }

            if (!want.Value)
            {
                _passwordHash = null;
                return Reply.Answered;
            }
        }

        var first = _asker.Secret(new Question(title, "A new password, 15 or more characters.", "Only a hash of it is kept (Argon2id), never the password."), PasswordHash.Refuse);
        if (first.Reply != Reply.Answered)
        {
            return first.Reply;
        }

        var again = _asker.Secret(new Question(title, "The same password again."), p => p == first.Value ? null : "the two do not match");
        if (again.Reply != Reply.Answered)
        {
            return again.Reply;
        }

        _passwordHash = PasswordHash.Hash(first.Value);
        return Reply.Answered;
    }

    private Reply StepApprovals(string title)
    {
        var now = PermissionsNow.Ask.Any(r => Capabilities.IsExactly(r, Capability.ModelCall));
        var answer = _asker.YesNo(new Question(title, "Should DipTick ask you before each answer?", "Most people say no. Say yes on a paid provider to approve every call."), _askEachAnswer ?? now);
        if (answer.Reply == Reply.Answered)
        {
            _askEachAnswer = answer.Value;
        }

        return answer.Reply;
    }

    private Reply StepSave(string title)
    {
        var summary = Summary();
        if (_context.Flags.DryRun)
        {
            var proved = Save(write: false);
            _result = proved.Outcome == SetupOutcome.Failed ? proved : new SetupResult(SetupOutcome.DryRun, "[dry run] Nothing was saved.");
            _asker.Tell(title, $"{summary}\n\n{_result.Message}");
            return Reply.Answered;
        }

        if (_context.Flags.Yes)
        {
            // --yes answers the question, but what is saved is still shown.
            _asker.Tell(title, summary);
        }
        else
        {
            if (!_asker.CanConfirm)
            {
                _result = new SetupResult(SetupOutcome.NoYes, "Saving needs a yes and a pipe never gives one; nothing was saved. Pass --yes.");
                return Reply.Answered;
            }

            var save = _asker.YesNo(new Question(title, "Save?", summary), true);
            if (save.Reply != Reply.Answered)
            {
                return save.Reply;
            }

            if (!save.Value)
            {
                _result = new SetupResult(SetupOutcome.Stopped, "Not saved; nothing changed.");
                return Reply.Answered;
            }
        }

        _result = Save(write: true);
        return Reply.Answered;
    }

    /// <summary>
    /// Proves every change against the file (or the starter, when there is no file yet), then, when
    /// <paramref name="write"/>, saves: the file from the starter if it is missing, the key, the
    /// changes in one go, the password.
    /// </summary>
    private SetupResult Save(bool write)
    {
        var path = _context.TargetPath;
        var exists = File.Exists(path);
        string? scratch = null;
        try
        {
            var proof = path;
            if (!exists)
            {
                scratch = Path.Combine(Path.GetTempPath(), $"diptick-setup-{Guid.NewGuid():N}.json");
                File.WriteAllText(scratch, StarterConfig.Text(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                proof = scratch;
            }

            if (MasterConfigWriter.Apply(proof, Edits(MasterConfig.Load(proof)), write: false) is { } refused)
            {
                return new SetupResult(SetupOutcome.Failed, $"Not saved: {refused}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SetupResult(SetupOutcome.Failed, $"Not saved: {ex.Message}");
        }
        finally
        {
            if (scratch is not null)
            {
                File.Delete(scratch);
            }
        }

        if (!write)
        {
            return new SetupResult(SetupOutcome.DryRun, "[dry run] Nothing was saved.");
        }

        if (!exists && StarterConfig.Write(path) is { } cannot)
        {
            return new SetupResult(SetupOutcome.Failed, $"Not saved: {cannot}");
        }

        if (_key == KeyPlan.New)
        {
            try
            {
                _context.Keys.Write(ApiKeys.HostOf(EndpointUri!), _newKey!);
            }
            catch (KeyStoreException ex)
            {
                return new SetupResult(SetupOutcome.Failed, $"Not saved: {ex.Message}");
            }
        }

        if (MasterConfigWriter.Apply(path, Edits(MasterConfig.Load(path)), write: true) is { } failed)
        {
            return new SetupResult(SetupOutcome.Failed, $"Not saved: {failed}");
        }

        if (_passwordHash is not null && MasterConfigWriter.SetPasswordHash(path, _passwordHash) is { } password)
        {
            return new SetupResult(SetupOutcome.Failed, $"Everything but the password was saved: {password}");
        }

        return new SetupResult(SetupOutcome.Saved, $"Saved to {path}. Change any of it later with /settings in the chat, or 'diptick settings'.");
    }

    /// <summary>The changes Save makes to the file, proved together and written together.</summary>
    private List<MasterConfigWriter.Edit> Edits(MasterConfig current)
    {
        var edits = new List<MasterConfigWriter.Edit>();
        if (!_modelOnly)
        {
            edits.Add(new("user", "name", Json(_name), _name));
        }

        edits.Add(new(_section, "provider", Json(_provider), _provider));
        edits.Add(_endpoint.Length > 0 ? new(_section, "endpoint", Json(_endpoint), _endpoint) : new(_section, "endpoint", null, null));
        edits.Add(_key == KeyPlan.Variable ? new(_section, "keyVariable", Json(_variable), _variable) : new(_section, "keyVariable", null, null));
        edits.Add(new(_section, "model", Json(_model), _model));

        // A workspace belongs to Claude through Anthropic's API or Claude Platform on AWS: kept for
        // those, and taken out of the file for any other provider, which would refuse it.
        if (MasterConfig.TakesWorkspace(_provider) && _workspace.Length > 0)
        {
            edits.Add(new(_section, "workspace", Json(_workspace), _workspace));
        }
        else if (!MasterConfig.TakesWorkspace(_provider))
        {
            edits.Add(new(_section, "workspace", null, null));
        }

        if (_newSeat)
        {
            var seats = current.Chat.Seats.Append(_section).ToList();
            edits.Add(new("chat", "seats", ListLiteral(seats), ListShown(seats)));
        }

        if (!_modelOnly && _askEachAnswer is { } ask)
        {
            var allow = current.Permissions.Allow.Where(r => !Capabilities.IsExactly(r, Capability.ModelCall)).ToList();
            var askFirst = current.Permissions.Ask.Where(r => !Capabilities.IsExactly(r, Capability.ModelCall)).ToList();
            (ask ? askFirst : allow).Add("model.call");
            edits.Add(new("permissions", "allow", ListLiteral(allow), ListShown(allow)));
            edits.Add(new("permissions", "ask", ListLiteral(askFirst), ListShown(askFirst)));
        }

        return edits;
    }

    /// <summary>What will be saved, in the person's words.</summary>
    private string Summary()
    {
        var uri = EndpointUri;
        var host = uri is null ? "(no address)" : ApiKeys.HostOf(uri);
        var lines = new List<(string, string)>();
        if (!_modelOnly)
        {
            lines.Add(("You", _name));
        }

        lines.Add((_section == "main" ? "Model" : $"Seat {_section}", $"{PlatformLabel} · {_model}    the conversation goes to {host}"));
        if (NeedsWorkspace)
        {
            lines.Add(("Workspace", _workspace));
        }

        lines.Add(("Key", Entry is { GoogleSignIn: true }
            ? (_context.Google ?? GoogleSignIn.Instance).Describe() is { Source: GoogleSignInSource.None } none
                ? $"{none.Missing} Its tokens will go only to {host}."
                : $"your Google Cloud sign-in ({(_context.Google ?? GoogleSignIn.Instance).Describe().Where}); its tokens go only to {host}"
            : _key switch
            {
                KeyPlan.Variable => $"from the variable {_variable}, read at each call",
                KeyPlan.Stored => $"the one already saved for {host}",
                KeyPlan.New => $"saved in Windows Credential Manager ({WindowsKeyStore.Target(host)})",
                _ => uri is { IsLoopback: true } ? "none needed (this PC)" : "none",
            }));
        if (!_modelOnly)
        {
            lines.Add(("Password", _passwordHash is not null ? "set now" : _context.Config.UserPasswordHash.Length > 0 ? "kept" : _full ? "not set" : "not set (Full setup sets one)"));
            if (_askEachAnswer is { } ask)
            {
                lines.Add(("Approvals", ask ? "each answer waits for your yes" : "answers come without asking"));
            }
            else if (_full && !PlainPermissions)
            {
                lines.Add(("Approvals", "your permissions are custom; setup leaves them alone (/settings, Permissions)"));
            }
        }

        lines.Add(("Settings", _context.TargetPath));
        var width = lines.Max(l => l.Item1.Length);
        var text = new StringBuilder();
        foreach (var (label, value) in lines)
        {
            text.Append("  ").Append(label.PadRight(width)).Append("  ").AppendLine(SafeText.Line(value));
        }

        // What agents may not do reads as a sentence, under the table, so it wraps on any screen.
        if (!_modelOnly)
        {
            text.AppendLine().Append("Agents ").Append(CannotDo(PermissionsNow, _context.Config.AgentFilesFolder));
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// What the permissions keep agents from, read from the lists themselves (deny beats ask beats
    /// allow; no folders of yours means none of your files; no hosts means no web), never assumed.
    /// <paramref name="ownFolder"/> is where agents keep what they make, always among the folders,
    /// so it is not one of yours.
    /// </summary>
    internal static string CannotDo(Permissions p, string ownFolder)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(ownFolder);
        var yours = p.Roots.Any(r => !string.Equals(Path.GetFullPath(r).TrimEnd('\\'), Path.GetFullPath(ownFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        string Rule(Capability cap) =>
            p.Deny.Any(r => Capabilities.Matches(r, cap)) ? "deny"
            : p.Ask.Any(r => Capabilities.Matches(r, cap)) ? "ask"
            : p.Allow.Any(r => Capabilities.Matches(r, cap)) ? "allow"
            : "deny";

        var cannot = new List<string>();
        var asks = new List<string>();
        void Sort(string what, bool possible, params Capability[] caps)
        {
            var rules = caps.Select(Rule).ToList();
            if (!possible || rules.All(r => r == "deny"))
            {
                cannot.Add(what);
            }
            else if (rules.Any(r => r == "ask"))
            {
                asks.Add(what);
            }
        }

        Sort("touch your files", yours, Capability.FsRead, Capability.FsWrite, Capability.FsDelete);
        Sort("reach the web", p.Hosts.Count > 0, Capability.NetFetch);
        Sort("use a database", true, Capability.DbRead, Capability.DbWrite);
        Sort("run commands", true, Capability.ShellRun);
        var text = cannot.Count == 0 ? string.Empty : $"cannot {Join(cannot)}.";
        if (asks.Count > 0)
        {
            text += $"{(text.Length > 0 ? " They" : "They")} ask you before they may {Join(asks)}.";
        }

        return text.Length == 0 ? "may do what the permissions allow (/settings, Permissions)." : text + " Change it in /settings, Permissions.";

        static string Join(List<string> parts) =>
            parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " or " + parts[^1];
    }

    private static string? NameProblem(string text) =>
        text.Length == 0 ? "a name is needed"
        : text.Length > 64 ? "at most 64 characters"
        : SafeText.HasControls(text) ? "no control characters"
        : null;

    private static string Json(string value) => JsonSerializer.Serialize(value);

    private static string ListLiteral(List<string> items) => "[" + string.Join(", ", items.Select(Json)) + "]";

    private static string ListShown(List<string> items) => items.Count == 0 ? "(none)" : string.Join(", ", items);
}
