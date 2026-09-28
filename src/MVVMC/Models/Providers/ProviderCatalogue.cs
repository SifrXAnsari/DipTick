using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DipTick.Models.Providers;

/// <summary>What sort of provider an entry is, which is how the lists group them.</summary>
internal enum ProviderKind
{
    /// <summary>Runs on this PC.</summary>
    Local,

    /// <summary>A maker of models, serving its own.</summary>
    Lab,

    /// <summary>Hosts other makers' models.</summary>
    Host,

    /// <summary>A cloud platform, reached through your own account there.</summary>
    Cloud,

    /// <summary>One key in front of many providers.</summary>
    Gateway,

    /// <summary>Anything else with the OpenAI chat API: <see cref="ProviderCatalogue.Another"/>.</summary>
    Other,
}

/// <summary>How DipTick talks to a provider: the OpenAI chat shape, Anthropic's messages, Google's generateContent, or not yet.</summary>
internal enum ProviderShape
{
    OpenAi,
    Anthropic,

    /// <summary>Google's own Gemini API, <c>generateContent</c>.</summary>
    Gemini,

    /// <summary>
    /// KServe's Open Inference Protocol (<c>POST v2/models/{name}/infer</c>, named inputs and outputs), as
    /// Inferless serves a model: one prompt, one text back.
    /// </summary>
    OpenInference,

    /// <summary>It does not chat through DipTick: <see cref="Provider.Needs"/> says why, and what it does instead where it makes media.</summary>
    None,
}

/// <summary>Where Anthropic's messages are served, which decides what a request may carry.</summary>
internal enum AnthropicFlavor
{
    /// <summary>Not an Anthropic-shaped provider.</summary>
    None,

    /// <summary>Anthropic's own API: betas, the server-side fallback and the model list.</summary>
    Api,

    /// <summary>Claude Platform on AWS: as <see cref="Api"/>, and every call names a workspace.</summary>
    Aws,

    /// <summary>Claude in Amazon Bedrock: no beta header, no fallback, no model list.</summary>
    Bedrock,

    /// <summary>Claude in Microsoft Foundry: no fallback, no model list; the model is a deployment's name.</summary>
    Foundry,

    /// <summary>Claude on Google Cloud, through Anthropic's own Vertex package and a Google sign-in.</summary>
    Vertex,
}

/// <summary>How a provider's key is sent, or that it has none.</summary>
internal enum KeyScheme
{
    /// <summary>No key is ever sent.</summary>
    None,

    /// <summary>A key is sent as a bearer token only when one is saved (a server you may lock).</summary>
    Optional,

    /// <summary><c>Authorization: Bearer</c>.</summary>
    Bearer,

    /// <summary><c>Authorization: Key</c> (fal).</summary>
    KeyHeader,

    /// <summary>An <c>api-key</c> header (Azure OpenAI's REST form).</summary>
    ApiKeyHeader,

    /// <summary><c>x-api-key</c>, sent by Anthropic's SDK.</summary>
    XApiKey,

    /// <summary><c>x-goog-api-key</c>, Google's header for an API key; never the <c>key</c> query parameter.</summary>
    GoogApiKey,

    /// <summary>A Google Cloud access token from the Google sign-in on this PC, renewed as it runs out.</summary>
    GoogleCloud,
}

/// <summary>Where a provider's models come from.</summary>
internal enum ModelSource
{
    /// <summary>Its own list (<c>GET {address}/models</c>), which its docs name.</summary>
    List,

    /// <summary>Its own list if it serves one; its docs do not say.</summary>
    Try,

    /// <summary>The IDs its maker publishes, carried in the catalogue; it has no list to ask.</summary>
    Fixed,

    /// <summary>No list: the person types the name (a deployment's, or from its models page).</summary>
    Typed,
}

