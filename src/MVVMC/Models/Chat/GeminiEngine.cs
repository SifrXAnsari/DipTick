using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DipTick.Models.Harness;

namespace DipTick.Models.Chat;

/// <summary>
/// Google's own Gemini API, <c>generateContent</c>, streamed: the Gemini
/// API with a key from Google AI Studio, Vertex AI in express mode with its API key, and gemma.cpp's
/// server on this PC, all three of which speak it.
/// </summary>
/// <remarks>
/// <para>
/// From Google's API reference (ai.google.dev/api/generate-content): <c>POST
/// {base}/models/{model}:streamGenerateContent?alt=sse</c>, answered as server-sent events, each a
/// <c>GenerateContentResponse</c> with <c>candidates[0].content.parts</c>, a <c>finishReason</c> on the
/// last, <c>groundingMetadata</c> where Google Search grounded it, and <c>usageMetadata</c>. The key goes
/// in the <c>x-goog-api-key</c> header, never in the address (Google Cloud: the query parameter exposes
/// "your key to theft through URL scans"). No redirect is followed.
/// </para>
/// <para>
/// Tools are <c>functionDeclarations</c> with their JSON Schema as <c>parametersJsonSchema</c>; a call
/// comes back as a <c>functionCall</c> part and its result goes as a <c>functionResponse</c> on the
/// person's side. A model's parts carry thought signatures that must come back unchanged, so when a
/// turn goes on after DipTick's tools the parts are handed back as they came (<see cref="EchoPart"/>).
/// Google Search (and URL context, on the Gemini API) is asked for where the round gives it
/// (<see cref="ChatRequest.ProviderTools"/>): Gemini 3 and later take it beside function calling.
/// </para>
/// </remarks>
internal sealed class GeminiEngine : IChatEngine, IDisposable
{
    /// <summary>The shape an <see cref="EchoPart"/> of this engine is in.</summary>
    public const string Shape = "gemini";

    private readonly HttpClient _http;
    private readonly string _base;
    private readonly string _model;
    private readonly Keys.KeyFinder _keys;
    private readonly bool _briefInFirstTurn;

    /// <param name="provider">The provider's id, for the engine's name.</param>
    /// <param name="baseUri">Where the API is: <c>…/v1beta</c> for the Gemini API and gemma.cpp, <c>…/v1/publishers/google</c> for Vertex AI express mode.</param>
    /// <param name="briefInFirstTurn">For a server with no system instruction (gemma.cpp, whose models have no system turn): the briefing goes first in the person's first message.</param>
    /// <param name="http">The client to send with; DipTick's own, which follows no redirect, by default.</param>
    public GeminiEngine(string provider, Uri baseUri, string model, Keys.KeyFinder keys, bool briefInFirstTurn = false, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentNullException.ThrowIfNull(keys);
        _base = baseUri.AbsoluteUri.TrimEnd('/');
        _model = Bare(model);
        _keys = keys;
        _briefInFirstTurn = briefInFirstTurn;
        Name = $"{provider} {_model}";
        _http = http ?? OpenAiCompatibleEngine.NewClient();
    }

    public string Name { get; }

    public void Dispose() => _http.Dispose();

    /// <summary>Where the gate and the audit log say the call goes.</summary>
    private string Target => $"{_base}/models/{Uri.EscapeDataString(_model)}:streamGenerateContent";

    /// <summary>A model's name without the <c>models/</c> Google's lists put before it.</summary>
    internal static string Bare(string model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.StartsWith("models/", StringComparison.Ordinal) ? model["models/".Length..] : model;
    }

