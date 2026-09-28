using DipTick.Models.Chat;

namespace DipTick.Models.Keys;

/// <summary>Where one model's key comes from, without the key itself.</summary>
internal enum KeySource
{
    /// <summary>The environment variable the settings name holds it.</summary>
    Variable,

    /// <summary>The settings name a variable, and it is empty.</summary>
    VariableEmpty,

    /// <summary>Saved in Windows Credential Manager for this host.</summary>
    Stored,

    /// <summary>The model runs on this PC and no key is saved for it: none is sent.</summary>
    NotNeeded,

    /// <summary>A key is needed and there is none.</summary>
    Missing,

    /// <summary>The key store could not be read.</summary>
    StoreFailed,

    /// <summary>A Google Cloud sign-in is on this PC; a token is fetched from it at each call.</summary>
    GoogleSignIn,

    /// <summary>No Google Cloud sign-in on this PC.</summary>
    GoogleMissing,
}

/// <summary>
/// What <see cref="KeyFinder.Describe"/> found: the source, the host and the variable. It never holds
/// the key, so it can be printed, logged or shown anywhere.
/// </summary>
internal sealed record KeyFinding(KeySource Source, string Host, string Variable, string Problem)
{
    public bool Usable => Source is KeySource.Variable or KeySource.Stored or KeySource.NotNeeded or KeySource.GoogleSignIn;

    /// <summary>A few plain words for a status line: "saved", "variable OPENAI_API_KEY", "MISSING".</summary>
    public string Words => Source switch
    {
        KeySource.Variable => $"variable {Variable}",
        KeySource.VariableEmpty => $"variable {Variable} is EMPTY",
        KeySource.Stored => "saved in Credential Manager",
        KeySource.NotNeeded => "none needed (this PC)",
        KeySource.StoreFailed => "store unreadable",
        KeySource.GoogleSignIn => "Google Cloud sign-in",
        KeySource.GoogleMissing => "NO Google Cloud sign-in",
        _ => "MISSING",
    };
}

/// <summary>
/// The rules for API keys: the host a key belongs to, what a key may look like, and the variable a
/// platform's own documentation names for it.
/// </summary>
internal static class ApiKeys
{
    /// <summary>
    /// The host a key belongs to: the endpoint's host, with the port only when it is not the scheme's
    /// default. <c>https://api.openai.com/v1</c> is <c>api.openai.com</c>; a server on this PC at
    /// <c>http://127.0.0.1:18080/v1</c> is <c>127.0.0.1:18080</c>.
    /// </summary>
    public static string HostOf(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        // System.Uri already lower-cases a DNS host. IdnHost is its ASCII (punycode) form; an IPv6
        // literal keeps its brackets so "[::1]:11434" still reads as one host and one port.
        var host = endpoint.HostNameType == UriHostNameType.IPv6 ? endpoint.Host : endpoint.IdnHost;
        return endpoint.IsDefaultPort ? host : $"{host}:{endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Why this cannot be an API key, or null when it can. Every platform DipTick knows issues keys of
    /// printable ASCII with no spaces, so anything else is a bad paste; and Credential Manager holds at
    /// most <see cref="WindowsKeyStore.MaxBlob"/> bytes. The message never repeats the key.
    /// </summary>
    public static string? Check(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0)
        {
            return "it is empty";
        }

        if (key.Any(c => c is < '!' or > '~'))
        {
            return "it has a space, a line break or a character no API key has; paste it again";
        }

        return key.Length > WindowsKeyStore.MaxBlob
            ? $"it is longer than Windows Credential Manager holds ({WindowsKeyStore.MaxBlob} bytes)"
            : null;
    }

    /// <summary>
    /// The variable a provider's own documentation uses for its key, as the catalogue records it
    /// (OPENAI_API_KEY for OpenAI, ANTHROPIC_API_KEY for Anthropic, and so on), or null.
    /// </summary>
    public static string? UsualVariable(string provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return Providers.ProviderCatalogue.Find(provider) is { KeyVariable.Length: > 0 } entry ? entry.KeyVariable : null;
    }
}

/// <summary>
/// One key held in memory for one host, never saved: a key typed in setup, checked before the
/// person chooses to save it.
/// </summary>
internal sealed class MemoryKeyStore(string heldFor, string heldKey) : IKeyStore
{
    public string? Read(string host) => string.Equals(host, heldFor, StringComparison.OrdinalIgnoreCase) ? heldKey : null;

    public void Write(string host, string key) => throw new KeyStoreException("a key held in memory is not saved from here.");

    public bool Delete(string host) => throw new KeyStoreException("a key held in memory is not removed from here.");
}