/// <summary>What a provider's models can do, where the provider says so.</summary>
[Flags]
internal enum Can
{
    None = 0,
    Vision = 1,
    Tools = 2,
    Images = 4,
    Video = 8,
    Search = 16,
    Pdf = 32,

    /// <summary>Turns speech into text.</summary>
    Speech = 64,
}

/// <summary>
/// What of OpenAI's own Chat Completions a provider takes beyond the shape every OpenAI-compatible
/// server shares, where its docs say so; without them DipTick sends only the common fields.
/// </summary>
[Flags]
internal enum WireTraits
{
    None = 0,

    /// <summary>The briefing as a <c>developer</c> message: "With o1 models and newer, use developer messages for this purpose instead" (OpenAI's reference).</summary>
    Developer = 1,

    /// <summary>The answer's cap as <c>max_completion_tokens</c>, which bounds reasoning tokens too; <c>max_tokens</c> is deprecated there.</summary>
    CompletionTokens = 2,

    /// <summary><c>stream_options.include_usage</c>: a last chunk, with no choices, that says what the answer used.</summary>
    IncludeUsage = 4,
}

/// <summary>
/// One part of an address a person gives (a project, a region, a resource's name), asked on its own
/// so nothing else of the address can be typed wrong. <paramref name="Noun"/> names it in a list of
/// what a provider needs; <paramref name="Problem"/> says why a value will not do, or null.
/// </summary>
internal sealed record AddressPart(string Name, string Noun, string Question, string Example, Func<string, string?> Problem);

/// <summary>
/// One provider, as the catalogue records it: who it is, how DipTick
/// talks to it, where, how its key goes, where its models come from, what they can do, its page on
/// what it keeps, and the day its pages were read.
/// </summary>
internal sealed record Provider(
    string Id,
    string Name,
    ProviderKind Kind,
    string About,
    ProviderShape Shape,
    AnthropicFlavor Flavor,
    string Needs,
    Uri? Address,
    string Give,
    KeyScheme Key,
    Uri? KeyPage,
    string KeyHow,
    string KeyVariable,
    ModelSource Models,
    IReadOnlyList<string> ModelIds,
    Uri? ModelsPage,
    Can Can,
    Uri? Keeps,
    string KeepsNote,
    string Note,
    DateOnly Checked,
    IReadOnlyList<Uri> Sources,
    WireTraits Wire = WireTraits.None)
{
    /// <summary>Whether DipTick can talk to it today.</summary>
    public bool WorksNow => Shape != ProviderShape.None;

    /// <summary>
    /// What DipTick makes through this provider's own API: images, video,
    /// speech made text, as <c>media.images</c>, <c>media.video</c> and <c>media.speech</c> may name it.
    /// </summary>
    public Can Makes =>
        (Media.ImageMaker.Providers.Contains(Id, StringComparer.Ordinal) ? Can.Images : Can.None)
        | (Media.VideoMaker.Providers.Contains(Id, StringComparer.Ordinal) ? Can.Video : Can.None)
        | (Media.SpeechMaker.Providers.Contains(Id, StringComparer.Ordinal) ? Can.Speech : Can.None);

    /// <summary>Whether the person gives its address (or parts of it).</summary>
    public bool Given => Address is null && Give.Length > 0;

    /// <summary>The parts of <see cref="Give"/> the person is asked for, in order; empty when the whole address is asked.</summary>
    public IReadOnlyList<string> Parts => ProviderCatalogue.PartsOf(Give).Where(p => p != ProviderCatalogue.GoogleHostPart).ToList();

    /// <summary>Whether it runs on this PC: its address, or the one given, is this machine.</summary>
    public bool OnThisPc => Kind == ProviderKind.Local || Address is { IsLoopback: true };

    /// <summary>Whether the key is a Google sign-in rather than a key the person pastes.</summary>
    public bool GoogleSignIn => Key == KeyScheme.GoogleCloud;

    /// <summary>Whether a key must be there for it to answer: a pasted key, or a Google sign-in.</summary>
    public bool NeedsKey => Key is KeyScheme.Bearer or KeyScheme.KeyHeader or KeyScheme.ApiKeyHeader or KeyScheme.XApiKey or KeyScheme.GoogApiKey or KeyScheme.GoogleCloud;
}

