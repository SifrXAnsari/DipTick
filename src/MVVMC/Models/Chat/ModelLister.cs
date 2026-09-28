using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Models;
using DipTick.Models.Harness;
using DipTick.Models.Keys;

namespace DipTick.Models.Chat;

/// <summary>One model a platform offers. <paramref name="Name"/> is empty when the platform gives none.</summary>
internal sealed record ModelInfo(string Id, string Name, DateTimeOffset? Created, int? Context);

/// <summary>Which platform to ask, where, how its key is found, and the workspace the key acts in (empty: none).</summary>
internal sealed record ModelQuery(string Section, string Provider, Uri Endpoint, KeyFinder Keys, string Workspace = "");

/// <summary>Lists what a platform offers.</summary>
internal interface IModelSource
{
    /// <exception cref="ChatException">Refused, not reached, not allowed, or not a list of models.</exception>
    Task<IReadOnlyList<ModelInfo>> ListAsync(ModelQuery query, Gate gate, CancellationToken cancel);
}

/// <summary>
/// The models a platform offers, from its own list. Listing
/// costs nothing, but it is still a call to a provider carrying a key, so it goes through the
/// gate as <see cref="Capability.ModelCall"/> and is recorded.
/// </summary>
/// <remarks>
/// <para>
/// OpenAI-shaped platforms answer <c>GET {base}/models</c> with <c>{"data":[{"id", "created", ...}]}</c>
/// (OpenAI's list shape; Ollama serves it at /v1/models; OpenRouter adds <c>name</c> and
/// <c>context_length</c>). Anthropic's list comes through its SDK (<c>IModelService.List</c>, pages
/// of at most 1000, newest first).
/// </para>
/// <para>
/// Everything that comes back is untrusted: the body is bounded, the count is bounded, an id that
/// is not plain printable ASCII of at most 200 characters is skipped, and names are shown through
/// <see cref="SafeText"/>. No redirect is followed.
/// </para>
/// </remarks>
internal sealed class ModelLister : IModelSource
{
    public const int MaxBody = 8 * 1024 * 1024;
    public const int MaxModels = 5000;
    public const int MaxId = 200;
    private const int MaxName = 100;
    private const int MaxPages = 5;

    public static ModelLister Instance { get; } = new();

    /// <summary>What to ask for a section's list, <c>main</c> or a seat: its provider, its endpoint and its key finder; null and why when it has none.</summary>
    public static ModelQuery? QueryFor(string section, MasterConfig config, out SeatConfig? seat, out string why)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(section);
        seat = null;
        why = string.Empty;
        string provider, endpoint, keyVariable, workspace;

        // "ai" is the main model's former name; a script that still says so means main.
        if (section is Store.ChatStores.MainSeat or Store.ChatStores.FormerMainSeat)
        {
            section = "main";
            (provider, endpoint, keyVariable, workspace) = (config.MainProvider, config.MainEndpoint, config.MainKeyVariable, config.MainWorkspace);
        }
        else if (config.Seat(section) is { } found)
        {
            seat = found;
            (provider, endpoint, keyVariable, workspace) = (found.Provider, found.Endpoint, found.KeyVariable, found.Workspace);
        }
        else
        {
            why = $"no seat '{section}'. It takes main or a seat id ('diptick seats' lists them).";
            return null;
        }

        if (provider.Length == 0)
        {
            why = section == "main" ? "no model is set up yet; 'diptick setup', or Settings, Models in the window, sets one up." : $"seat '{section}' has no provider yet.";
            return null;
        }

        if (Engines.Endpoint(provider, endpoint) is not { } uri || Engines.KeysFor(section, provider, endpoint, keyVariable) is not { } keys)
        {
            why = $"{provider} needs {section}.endpoint: its address. 'diptick providers show {provider}' says what it takes.";
            return null;
        }

        if (Engines.EndpointProblem(provider, endpoint) is { } wrongPlace)
        {
            why = $"{section}.endpoint {wrongPlace}.";
            return null;
        }

