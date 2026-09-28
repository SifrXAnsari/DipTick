using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DipTick.Models.Chat;
using DipTick.Models.Keys;

namespace DipTick.Models.Research;

/// <summary>One result of a search: its number among the results, its address, its title, the passages given with it, and its date where the service knows one.</summary>
internal sealed record SearchHit(int Number, string Address, string Title, IReadOnlyList<string> Passages, string Date);

/// <summary>What one search found: the service, the words, and the results in the service's order.</summary>
internal sealed record SearchAnswer(string Service, string Query, IReadOnlyList<SearchHit> Hits);

/// <summary>Why a search found nothing it could give, in words for the model and the chat.</summary>
internal sealed class SearchException : Exception
{
    public SearchException()
    {
    }

    public SearchException(string message)
        : base(message)
    {
    }

    public SearchException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// The search service agents search the web through: the one the person
/// chose (<c>research.search</c>), with the person's own key; DipTick has none (no account model).
/// Brave's LLM Context endpoint, which Brave recommends for agents ("Looking to power agents or
/// chatbots? Use the LLM Context endpoint instead"), gives each result's passages; a SearXNG the person
/// runs gives its results as JSON when <c>json</c> is among its <c>search.formats</c>. Both are called
/// like a provider: no redirect is followed, and Brave's key goes only to Brave's host.
/// </summary>
internal static class SearchService
{
    /// <summary>Brave's address, unless <c>research.braveUrl</c> names another.</summary>
    public const string BraveAddress = "https://api.search.brave.com";

    /// <summary>How many results a search asks for: Brave's default, and its own page's size.</summary>
    public const int Results = 20;

    /// <summary>How many tokens of passages Brave gives: its default, "Standard queries".</summary>
    public const int Tokens = 8192;

    /// <summary>The most words Brave takes (its reference: 1 to 600 characters, at most 75 words).</summary>
    public const int MostCharacters = 600;

    public const int MostWords = 75;

    /// <summary>The most of a service's answer read: 8,192 tokens of passages are well under it.</summary>
    private const int MostAnswer = 2 * 1024 * 1024;

    /// <summary>How long a search may take; Brave's own advice is a 30-second timeout.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    /// <summary>The service's name for a person: Brave, SearXNG, or none.</summary>
    public static string Name(MasterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.ResearchSearch switch
        {
            "brave" => "Brave",
            "searxng" => "SearXNG",
            _ => "none",
        };
    }

    /// <summary>Whether a search service is set up and has what it needs: Brave its key, SearXNG its address.</summary>
    public static bool Ready(MasterConfig config) => Problem(config) is null;

    /// <summary>What the search service still needs, in words, or null when it is ready.</summary>
    public static string? Problem(MasterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        switch (config.ResearchSearch)
        {
            case "brave":
                var finding = Keys(config).Describe();
                return finding.Usable ? null : $"Brave needs your key: 'diptick key set research', or research.keyVariable. {finding.Problem}".Trim();
            case "searxng":
                return config.ResearchSearxngUrl.Length > 0 ? null : "SearXNG needs its address: research.searxngUrl.";
            default:
                return "No search service is set: research.search is none. Brave (with your key) or a SearXNG you run.";
        }
    }

    /// <summary>The hosts the search service is reached at, for the network check.</summary>
    public static IEnumerable<Uri> Hosts(MasterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (Address(config) is { } address)
        {
            yield return address;
        }
    }

    /// <summary>The service's base address, or null when none is set.</summary>
    public static Uri? Address(MasterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var text = config.ResearchSearch switch
        {
            "brave" => config.ResearchBraveUrl.Length > 0 ? config.ResearchBraveUrl : BraveAddress,
            "searxng" => config.ResearchSearxngUrl,
            _ => string.Empty,
        };
        return text.Length > 0 && Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri : null;
    }

    /// <summary>Where Brave's key comes from: <c>research.keyVariable</c>, else the key saved for Brave's host.</summary>
    public static KeyFinder Keys(MasterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var address = config.ResearchBraveUrl.Length > 0 && Uri.TryCreate(config.ResearchBraveUrl, UriKind.Absolute, out var set) ? set : new Uri(BraveAddress);
        return new KeyFinder("research", config.ResearchKeyVariable, address, alwaysNeeded: !address.IsLoopback, WindowsKeyStore.Instance, Environment.GetEnvironmentVariable);
    }