/// <summary>
/// Every provider DipTick knows, from <c>catalogue.json</c> compiled into diptick.exe.
/// It never changes over the network: it changes with DipTick, by hand,
/// from each provider's own pages, and each entry carries the day those were read.
/// </summary>
internal static partial class ProviderCatalogue
{
    /// <summary>Any server with the OpenAI chat API, at the address the person gives.</summary>
    public const string Another = "openai-compatible";

    /// <summary>The placeholder a Google Cloud address's host takes, worked out from its location.</summary>
    public const string GoogleHostPart = "google-host";

    private static readonly Lazy<Catalogue> Loaded = new(() => Parse(ReadResource()));

    private sealed record Catalogue(DateOnly Checked, DateOnly Latest, IReadOnlyList<Provider> Providers);

    /// <summary>The day the catalogue was last read through: every entry's date unless it has its own.</summary>
    public static DateOnly Checked => Loaded.Value.Checked;

    /// <summary>Every entry, in the file's order, then <see cref="Another"/>.</summary>
    public static IReadOnlyList<Provider> All => Loaded.Value.Providers;

    /// <summary>
    /// Every entry grouped by kind (this PC, makers, hosts, clouds, gateways, anything else), those
    /// that work now first in each group, then by name: how the lists show them.
    /// </summary>
    public static IReadOnlyList<Provider> Ordered =>
        [.. All.OrderBy(p => p.Kind).ThenBy(p => p.WorksNow ? 0 : 1).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>The entry with this id (any case), or null.</summary>
    public static Provider? Find(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A kind in a person's words, as the lists head its group.</summary>
    public static string KindWords(ProviderKind kind) => kind switch
    {
        ProviderKind.Local => "On this PC",
        ProviderKind.Lab => "Model makers",
        ProviderKind.Host => "Hosts",
        ProviderKind.Cloud => "Clouds",
        ProviderKind.Gateway => "Gateways",
        _ => "Anything else",
    };

    /// <summary>The word <c>--kind</c> takes for a kind.</summary>
    public static string KindName(ProviderKind kind) => kind switch
    {
        ProviderKind.Local => "local",
        ProviderKind.Lab => "lab",
        ProviderKind.Host => "host",
        ProviderKind.Cloud => "cloud",
        ProviderKind.Gateway => "gateway",
        _ => "other",
    };

    /// <summary>The words <c>--can</c> takes, in the order they are shown.</summary>
    public static IReadOnlyList<(Can Can, string Name, string Words)> Abilities { get; } =
    [
        (Can.Vision, "vision", "sees images"),
        (Can.Tools, "tools", "calls tools"),
        (Can.Images, "images", "makes images"),
        (Can.Video, "video", "makes video"),
        (Can.Search, "search", "searches the web"),
        (Can.Pdf, "pdf", "reads PDFs"),
        (Can.Speech, "speech", "turns speech into text"),
    ];

    /// <summary>What a provider's models can do, in a few words; empty when its pages say nothing.</summary>
    public static string CanWords(Can can) => string.Join(", ", Abilities.Where(a => can.HasFlag(a.Can)).Select(a => a.Words));

    /// <summary>What a provider needs from the person, in a few words: a key, an address, a sign-in, nothing.</summary>
    public static string NeedsWords(Provider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!provider.WorksNow)
        {
            return "not yet";
        }

        var needs = new List<string>();
        if (provider.GoogleSignIn)
        {
            needs.Add("a Google Cloud sign-in");
        }
        else if (provider.NeedsKey)
        {
            needs.Add("a key");
        }

        if (provider.Given)
        {
            needs.Add(provider.Parts.Count == 0 ? "its address" : string.Join(" and ", provider.Parts.Select(p => Part(p)?.Noun ?? p)));
        }

        if (provider.Flavor == AnthropicFlavor.Aws)
        {
            needs.Add("your workspace ID");
        }

        return needs.Count == 0 ? (provider.OnThisPc ? "nothing: it runs here" : "nothing") : string.Join(", ", needs);
    }

    /// <summary>The parts a template names, in order: <c>{project}</c> gives <c>project</c>.</summary>
    public static IReadOnlyList<string> PartsOf(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return [.. PartPattern().Matches(template).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>A part of an address the catalogue asks for, or null for a name it does not know.</summary>
    public static AddressPart? Part(string name) => Parts.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// The address as far as the answers go, for a person to read: each part given filled in, the
    /// rest named in words (<c>&lt;project ID&gt;</c>), and a Google Cloud host once its location is known.
    /// </summary>
    public static string Sketch(Provider provider, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(values);
        return PartPattern().Replace(provider.Give, m =>
        {
            var name = m.Groups[1].Value;
            if (name == GoogleHostPart)
            {
                return values.TryGetValue("location", out var location) && GoogleCloud.HostFor(location) is { } host ? host : "<the location's host>";
            }

            var part = Part(name);
            if (values.TryGetValue(name, out var given) && given.Length > 0 && part is not null && part.Problem(given) is null)
            {
                return given;
            }

            var noun = part?.Noun ?? name;
            foreach (var article in (string[])["your ", "its ", "a ", "an "])
            {
                if (noun.StartsWith(article, StringComparison.Ordinal))
                {
                    noun = noun[article.Length..];
                    break;
                }
            }

            return $"<{noun}>";
        });
    }

    /// <summary>
    /// The address from a template and the person's answers: every part checked, a Google Cloud host
    /// worked out from the location. Null, with <paramref name="why"/>, when a part will not do.
    /// </summary>
    public static Uri? Compose(Provider provider, IReadOnlyDictionary<string, string> values, out string why)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(values);
        why = string.Empty;
        var text = provider.Give;
        foreach (var name in PartsOf(provider.Give))
        {
            string value;
            if (name == GoogleHostPart)
            {
                if (!values.TryGetValue("location", out var location) || GoogleCloud.HostFor(location) is not { } host)
                {
                    why = "a Google Cloud location is needed to find its address";
                    return null;
                }

                value = host;
            }
            else
            {
                if (!values.TryGetValue(name, out var given) || Part(name) is not { } part)
                {
                    why = $"'{name}' is needed";
                    return null;
                }

                if (part.Problem(given) is { } problem)
                {
                    why = $"{part.Question}: {problem}";
                    return null;
                }

                value = given;
            }

            text = text.Replace("{" + name + "}", value, StringComparison.Ordinal);
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || AddressProblem(uri) is { } bad)
        {
            why = $"the address would not do ({text})";
            return null;
        }

        return uri;
    }

    /// <summary>
    /// Why an address cannot be a provider's, or null: HTTPS, or HTTP to this machine only; no user
    /// name, query or fragment, which could carry a secret or change where it goes.
    /// </summary>
    public static string? AddressProblem(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            return "must be https, or http to this PC only";
        }

        return uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0
            ? "must have no user name, query or fragment"
            : null;
    }

    /// <summary>
    /// What is wrong with an entry, checked as the catalogue is read: nothing, for every entry that
    /// ships. Every entry has its sources and date, a known shape, an address that is HTTPS unless
    /// it is this PC's, and its page on what it keeps (or why there is none). <paramref name="catalogueChecked"/>
    /// is the catalogue's newest check: no entry is dated after it.
    /// </summary>
    internal static IReadOnlyList<string> Problems(Provider p, DateOnly catalogueChecked)
    {
        ArgumentNullException.ThrowIfNull(p);
        var problems = new List<string>();
        if (!IdPattern().IsMatch(p.Id) || p.Id == Another)
        {
            problems.Add("its id must be 2 to 24 of a-z and 0-9, starting with a letter");
        }

        if (p.Name.Length == 0 || p.About.Length == 0)
        {
            problems.Add("it needs a name and a line on what it is");
        }

        if (p.Sources.Count == 0 || p.Sources.Any(s => s.Scheme != Uri.UriSchemeHttps))
        {
            problems.Add("it needs its sources, every one https");
        }

        if (p.Checked > catalogueChecked || p.Checked < new DateOnly(2026, 9, 24))
        {
            problems.Add("its checked date must be between 2026-09-24, when the list was first read through, and the catalogue's newest check");
        }

        if (p.Address is { } address && AddressProblem(address) is { } bad)
        {
            problems.Add($"its address {bad}");
        }

        if (p.Address is not null && p.Give.Length > 0)
        {
            problems.Add("it has both an address and a template for one");
        }

        if (p.WorksNow && p.Address is null && p.Give.Length == 0)
        {
            problems.Add("it works now, so it needs an address or a template for one");
        }

        if (p.Give.Length > 0)
        {
            foreach (var part in PartsOf(p.Give).Where(n => n != GoogleHostPart && Part(n) is null))
            {
                problems.Add($"its template names '{part}', which no question asks");
            }

            var examples = PartsOf(p.Give).Where(n => n != GoogleHostPart).ToDictionary(n => n, n => Part(n)?.Example ?? string.Empty, StringComparer.Ordinal);
            if (PartsOf(p.Give).Contains(GoogleHostPart) && !examples.ContainsKey("location"))
            {
                problems.Add("a Google host needs a location part");
            }

            if (PartsOf(p.Give).Count > 0 && Compose(p, examples, out var why) is null)
            {
                problems.Add($"its template does not make an address from the examples: {why}");
            }
        }

        if (p.Shape == ProviderShape.None && p.Needs.Length == 0)
        {
            problems.Add("it does not work yet, so it must say what it needs");
        }

        if ((p.Shape == ProviderShape.Anthropic) != (p.Flavor != AnthropicFlavor.None))
        {
            problems.Add("an Anthropic-shaped entry, and only one, names where its messages are served");
        }

        if (p.Key == KeyScheme.XApiKey && p.Shape != ProviderShape.Anthropic)
        {
            problems.Add("x-api-key is Anthropic's SDK's; an OpenAI-shaped entry sends its key another way");
        }

        if ((p.Key == KeyScheme.GoogApiKey) != (p.Shape == ProviderShape.Gemini && p.Key != KeyScheme.None))
        {
            problems.Add("x-goog-api-key is Google's generateContent's header; a Gemini-shaped entry with a key sends it so, and nothing else does");
        }

        if (p.Wire != WireTraits.None && p.Shape != ProviderShape.OpenAi)
        {
            problems.Add("wire traits are fields of OpenAI's chat shape; only an OpenAI-shaped entry takes them");
        }

        if (p.Shape == ProviderShape.OpenInference && (p.Can.HasFlag(Can.Tools) || p.Can.HasFlag(Can.Vision)))
        {
            problems.Add("the Open Inference Protocol engine sends one prompt and takes one text back: no tools, no images");
        }

        if (p.Shape == ProviderShape.Anthropic && p.Key is not (KeyScheme.XApiKey or KeyScheme.GoogleCloud))
        {
            problems.Add("an Anthropic-shaped entry sends its key through Anthropic's SDK, or signs in to Google");
        }

        if ((p.Flavor == AnthropicFlavor.Vertex) != (p.Shape == ProviderShape.Anthropic && p.Key == KeyScheme.GoogleCloud))
        {
            problems.Add("Claude on Google Cloud, and only it, is Anthropic-shaped with a Google sign-in");
        }

        if (p.Key == KeyScheme.GoogleCloud && !PartsOf(p.Give).Contains(GoogleHostPart))
        {
            problems.Add("a Google sign-in goes only to a Google host, so its address must be built from one");
        }

        if (p.Models == ModelSource.Fixed && (p.ModelIds.Count == 0 || p.ModelIds.Any(m => !Chat.ModelLister.UsableId(m))))
        {
            problems.Add("fixed models need their IDs, each plain printable text");
        }

        if (p.Models != ModelSource.Fixed && p.ModelIds.Count > 0)
        {
            problems.Add("only fixed models carry IDs");
        }

        if (p.Kind != ProviderKind.Local && p.Keeps is null && p.KeepsNote.Length == 0)
        {
            problems.Add("it needs its page on what it keeps, or a note saying why there is none");
        }

        if (p.Keeps is { } keeps && keeps.Scheme != Uri.UriSchemeHttps)
        {
            problems.Add("its page on what it keeps must be https");
        }

        if (p.KeyPage is { } page && page.Scheme != Uri.UriSchemeHttps)
        {
            problems.Add("its key page must be https");
        }

        if (p.KeyVariable.Length > 0 && !VariablePattern().IsMatch(p.KeyVariable))
        {
            problems.Add("its key variable must be a variable's name");
        }

        return problems;
    }

    private static Catalogue Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var root = document.RootElement;
        var checkedOn = Date(root, "checked") ?? throw new InvalidDataException("the catalogue has no 'checked' date");
        var latest = Date(root, "rechecked") is { } rechecked && rechecked > checkedOn ? rechecked : checkedOn;
        var providers = new List<Provider>();
        foreach (var item in root.GetProperty("providers").EnumerateArray())
        {
            var provider = Entry(item, checkedOn);
            if (Problems(provider, latest) is { Count: > 0 } problems)
            {
                throw new InvalidDataException($"catalogue entry '{provider.Id}': {string.Join("; ", problems)}");
            }

            if (providers.Any(p => p.Id == provider.Id))
            {
                throw new InvalidDataException($"catalogue entry '{provider.Id}' appears twice");
            }

            providers.Add(provider);
        }

        providers.Add(new Provider(
            Another,
            "Another server",
            ProviderKind.Other,
            "any server with the OpenAI chat API, at the address you give",
            ProviderShape.OpenAi,
            AnthropicFlavor.None,
            string.Empty,
            null,
            "{address}",
            KeyScheme.Optional,
            null,
            "its own key, if it asks for one",
            string.Empty,
            ModelSource.Try,
            [],
            null,
            Can.None,
            null,
            "It keeps what its owner decides.",
            string.Empty,
            checkedOn,
            []));
        return new Catalogue(checkedOn, latest, providers);
    }

