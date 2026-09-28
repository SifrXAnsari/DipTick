using DipTick.Models.Keys;
using DipTick.Models.Providers;

namespace DipTick.Models.Chat;

/// <summary>
/// Picks the engine for a provider, for the <c>main</c> section or for one seat, from the provider
/// catalogue: every entry DipTick can talk to, with the address its own
/// documentation gives, or none when the person gives it; and <c>openai-compatible</c> for any other
/// server speaking the OpenAI chat API at an address set.
/// </summary>
internal static class Engines
{
    /// <summary>Every provider DipTick can talk to now, and the address it uses when none is set (null: the person gives one).</summary>
    public static IReadOnlyDictionary<string, Uri?> Known { get; } =
        ProviderCatalogue.All.Where(p => p.WorksNow).ToDictionary(p => p.Id, p => p.Address, StringComparer.OrdinalIgnoreCase);

    /// <summary>The engine for the <c>main</c> section: what <c>diptick ask</c> uses with no --to.</summary>
    public static IChatEngine Create(MasterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Create("main", config.MainProvider, config.MainModel, config.MainEndpoint, config.MainKeyVariable, config.MainWorkspace);
    }

    /// <summary>The engine for one seat.</summary>
    public static IChatEngine Create(SeatConfig seat)
    {
        ArgumentNullException.ThrowIfNull(seat);
        return Create(seat.Id, seat.Provider, seat.Model, seat.Endpoint, seat.KeyVariable, seat.Workspace);
    }

    /// <summary>
    /// Where a provider's calls go: the endpoint set, else the provider's own; null when neither is known.
    /// A Gemini-shaped provider's address that names Google's OpenAI-compatible path
    /// (<c>…/v1beta/openai</c>) is read as the API it sits under, which the Gemini engine speaks.
    /// </summary>
    public static Uri? Endpoint(string provider, string endpointText)
    {
        if (endpointText.Length > 0 && Uri.TryCreate(endpointText, UriKind.Absolute, out var set))
        {
            var path = set.AbsolutePath.TrimEnd('/');
            return ProviderCatalogue.Find(provider) is { Shape: ProviderShape.Gemini } && path.EndsWith("/openai", StringComparison.OrdinalIgnoreCase)
                ? new UriBuilder(set) { Path = path[..^"/openai".Length] }.Uri
                : set;
        }

        return Known.TryGetValue(provider, out var fallback) ? fallback : null;
    }

