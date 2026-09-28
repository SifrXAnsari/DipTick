using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic.Vertex;
using DipTick.Models.Chat;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;

namespace DipTick.Models.Providers;

/// <summary>Where the Google Cloud sign-in on this PC comes from, found without the network.</summary>
internal enum GoogleSignInSource
{
    /// <summary>GOOGLE_APPLICATION_CREDENTIALS names a file that is there.</summary>
    Variable,

    /// <summary>The file 'gcloud auth application-default login' writes.</summary>
    Gcloud,

    /// <summary>
    /// Neither file, on a Google Compute Engine machine, with <c>providers.googleMachineAccount</c> on: the
    /// machine's own account, through its metadata server.
    /// </summary>
    Machine,

    /// <summary>
    /// None. A Google Cloud machine's own account is looked for only when the switch is on and the
    /// firmware says it is one: finding it means asking the metadata server on the network.
    /// </summary>
    None,
}

/// <summary>
/// The sign-in DipTick would use, and where it is: never the credential itself. With none, what to
/// do about it in one sentence (<paramref name="Missing"/>), said the same wherever it shows.
/// </summary>
internal sealed record GoogleSignInState(GoogleSignInSource Source, string Where, string Missing = "");

/// <summary>A Google Cloud sign-in: what it is, and a token from it.</summary>
internal interface IGoogleSignIn
{
    /// <summary>Where the sign-in would come from, read from this PC only.</summary>
    GoogleSignInState Describe();

    /// <summary>An access token for Google Cloud, renewed when it has run out.</summary>
    /// <exception cref="ChatException">No sign-in, or Google refused it.</exception>
    Task<string> TokenAsync(CancellationToken cancel);
}

/// <summary>
/// Google Cloud for DipTick: which hosts are Google's, the address a location has, and the sign-in.
/// </summary>
/// <remarks>
/// A Google access token opens the person's whole Google Cloud project, so it goes only to Google's
/// own model hosts, over HTTPS, and is never written anywhere.
/// </remarks>
internal static partial class GoogleCloud
{
    /// <summary>The scope Google's own sample for its OpenAI-compatible route asks for (docs.cloud.google.com, "OpenAI compatibility").</summary>
    public const string Scope = "https://www.googleapis.com/auth/cloud-platform";

    /// <summary>
    /// The host a location is served from, as Anthropic's page on Claude on Google Cloud gives the
    /// three kinds (platform.claude.com, "Global, multi-region, and regional endpoints"): global at
    /// aiplatform.googleapis.com; the multi-regions us and eu at aiplatform.{us|eu}.rep.googleapis.com;
    /// a region at {region}-aiplatform.googleapis.com. Null for anything that is not a location.
    /// </summary>
    public static string? HostFor(string location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return location switch
        {
            "global" => "aiplatform.googleapis.com",
            "us" or "eu" => $"aiplatform.{location}.rep.googleapis.com",
            _ when Region().IsMatch(location) => $"{location}-aiplatform.googleapis.com",
            _ => null,
        };
    }

    /// <summary>Whether an address is one of Google's model hosts, over HTTPS: the only place a Google token goes.</summary>
    public static bool IsGoogleHost(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0
            && (uri.Host == "aiplatform.googleapis.com" || MultiRegionHost().IsMatch(uri.Host) || RegionHost().IsMatch(uri.Host));
    }

    /// <summary>
    /// The project and location an address names (<c>/v1/projects/{project}/locations/{location}</c>,
    /// with <c>/endpoints/openapi</c> or nothing after), when its host is the one that location is
    /// served from; null otherwise.
    /// </summary>
    public static (string Project, string Location)? Parse(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!IsGoogleHost(endpoint))
        {
            return null;
        }