    public async IAsyncEnumerable<EnginePart> StreamAsync(ChatRequest request, Gate gate, [EnumeratorCancellation] CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(gate);
        if (!(request.Continues ? gate.Continue(request.Verb, Capability.ModelCall, Target) : gate.Request(request.Verb, Capability.ModelCall, Target)))
        {
            throw new ChatException("The harness did not allow this call (model.call). 'diptick audit' says why.");
        }

        var key = await _keys.TakeAsync(cancel).ConfigureAwait(false);
        var address = new Uri(Target + "?alt=sse");
        using var message = new HttpRequestMessage(HttpMethod.Post, address)
        {
            Content = new StringContent(Body(request), Encoding.UTF8, "application/json"),
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (key is not null)
        {
            message.Headers.Add("x-goog-api-key", key);
        }

        HttpResponseMessage response;
        string? failure = null;
        try
        {
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            response = null!;
            failure = $"Could not reach {address.Host}: {ex.Message}";
        }

        if (failure is not null)
        {
            throw new ChatException(failure);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new ChatException(await OpenAiCompatibleEngine.Refusal(address.Host, response, cancel).ConfigureAwait(false));
            }

            var reading = new GeminiReading();
            var total = 0;
            var body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            await using var bodyScope = body.ConfigureAwait(false);
            await foreach (var data in EventStream.ReadAsync(body, cancel).ConfigureAwait(false))
            {
                // gemma.cpp ends its stream so; Google's own ends after the last chunk.
                if (data == "[DONE]")
                {
                    reading.Whole = true;
                    break;
                }

                foreach (var part in reading.Chunk(data))
                {
                    if (part is TextPart text)
                    {
                        total += text.Text.Length;
                        if (total > OpenAiCompatibleEngine.MaxAnswer)
                        {
                            throw new ChatException("Stopped: the answer passed 1 MiB.");
                        }
                    }

                    yield return part;
                }
            }

            foreach (var part in reading.Grounding())
            {
                yield return part;
            }

            var end = reading.Finish(address.Host);
            if (end.End == AnswerEnd.Tools)
            {
                yield return new EchoPart(Shape, reading.Parts);
                foreach (var call in reading.Calls)
                {
                    yield return new ToolCallPart(call);
                }
            }

            yield return end;
        }
    }