    /// <summary>Why these words cannot be searched, or null: some words, within Brave's limits.</summary>
    public static string? QueryProblem(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var words = query.Split((char[])[' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;
        return query.Trim().Length == 0 ? "give the words to search for"
            : query.Length > MostCharacters || words > MostWords ? string.Create(CultureInfo.InvariantCulture, $"a search is at most {MostCharacters} characters and {MostWords} words")
            : null;
    }

    /// <summary>Searches through the service the person chose. <paramref name="http"/> sends, a provider's client by default.</summary>
    /// <exception cref="SearchException">The search could not be done, and why.</exception>
    public static async Task<SearchAnswer> SearchAsync(MasterConfig config, string query, CancellationToken cancel, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(query);
        if (Problem(config) is { } missing)
        {
            throw new SearchException(missing);
        }

        if (QueryProblem(query) is { } bad)
        {
            throw new SearchException(bad);
        }

        if (http is null)
        {
            using var client = OpenAiCompatibleEngine.NewClient();
            return await SearchWithAsync(config, query.Trim(), client, cancel).ConfigureAwait(false);
        }

        return await SearchWithAsync(config, query.Trim(), http, cancel).ConfigureAwait(false);
    }

    private static async Task<SearchAnswer> SearchWithAsync(MasterConfig config, string query, HttpClient client, CancellationToken cancel)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(Limit);
            return config.ResearchSearch == "brave"
                ? await BraveAsync(config, query, client, limit.Token).ConfigureAwait(false)
                : await SearxngAsync(config, query, client, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new SearchException(string.Create(CultureInfo.InvariantCulture, $"{Name(config)} gave no answer within {Limit.TotalSeconds} s"));
        }
        catch (HttpRequestException ex)
        {
            throw new SearchException($"could not reach {Name(config)}: {SafeText.Line(ex.InnerException?.Message ?? ex.Message)}", ex);
        }
        catch (JsonException ex)
        {
            throw new SearchException($"{Name(config)}'s answer was not the JSON it documents", ex);
        }
    }

    /// <summary>
    /// Brave's LLM Context (its reference): <c>GET /res/v1/llm/context?q=…</c> with the key in
    /// <c>X-Subscription-Token</c>; the answer's <c>grounding.generic</c> holds each page's address,
    /// title and passages, and <c>sources</c> each page's date as <c>age[1]</c>, YYYY-MM-DD.
    /// </summary>
    private static async Task<SearchAnswer> BraveAsync(MasterConfig config, string query, HttpClient http, CancellationToken cancel)
    {
        var baseAddress = Address(config)!.AbsoluteUri.TrimEnd('/');
        var address = new Uri(string.Create(CultureInfo.InvariantCulture, $"{baseAddress}/res/v1/llm/context?q={Uri.EscapeDataString(query)}&count={Results}&maximum_number_of_tokens={Tokens}"));
        var key = Keys(config).Take();
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (key is not null)
        {
            request.Headers.Add("X-Subscription-Token", key);
        }

        using var doc = await AnswerAsync(http, request, "Brave", cancel).ConfigureAwait(false);
        var root = doc.RootElement;
        var dates = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Object)
        {
            foreach (var source in sources.EnumerateObject())
            {
                if (source.Value.ValueKind == JsonValueKind.Object && source.Value.TryGetProperty("age", out var age) && age.ValueKind == JsonValueKind.Array && age.GetArrayLength() > 1 && age[1].ValueKind == JsonValueKind.String)
                {
                    dates[source.Name] = age[1].GetString() ?? string.Empty;
                }
            }
        }

        var hits = new List<SearchHit>();
        if (root.TryGetProperty("grounding", out var grounding) && grounding.ValueKind == JsonValueKind.Object
            && grounding.TryGetProperty("generic", out var generic) && generic.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in generic.EnumerateArray())
            {
                if (Text(item, "url") is not { Length: > 0 } url || Tools.Seen.Normal(url) is null)
                {
                    continue;
                }

                var passages = item.TryGetProperty("snippets", out var snippets) && snippets.ValueKind == JsonValueKind.Array
                    ? snippets.EnumerateArray().Select(s => s.ValueKind == JsonValueKind.String ? s.GetString() ?? string.Empty : s.GetRawText()).Where(s => s.Length > 0).ToList()
                    : [];
                hits.Add(new SearchHit(hits.Count + 1, url, Text(item, "title") ?? url, passages, dates.GetValueOrDefault(url, string.Empty)));
            }
        }