    private static Provider Entry(JsonElement e, DateOnly catalogueChecked)
    {
        var id = Text(e, "id");
        try
        {
            var shape = Text(e, "shape", "openai") switch
            {
                "openai" => ProviderShape.OpenAi,
                "anthropic" => ProviderShape.Anthropic,
                "gemini" => ProviderShape.Gemini,
                "openinference" => ProviderShape.OpenInference,
                "none" => ProviderShape.None,
                var other => throw new InvalidDataException($"unknown shape '{other}'"),
            };
            return new Provider(
                id,
                Text(e, "name"),
                Text(e, "kind") switch
                {
                    "local" => ProviderKind.Local,
                    "lab" => ProviderKind.Lab,
                    "host" => ProviderKind.Host,
                    "cloud" => ProviderKind.Cloud,
                    "gateway" => ProviderKind.Gateway,
                    var other => throw new InvalidDataException($"unknown kind '{other}'"),
                },
                Text(e, "about"),
                shape,
                Text(e, "flavor") switch
                {
                    "" => AnthropicFlavor.None,
                    "api" => AnthropicFlavor.Api,
                    "aws" => AnthropicFlavor.Aws,
                    "bedrock" => AnthropicFlavor.Bedrock,
                    "foundry" => AnthropicFlavor.Foundry,
                    "vertex" => AnthropicFlavor.Vertex,
                    var other => throw new InvalidDataException($"unknown flavor '{other}'"),
                },
                Text(e, "needs"),
                Link(e, "address"),
                Text(e, "give"),
                Text(e, "key", "bearer") switch
                {
                    "none" => KeyScheme.None,
                    "optional" => KeyScheme.Optional,
                    "bearer" => KeyScheme.Bearer,
                    "key" => KeyScheme.KeyHeader,
                    "api-key" => KeyScheme.ApiKeyHeader,
                    "x-api-key" => KeyScheme.XApiKey,
                    "x-goog-api-key" => KeyScheme.GoogApiKey,
                    "google-cloud" => KeyScheme.GoogleCloud,
                    var other => throw new InvalidDataException($"unknown key scheme '{other}'"),
                },
                Link(e, "keyPage"),
                Text(e, "keyHow"),
                Text(e, "keyVariable"),
                Text(e, "models", "list") switch
                {
                    "list" => ModelSource.List,
                    "try" => ModelSource.Try,
                    "fixed" => ModelSource.Fixed,
                    "typed" => ModelSource.Typed,
                    var other => throw new InvalidDataException($"unknown models source '{other}'"),
                },
                Texts(e, "modelIds"),
                Link(e, "modelsPage"),
                Texts(e, "can").Aggregate(Can.None, (all, word) => all | (Abilities.FirstOrDefault(a => a.Name == word) is { Name.Length: > 0 } found ? found.Can : throw new InvalidDataException($"unknown ability '{word}'"))),
                Link(e, "keeps"),
                Text(e, "keepsNote"),
                Text(e, "note"),
                Date(e, "checked") ?? catalogueChecked,
                [.. Texts(e, "sources").Select(s => Uri.TryCreate(s, UriKind.Absolute, out var u) ? u : throw new InvalidDataException($"source '{s}' is not an address"))],
                Texts(e, "wire").Aggregate(WireTraits.None, (all, word) => all | word switch
                {
                    "developer" => WireTraits.Developer,
                    "max-completion-tokens" => WireTraits.CompletionTokens,
                    "include-usage" => WireTraits.IncludeUsage,
                    _ => throw new InvalidDataException($"unknown wire trait '{word}'"),
                }));
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new InvalidDataException($"catalogue entry '{id}': {ex.Message}", ex);
        }
    }