    /// <summary>
    /// The request body (Google's reference): the briefing as <c>systemInstruction</c>, or first in the
    /// person's first message where the server has none; the messages as <c>contents</c>; DipTick's tools
    /// as function declarations and Google's own as their tools; the answer's cap.
    /// </summary>
    internal string Body(ChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            if (request.System.Length > 0 && !_briefInFirstTurn)
            {
                json.WriteStartObject("systemInstruction");
                json.WriteStartArray("parts");
                json.WriteStartObject();
                json.WriteString("text", request.System);
                json.WriteEndObject();
                json.WriteEndArray();
                json.WriteEndObject();
            }

            json.WriteStartArray("contents");
            var first = true;
            foreach (var line in request.Messages.Where(m => !m.IsEmpty))
            {
                var brief = first && _briefInFirstTurn && line.Role == WireRole.User && request.System.Length > 0 ? request.System : null;
                WriteContent(json, line, brief);
                first = false;
            }

            json.WriteEndArray();
            if (request.Tools.Count > 0 || request.ProviderTools.Count > 0)
            {
                json.WriteStartArray("tools");
                if (request.Tools.Count > 0)
                {
                    json.WriteStartObject();
                    json.WriteStartArray("functionDeclarations");
                    foreach (var tool in request.Tools)
                    {
                        json.WriteStartObject();
                        json.WriteString("name", tool.Name);
                        json.WriteString("description", tool.Description);
                        json.WritePropertyName("parametersJsonSchema");
                        tool.Parameters.WriteTo(json);
                        json.WriteEndObject();
                    }

                    json.WriteEndArray();
                    json.WriteEndObject();
                }

                if (request.ProviderTools.Contains(Research.ProviderResearch.Search))
                {
                    json.WriteStartObject();
                    json.WriteStartObject("googleSearch");
                    json.WriteEndObject();
                    json.WriteEndObject();
                }

                if (request.ProviderTools.Contains(Research.ProviderResearch.Fetch))
                {
                    json.WriteStartObject();
                    json.WriteStartObject("urlContext");
                    json.WriteEndObject();
                    json.WriteEndObject();
                }

                json.WriteEndArray();
            }

            if (request.MaxTokens > 0)
            {
                json.WriteStartObject("generationConfig");
                json.WriteNumber("maxOutputTokens", request.MaxTokens);
                json.WriteEndObject();
            }

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// One message as a <c>Content</c>: the model's parts as they came where it has them; else its words
    /// and calls; the person's side with each call's result as a <c>functionResponse</c>, then files
    /// (images and PDFs inline, text under its name), then the words.
    /// </summary>
    private static void WriteContent(Utf8JsonWriter json, WireMessage line, string? brief)
    {
        json.WriteStartObject();
        json.WriteString("role", line.Role == WireRole.User ? "user" : "model");
        json.WriteStartArray("parts");
        if (line.Role == WireRole.Assistant && line.Blocks.Count > 0 && line.BlocksFrom == Shape)
        {
            foreach (var part in line.Blocks)
            {
                part.WriteTo(json);
            }
        }
        else if (line.Role == WireRole.Assistant)
        {
            if (line.Text.Length > 0)
            {
                WriteText(json, line.Text);
            }

            foreach (var call in line.Calls)
            {
                json.WriteStartObject();
                json.WriteStartObject("functionCall");
                json.WriteString("id", call.Id);
                json.WriteString("name", call.Name);
                json.WritePropertyName("args");
                Arguments(call.Arguments).WriteTo(json);
                json.WriteEndObject();
                json.WriteEndObject();
            }
        }
        else
        {
            foreach (var result in line.Results)
            {
                json.WriteStartObject();
                json.WriteStartObject("functionResponse");
                json.WriteString("id", result.CallId);
                json.WriteString("name", result.Name);
                json.WriteStartObject("response");
                json.WriteString(result.IsError ? "error" : "result", result.Content);
                json.WriteEndObject();
                json.WriteEndObject();
                json.WriteEndObject();
            }

            // A file a call brought back (a fetched PDF) goes inline beside the results, as Gemini reads PDFs.
            foreach (var file in line.Results.Select(r => r.File).OfType<WireFile>())
            {
                json.WriteStartObject();
                json.WriteStartObject("inlineData");
                json.WriteString("mimeType", file.MediaType);
                json.WriteString("data", Convert.ToBase64String(file.Bytes));
                json.WriteEndObject();
                json.WriteEndObject();
            }

            foreach (var file in line.Files)
            {
                if (file.MediaType.StartsWith("image/", StringComparison.Ordinal) || file.MediaType == "application/pdf")
                {
                    json.WriteStartObject();
                    json.WriteStartObject("inlineData");
                    json.WriteString("mimeType", file.MediaType);
                    json.WriteString("data", Convert.ToBase64String(file.Bytes));
                    json.WriteEndObject();
                    json.WriteEndObject();
                }
                else
                {
                    WriteText(json, AnthropicEngine.FileText(file));
                }
            }

            var words = brief is null ? line.Text : line.Text.Length > 0 ? $"{brief}\n\n{line.Text}" : brief;
            if (words.Length > 0)
            {
                WriteText(json, words);
            }
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void WriteText(Utf8JsonWriter json, string text)
    {
        json.WriteStartObject();
        json.WriteString("text", text);
        json.WriteEndObject();
    }

    /// <summary>A call's arguments as the object Google takes: the model's JSON object, or an empty one.</summary>
    private static JsonElement Arguments(string arguments)
    {
        try
        {
            using var doc = JsonDocument.Parse(arguments.Trim().Length == 0 ? "{}" : arguments);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                return doc.RootElement.Clone();
            }
        }
        catch (JsonException)
        {
        }

        using var empty = JsonDocument.Parse("{}");
        return empty.RootElement.Clone();
    }
}

/// <summary>
/// One streamed answer from Google's <c>generateContent</c>, read chunk by chunk: its words, the parts as
/// they came (to send back), the function calls, how it ended, what it used, and what Google Search
/// and URL context grounded it in.
/// </summary>
internal sealed class GeminiReading
{
    private readonly List<JsonElement> _parts = [];
    private readonly List<ToolCall> _calls = [];
    private readonly List<(string Address, string Title)> _chunks = [];
    private readonly List<(string Text, IReadOnlyList<int> Chunks)> _supports = [];
    private readonly List<string> _queries = [];
    private readonly List<(string Address, string Status)> _urls = [];
    private string? _finish;
    private string _finishMessage = string.Empty;
    private string? _blocked;
    private bool _anyText;

    /// <summary>Whether the server said the stream ended (gemma.cpp's <c>[DONE]</c>).</summary>
    public bool Whole { get; set; }

    /// <summary>The model's parts as they came, in order.</summary>
    public IReadOnlyList<JsonElement> Parts => _parts;

    /// <summary>The function calls the model made.</summary>
    public IReadOnlyList<ToolCall> Calls => _calls;

    /// <summary>What a chunk brings: words to show, and what it used; its parts, calls, finish and grounding kept.</summary>
    public IEnumerable<EnginePart> Chunk(string data)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(data);
        }
        catch (JsonException)
        {
            throw new ChatException("The server sent a chunk that is not JSON.");
        }

        var parts = new List<EnginePart>();
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return parts;
            }

            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                throw new ChatException($"The server reported an error part-way through: {OpenAiCompatibleEngine.Clip(Str(error, "message"))}");
            }

            if (root.TryGetProperty("promptFeedback", out var feedback) && Str(feedback, "blockReason") is { Length: > 0 } blocked)
            {
                _blocked = blocked;
            }

            if (root.TryGetProperty("usageMetadata", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                var read = Count(usage, "promptTokenCount");
                var wrote = Count(usage, "candidatesTokenCount");
                var thought = Count(usage, "thoughtsTokenCount");
                parts.Add(new UsagePart(read, wrote is null && thought is null ? null : (wrote ?? 0) + (thought ?? 0)));
            }

            if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
            {
                return parts;
            }

            var candidate = candidates[0];
            if (candidate.ValueKind != JsonValueKind.Object)
            {
                return parts;
            }

            if (candidate.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object && content.TryGetProperty("parts", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in list.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    _parts.Add(part.Clone());
                    var thought = part.TryGetProperty("thought", out var t) && t.ValueKind == JsonValueKind.True;
                    if (!thought && part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String && text.GetString() is { Length: > 0 } words)
                    {
                        _anyText = true;
                        parts.Add(new TextPart(words));
                    }

                    if (part.TryGetProperty("functionCall", out var call) && call.ValueKind == JsonValueKind.Object)
                    {
                        var id = Str(call, "id");
                        var arguments = call.TryGetProperty("args", out var args) ? args.GetRawText() : "{}";
                        _calls.Add(new ToolCall(id.Length > 0 ? id : string.Create(CultureInfo.InvariantCulture, $"call_{_calls.Count}"), Str(call, "name"), arguments));
                    }
                }
            }

            if (Str(candidate, "finishReason") is { Length: > 0 } finish)
            {
                _finish = finish;
                _finishMessage = Str(candidate, "finishMessage");
            }

            if (candidate.TryGetProperty("groundingMetadata", out var grounding) && grounding.ValueKind == JsonValueKind.Object)
            {
                Ground(grounding);
            }

            if (candidate.TryGetProperty("urlContextMetadata", out var urls) && urls.ValueKind == JsonValueKind.Object && urls.TryGetProperty("urlMetadata", out var urlList) && urlList.ValueKind == JsonValueKind.Array)
            {
                foreach (var url in urlList.EnumerateArray())
                {
                    if (Str(url, "retrievedUrl") is { Length: > 0 } address)
                    {
                        _urls.Add((address, Str(url, "urlRetrievalStatus")));
                    }
                }
            }
        }

        return parts;
    }

    /// <summary>
    /// Grounding, as it streams (Google's reference): each chunk brings only grounding chunks not given
    /// before, and a support's chunk indices count across every response, so the chunks are gathered in
    /// order and the supports read against the gathered list.
    /// </summary>
    private void Ground(JsonElement grounding)
    {
        if (grounding.TryGetProperty("webSearchQueries", out var queries) && queries.ValueKind == JsonValueKind.Array)
        {
            foreach (var query in queries.EnumerateArray().Where(q => q.ValueKind == JsonValueKind.String))
            {
                var words = query.GetString() ?? string.Empty;
                if (words.Length > 0 && !_queries.Contains(words))
                {
                    _queries.Add(words);
                }
            }
        }

        if (grounding.TryGetProperty("groundingChunks", out var chunks) && chunks.ValueKind == JsonValueKind.Array)
        {
            foreach (var chunk in chunks.EnumerateArray())
            {
                var web = chunk.TryGetProperty("web", out var w) ? w : default;
                _chunks.Add((Str(web, "uri"), Str(web, "title")));
            }
        }

        if (grounding.TryGetProperty("groundingSupports", out var supports) && supports.ValueKind == JsonValueKind.Array)
        {
            foreach (var support in supports.EnumerateArray())
            {
                var text = support.TryGetProperty("segment", out var segment) ? Str(segment, "text") : string.Empty;
                var indices = support.TryGetProperty("groundingChunkIndices", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Where(i => i.TryGetInt32(out _)).Select(i => i.GetInt32()).ToList()
                    : [];
                _supports.Add((text, indices));
            }
        }
    }

    /// <summary>
    /// What Google's own tools did, once the stream has ended: a search row with its words and the pages
    /// it found, a fetch row for URL context, and a citation for each supported sentence and its page.
    /// </summary>
    public IEnumerable<EnginePart> Grounding()
    {
        var pages = _chunks.Where(c => c.Address.Length > 0).Select(c => new FoundPage(c.Address, c.Title, Read: false)).ToList();
        if (_queries.Count > 0 || pages.Count > 0)
        {
            var words = string.Join(" | ", _queries);
            yield return new ProviderToolPart(Research.ProviderResearch.Search, words, string.Create(CultureInfo.InvariantCulture, $"{pages.Count} source{(pages.Count == 1 ? string.Empty : "s")} from Google Search"), pages);
        }

        if (_urls.Count > 0)
        {
            var read = _urls.Count(u => u.Status == "URL_RETRIEVAL_STATUS_SUCCESS");
            yield return new ProviderToolPart(
                Research.ProviderResearch.Fetch,
                string.Join(" ", _urls.Select(u => u.Address)),
                string.Create(CultureInfo.InvariantCulture, $"{read} of {_urls.Count} page{(_urls.Count == 1 ? string.Empty : "s")} read by Google"),
                [.. _urls.Select(u => new FoundPage(u.Address, u.Address, u.Status == "URL_RETRIEVAL_STATUS_SUCCESS"))],
                Failed: read == 0);
        }

        var cited = new HashSet<(string, string)>();
        foreach (var (text, indices) in _supports)
        {
            foreach (var i in indices.Where(i => i >= 0 && i < _chunks.Count && _chunks[i].Address.Length > 0))
            {
                if (cited.Add((_chunks[i].Address, text)))
                {
                    yield return new CitationPart(_chunks[i].Address, _chunks[i].Title, text);
                }
            }
        }
    }

    /// <summary>
    /// How the answer ended (Google's <c>FinishReason</c>): a blocked question refused; STOP whole, or
    /// waiting for its calls' results when it made some; MAX_TOKENS cut; the safety and policy reasons
    /// refused; a malformed or unexpected call, too many calls or a missing signature cut, with why; no
    /// reason at all whole only where the server said the stream ended.
    /// </summary>
    public FinishPart Finish(string host)
    {
        if (_blocked is not null)
        {
            return new FinishPart(AnswerEnd.Refused, $"{host} blocked the question ({_blocked}).");
        }

        var why = _finishMessage.Length > 0 ? $": {OpenAiCompatibleEngine.Clip(_finishMessage)}" : string.Empty;
        return _finish switch
        {
            "STOP" when _calls.Count > 0 => new FinishPart(AnswerEnd.Tools),
            "STOP" => new FinishPart(AnswerEnd.Done),
            null when _calls.Count > 0 && Whole => new FinishPart(AnswerEnd.Tools),
            null when Whole => new FinishPart(AnswerEnd.Done),
            null => new FinishPart(AnswerEnd.Cut, "the answer ended before the server said it was whole"),
            "MAX_TOKENS" => new FinishPart(AnswerEnd.Cut, _anyText ? "it reached its token cap" : "thinking used the whole token cap before any answer"),
            "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" or "LANGUAGE" or "IMAGE_SAFETY" or "IMAGE_PROHIBITED_CONTENT" or "IMAGE_RECITATION"
                => new FinishPart(AnswerEnd.Refused, $"{host} stopped the answer ({_finish}){why}"),
            "MALFORMED_FUNCTION_CALL" => new FinishPart(AnswerEnd.Cut, $"it wrote a tool call {host} could not read{why}"),
            "UNEXPECTED_TOOL_CALL" => new FinishPart(AnswerEnd.Cut, "it called a tool it was not offered"),
            "TOO_MANY_TOOL_CALLS" => new FinishPart(AnswerEnd.Cut, "it called too many tools in a row"),
            "MISSING_THOUGHT_SIGNATURE" => new FinishPart(AnswerEnd.Cut, "a thought signature was missing from what was sent back"),
            _ => new FinishPart(AnswerEnd.Cut, $"it stopped for a reason DipTick does not know ({OpenAiCompatibleEngine.Clip(_finish)}){why}"),
        };
    }

    private static int? Count(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) && count >= 0 ? count : null;

    private static string Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
}
