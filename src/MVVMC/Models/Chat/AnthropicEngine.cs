using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic;
using Anthropic.Vertex;
using DipTick.Models.Harness;
using DipTick.Models.Providers;
using ApiMessages = Anthropic.Models.Messages;
using BetaMessages = Anthropic.Models.Beta.Messages;

namespace DipTick.Models.Chat;

/// <summary>
/// Claude, through Anthropic's official C# SDK (NuGet <c>Anthropic</c>), streamed: from Anthropic
/// itself, from Claude Platform on AWS, from Amazon Bedrock, from Microsoft Foundry, or from Google
/// Cloud through Anthropic's own Vertex package.
/// </summary>
/// <remarks>
/// <para>
/// Built from the Claude API reference bundled with Claude Code:
/// the official SDK rather than an OpenAI-compatible shim; <c>CreateStreaming</c> for text as it
/// arrives; <c>stop_reason: refusal</c> handled before any text is trusted as an answer. Where the
/// platform takes them (Anthropic's API, Claude Platform on AWS), the server-side refusal fallback in
/// its <c>"default"</c> form (beta <c>server-side-fallback-2026-07-01</c>), so a policy decline is
/// re-served by Anthropic's recommended model. Bedrock, Foundry and Google Cloud take neither the
/// beta header nor the fallback (platform.claude.com, each platform's page), so their calls are the
/// plain Messages API.
/// </para>
/// <para>
/// Harness rules as for every seat: one <see cref="Capability.ModelCall"/> through the gate per
/// answer; what is sent is the briefing, as the top-level <c>system</c>, and the messages DipTick
/// composes, the person's side last; the key is found at the moment of the
/// call (<see cref="Keys.KeyFinder"/>); the SDK is handed DipTick's own HttpClient, which follows no
/// redirect, so a key never travels to a host nobody named; answers are bounded. The SDK is given
/// its key and address explicitly, and no token of its own from the environment, so a credential for
/// one platform can never ride along to another.
/// </para>
/// <para>
/// Anthropic's own web search and fetch are the basic versions,
/// <c>web_search_20250305</c> and <c>web_fetch_20250910</c>, where the platform has them: the newer ones
/// run code on Anthropic's machines for the request. What they do comes back inside the answer: its
/// blocks are rebuilt as they came (<see cref="AnthropicBlocks"/>), shown as tool rows and sources, a
/// <c>pause_turn</c> is continued here with the answer so far sent back unchanged, and a turn that goes
/// on after DipTick's tools gets the blocks back to send (<see cref="EchoPart"/>).
/// </para>
/// </remarks>
internal sealed class AnthropicEngine : IChatEngine, IDisposable
{
    public const string DefaultBase = "https://api.anthropic.com";
    public const int MaxAnswer = OpenAiCompatibleEngine.MaxAnswer;

    /// <summary>The shape an <see cref="EchoPart"/> of this engine is in, and <see cref="WireMessage.BlocksFrom"/> names.</summary>
    public const string Shape = "anthropic";

    /// <summary>How many times one request's <c>pause_turn</c> is continued (Anthropic: cap the continuations "as you would any retry loop").</summary>
    public const int MostContinuations = 5;

    /// <summary>The most searches, and the most fetches, one request may make: a turn's cap of calls.</summary>
    public const int ProviderUses = 20;

    /// <summary>The most tokens of one fetched page: Anthropic's figure for a large documentation page (100 kB, ~25,000 tokens).</summary>
    public const int FetchTokens = 25_000;

    private const long MaxTokens = 16000;

    private readonly HttpClient _http;
    private readonly string _model;
    private readonly Keys.KeyFinder _keys;
    private readonly string _base;
    private readonly AnthropicFlavor _flavor;
    private readonly string _workspace;
    private readonly IGoogleSignIn _google;