    private static string Text(JsonElement e, string name, string fallback = "") =>
        e.TryGetProperty(name, out var value) ? value.GetString() ?? fallback : fallback;

    private static List<string> Texts(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) ? [.. value.EnumerateArray().Select(v => v.GetString() ?? string.Empty)] : [];

    private static Uri? Link(JsonElement e, string name) =>
        Text(e, name) is { Length: > 0 } text
            ? Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri : throw new InvalidDataException($"'{name}' is not an address: {text}")
            : null;

    private static DateOnly? Date(JsonElement e, string name) =>
        Text(e, name) is { Length: > 0 } text
            ? DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;

    private static string ReadResource()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("catalogue.json")
            ?? throw new InvalidDataException("the provider catalogue is missing from diptick.exe");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The parts of addresses the catalogue asks for, each checked so an answer can only fill its own
    /// place: no slash, colon, at-sign, question mark or hash can move the address somewhere else.
    /// </summary>
    private static readonly AddressPart[] Parts =
    [
        new("address", "its address", "Its address", "https://llm.example.com/v1", t => Uri.TryCreate(t, UriKind.Absolute, out var u) ? AddressProblem(u) : "must be a full address, like https://llm.example.com/v1"),
        new("port", "its port", "The port it listens on", "5273", t => int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 65535 ? null : "a number from 1 to 65535"),
        // cloud.google.com/resource-manager/docs/creating-managing-projects: 6 to 30 characters,
        // lowercase letters, numbers and hyphens, starting with a letter, not ending with a hyphen.
        new("project", "your project ID", "Your Google Cloud project ID", "my-project-123", t => GoogleProject().IsMatch(t) ? null : "6 to 30 of a-z, 0-9 and -, starting with a letter and not ending with -"),
        new("location", "a location", "Its location: global, us, eu, or a region like us-east5", "global", t => GoogleCloud.HostFor(t) is null ? "global, us, eu, or a region like us-east5" : null),
        new("aws-region", "your AWS region", "Your AWS region", "us-east-1", t => AwsRegion().IsMatch(t) ? null : "an AWS region, like us-east-1"),
        new("azure-resource", "your resource's name", "Your Azure resource's name", "my-resource", t => Label().IsMatch(t) ? null : "a resource name: letters, digits and -"),
        new("cloudflare-account", "your account ID", "Your Cloudflare account ID", "0123456789abcdef0123456789abcdef", t => Token().IsMatch(t) ? null : "an account ID: letters and digits"),
        new("databricks-host", "your workspace's host", "Your Databricks workspace's host", "dbc-1234.cloud.databricks.com", t => HostName().IsMatch(t) ? null : "a host name, like dbc-1234.cloud.databricks.com"),
        new("runpod-endpoint", "your endpoint's ID", "Your RunPod endpoint's ID", "abc123xyz", t => Token().IsMatch(t) ? null : "an endpoint ID: letters and digits"),
        new("snowflake-account", "your account identifier", "Your Snowflake account identifier", "myorg-myaccount", t => AccountName().IsMatch(t) ? null : "an account identifier, like myorg-myaccount"),
        new("oci-region", "your OCI region", "Your OCI region", "us-chicago-1", t => AwsRegion().IsMatch(t) ? null : "an OCI region, like us-chicago-1"),
        new("heroku-region", "its region", "Your add-on's region: us or eu", "us", t => t is "us" or "eu" ? null : "us or eu"),
        new("watsonx-host", "your region's host", "Your watsonx region's host", "us-south.ml.cloud.ibm.com", t => HostName().IsMatch(t) ? null : "a host name"),
        new("alibaba-host", "your region's host", "Your Model Studio host", "dashscope-us.aliyuncs.com", t => HostName().IsMatch(t) && t.EndsWith(".aliyuncs.com", StringComparison.Ordinal) ? null : "a host ending in .aliyuncs.com"),
        new("chute-host", "your chute's host", "Your chute's host", "myuser-my-chat.chutes.ai", t => HostName().IsMatch(t) && t.EndsWith(".chutes.ai", StringComparison.Ordinal) ? null : "a host ending in .chutes.ai"),
        new("modal-host", "your app's host", "Your Modal app's host", "my-workspace--my-app.us-east.modal.direct", t => HostName().IsMatch(t) && (t.EndsWith(".modal.direct", StringComparison.Ordinal) || t.EndsWith(".modal.run", StringComparison.Ordinal)) ? null : "a host ending in .modal.direct or .modal.run"),
    ];

    [GeneratedRegex(@"\{([a-z-]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex PartPattern();

    [GeneratedRegex("^[a-z][a-z0-9]{1,23}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[A-Z_][A-Z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex VariablePattern();

    [GeneratedRegex("^[a-z][a-z0-9-]{4,28}[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex GoogleProject();

    [GeneratedRegex("^[a-z]{2,}(-[a-z]+)+-[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex AwsRegion();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex Label();

    [GeneratedRegex("^[A-Za-z0-9]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,254}$", RegexOptions.CultureInvariant)]
    private static partial Regex AccountName();

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$", RegexOptions.CultureInvariant)]
    private static partial Regex HostName();
}