    /// <summary>The provider's name as the catalogue writes it, or the host for any other server.</summary>
    public static string DisplayName(string provider, string endpointText)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.Length == 0)
        {
            return "no model";
        }

        if (string.Equals(provider, ProviderCatalogue.Another, StringComparison.OrdinalIgnoreCase))
        {
            return Endpoint(provider, endpointText) is { } uri ? ApiKeys.HostOf(uri) : "another server";
        }

        return ProviderCatalogue.Find(provider)?.Name ?? provider;
    }

    /// <summary>
    /// Why a setting cannot name this provider, worded to follow the setting's name, or null: one
    /// DipTick can talk to now is fine; one in the catalogue that needs an engine DipTick does not
    /// have says which; anything else is not a provider DipTick knows.
    /// </summary>
    public static string? ProviderProblem(string provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (Known.ContainsKey(provider))
        {
            return null;
        }

        return ProviderCatalogue.Find(provider) is { } entry
            ? $"names {entry.Name}, which DipTick cannot talk to yet: {entry.Needs}"
            : $"is not a provider DipTick knows; 'diptick providers' lists them, and {ProviderCatalogue.Another} takes any server with the OpenAI chat API";
    }

    /// <summary>
    /// Why a provider cannot be sent to this endpoint, or null. A Google sign-in opens the person's
    /// whole Google Cloud project, so a provider signed in to Google takes only a Google address.
    /// </summary>
    public static string? EndpointProblem(string provider, string endpointText)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(endpointText);
        if (endpointText.Length == 0 || ProviderCatalogue.Find(provider) is not { GoogleSignIn: true } entry)
        {
            return null;
        }

        return Uri.TryCreate(endpointText, UriKind.Absolute, out var uri) && GoogleCloud.Parse(uri) is not null
            ? null
            : $"must be a Google Cloud address for {entry.Name} (https://aiplatform.googleapis.com/v1/projects/<project>/locations/<location>...), because its sign-in goes only to Google";
    }

    /// <summary>The key finder for the <c>main</c> section, or null when it has no endpoint yet.</summary>
    public static KeyFinder? KeysFor(MasterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return KeysFor("main", config.MainProvider, config.MainEndpoint, config.MainKeyVariable);
    }

    /// <summary>The key finder for one seat, or null when it has no endpoint yet.</summary>
    public static KeyFinder? KeysFor(SeatConfig seat)
    {
        ArgumentNullException.ThrowIfNull(seat);
        return KeysFor(seat.Id, seat.Provider, seat.Endpoint, seat.KeyVariable);
    }

    /// <summary>The key finder for a section's settings, or null when they name no endpoint.</summary>
    public static KeyFinder? KeysFor(string section, string provider, string endpointText, string keyVariable) =>
        provider.Length == 0 || Endpoint(provider, endpointText) is not { } endpoint
            ? null
            : KeysFor(section, provider, endpoint, keyVariable, WindowsKeyStore.Instance, Environment.GetEnvironmentVariable);

    /// <summary>
    /// The key finder for a provider at an endpoint: its key scheme from the catalogue; a key always
    /// needed for Anthropic's messages and for a server on this PC that locks itself; Google's
    /// sign-in for a Google provider.
    /// </summary>
    public static KeyFinder KeysFor(string section, string provider, Uri endpoint, string keyVariable, IKeyStore store, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var entry = ProviderCatalogue.Find(provider);
        var scheme = entry?.Key ?? KeyScheme.Bearer;
        var always = entry is { Shape: ProviderShape.Anthropic } || (entry is { Kind: ProviderKind.Local } && entry.NeedsKey);
        return new KeyFinder(section, keyVariable, endpoint, always, store, environment, scheme, scheme == KeyScheme.GoogleCloud ? GoogleSignIn.Instance : null);
    }

    /// <param name="section">Where the settings live, for the messages: "main" or a seat id.</param>
    private static IChatEngine Create(string section, string provider, string model, string endpointText, string keyVariable, string workspace)
    {
        if (provider.Length == 0)
        {
            return new NoEngine(section == "main"
                ? "No model is set up yet. Settings, Models, Provider picks one (it takes about a minute), or run 'diptick setup'."
                : $"Seat '{section}' has no provider. 'diptick config set {section}.provider <name>', or /settings.");
        }

        if (ProviderProblem(provider) is { } unknown)
        {
            return new NoEngine($"{section}.provider {unknown}");
        }

        if (Endpoint(provider, endpointText) is not { } endpoint)
        {
            return new NoEngine($"{DisplayName(provider, endpointText)} needs {section}.endpoint: its address. 'diptick providers show {provider}' says what it takes.");
        }

        if (EndpointProblem(provider, endpointText) is { } wrongPlace)
        {
            return new NoEngine($"{section}.endpoint {wrongPlace}.");
        }

        if (model.Length == 0)
        {
            return new NoEngine($"No model is set. /model picks one, or 'diptick config set {section}.model <name>'.");
        }

        var keys = KeysFor(section, provider, endpointText, keyVariable)!;
        var entry = ProviderCatalogue.Find(provider);
        if (entry is { Shape: ProviderShape.Anthropic })
        {
            return new AnthropicEngine(model, keys, endpointText.Length > 0 ? endpoint : null, entry.Flavor, workspace, entry.Name);
        }

        if (entry is { Shape: ProviderShape.Gemini })
        {
            // gemma.cpp's models have no system turn and its server takes no systemInstruction: the briefing goes first in the first message.
            return new GeminiEngine(entry.Id, endpoint, model, keys, briefInFirstTurn: entry.Kind == ProviderKind.Local);
        }

        if (entry is { Shape: ProviderShape.OpenInference })
        {
            return new OpenInferenceEngine(entry.Id, endpoint, model, keys);
        }

        var name = entry is null || entry.Id == ProviderCatalogue.Another ? endpoint.Host : provider;
        return new OpenAiCompatibleEngine(name, endpoint, model, keys, entry?.Wire ?? WireTraits.None);
    }
}