    /// <param name="endpoint">Where to send, or null for Anthropic's own address.</param>
    /// <param name="flavor">Which platform serves the messages, which decides what a request may carry.</param>
    /// <param name="workspace">The workspace the key acts in, for Anthropic's API and Claude Platform on AWS; empty for none.</param>
    /// <param name="provider">The provider's id, for the engine's name.</param>
    /// <param name="google">The Google sign-in, for Claude on Google Cloud; Google's own by default.</param>
    /// <param name="http">The client to send with; DipTick's own, which follows no redirect, by default.</param>
    public AnthropicEngine(
        string model,
        Keys.KeyFinder keys,
        Uri? endpoint,
        AnthropicFlavor flavor = AnthropicFlavor.Api,
        string workspace = "",
        string provider = "anthropic",
        IGoogleSignIn? google = null,
        HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _model = model;
        _keys = keys;
        _base = (endpoint?.AbsoluteUri ?? DefaultBase).TrimEnd('/');
        _flavor = flavor == AnthropicFlavor.None ? AnthropicFlavor.Api : flavor;
        _workspace = workspace;
        _google = google ?? GoogleSignIn.Instance;
        Name = $"{provider} {model}";
        _http = http ?? OpenAiCompatibleEngine.NewClient();
    }

    public string Name { get; }

    public void Dispose() => _http.Dispose();

    /// <summary>Whether the platform takes Anthropic's beta header and the server-side fallback.</summary>
    private bool TakesBetas => _flavor is AnthropicFlavor.Api or AnthropicFlavor.Aws;

    /// <summary>Where the gate and the audit log say the call goes.</summary>
    private string Target => _flavor == AnthropicFlavor.Vertex
        ? $"{_base}/publishers/anthropic/models/{_model}:streamRawPredict"
        : _base + "/v1/messages";

    public async IAsyncEnumerable<EnginePart> StreamAsync(ChatRequest request, Gate gate, [EnumeratorCancellation] CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(gate);

        if (!(request.Continues ? gate.Continue(request.Verb, Capability.ModelCall, Target) : gate.Request(request.Verb, Capability.ModelCall, Target)))
        {
            throw new ChatException("The harness did not allow this call (model.call). 'diptick audit' says why.");
        }

        var turns = request.Messages.Where(m => !m.IsEmpty).ToList();
        if (turns.Count == 0 || turns[^1].Role != WireRole.User)
        {
            // Claude 5 answers a trailing assistant message with a 400 (no prefill); DipTick always ends with the person's side.
            throw new ChatException("There is nothing from you to answer.");
        }

        // The provider's own research tools, only where this platform has them.
        var said = request with { ProviderTools = [.. request.ProviderTools.Where(OffersHere)] };
        using var client = await ClientAsync(cancel).ConfigureAwait(false);
        var host = new Uri(_base).Host;
        var total = 0;
        string? stop = null;
        long spentIn = 0, spentOut = 0;
        var calls = new List<ToolCall>();
        var blocks = new AnthropicBlocks();
        var cap = request.MaxTokens > 0 ? request.MaxTokens : MaxTokens;
        var sending = turns;
        var continued = 0;
        while (true)
        {
            long? tokensIn = null, tokensOut = null;
            stop = null;
            var pieces = TakesBetas ? BetaStream(client, said, sending, cap, blocks, cancel) : PlainStream(client, said, sending, cap, blocks, cancel);
            await foreach (var piece in pieces.ConfigureAwait(false))
            {
                if (piece.Stop is not null)
                {
                    stop = piece.Stop;
                }

                if (piece.TokensIn is not null || piece.TokensOut is not null)
                {
                    tokensIn = piece.TokensIn ?? tokensIn;
                    tokensOut = piece.TokensOut ?? tokensOut;
                    yield return new UsagePart(Small(spentIn + (tokensIn ?? 0)), Small(spentOut + (tokensOut ?? 0)));
                }

                if (piece.Call is { } call)
                {
                    total += call.Arguments.Length;
                    calls.Add(call);
                }

                if (piece.Cited is { } cited)
                {
                    yield return cited;
                }

                if (piece.Provider is { } provider)
                {
                    yield return provider;
                }

                if (piece.Text.Length == 0)
                {
                    continue;
                }

                total += piece.Text.Length;
                if (total > MaxAnswer)
                {
                    throw new ChatException("Stopped: the answer passed 1 MiB.");
                }

                yield return new TextPart(piece.Text);
            }

            spentIn += tokensIn ?? 0;
            spentOut += tokensOut ?? 0;
            if (stop != "pause_turn" || continued >= MostContinuations)
            {
                break;
            }

            // A paused turn goes on: the answer so far sent back as it came, with the same tools (Anthropic's server tools page).
            continued++;
            if (!gate.Continue(request.Verb, Capability.ModelCall, Target))
            {
                throw new ChatException("The harness did not allow this call (model.call). 'diptick audit' says why.");
            }

            sending = [.. turns, new WireMessage(WireRole.Assistant, string.Empty) { Blocks = blocks.Blocks, BlocksFrom = Shape }];
        }

        var end = Finish(stop, host, TakesBetas, total > 0, calls.Count > 0);
        if (end.End == AnswerEnd.Tools)
        {
            // What the turn sends back with the results: every block as it came, where words and calls cannot rebuild it.
            if (blocks.NeedsEcho)
            {
                yield return new EchoPart(Shape, blocks.Blocks);
            }

            foreach (var call in calls)
            {
                yield return new ToolCallPart(call);
            }
        }

        yield return end;
    }

