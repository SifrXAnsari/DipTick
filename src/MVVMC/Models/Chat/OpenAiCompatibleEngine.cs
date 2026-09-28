using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DipTick.Models.Harness;

namespace DipTick.Models.Chat;

/// <summary>
/// Any server speaking the OpenAI-compatible chat completions API, streamed: Ollama and LM Studio
/// on this machine, and the many hosted platforms that offer the same shape.
/// </summary>
/// <remarks>
/// <para>
/// Wire format, from OpenRouter's streaming reference (openrouter.ai/docs/api/reference/streaming):
/// <c>POST {base}/chat/completions</c> with <c>{"model", "messages":[{"role","content"}], "stream": true}</c>
/// and <c>Authorization: Bearer</c>; the answer is an event stream whose events are JSON chunks
/// carrying <c>choices[0].delta.content</c>, a top-level <c>error</c> object on a failure mid-stream,
/// and a last event of <c>[DONE]</c>. Ollama serves the same at <c>http://localhost:11434/v1</c> and
/// ignores the key (docs.ollama.com/api/openai-compatibility).
/// </para>
/// <para>
/// Harness rules: every answer is one <see cref="Capability.ModelCall"/> through the gate, recorded in
/// the audit log. What is sent is the briefing and the messages DipTick composes;
/// DipTick's own notices never are. Redirects are refused, so a key can never follow one to another host. The key is found at
/// the moment of the call (<see cref="KeyFinder"/>: the variable the config names, else the key saved
/// for this host) and is never logged or shown. Every response is bounded (<see cref="EventStream"/>,
/// <see cref="MaxAnswer"/>).
/// </para>
/// </remarks>
internal sealed class OpenAiCompatibleEngine : IChatEngine, IDisposable
{
    public const int MaxAnswer = 1024 * 1024;
    private const int MaxErrorBody = 4096;

    private readonly HttpClient _http;
    private readonly Uri _chat;
    private readonly string _model;
    private readonly Keys.KeyFinder _keys;
    private readonly Providers.WireTraits _wire;

