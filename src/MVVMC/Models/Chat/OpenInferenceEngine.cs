using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DipTick.Models.Harness;

namespace DipTick.Models.Chat;

/// <summary>
/// A model served through KServe's Open Inference Protocol, as Inferless
/// serves one: <c>POST {address}</c>, the address being the model's own <c>…/v2/models/{name}/versions/1/infer</c>,
/// with <c>{"inputs":[{"name","shape","datatype","data"}]}</c> and an answer of <c>{"outputs":[…]}</c> in the
/// same form, or <c>{"error"}</c> (the protocol's REST specification; BYTES data "represented as strings in
/// JSON"). Inferless's own templates read the input <c>prompt</c> and answer <c>generated_text</c>, with the
/// workspace key as Bearer (its Python client and template READMEs, read 2026-09-28).
/// </summary>
/// <remarks>
/// Such a model takes one prompt, so the whole request goes as one text: the briefing, then each message
/// with who said it, then a line for the answer. No tools and no files: a model served so declares only
/// the inputs its own code reads. Nothing streams: the answer comes whole.
/// </remarks>
internal sealed class OpenInferenceEngine : IChatEngine, IDisposable
{
    /// <summary>The input a model's prompt goes in.</summary>
    public const string PromptInput = "prompt";

    /// <summary>The output its answer comes in, where the model names one so; else its first text output.</summary>
    public const string AnswerOutput = "generated_text";

    private readonly HttpClient _http;
    private readonly Uri _address;
    private readonly Keys.KeyFinder _keys;

    public OpenInferenceEngine(string provider, Uri address, string model, Keys.KeyFinder keys, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(keys);
        _address = address;
        _keys = keys;
        Name = $"{provider} {model}";
        _http = http ?? OpenAiCompatibleEngine.NewClient();
    }

    public string Name { get; }

    public void Dispose() => _http.Dispose();

    /// <summary>The request as one prompt: the briefing, each message named by who said it, and the line the answer follows.</summary>
    internal static string Prompt(ChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var text = new StringBuilder();
        if (request.System.Length > 0)
        {
            text.Append(request.System.Trim()).Append("\n\n");
        }

        foreach (var message in request.Messages.Where(m => m.Text.Length > 0))
        {
            text.Append(message.Role == WireRole.User ? "The person: " : "You: ").Append(message.Text.Trim()).Append("\n\n");
        }

        return text.Append("You:").ToString();
    }

    /// <summary>The body the protocol takes: the prompt as the one input, BYTES, shape [1].</summary>
    internal static string Body(string prompt) =>
        new JsonObject
        {
            ["inputs"] = new JsonArray(new JsonObject
            {
                ["name"] = PromptInput,
                ["shape"] = new JsonArray(1),
                ["datatype"] = "BYTES",
                ["data"] = new JsonArray(prompt),
            }),
        }.ToJsonString();

    /// <summary>The answer in an inference response: the output named <see cref="AnswerOutput"/>, else the first BYTES one; its first item.</summary>
    /// <exception cref="ChatException">The response holds none, or says why.</exception>
    internal static string Answer(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
        {
            throw new ChatException($"The model answered with an error: {OpenAiCompatibleEngine.Clip(error.ToString())}");
        }

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Array)
        {
            throw new ChatException("The model's answer holds no outputs.");
        }

        var chosen = outputs.EnumerateArray().FirstOrDefault(o => Named(o, "name", AnswerOutput));
        if (chosen.ValueKind != JsonValueKind.Object)
        {
            chosen = outputs.EnumerateArray().FirstOrDefault(o => Named(o, "datatype", "BYTES"));
        }

        return chosen.ValueKind == JsonValueKind.Object && chosen.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 && data[0].ValueKind == JsonValueKind.String
            ? data[0].GetString() ?? string.Empty
            : throw new ChatException($"The model's answer holds no text: its outputs are not {AnswerOutput}, and none is text.");
    }

    /// <summary>Whether an output is an object whose <paramref name="member"/> is the text <paramref name="value"/>; false for any other shape.</summary>
    private static bool Named(JsonElement output, string member, string value) =>
        output.ValueKind == JsonValueKind.Object && output.TryGetProperty(member, out var found) && found.ValueKind == JsonValueKind.String && found.GetString() == value;

    public async IAsyncEnumerable<EnginePart> StreamAsync(ChatRequest request, Gate gate, [EnumeratorCancellation] CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(gate);
        var target = _address.AbsoluteUri;
        if (!(request.Continues ? gate.Continue(request.Verb, Capability.ModelCall, target) : gate.Request(request.Verb, Capability.ModelCall, target)))
        {
            throw new ChatException("The harness did not allow this call (model.call). 'diptick audit' says why.");
        }

        var key = await _keys.TakeAsync(cancel).ConfigureAwait(false);
        using var message = new HttpRequestMessage(HttpMethod.Post, _address)
        {
            Content = new StringContent(Body(Prompt(request)), Encoding.UTF8, "application/json"),
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (key is not null)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ChatException($"Could not reach {_address.Host}: {ex.Message}", ex);
        }

        string answer;
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new ChatException(await OpenAiCompatibleEngine.Refusal(_address.Host, response, cancel).ConfigureAwait(false));
            }

            var body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            await using var bodyScope = body.ConfigureAwait(false);
            using var kept = new MemoryStream();
            var buffer = new byte[16384];
            int read;
            while ((read = await body.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
            {
                if (kept.Length + read > OpenAiCompatibleEngine.MaxAnswer * 2)
                {
                    throw new ChatException("Stopped: the answer passed 1 MiB.");
                }

                await kept.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
            }

            try
            {
                using var doc = JsonDocument.Parse(kept.ToArray());
                answer = Answer(doc.RootElement);
            }
            catch (JsonException ex)
            {
                throw new ChatException($"{_address.Host}'s answer was not JSON.", ex);
            }
        }

        yield return new TextPart(answer);
        yield return new FinishPart(AnswerEnd.Done);
    }
}