    /// <summary>Whether this platform has the provider tool DipTick names: search and fetch on Anthropic's API, AWS and Foundry; search alone on Google Cloud; neither on Bedrock.</summary>
    private bool OffersHere(string tool) => _flavor switch
    {
        AnthropicFlavor.Api or AnthropicFlavor.Aws or AnthropicFlavor.Foundry => tool is Research.ProviderResearch.Search or Research.ProviderResearch.Fetch,
        AnthropicFlavor.Vertex => tool == Research.ProviderResearch.Search,
        _ => false,
    };

    /// <summary>
    /// How an answer ended, from its <c>stop_reason</c>: <c>end_turn</c> and
    /// <c>stop_sequence</c> whole; <c>max_tokens</c> cut, and when no text came, thinking used the
    /// whole cap; <c>refusal</c> refused; <c>tool_use</c> with the calls DipTick offered waits for
    /// their results, and without, cut; <c>pause_turn</c> still there after its continuations, cut;
    /// anything else cut; none at all, cut off on the way.
    /// </summary>
    internal static FinishPart Finish(string? stop, string host, bool fallback, bool anyText, bool calls = false) => stop switch
    {
        "end_turn" or "stop_sequence" => new FinishPart(AnswerEnd.Done),
        "max_tokens" => new FinishPart(AnswerEnd.Cut, anyText ? "it reached its token cap" : "thinking used the whole token cap before any answer"),
        "refusal" => new FinishPart(AnswerEnd.Refused, fallback
            ? "Claude declined to answer (a policy refusal), and its fallback declined too."
            : $"Claude declined to answer (a policy refusal). {host} has no fallback to ask instead."),
        "tool_use" when calls => new FinishPart(AnswerEnd.Tools),
        "tool_use" => new FinishPart(AnswerEnd.Cut, "it asked for a tool, and none was offered"),
        "pause_turn" => new FinishPart(AnswerEnd.Cut, $"its provider paused a long turn {MostContinuations} times over"),
        "model_context_window_exceeded" => new FinishPart(AnswerEnd.Cut, "the chat outgrew the model's context window"),
        null => new FinishPart(AnswerEnd.Cut, "the answer ended before the server said it was whole"),
        _ => new FinishPart(AnswerEnd.Cut, $"it stopped for a reason DipTick does not know ({OpenAiCompatibleEngine.Clip(stop)})"),
    };