    /// <param name="wire">What of OpenAI's own fields the provider takes, from the catalogue; none for an unknown server.</param>
    public OpenAiCompatibleEngine(string provider, Uri baseUri, string model, Keys.KeyFinder keys, Providers.WireTraits wire = Providers.WireTraits.None)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentNullException.ThrowIfNull(keys);
        Name = model.Length == 0 ? provider : $"{provider} {model}";
        _model = model;
        _keys = keys;
        _wire = wire;
        _chat = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/chat/completions");
        _http = NewClient();
    }

    public string Name { get; }

    public Uri Endpoint => _chat;

    public void Dispose() => _http.Dispose();

    public async IAsyncEnumerable<EnginePart> StreamAsync(ChatRequest request, Gate gate, [EnumeratorCancellation] CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(gate);

        var target = _chat.GetLeftPart(UriPartial.Path);
        if (!(request.Continues ? gate.Continue(request.Verb, Capability.ModelCall, target) : gate.Request(request.Verb, Capability.ModelCall, target)))
        {
            throw new ChatException("The harness did not allow this call (model.call). 'diptick audit' says why.");
        }

        var key = await _keys.TakeAsync(cancel).ConfigureAwait(false);

        using var message = new HttpRequestMessage(HttpMethod.Post, _chat)
        {
            Content = new StringContent(Body(request), Encoding.UTF8, "application/json"),
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        Sign(message, _keys.Scheme, key);

        HttpResponseMessage response;
        string? failure = null;
        try
        {
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            response = null!;
            failure = $"Could not reach {_chat.Host}: {ex.Message}";
        }

        if (failure is not null)
        {
            throw new ChatException(failure);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new ChatException(await Refusal(_chat.Host, response, cancel).ConfigureAwait(false));
            }

            var total = 0;
            var refusal = new StringBuilder();
            string? finish = null;
            var whole = false;

            // A streamed tool call comes in pieces, each naming its call by index: the id and the name
            // first, then the arguments, a string in fragments (OpenAI's streaming reference).
            var calls = new SortedDictionary<int, (string? Id, string? Name, StringBuilder Arguments)>();
            var body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            await using var bodyScope = body.ConfigureAwait(false);
            await foreach (var data in EventStream.ReadAsync(body, cancel).ConfigureAwait(false))
            {
                if (data == "[DONE]")
                {
                    whole = true;
                    break;
                }

                var chunk = Chunk(data);
                if (chunk.Error is not null)
                {
                    throw new ChatException($"The server reported an error part-way through: {chunk.Error}");
                }

                if (chunk.TokensIn is not null || chunk.TokensOut is not null)
                {
                    yield return new UsagePart(chunk.TokensIn, chunk.TokensOut);
                }

                finish = chunk.Finish ?? finish;
                total += chunk.Text.Length + chunk.Refusal.Length + chunk.Calls.Sum(c => c.Arguments.Length);
                if (total > MaxAnswer)
                {
                    throw new ChatException("Stopped: the answer passed 1 MiB.");
                }

                refusal.Append(chunk.Refusal);
                foreach (var piece in chunk.Calls)
                {
                    var known = calls.TryGetValue(piece.Index, out var call) ? call : (null, null, new StringBuilder());
                    known.Arguments.Append(piece.Arguments);
                    calls[piece.Index] = (known.Id ?? piece.Id, known.Name ?? piece.Name, known.Arguments);
                }

                if (chunk.Text.Length > 0)
                {
                    yield return new TextPart(chunk.Text);
                }
            }

            var end = Finish(_chat.Host, refusal.ToString(), finish, whole, calls.Count > 0);
            if (end.End == AnswerEnd.Tools)
            {
                foreach (var (index, call) in calls)
                {
                    yield return new ToolCallPart(new ToolCall(call.Id ?? $"call_{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}", call.Name ?? string.Empty, call.Arguments.ToString()));
                }
            }

            yield return end;
        }
    }

    /// <summary>
    /// How an answer ended, from the last <c>finish_reason</c> the server sent (OpenAI's reference:
    /// stop, length, content_filter, tool_calls, function_call) and any <c>delta.refusal</c> text: a
    /// refusal whatever else came; tool calls, when some came whole and the answer was not cut, wait
    /// for their results; <c>stop</c>, or <c>[DONE]</c> with no reason, whole; anything else cut, with
    /// why. A stream that ends with neither was cut off on the way.
    /// </summary>
    internal static FinishPart Finish(string host, string refusal, string? finish, bool whole, bool calls = false) =>
        refusal.Trim().Length > 0 ? new FinishPart(AnswerEnd.Refused, $"{host} declined to answer: {Clip(refusal)}")
        : calls && finish is "tool_calls" or "stop" ? new FinishPart(AnswerEnd.Tools)
        : calls && finish is null && whole ? new FinishPart(AnswerEnd.Tools)
        : finish switch
        {
            "stop" => new FinishPart(AnswerEnd.Done),
            null when whole => new FinishPart(AnswerEnd.Done),
            null => new FinishPart(AnswerEnd.Cut, "the answer ended before the server said it was whole"),
            "length" => new FinishPart(AnswerEnd.Cut, calls ? "it reached its token cap in the middle of a tool call" : "it reached its token cap"),
            "content_filter" => new FinishPart(AnswerEnd.Cut, $"{host}'s content filter held part of it back"),
            "tool_calls" or "function_call" => new FinishPart(AnswerEnd.Cut, "it asked for a tool, and none was offered"),
            _ => new FinishPart(AnswerEnd.Cut, $"it stopped for a reason DipTick does not know ({Clip(finish)})"),
        };

    /// <summary>
    /// The body OpenAI's chat shape takes: the briefing first, as <c>developer</c> where the provider
    /// takes that and <c>system</c> otherwise; the messages; the cap under the name the provider takes;
    /// and a request for the usage chunk only where the provider documents it, since an unknown server
    /// may refuse a field it does not know.
    /// </summary>
    internal string Body(ChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("model", _model);
            json.WriteBoolean("stream", true);
            if (request.MaxTokens > 0)
            {
                json.WriteNumber(_wire.HasFlag(Providers.WireTraits.CompletionTokens) ? "max_completion_tokens" : "max_tokens", request.MaxTokens);
            }

            if (_wire.HasFlag(Providers.WireTraits.IncludeUsage))
            {
                json.WriteStartObject("stream_options");
                json.WriteBoolean("include_usage", true);
                json.WriteEndObject();
            }

            // The tools the model may call, in the function shape (OpenAI's reference). No tool_choice:
            // auto is every provider's default, and Ollama does not take the field.
            if (request.Tools.Count > 0)
            {
                json.WriteStartArray("tools");
                foreach (var tool in request.Tools)
                {
                    json.WriteStartObject();
                    json.WriteString("type", "function");
                    json.WriteStartObject("function");
                    json.WriteString("name", tool.Name);
                    json.WriteString("description", tool.Description);
                    json.WritePropertyName("parameters");
                    tool.Parameters.WriteTo(json);
                    json.WriteEndObject();
                    json.WriteEndObject();
                }

                json.WriteEndArray();
            }

            json.WriteStartArray("messages");
            if (request.System.Length > 0)
            {
                Write(json, _wire.HasFlag(Providers.WireTraits.Developer) ? "developer" : "system", request.System);
            }

            foreach (var line in request.Messages.Where(m => !m.IsEmpty))
            {
                WriteMessage(json, line);
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// One wire message in the chat shape: an agent's words with the tool calls it made; the person's
    /// side as one <c>tool</c> message for each result it carries, then its words and files, if any.
    /// </summary>
    private static void WriteMessage(Utf8JsonWriter json, WireMessage line)
    {
        if (line.Role == WireRole.Assistant)
        {
            json.WriteStartObject();
            json.WriteString("role", "assistant");
            if (line.Text.Length > 0)
            {
                json.WriteString("content", line.Text);
            }
            else
            {
                json.WriteNull("content");
            }

            if (line.Calls.Count > 0)
            {
                json.WriteStartArray("tool_calls");
                foreach (var call in line.Calls)
                {
                    json.WriteStartObject();
                    json.WriteString("id", call.Id);
                    json.WriteString("type", "function");
                    json.WriteStartObject("function");
                    json.WriteString("name", call.Name);
                    json.WriteString("arguments", call.Arguments);
                    json.WriteEndObject();
                    json.WriteEndObject();
                }

                json.WriteEndArray();
            }

            json.WriteEndObject();
            return;
        }

        foreach (var result in line.Results)
        {
            json.WriteStartObject();
            json.WriteString("role", "tool");
            json.WriteString("tool_call_id", result.CallId);
            json.WriteString("content", result.Content);
            json.WriteEndObject();
        }

        if (line.Files.Count > 0)
        {
            WriteWithFiles(json, line);
        }
        else if (line.Text.Length > 0)
        {
            Write(json, "user", line.Text);
        }
    }

    private static void Write(Utf8JsonWriter json, string role, string content)
    {
        json.WriteStartObject();
        json.WriteString("role", role);
        json.WriteString("content", content);
        json.WriteEndObject();
    }

    /// <summary>
    /// The person's side with files, as the chat shape's content parts
    /// (OpenAI's reference): each image an <c>image_url</c> part with a <c>data:</c> address, each text file
    /// a text part under its name, then the words. A PDF is not sent through this shape: the composer
    /// says beforehand which agents cannot read which files.
    /// </summary>
    private static void WriteWithFiles(Utf8JsonWriter json, WireMessage line)
    {
        json.WriteStartObject();
        json.WriteString("role", "user");
        json.WriteStartArray("content");
        foreach (var file in line.Files)
        {
            if (file.MediaType.StartsWith("image/", StringComparison.Ordinal))
            {
                json.WriteStartObject();
                json.WriteString("type", "image_url");
                json.WriteStartObject("image_url");
                json.WriteString("url", $"data:{file.MediaType};base64,{Convert.ToBase64String(file.Bytes)}");
                json.WriteEndObject();
                json.WriteEndObject();
            }
            else if (file.MediaType != "application/pdf")
            {
                WriteTextPart(json, AnthropicEngine.FileText(file));
            }
        }

        if (line.Text.Length > 0)
        {
            WriteTextPart(json, line.Text);
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void WriteTextPart(Utf8JsonWriter json, string text)
    {
        json.WriteStartObject();
        json.WriteString("type", "text");
        json.WriteString("text", text);
        json.WriteEndObject();
    }

    /// <summary>A tool call's piece in a chunk: which call it belongs to, and what of its id, name and arguments it brings.</summary>
    private readonly record struct CallPiece(int Index, string? Id, string? Name, string Arguments);

    /// <summary>One chunk of the stream: its words, a refusal's words, a finish reason, the tokens a usage object gives, tool calls' pieces, or an error.</summary>
    private readonly record struct Piece(string Text, string Refusal, string? Finish, int? TokensIn, int? TokensOut, string? Error)
    {
        public IReadOnlyList<CallPiece> Calls { get; init; } = [];
    }

    private static Piece Chunk(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new Piece(string.Empty, string.Empty, null, null, null, null);
            }

            if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            {
                var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString()
                    : error.ToString();
                return new Piece(string.Empty, string.Empty, null, null, null, Clip(message ?? "unknown"));
            }

            // A usage object may come on any chunk, and on OpenAI's last it comes with no choices at all.
            int? tokensIn = null, tokensOut = null;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                tokensIn = Count(usage, "prompt_tokens");
                tokensOut = Count(usage, "completion_tokens");
            }

            string text = string.Empty, refusal = string.Empty;
            string? finish = null;
            var calls = new List<CallPiece>();
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.ValueKind != JsonValueKind.Object)
                {
                    return new Piece(string.Empty, string.Empty, null, null, null, "a chunk whose choice is not an object");
                }

                if (first.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                {
                    text = delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String ? content.GetString() ?? string.Empty : string.Empty;
                    refusal = delta.TryGetProperty("refusal", out var declined) && declined.ValueKind == JsonValueKind.String ? declined.GetString() ?? string.Empty : string.Empty;
                    if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                    {
                        var position = 0;
                        foreach (var call in toolCalls.EnumerateArray())
                        {
                            if (call.ValueKind != JsonValueKind.Object)
                            {
                                return new Piece(string.Empty, string.Empty, null, null, null, "a tool call that is not an object");
                            }

                            // A server that leaves the index out sends each call whole, in order.
                            var index = call.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out var at) ? at : position;
                            var id = call.TryGetProperty("id", out var callId) && callId.ValueKind == JsonValueKind.String ? callId.GetString() : null;
                            string? name = null, arguments = null;
                            if (call.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
                            {
                                name = function.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                                arguments = function.TryGetProperty("arguments", out var a) ? a.ValueKind switch
                                {
                                    JsonValueKind.String => a.GetString(),
                                    JsonValueKind.Object => a.GetRawText(),
                                    _ => null,
                                } : null;
                            }

                            calls.Add(new CallPiece(index, id, name, arguments ?? string.Empty));
                            position++;
                        }
                    }
                }

                finish = first.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null;
            }

            return new Piece(text, refusal, finish, tokensIn, tokensOut, null) { Calls = calls };
        }
        catch (JsonException)
        {
            return new Piece(string.Empty, string.Empty, null, null, null, "a chunk that is not JSON");
        }
    }

    private static int? Count(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) && count >= 0 ? count : null;

    /// <summary>
    /// Puts the key on a request the way its provider takes it (the catalogue says which):
    /// <c>Authorization: Bearer</c> for most and for a Google token, <c>Authorization: Key</c> for fal,
    /// an <c>api-key</c> header for Azure OpenAI. No key, no header.
    /// </summary>
    internal static void Sign(HttpRequestMessage request, Providers.KeyScheme scheme, string? key)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (key is null)
        {
            return;
        }

        switch (scheme)
        {
            case Providers.KeyScheme.KeyHeader:
                request.Headers.Authorization = new AuthenticationHeaderValue("Key", key);
                break;
            case Providers.KeyScheme.ApiKeyHeader:
                request.Headers.Add("api-key", key);
                break;
            default:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                break;
        }
    }

    /// <summary>
    /// The client every call to a provider uses: no redirect is ever followed (a key must not
    /// travel to a host nobody named), no timeout of its own (the caller's token decides).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The handler is owned by the HttpClient from the moment it is constructed with disposeHandler: true, and the " +
                        "caller disposes the client; the catch disposes the handler if the client never took it. CA2000 does not " +
                        "follow ownership through that constructor.")]
    internal static HttpClient NewClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        try
        {
            return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        }
        catch
        {
            handler.Dispose();
            throw;
        }
    }

    /// <summary>What a refusal says, in one line: the status, and the platform's own message when it gave one.</summary>
    internal static async Task<string> Refusal(string host, HttpResponseMessage response, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(response);
        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
        {
            return $"{host} answered with a redirect ({status}); redirects are refused so the key cannot follow one. Set the endpoint to where it points.";
        }

        var text = string.Empty;
        try
        {
            var body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            await using var bodyScope = body.ConfigureAwait(false);
            var bytes = new byte[MaxErrorBody];
            var read = await body.ReadAtLeastAsync(bytes, MaxErrorBody, throwOnEndOfStream: false, cancel).ConfigureAwait(false);
            text = Encoding.UTF8.GetString(bytes, 0, read);
        }
        catch (IOException)
        {
        }

        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var e))
            {
                message = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : e.ToString();
            }
        }
        catch (JsonException)
        {
        }

        return $"{host} refused ({status} {response.ReasonPhrase}): {Clip(message ?? text)}";
    }

    internal static string Clip(string text)
    {
        var one = text.ReplaceLineEndings(" ").Trim();
        return one.Length <= 300 ? one : one[..300] + "…";
    }
}