        return new SearchAnswer("Brave", query, hits);
    }

    /// <summary>
    /// SearXNG's search API: <c>GET /search?q=…&amp;format=json</c>; its answer's <c>results</c> each have
    /// <c>url</c>, <c>title</c>, <c>content</c> and, where the engine knows it, <c>publishedDate</c>.
    /// A 403 means JSON is not switched on in its settings.
    /// </summary>
    private static async Task<SearchAnswer> SearxngAsync(MasterConfig config, string query, HttpClient http, CancellationToken cancel)
    {
        var baseAddress = Address(config)!.AbsoluteUri.TrimEnd('/');
        var address = new Uri($"{baseAddress}/search?q={Uri.EscapeDataString(query)}&format=json");
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var doc = await AnswerAsync(http, request, "SearXNG", cancel).ConfigureAwait(false);
        var hits = new List<SearchHit>();
        if (doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in results.EnumerateArray())
            {
                if (hits.Count >= Results)
                {
                    break;
                }

                if (Text(item, "url") is not { Length: > 0 } url || Tools.Seen.Normal(url) is null)
                {
                    continue;
                }

                var content = Text(item, "content") ?? string.Empty;
                var date = Text(item, "publishedDate") is { Length: >= 10 } published ? published[..10] : string.Empty;
                hits.Add(new SearchHit(hits.Count + 1, url, Text(item, "title") ?? url, content.Length > 0 ? [content] : [], date));
            }
        }

        return new SearchAnswer("SearXNG", query, hits);
    }

    /// <summary>The service's answer as JSON, capped; a refusal made words (SearXNG's 403 explained).</summary>
    private static async Task<JsonDocument> AnswerAsync(HttpClient http, HttpRequestMessage request, string service, CancellationToken cancel)
    {
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            if (service == "SearXNG" && status == 403)
            {
                throw new SearchException("SearXNG refused JSON (403): add json to search.formats in its settings.yml, then restart it.");
            }

            throw new SearchException(await Refusal(service, response, cancel).ConfigureAwait(false));
        }

        var body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        await using var bodyScope = body.ConfigureAwait(false);
        var bytes = new byte[MostAnswer];
        var read = await body.ReadAtLeastAsync(bytes, MostAnswer, throwOnEndOfStream: false, cancel).ConfigureAwait(false);
        if (read == MostAnswer && body.ReadByte() >= 0)
        {
            throw new SearchException($"{service}'s answer was longer than DipTick reads");
        }

        return JsonDocument.Parse(bytes.AsMemory(0, read));
    }

    /// <summary>A refusal in one line: the status and the service's own words, Brave's <c>error.detail</c> among them.</summary>
    private static async Task<string> Refusal(string service, HttpResponseMessage response, CancellationToken cancel)
    {
        var status = (int)response.StatusCode;
        var text = string.Empty;
        try
        {
            var body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            await using var bodyScope = body.ConfigureAwait(false);
            var bytes = new byte[4096];
            var read = await body.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, cancel).ConfigureAwait(false);
            text = Encoding.UTF8.GetString(bytes, 0, read);
        }
        catch (IOException)
        {
        }

        string? detail = null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                detail = Text(error, "detail") ?? Text(error, "message");
            }
        }
        catch (JsonException)
        {
        }

        var why = status == 401 || status == 403 ? " (is the key right?)" : status == 429 ? " (too many searches for now)" : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"{service} refused ({status}){why}: {OpenAiCompatibleEngine.Clip(detail ?? text)}");
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