    private static int? Small(long? tokens) => tokens is { } count ? (int)Math.Clamp(count, 0, int.MaxValue) : null;

    /// <summary>One event of the stream, as the engine reads it: words, a finished tool call, a citation, a provider's tool result, a stop reason, or token counts.</summary>
    private readonly record struct Piece(string Text, string? Stop = null, long? TokensIn = null, long? TokensOut = null, ToolCall? Call = null, CitationPart? Cited = null, ProviderToolPart? Provider = null);

    /// <summary>A tool call's input as the SDK takes it: the model's JSON object as a dictionary; an empty one when it wrote none or no object.</summary>
    private static Dictionary<string, JsonElement> Input(string arguments)
    {
        try
        {
            using var doc = JsonDocument.Parse(arguments.Trim().Length == 0 ? "{}" : arguments);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal)
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>A tool's parameters (a JSON Schema object) as the SDK's input schema: its properties and the ones required.</summary>
    private static (Dictionary<string, JsonElement> Properties, List<string> Required) Schema(ToolSpec tool)
    {
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var required = new List<string>();
        if (tool.Parameters.ValueKind == JsonValueKind.Object)
        {
            if (tool.Parameters.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in props.EnumerateObject())
                {
                    properties[p.Name] = p.Value.Clone();
                }
            }

            if (tool.Parameters.TryGetProperty("required", out var needed) && needed.ValueKind == JsonValueKind.Array)
            {
                required.AddRange(needed.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.String).Select(r => r.GetString()!));
            }
        }