        return new ModelQuery(section, provider, uri, keys, workspace);
    }

    public async Task<IReadOnlyList<ModelInfo>> ListAsync(ModelQuery query, Gate gate, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(gate);
        var entry = Providers.ProviderCatalogue.Find(query.Provider);

        // A provider with no list to ask: the IDs its maker publishes, with nothing sent anywhere; or
        // nothing, and the person types the name.
        if (entry is { Models: Providers.ModelSource.Fixed })
        {
            return Fixed(entry);
        }

        if (entry is { Models: Providers.ModelSource.Typed })
        {
            throw new ChatException($"{entry.Name} has no list of models to ask; type the model's name{(entry.ModelsPage is { } page ? $" ({page} lists them)" : string.Empty)}.");
        }

        var anthropic = entry is { Shape: Providers.ProviderShape.Anthropic };
        var gemini = entry is { Shape: Providers.ProviderShape.Gemini };
        var root = query.Endpoint.AbsoluteUri.TrimEnd('/');
        var target = anthropic ? root + "/v1/models" : root + "/models";
        if (!gate.Request("models", Capability.ModelCall, target))
        {
            throw new ChatException("The harness did not allow this call (model.call). 'diptick audit' says why.");
        }

        var key = await query.Keys.TakeAsync(cancel).ConfigureAwait(false);
        return anthropic ? await FromAnthropicAsync(root, key!, query.Workspace, cancel).ConfigureAwait(false)
            : gemini ? await FromGeminiAsync(new Uri(target), key, cancel).ConfigureAwait(false)
            : await FromOpenAiShapeAsync(new Uri(target), query.Keys.Scheme, key, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Google's list (its API reference, <c>models.list</c>): <c>GET {base}/models</c> with the key in
    /// <c>x-goog-api-key</c>, pages of <c>models[]</c> {name <c>models/…</c>, displayName, inputTokenLimit,
    /// supportedGenerationMethods} joined by <c>nextPageToken</c>. The <c>models/</c> is dropped from each
    /// id, and a model whose methods are listed without <c>generateContent</c> cannot chat, so it is left out.
    /// </summary>
    private static async Task<List<ModelInfo>> FromGeminiAsync(Uri uri, string? key, CancellationToken cancel)
    {
        using var http = OpenAiCompatibleEngine.NewClient();
        var models = new List<ModelInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var token = string.Empty;
        for (var pages = 0; pages < MaxPages; pages++)
        {
            var address = token.Length == 0 ? new Uri(uri.AbsoluteUri + "?pageSize=1000") : new Uri(uri.AbsoluteUri + "?pageSize=1000&pageToken=" + Uri.EscapeDataString(token));
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (key is not null)
            {
                request.Headers.Add("x-goog-api-key", key);
            }

            byte[] body;
            try
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new ChatException(await OpenAiCompatibleEngine.Refusal(uri.Host, response, cancel).ConfigureAwait(false));
                }

                var stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
                await using var streamScope = stream.ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk, cancel).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaxBody)
                    {
                        throw new ChatException($"{uri.Host}'s list of models is larger than {MaxBody / (1024 * 1024)} MiB; not read.");
                    }

                    await buffer.WriteAsync(chunk.AsMemory(0, read), cancel).ConfigureAwait(false);
                }

                body = buffer.ToArray();
            }
            catch (HttpRequestException ex)
            {
                throw new ChatException($"Could not reach {uri.Host}: {OpenAiCompatibleEngine.Clip(ex.Message)}", ex);
            }

            token = ParseGemini(body, uri.Host, models, seen);
            if (token.Length == 0 || models.Count >= MaxModels)
            {
                break;
            }
        }

        return Order(models);
    }

    /// <summary>One page of Google's list added to <paramref name="models"/>; returns the next page's token, or empty.</summary>
    /// <exception cref="ChatException">Not JSON, or no <c>models</c> list.</exception>
    internal static string ParseGemini(ReadOnlyMemory<byte> body, string host, List<ModelInfo> models, HashSet<string> seen)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(seen);
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("models", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                throw new ChatException($"{host} answered, but not with a list of models.");
            }

            foreach (var item in list.EnumerateArray())
            {
                if (models.Count >= MaxModels || item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (item.TryGetProperty("supportedGenerationMethods", out var methods) && methods.ValueKind == JsonValueKind.Array
                    && !methods.EnumerateArray().Any(m => m.ValueKind == JsonValueKind.String && m.GetString() is "generateContent" or "streamGenerateContent"))
                {
                    continue;
                }

                var id = GeminiEngine.Bare(name.GetString()!);
                if (!UsableId(id) || !seen.Add(id))
                {
                    continue;
                }

                var display = item.TryGetProperty("displayName", out var shown) && shown.ValueKind == JsonValueKind.String ? Clip(shown.GetString()) : string.Empty;
                var context = item.TryGetProperty("inputTokenLimit", out var limit) && limit.ValueKind == JsonValueKind.Number && limit.TryGetInt32(out var tokens) && tokens > 0 ? tokens : (int?)null;
                models.Add(new ModelInfo(id, display, null, context));
            }

            return root.TryGetProperty("nextPageToken", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() ?? string.Empty : string.Empty;
        }
        catch (JsonException ex)
        {
            throw new ChatException($"{host} answered, but not with JSON.", ex);
        }
    }

    /// <summary>The model IDs a provider's maker publishes, as the catalogue carries them, in its order.</summary>
    internal static IReadOnlyList<ModelInfo> Fixed(Providers.Provider entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return [.. entry.ModelIds.Select(id => new ModelInfo(id, string.Empty, null, null))];
    }

    /// <summary>
    /// The models in an OpenAI-shaped list: <c>data[].id</c>, with <c>name</c> or <c>display_name</c>,
    /// <c>created</c> (Unix seconds) or <c>created_at</c>, and <c>context_length</c> when given.
    /// Newest first, then by id; duplicates and unusable ids dropped.
    /// </summary>
    /// <exception cref="ChatException">Not JSON, or no <c>data</c> list.</exception>
    internal static List<ModelInfo> Parse(ReadOnlyMemory<byte> body, string host)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                throw new ChatException($"{host} answered, but not with a list of models.");
            }

            var models = new List<ModelInfo>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in data.EnumerateArray())
            {
                if (models.Count >= MaxModels)
                {
                    break;
                }

                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var id = idElement.GetString()!;
                if (!UsableId(id) || !seen.Add(id))
                {
                    continue;
                }

                models.Add(new ModelInfo(id, NameOf(item), CreatedOf(item), ContextOf(item)));
            }

            return Order(models);
        }
        catch (JsonException ex)
        {
            throw new ChatException($"{host} answered, but not with JSON.", ex);
        }
    }

    /// <summary>Plain printable ASCII, no spaces, 1 to <see cref="MaxId"/> characters: what every platform's ids are.</summary>
    internal static bool UsableId(string id) => id.Length is > 0 and <= MaxId && id.All(c => c is >= '!' and <= '~');

    /// <summary>Newest first where the platform gave a date, those without one after; then by id.</summary>
    internal static List<ModelInfo> Order(IEnumerable<ModelInfo> models) =>
        models.OrderByDescending(m => m.Created ?? DateTimeOffset.MinValue).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();

    private static async Task<List<ModelInfo>> FromOpenAiShapeAsync(Uri uri, Providers.KeyScheme scheme, string? key, CancellationToken cancel)
    {
        using var http = OpenAiCompatibleEngine.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        OpenAiCompatibleEngine.Sign(request, scheme, key);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ChatException($"Could not reach {uri.Host}: {OpenAiCompatibleEngine.Clip(ex.Message)}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new ChatException(await OpenAiCompatibleEngine.Refusal(uri.Host, response, cancel).ConfigureAwait(false));
            }

            if (response.Content.Headers.ContentLength is > MaxBody)
            {
                throw new ChatException($"{uri.Host}'s list of models is larger than {MaxBody / (1024 * 1024)} MiB; not read.");
            }

            var stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            await using var streamScope = stream.ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancel).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxBody)
                {
                    throw new ChatException($"{uri.Host}'s list of models is larger than {MaxBody / (1024 * 1024)} MiB; not read.");
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancel).ConfigureAwait(false);
            }

            return Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), uri.Host);
        }
    }

    private static async Task<List<ModelInfo>> FromAnthropicAsync(string root, string key, string workspace, CancellationToken cancel)
    {
        var host = new Uri(root).Host;
        using var http = OpenAiCompatibleEngine.NewClient();
        using var client = new AnthropicClient { ApiKey = key, AuthToken = null, BaseUrl = root, HttpClient = http };
        var models = new List<ModelInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            // Claude Platform on AWS answers only for a named workspace; Anthropic's API, for one when the key has several.
            var service = workspace.Length == 0
                ? client.Models
                : client.Models.WithOptions(o => o with { ExtraHeaders = new Dictionary<string, string>(StringComparer.Ordinal) { ["anthropic-workspace-id"] = workspace } });
            var page = await service.List(new ModelListParams { Limit = 1000 }, cancel).ConfigureAwait(false);
            for (var pages = 1; ; pages++)
            {
                foreach (var model in page.Items)
                {
                    if (models.Count < MaxModels && UsableId(model.ID) && seen.Add(model.ID))
                    {
                        models.Add(new ModelInfo(model.ID, Clip(model.DisplayName), model.CreatedAt, Context: null));
                    }
                }

                if (pages >= MaxPages || models.Count >= MaxModels || !page.HasNext())
                {
                    break;
                }

                page = await page.Next(cancel).ConfigureAwait(false);
            }
        }
        catch (Anthropic.Exceptions.AnthropicException ex)
        {
            throw new ChatException(AnthropicEngine.Failure(host, ex), ex);
        }

        return Order(models);
    }

    private static readonly string[] NameFields = ["name", "display_name"];

    private static string NameOf(JsonElement item)
    {
        foreach (var field in NameFields)
        {
            if (item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return Clip(value.GetString());
            }
        }

        return string.Empty;
    }

    private static DateTimeOffset? CreatedOf(JsonElement item)
    {
        // Unix seconds (OpenAI, OpenRouter, Ollama), or an RFC 3339 string (Anthropic's raw list).
        if (item.TryGetProperty("created", out var created) && created.ValueKind == JsonValueKind.Number
            && created.TryGetInt64(out var seconds) && seconds is > 0 and < 253402300800)
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        return item.TryGetProperty("created_at", out var at) && at.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
            ? when
            : null;
    }

    private static int? ContextOf(JsonElement item) =>
        item.TryGetProperty("context_length", out var context) && context.ValueKind == JsonValueKind.Number
        && context.TryGetInt32(out var tokens) && tokens > 0
            ? tokens
            : null;

    private static string Clip(string? name)
    {
        var line = SafeText.Line(name ?? string.Empty).Trim();
        return line.Length <= MaxName ? line : line[..MaxName] + "…";
    }
}