        var match = ProjectPath().Match(endpoint.AbsolutePath);
        return match.Success && HostFor(match.Groups[2].Value) == endpoint.Host
            ? (match.Groups[1].Value, match.Groups[2].Value)
            : null;
    }

    [GeneratedRegex("^[a-z]+-[a-z]+[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Region();

    [GeneratedRegex(@"^aiplatform\.(us|eu)\.rep\.googleapis\.com$", RegexOptions.CultureInvariant)]
    private static partial Regex MultiRegionHost();

    [GeneratedRegex(@"^[a-z]+-[a-z]+[0-9]+-aiplatform\.googleapis\.com$", RegexOptions.CultureInvariant)]
    private static partial Regex RegionHost();

    [GeneratedRegex(@"^/v1/projects/([a-z][a-z0-9-]{4,28}[a-z0-9])/locations/([a-z0-9-]+)(/endpoints/openapi)?/?$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectPath();
}

/// <summary>
/// The Google Cloud sign-in on this PC, from the two files Application Default Credentials look at
/// first (docs.cloud.google.com, "How Application Default Credentials works"): the one
/// GOOGLE_APPLICATION_CREDENTIALS names, else the one 'gcloud auth application-default login' writes
/// (%APPDATA%\gcloud\application_default_credentials.json on Windows); with neither, and only where
/// <paramref name="machineAccount"/> says so and the firmware says this is a Google Compute Engine
/// machine, the machine's own account (<see cref="MachineAccount"/>).
/// </summary>
/// <remarks>
/// DipTick reads the file itself instead of asking Google's library for its default, which keeps its
/// first answer for the life of the process, a failure included, and with neither file asks a Google
/// Cloud machine's metadata server on the network, from every PC (DefaultCredentialProvider.cs,
/// googleapis/google-api-dotnet-client). The file is loaded as the type it says it is, when that is one
/// of the five the library knows, by the library's own typed loader (CredentialFactory.FromFileAsync);
/// it is loaded again when it changes, so signing in again needs no restart. The library renews the
/// token as it runs out.
/// </remarks>
internal sealed class GoogleSignIn(Func<string, string?> environment, Func<bool>? machineAccount = null, MachineAccount? machine = null) : IGoogleSignIn, IDisposable
{
    /// <summary>
    /// <c>providers.googleMachineAccount</c> in the settings this process uses: set where they are loaded
    /// for use (the command line's, and the window's on each reload). Off unless the person turns it on.
    /// </summary>
    public static bool UseMachineAccount { get; set; }

    private readonly Func<bool> _machineAccount = machineAccount ?? (() => false);

    /// <summary>The machine's account: shared, and holding nothing to dispose.</summary>
    private readonly MachineAccount _machine = machine ?? MachineAccount.Real;
    /// <summary>A sign-in file is a few kilobytes; one past this is not a sign-in.</summary>
    private const long MaxFile = 64 * 1024;

    /// <summary>The kinds of sign-in file Google's library loads (JsonCredentialParameters).</summary>
    private static readonly string[] Kinds =
    [
        JsonCredentialParameters.AuthorizedUserCredentialType,
        JsonCredentialParameters.ServiceAccountCredentialType,
        JsonCredentialParameters.ImpersonatedServiceAccountCredentialType,
        JsonCredentialParameters.ExternalAccountCredentialType,
        JsonCredentialParameters.ExternalAccountAuthorizedUserCredentialType,
    ];

    public static GoogleSignIn Instance { get; } = new(Environment.GetEnvironmentVariable, () => UseMachineAccount, MachineAccount.Real);

    private readonly SemaphoreSlim _once = new(1, 1);
    private ITokenAccess? _credential;
    private (string Path, DateTime Written) _loaded;

    public void Dispose() => _once.Dispose();

    public GoogleSignInState Describe()
    {
        if (environment("GOOGLE_APPLICATION_CREDENTIALS") is { Length: > 0 } named)
        {
            return File.Exists(named)
                ? new GoogleSignInState(GoogleSignInSource.Variable, named)
                : new GoogleSignInState(
                    GoogleSignInSource.None,
                    $"GOOGLE_APPLICATION_CREDENTIALS names {named}, which is not there",
                    $"No Google Cloud sign-in: GOOGLE_APPLICATION_CREDENTIALS names {named}, which is not there. Point it at a service account's key file, or clear it and run 'gcloud auth application-default login'.");
        }

        var gcloud = Path.Combine(environment("APPDATA") ?? string.Empty, "gcloud", "application_default_credentials.json");
        if (File.Exists(gcloud))
        {
            return new GoogleSignInState(GoogleSignInSource.Gcloud, gcloud);
        }

        // The firmware is read from this PC only; the metadata server is asked only once the switch is on.
        var onGoogle = _machine.OnComputeEngine();
        if (onGoogle && _machineAccount())
        {
            return new GoogleSignInState(GoogleSignInSource.Machine, "this Google Compute Engine machine's own account, through its metadata server");
        }

        return new GoogleSignInState(
            GoogleSignInSource.None,
            "no Google Cloud sign-in on this PC",
            onGoogle
                ? "No Google Cloud sign-in file on this machine. It is a Google Compute Engine machine: 'diptick config set providers.googleMachineAccount true' uses its own account, or run 'gcloud auth application-default login'."
                : "No Google Cloud sign-in on this PC: run 'gcloud auth application-default login', or set GOOGLE_APPLICATION_CREDENTIALS to a service account's key file.");
    }

    public async Task<string> TokenAsync(CancellationToken cancel)
    {
        // Looked for on every call, so a sign-in made meanwhile is found without starting DipTick again.
        var state = Describe();
        if (state.Source == GoogleSignInSource.None)
        {
            throw new ChatException(state.Missing);
        }

        if (state.Source == GoogleSignInSource.Machine)
        {
            return await _machine.TokenAsync(cancel).ConfigureAwait(false);
        }

        ITokenAccess credential;
        await _once.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var file = (state.Where, Written(state.Where));
            if (_credential is null || _loaded != file)
            {
                _credential = (await LoadAsync(state.Where, cancel).ConfigureAwait(false)).CreateScoped(GoogleCloud.Scope);
                _loaded = file;
            }

            credential = _credential;
        }
        finally
        {
            _once.Release();
        }

        try
        {
            return await credential.GetAccessTokenForRequestAsync(null, cancel).ConfigureAwait(false);
        }
        catch (TokenResponseException ex)
        {
            throw new ChatException($"Google refused the sign-in: {OpenAiCompatibleEngine.Clip(ex.Error?.ErrorDescription ?? ex.Message)}. 'gcloud auth application-default login' signs in again.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ChatException($"Could not reach Google to sign in: {OpenAiCompatibleEngine.Clip(ex.Message)}", ex);
        }
    }

    /// <summary>When the file was last written, so a new sign-in in the same place is loaded again; MinValue when it cannot be read.</summary>
    private static DateTime Written(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>The sign-in in a file, as the kind the file says it is, when that is one Google's library knows.</summary>
    private static async Task<GoogleCredential> LoadAsync(string path, CancellationToken cancel)
    {
        string? kind;
        try
        {
            if (new FileInfo(path).Length > MaxFile)
            {
                throw new ChatException($"{path} is too large to be a Google Cloud sign-in; nothing was sent.");
            }

            var stream = File.OpenRead(path);
            await using (stream.ConfigureAwait(false))
            {
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancel).ConfigureAwait(false);
                kind = json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                    ? type.GetString()
                    : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new ChatException($"Could not read the Google Cloud sign-in at {path}: {OpenAiCompatibleEngine.Clip(ex.Message)}", ex);
        }

        if (kind is null || !Kinds.Contains(kind, StringComparer.Ordinal))
        {
            var said = kind is null ? "it names no kind of sign-in" : $"its kind is '{OpenAiCompatibleEngine.Clip(kind)}'";
            throw new ChatException($"{path} is not a Google Cloud sign-in DipTick can use ({said}): it takes the file 'gcloud auth application-default login' writes, or a service account's key file.");
        }

        try
        {
            return await CredentialFactory.FromFileAsync(path, kind, cancel).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or FormatException or CryptographicException)
        {
            throw new ChatException($"The Google Cloud sign-in at {path} could not be used: {OpenAiCompatibleEngine.Clip(ex.Message)}", ex);
        }
    }
}

/// <summary>
/// The sign-in for Claude on Google Cloud, handed to Anthropic's Vertex client: its region and
/// project, and a Google token on each request, but only when that request goes to a Google host.
/// A request anywhere else leaves with no token, and Google's sign-in never travels.
/// </summary>
internal sealed class GoogleVertexCredentials(string region, string project, IGoogleSignIn signIn) : IAnthropicVertexCredentials
{
    public string Region { get; } = region;

    public string Project { get; } = project;

    public async ValueTask ApplyAsync(HttpRequestMessage requestMessage)
    {
        ArgumentNullException.ThrowIfNull(requestMessage);
        if (requestMessage.RequestUri is { } uri && GoogleCloud.IsGoogleHost(uri))
        {
            var token = await signIn.TokenAsync(CancellationToken.None).ConfigureAwait(false);
            requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }
}