        return (properties, required);
    }

    /// <summary>
    /// The SDK's client for this platform: Anthropic's with the key and address set, or Anthropic's
    /// Vertex client with DipTick's Google sign-in. Neither takes a key or token from the environment.
    /// </summary>
    private async Task<AnthropicClient> ClientAsync(CancellationToken cancel)
    {
        if (_flavor == AnthropicFlavor.Vertex)
        {
            if (!Uri.TryCreate(_base, UriKind.Absolute, out var uri) || GoogleCloud.Parse(uri) is not { } where)
            {
                throw new ChatException($"{_base} is not a Google Cloud address with a project and a location; nothing was sent.");
            }

            // Anthropic.Vertex 0.5.3 clears both in its own constructor too; they are cleared here again
            // so a first-party key can never reach Google whatever a later version of the package does.
            return new AnthropicVertexClient(new GoogleVertexCredentials(where.Location, where.Project, _google))
            {
                ApiKey = null,
                AuthToken = null,
                HttpClient = _http,
            };
        }

        // Anthropic never answers without a key, so the finder never returns null here.
        var key = await _keys.TakeAsync(cancel).ConfigureAwait(false);
        return new AnthropicClient { ApiKey = key, AuthToken = null, BaseUrl = _base, HttpClient = _http };
    }

    /// <summary>The Messages API with its betas: the server-side fallback, and the workspace when one is set.</summary>
    private async IAsyncEnumerable<Piece> BetaStream(AnthropicClient client, ChatRequest said, List<WireMessage> turns, long cap, AnthropicBlocks blocks, [EnumeratorCancellation] CancellationToken cancel)
    {
        var request = new BetaMessages.MessageCreateParams
        {
            Model = _model,
            MaxTokens = cap,
            Messages = [.. turns.Select(BetaMessage)],
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new BetaMessages.Default(),
            WorkspaceID = _workspace.Length > 0 ? _workspace : null,
        };
        if (said.System.Length > 0)
        {
            request = request with { System = said.System };
        }

        var tools = BetaTools(said);
        if (tools.Count > 0)
        {
            request = request with { Tools = tools };
        }

        var events = client.Beta.Messages.CreateStreaming(request, cancel).GetAsyncEnumerator(cancel);
        await using var eventsScope = events.ConfigureAwait(false);
        while (await Step(events.MoveNextAsync()).ConfigureAwait(false))
        {
            var streamEvent = events.Current;
            if (streamEvent.TryPickContentBlockDelta(out var delta))
            {
                var (text, cited) = blocks.Delta(delta.Index, delta.Delta.Json);
                yield return new Piece(text, Cited: cited);
            }
            else if (streamEvent.TryPickContentBlockStart(out var block))
            {
                if (blocks.Start(block.Index, block.ContentBlock.Json) is { } provider)
                {
                    yield return new Piece(string.Empty, Provider: provider);
                }
            }
            else if (streamEvent.TryPickContentBlockStop(out var stopped))
            {
                if (blocks.Stop(stopped.Index) is { } call)
                {
                    yield return new Piece(string.Empty, Call: call);
                }
            }
            else if (streamEvent.TryPickStart(out var start))
            {
                yield return new Piece(string.Empty, TokensIn: start.Message.Usage.InputTokens);
            }
            else if (streamEvent.TryPickDelta(out var messageDelta))
            {
                // The wire value, not the enum's name: ApiEnum.Raw() is what the server sent. Output tokens are cumulative.
                yield return new Piece(string.Empty, messageDelta.Delta.StopReason?.Raw(), TokensOut: messageDelta.Usage.OutputTokens);
            }
        }
    }

    /// <summary>The plain Messages API, for the platforms that take no beta header and no fallback.</summary>
    private async IAsyncEnumerable<Piece> PlainStream(AnthropicClient client, ChatRequest said, List<WireMessage> turns, long cap, AnthropicBlocks blocks, [EnumeratorCancellation] CancellationToken cancel)
    {
        var request = new ApiMessages.MessageCreateParams
        {
            Model = _model,
            MaxTokens = cap,
            Messages = [.. turns.Select(PlainMessage)],
        };
        if (said.System.Length > 0)
        {
            request = request with { System = said.System };
        }

        var tools = PlainTools(said);
        if (tools.Count > 0)
        {
            request = request with { Tools = tools };
        }

        var events = client.Messages.CreateStreaming(request, cancel).GetAsyncEnumerator(cancel);
        await using var eventsScope = events.ConfigureAwait(false);
        while (await Step(events.MoveNextAsync()).ConfigureAwait(false))
        {
            var streamEvent = events.Current;
            if (streamEvent.TryPickContentBlockDelta(out var delta))
            {
                var (text, cited) = blocks.Delta(delta.Index, delta.Delta.Json);
                yield return new Piece(text, Cited: cited);
            }
            else if (streamEvent.TryPickContentBlockStart(out var block))
            {
                if (blocks.Start(block.Index, block.ContentBlock.Json) is { } provider)
                {
                    yield return new Piece(string.Empty, Provider: provider);
                }
            }
            else if (streamEvent.TryPickContentBlockStop(out var stopped))
            {
                if (blocks.Stop(stopped.Index) is { } call)
                {
                    yield return new Piece(string.Empty, Call: call);
                }
            }
            else if (streamEvent.TryPickStart(out var start))
            {
                yield return new Piece(string.Empty, TokensIn: start.Message.Usage.InputTokens);
            }
            else if (streamEvent.TryPickDelta(out var messageDelta))
            {
                yield return new Piece(string.Empty, messageDelta.Delta.StopReason?.Raw(), TokensOut: messageDelta.Usage.OutputTokens);
            }
        }
    }

    /// <summary>
    /// A wire message as the beta API takes it: an agent's message with its blocks, as it came; plain
    /// words as a string; an agent's words with its tool calls as blocks; the person's side with each
    /// call's result first, then files, then words (claude-api's C# tool-use page: one result for every
    /// tool use, or the API refuses; Anthropic's server tools page: results alone after a mixed turn).
    /// </summary>
    private static BetaMessages.BetaMessageParam BetaMessage(WireMessage line)
    {
        var role = line.Role == WireRole.User ? BetaMessages.Role.User : BetaMessages.Role.Assistant;
        if (line.Blocks.Count > 0 && line.BlocksFrom == Shape)
        {
            // Anthropic's own C# example continues a paused turn this way: each block's own JSON, unchanged.
            return new BetaMessages.BetaMessageParam { Role = role, Content = line.Blocks.Select(b => new BetaMessages.BetaContentBlockParam(b)).ToList() };
        }

        if (line.Calls.Count == 0 && line.Results.Count == 0 && line.Files.Count == 0)
        {
            return new BetaMessages.BetaMessageParam { Role = role, Content = line.Text };
        }

        List<BetaMessages.BetaContentBlockParam> blocks = [];
        if (line.Role == WireRole.Assistant)
        {
            if (line.Text.Length > 0)
            {
                blocks.Add(new BetaMessages.BetaTextBlockParam { Text = line.Text });
            }

            blocks.AddRange(line.Calls.Select(c => (BetaMessages.BetaContentBlockParam)new BetaMessages.BetaToolUseBlockParam { ID = c.Id, Name = c.Name, Input = Input(c.Arguments) }));
        }
        else
        {
            blocks.AddRange(line.Results.Select(r => (BetaMessages.BetaContentBlockParam)BetaResult(r)));
            blocks.AddRange(BetaFiles(line.Files));
            if (line.Text.Length > 0)
            {
                blocks.Add(new BetaMessages.BetaTextBlockParam { Text = line.Text });
            }
        }

        return new BetaMessages.BetaMessageParam { Role = role, Content = blocks };
    }

    /// <summary>A wire message as the plain API takes it, the same way as <see cref="BetaMessage"/>.</summary>
    private static ApiMessages.MessageParam PlainMessage(WireMessage line)
    {
        var role = line.Role == WireRole.User ? ApiMessages.Role.User : ApiMessages.Role.Assistant;
        if (line.Blocks.Count > 0 && line.BlocksFrom == Shape)
        {
            return new ApiMessages.MessageParam { Role = role, Content = line.Blocks.Select(b => new ApiMessages.ContentBlockParam(b)).ToList() };
        }

        if (line.Calls.Count == 0 && line.Results.Count == 0 && line.Files.Count == 0)
        {
            return new ApiMessages.MessageParam { Role = role, Content = line.Text };
        }

        List<ApiMessages.ContentBlockParam> blocks = [];
        if (line.Role == WireRole.Assistant)
        {
            if (line.Text.Length > 0)
            {
                blocks.Add(new ApiMessages.TextBlockParam { Text = line.Text });
            }

            blocks.AddRange(line.Calls.Select(c => (ApiMessages.ContentBlockParam)new ApiMessages.ToolUseBlockParam { ID = c.Id, Name = c.Name, Input = Input(c.Arguments) }));
        }
        else
        {
            blocks.AddRange(line.Results.Select(r => (ApiMessages.ContentBlockParam)PlainResult(r)));
            blocks.AddRange(PlainFiles(line.Files));
            if (line.Text.Length > 0)
            {
                blocks.Add(new ApiMessages.TextBlockParam { Text = line.Text });
            }
        }

        return new ApiMessages.MessageParam { Role = role, Content = blocks };
    }

    /// <summary>One tool call's result: its words, and a PDF it brought back as a document beside them (a tool result may hold text and document blocks).</summary>
    private static BetaMessages.BetaToolResultBlockParam BetaResult(ToolResult result)
    {
        if (result.File is { MediaType: "application/pdf" } file)
        {
            List<BetaMessages.Block> parts =
            [
                new BetaMessages.BetaTextBlockParam { Text = result.Content },
                new BetaMessages.BetaRequestDocumentBlock { Source = new BetaMessages.BetaBase64PdfSource { Data = Convert.ToBase64String(file.Bytes) }, Title = file.Name },
            ];
            return new BetaMessages.BetaToolResultBlockParam { ToolUseID = result.CallId, Content = parts, IsError = result.IsError ? true : null };
        }

        return new BetaMessages.BetaToolResultBlockParam { ToolUseID = result.CallId, Content = result.Content, IsError = result.IsError ? true : null };
    }

    /// <summary>One tool call's result as the plain API takes it, the same way as <see cref="BetaResult"/>.</summary>
    private static ApiMessages.ToolResultBlockParam PlainResult(ToolResult result)
    {
        if (result.File is { MediaType: "application/pdf" } file)
        {
            List<ApiMessages.Block> parts =
            [
                new ApiMessages.TextBlockParam { Text = result.Content },
                new ApiMessages.DocumentBlockParam { Source = new ApiMessages.Base64PdfSource { Data = Convert.ToBase64String(file.Bytes) }, Title = file.Name },
            ];
            return new ApiMessages.ToolResultBlockParam { ToolUseID = result.CallId, Content = parts, IsError = result.IsError ? true : null };
        }

        return new ApiMessages.ToolResultBlockParam { ToolUseID = result.CallId, Content = result.Content, IsError = result.IsError ? true : null };
    }

    /// <summary>
    /// Files sent with the person's words, as Anthropic's blocks: an image
    /// (JPEG, PNG, GIF, WebP) as an image, a PDF as a document, anything else as its text under its name.
    /// </summary>
    private static IEnumerable<BetaMessages.BetaContentBlockParam> BetaFiles(IReadOnlyList<WireFile> files)
    {
        foreach (var file in files)
        {
            if (ImageType(file.MediaType) is { } image)
            {
                yield return new BetaMessages.BetaImageBlockParam { Source = new BetaMessages.BetaBase64ImageSource { Data = Convert.ToBase64String(file.Bytes), MediaType = (BetaMessages.MediaType)image } };
            }
            else if (file.MediaType == "application/pdf")
            {
                yield return new BetaMessages.BetaRequestDocumentBlock { Source = new BetaMessages.BetaBase64PdfSource { Data = Convert.ToBase64String(file.Bytes) }, Title = file.Name };
            }
            else
            {
                yield return new BetaMessages.BetaTextBlockParam { Text = FileText(file) };
            }
        }
    }

    /// <summary>Files as the plain API takes them, the same way as <see cref="BetaFiles"/>.</summary>
    private static IEnumerable<ApiMessages.ContentBlockParam> PlainFiles(IReadOnlyList<WireFile> files)
    {
        foreach (var file in files)
        {
            if (ImageType(file.MediaType) is { } image)
            {
                yield return new ApiMessages.ImageBlockParam { Source = new ApiMessages.Base64ImageSource { Data = Convert.ToBase64String(file.Bytes), MediaType = (ApiMessages.MediaType)image } };
            }
            else if (file.MediaType == "application/pdf")
            {
                yield return new ApiMessages.DocumentBlockParam { Source = new ApiMessages.Base64PdfSource { Data = Convert.ToBase64String(file.Bytes) }, Title = file.Name };
            }
            else
            {
                yield return new ApiMessages.TextBlockParam { Text = FileText(file) };
            }
        }
    }

    /// <summary>
    /// The image types Anthropic takes, as the position of its media-type enum (the same order in both
    /// namespaces: JPEG, PNG, GIF, WebP); null for anything else.
    /// </summary>
    private static int? ImageType(string mediaType) => mediaType switch
    {
        "image/jpeg" => (int)ApiMessages.MediaType.ImageJpeg,
        "image/png" => (int)ApiMessages.MediaType.ImagePng,
        "image/gif" => (int)ApiMessages.MediaType.ImageGif,
        "image/webp" => (int)ApiMessages.MediaType.ImageWebP,
        _ => null,
    };

    /// <summary>A text file as the model reads it: its name, then its words.</summary>
    internal static string FileText(WireFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return $"{file.Name}:\n{System.Text.Encoding.UTF8.GetString(file.Bytes)}";
    }

    /// <summary>DipTick's tools as the beta API takes them, then the provider's own that the request asks for.</summary>
    private static List<BetaMessages.BetaToolUnion> BetaTools(ChatRequest request)
    {
        List<BetaMessages.BetaToolUnion> tools = [];
        foreach (var tool in request.Tools)
        {
            var (properties, required) = Schema(tool);
            tools.Add(new BetaMessages.BetaTool { Name = tool.Name, Description = tool.Description, InputSchema = new() { Properties = properties, Required = required } });
        }

        tools.AddRange(BetaProviderTools(request.ProviderTools));
        return tools;
    }

    /// <summary>DipTick's tools as the plain API takes them, then the provider's own that the request asks for.</summary>
    private static List<ApiMessages.ToolUnion> PlainTools(ChatRequest request)
    {
        List<ApiMessages.ToolUnion> tools = [];
        foreach (var tool in request.Tools)
        {
            var (properties, required) = Schema(tool);
            tools.Add(new ApiMessages.Tool { Name = tool.Name, Description = tool.Description, InputSchema = new() { Properties = properties, Required = required } });
        }

        tools.AddRange(PlainProviderTools(request.ProviderTools));
        return tools;
    }

    /// <summary>
    /// Anthropic's own search and fetch, the basic versions: each
    /// capped at a turn's calls; the fetch with citations on and at most <see cref="FetchTokens"/> of a
    /// page; no location, so nothing about where the person is goes with a search.
    /// </summary>
    private static IEnumerable<BetaMessages.BetaToolUnion> BetaProviderTools(IReadOnlyList<string> names)
    {
        if (names.Contains(Research.ProviderResearch.Search))
        {
            yield return new BetaMessages.BetaWebSearchTool20250305 { MaxUses = ProviderUses };
        }

        if (names.Contains(Research.ProviderResearch.Fetch))
        {
            yield return new BetaMessages.BetaWebFetchTool20250910 { MaxUses = ProviderUses, Citations = new() { Enabled = true }, MaxContentTokens = FetchTokens };
        }
    }

    /// <summary>Anthropic's own search and fetch as the plain API takes them, the same way as <see cref="BetaProviderTools"/>.</summary>
    private static IEnumerable<ApiMessages.ToolUnion> PlainProviderTools(IReadOnlyList<string> names)
    {
        if (names.Contains(Research.ProviderResearch.Search))
        {
            yield return new ApiMessages.WebSearchTool20250305 { MaxUses = ProviderUses };
        }

        if (names.Contains(Research.ProviderResearch.Fetch))
        {
            yield return new ApiMessages.WebFetchTool20250910 { MaxUses = ProviderUses, Citations = new() { Enabled = true }, MaxContentTokens = FetchTokens };
        }
    }

    /// <summary>
    /// One step forward in the stream, with the SDK's own errors (refused, rate limited, unreachable)
    /// made DipTick's, so every caller tells an answer that never came from the model's words the
    /// same way. A yield cannot sit inside a try with a catch, so only the step is guarded.
    /// </summary>
    private async Task<bool> Step(ValueTask<bool> next)
    {
        try
        {
            return await next.ConfigureAwait(false);
        }
        catch (Anthropic.Exceptions.AnthropicException ex)
        {
            throw new ChatException(Failure(new Uri(_base).Host, ex), ex);
        }
    }

    /// <summary>
    /// One line for an SDK error: the status and Anthropic's message when the API answered, or why
    /// it could not be reached. Anthropic's errors never repeat the key.
    /// </summary>
    internal static string Failure(string host, Anthropic.Exceptions.AnthropicException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error is Anthropic.Exceptions.AnthropicApiException api
            ? $"{host} refused ({Convert.ToInt32(api.StatusCode, System.Globalization.CultureInfo.InvariantCulture)}): {OpenAiCompatibleEngine.Clip(api.Message)}"
            : $"Could not reach {host}: {OpenAiCompatibleEngine.Clip(error.Message)}";
    }
}