/// <summary>
/// Finds the key for one model's calls at the moment of each call, in one order everywhere: the
/// variable the settings name; else the key saved for the host the call goes to; else none, for a
/// model on this PC; else the call fails. A key saved for one host is never looked up for another, so a
/// changed endpoint finds no key and the key cannot follow the conversation somewhere new.
/// </summary>
/// <param name="section">Where the settings live, for the messages: "main" or a seat id.</param>
/// <param name="alwaysNeeded">True for a platform that never answers without a key (Anthropic).</param>
/// <param name="scheme">How the key is sent: the header, or a Google sign-in in place of a key.</param>
/// <param name="google">The Google Cloud sign-in, for <see cref="KeyScheme.GoogleCloud"/>.</param>
internal sealed class KeyFinder(
    string section,
    string keyVariable,
    Uri endpoint,
    bool alwaysNeeded,
    IKeyStore store,
    Func<string, string?> environment,
    Providers.KeyScheme scheme = Providers.KeyScheme.Bearer,
    Providers.IGoogleSignIn? google = null)
{
    public string Section { get; } = section;

    public string Host { get; } = ApiKeys.HostOf(endpoint);

    public string KeyVariable { get; } = keyVariable;

    /// <summary>How the key is sent to this provider.</summary>
    public Providers.KeyScheme Scheme { get; } = scheme;

    /// <summary>Where the key would come from, for a status line. Reads the store, keeps nothing.</summary>
    public KeyFinding Describe()
    {
        if (Scheme == Providers.KeyScheme.GoogleCloud)
        {
            var state = (google ?? throw new InvalidOperationException("a Google sign-in needs its source")).Describe();
            return state.Source == Providers.GoogleSignInSource.None
                ? new KeyFinding(KeySource.GoogleMissing, Host, string.Empty, state.Missing)
                : new KeyFinding(KeySource.GoogleSignIn, Host, string.Empty, string.Empty);
        }

        return Find().Finding;
    }

    /// <summary>The key to send, or null when none is needed. A Google sign-in is fetched, so it is <see cref="TakeAsync"/>'s.</summary>
    /// <exception cref="ChatException">A key is needed and there is none to send.</exception>
    public string? Take()
    {
        if (Scheme == Providers.KeyScheme.GoogleCloud)
        {
            throw new ChatException("A Google Cloud token is fetched when it is needed, and this call cannot wait for one.");
        }

        var (finding, key) = Find();
        return finding.Usable ? key : throw new ChatException(finding.Problem);
    }

    /// <summary>
    /// The key to send, or null when none is needed; for a Google sign-in, a token fetched now, and
    /// only when the call goes to one of Google's own hosts.
    /// </summary>
    /// <exception cref="ChatException">A key is needed and there is none, or a Google token would leave Google.</exception>
    public async Task<string?> TakeAsync(CancellationToken cancel)
    {
        if (Scheme != Providers.KeyScheme.GoogleCloud)
        {
            return Take();
        }

        if (!Providers.GoogleCloud.IsGoogleHost(endpoint))
        {
            throw new ChatException($"A Google Cloud sign-in goes only to Google's own hosts, and {Host} is not one; nothing was sent.");
        }

        return await (google ?? throw new InvalidOperationException("a Google sign-in needs its source")).TokenAsync(cancel).ConfigureAwait(false);
    }

    private (KeyFinding Finding, string? Key) Find()
    {
        if (KeyVariable.Length > 0)
        {
            var value = environment(KeyVariable);
            return string.IsNullOrEmpty(value)
                ? (new KeyFinding(KeySource.VariableEmpty, Host, KeyVariable, $"The variable {KeyVariable} is empty; it should hold the API key for {Host}."), null)
                : (new KeyFinding(KeySource.Variable, Host, KeyVariable, string.Empty), value);
        }

        string? stored;
        try
        {
            stored = store.Read(Host);
        }
        catch (KeyStoreException ex)
        {
            return (new KeyFinding(KeySource.StoreFailed, Host, string.Empty, $"The key for {Host}: {ex.Message}"), null);
        }

        if (!string.IsNullOrEmpty(stored))
        {
            return (new KeyFinding(KeySource.Stored, Host, string.Empty, string.Empty), stored);
        }

        // No key on this PC, or for a server whose key is its owner's choice (a deployment you may lock).
        return (endpoint.IsLoopback || Scheme is Providers.KeyScheme.Optional or Providers.KeyScheme.None) && !alwaysNeeded
            ? (new KeyFinding(KeySource.NotNeeded, Host, string.Empty, string.Empty), null)
            : (new KeyFinding(KeySource.Missing, Host, string.Empty, $"No key for {Host}. Save one in Settings, Models, Key, or run 'diptick key set {Section}'."), null);
    }
}
