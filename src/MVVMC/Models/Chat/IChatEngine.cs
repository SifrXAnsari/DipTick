using System.Runtime.CompilerServices;
using DipTick.Models.Harness;

namespace DipTick.Models.Chat;

/// <summary>Who said a line in the transcript.</summary>
internal enum Role
{
    Person,
    Agent,
    System,
    Tool,
}

/// <summary>One line in the transcript; an agent's answer also says who wrote it, where in its round, and what it used.</summary>
internal sealed record ChatLine(Role Role, string Text, DateTimeOffset When)
{
    /// <summary>The agent's name at the chat's table; empty for anyone else's line.</summary>
    public string Seat { get; init; } = string.Empty;

    /// <summary>The model the agent answered with.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>The agent's colour (<c>&lt;id&gt;.color</c>; green for the model that answers you).</summary>
    public string Color { get; init; } = string.Empty;

    /// <summary>The line's number in its chat, once it has one.</summary>
    public int? Seq { get; init; }

    /// <summary>The round the line came in, from 1; 0 when it is not known.</summary>
    public int Round { get; init; }

    /// <summary>The step of its round: answers of one step were given at the same time, blind to each other.</summary>
    public int Step { get; init; }

    /// <summary>How the line ended (<see cref="Store.MessageStatus"/>); streaming while it comes.</summary>
    public string Status { get; init; } = Store.MessageStatus.Done;

    /// <summary>Tokens the answer read, where known.</summary>
    public int? TokensIn { get; init; }

    /// <summary>Tokens the answer wrote, where known.</summary>
    public int? TokensOut { get; init; }

    /// <summary>Whether DipTick counted the tokens itself, the provider not having said.</summary>
    public bool Estimated { get; init; }

    /// <summary>What the answer cost, where its agent has prices.</summary>
    public decimal? Cost { get; init; }

    /// <summary>What DipTick read of the answer's act block, in words: a question put to another agent.</summary>
    public IReadOnlyList<string> Acts { get; init; } = [];

    /// <summary>The files that came with the line: attached by the person, or made by an agent or by <c>/image</c>.</summary>
    public IReadOnlyList<ChatFile> Files { get; init; } = [];
}

/// <summary>
/// A file of a chat as a screen shows it: its id in the chat (0 until the chat has kept it), name, media
/// type, size, SHA-256, who brought it, and where it is on disk, when it is anywhere.
/// </summary>
internal sealed record ChatFile(long Id, string Name, string Type, long Size, string Sha256, string Author, string? Path);

/// <summary>Who a message on the wire is from, as a provider's API takes it: the person's side, or the agent's own.</summary>
internal enum WireRole
{
    User,
    Assistant,
}

/// <summary>
/// One message as it is sent. DipTick composes them; a provider sees only these and the briefing. An
/// agent's message may carry the tool calls it made (<see cref="Calls"/>); the person's side may carry
/// what those calls gave back (<see cref="Results"/>), and files (<see cref="Files"/>).
/// </summary>
internal sealed record WireMessage(WireRole Role, string Text)
{
    /// <summary>The tool calls an agent's message made, in order.</summary>
    public IReadOnlyList<ToolCall> Calls { get; init; } = [];

    /// <summary>What each of the calls just before gave back, one result a call.</summary>
    public IReadOnlyList<ToolResult> Results { get; init; } = [];

    /// <summary>Files sent with the person's side: images and documents.</summary>
    public IReadOnlyList<WireFile> Files { get; init; } = [];

    /// <summary>
    /// An agent's message as its provider sent it, block by block, to be sent back unchanged within the
    /// turn: a provider's own tool calls and results, thought signatures.
    /// Only the engine named by <see cref="BlocksFrom"/> reads them; any other sends the words and calls.
    /// </summary>
    public IReadOnlyList<System.Text.Json.JsonElement> Blocks { get; init; } = [];

    /// <summary>The engine shape the blocks are in (<see cref="EchoPart.Shape"/>); empty when there are none.</summary>
    public string BlocksFrom { get; init; } = string.Empty;

    /// <summary>Whether the message carries anything at all.</summary>
    public bool IsEmpty => Text.Length == 0 && Calls.Count == 0 && Results.Count == 0 && Files.Count == 0 && Blocks.Count == 0;
}

/// <summary>A tool as a model is told of it: its name, what it does, and its arguments as a JSON Schema object.</summary>
internal sealed record ToolSpec(string Name, string Description, System.Text.Json.JsonElement Parameters);

/// <summary>A tool call a model made: the id its API gave it, the tool's name, and its arguments as the JSON text the model wrote.</summary>
internal sealed record ToolCall(string Id, string Name, string Arguments);

/// <summary>
/// What one tool call gave back: the call it answers, the tool, its words (already in DipTick's
/// envelope), whether it failed, and a file it brought back (a fetched PDF) for a model that reads one.
/// </summary>
internal sealed record ToolResult(string CallId, string Name, string Content, bool IsError = false)
{
    /// <summary>A file the call brought back, sent beside its words where the engine can; null for none.</summary>
    public WireFile? File { get; init; }
}

/// <summary>A file sent to a model: its name, its media type, and its bytes.</summary>
internal sealed record WireFile(string Name, string MediaType, byte[] Bytes);

/// <summary>
/// One request to a model: the briefing (its system text; empty for none), the
/// messages, the person's side last, the most tokens its answer may take (0: the engine's own
/// default), and the verb the audit log names the call under. <see cref="Tools"/> are the tools the
/// model may call in this request; none by default.
/// </summary>
internal sealed record ChatRequest(string System, IReadOnlyList<WireMessage> Messages, int MaxTokens = 0, string Verb = "ask")
{
    /// <summary>The tools the model may call; none unless the round offers them.</summary>
    public IReadOnlyList<ToolSpec> Tools { get; init; } = [];

    /// <summary>Tools the provider runs itself (its own web search and fetch), by DipTick's name for each; none by default.</summary>
    public IReadOnlyList<string> ProviderTools { get; init; } = [];

    /// <summary>
    /// The next request of an answer the gate already let through, after its tool calls: recorded, and
    /// not asked about again (<see cref="Gate.Continue"/>).
    /// </summary>
    public bool Continues { get; init; }

    /// <summary>A transcript as a request with no briefing: the person's and the agent's lines, nothing else.</summary>
    public static ChatRequest From(IReadOnlyList<ChatLine> transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        return new ChatRequest(
            string.Empty,
            [.. transcript.Where(l => l.Role is Role.Person or Role.Agent && l.Text.Length > 0)
                .Select(l => new WireMessage(l.Role == Role.Person ? WireRole.User : WireRole.Assistant, l.Text))]);
    }
}

/// <summary>How an answer ended, as the store keeps it.</summary>
internal enum AnswerEnd
{
    /// <summary>It ended where the model meant it to.</summary>
    Done,

    /// <summary>Cut short: the token cap, a content filter, or a stop DipTick does not take (tools it did not offer).</summary>
    Cut,

    /// <summary>The model, or its provider, declined to answer.</summary>
    Refused,

    /// <summary>The model called tools and waits for what they give back.</summary>
    Tools,
}

/// <summary>A piece of an answer as it streams: its words, what it used, or how it ended.</summary>
internal abstract record EnginePart;

/// <summary>More of the answer's words.</summary>
internal sealed record TextPart(string Text) : EnginePart;

/// <summary>A tool call the model finished writing: its id, the tool, and its arguments as JSON text.</summary>
internal sealed record ToolCallPart(ToolCall Call) : EnginePart;

/// <summary>
/// A source a provider's own tool read and cited (Anthropic's web search and fetch, Google's grounding):
/// its address, its title, and the words cited from it, when the answer gives them.
/// </summary>
internal sealed record CitationPart(string Address, string Title, string CitedText) : EnginePart;

/// <summary>A page a provider's own tool found or read: its address, its title, and whether the provider read it whole.</summary>
internal sealed record FoundPage(string Address, string Title, bool Read);

/// <summary>
/// A provider's own tool ran inside the answer: which (<c>web_search</c>,
/// <c>web_fetch</c>), what it was asked, one line on what came of it, the pages it found or read, and
/// whether it failed. DipTick did not run it; it shows it, keeps its pages as sources, and records it.
/// </summary>
internal sealed record ProviderToolPart(string Tool, string Asked, string Summary, IReadOnlyList<FoundPage> Pages, bool Failed = false) : EnginePart;

/// <summary>
/// The agent's message of this request as its provider sent it, to be sent back unchanged when the
/// turn goes on after DipTick's tools: its blocks, and the engine shape
/// they are in. Sent just before the tool calls.
/// </summary>
internal sealed record EchoPart(string Shape, IReadOnlyList<System.Text.Json.JsonElement> Blocks) : EnginePart;

/// <summary>What the answer used so far, as the provider counts it: the tokens read and written, each null when not said.</summary>
internal sealed record UsagePart(int? TokensIn, int? TokensOut) : EnginePart;

/// <summary>How the answer ended, and DipTick's words for why when it did not end whole (a refusal's own words among them).</summary>
internal sealed record FinishPart(AnswerEnd End, string Why = "") : EnginePart;

/// <summary>
/// Whatever answers. Streams its answer in parts so the screen can draw as it arrives. Every call it
/// makes goes through the harness gate it is handed, never around it.
/// </summary>
internal interface IChatEngine
{
    /// <summary>A short name for the status line, e.g. "ollama llama3".</summary>
    string Name { get; }

    IAsyncEnumerable<EnginePart> StreamAsync(ChatRequest request, Gate gate, CancellationToken cancel);
}

/// <summary>What the callers that want only words ask of an engine.</summary>
internal static class ChatEngineAnswers
{
    /// <summary>
    /// The answer's words alone, to a transcript sent with no briefing: what <c>ask</c> wants. A
    /// refusal is thrown.
    /// </summary>
    public static async IAsyncEnumerable<string> AnswerAsync(this IChatEngine engine, IReadOnlyList<ChatLine> transcript, Gate gate, [EnumeratorCancellation] CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(engine);
        await foreach (var part in engine.StreamAsync(ChatRequest.From(transcript), gate, cancel).ConfigureAwait(false))
        {
            switch (part)
            {
                case TextPart text:
                    yield return text.Text;
                    break;
                case FinishPart { End: AnswerEnd.Refused } refused:
                    throw new ChatException(refused.Why);
            }
        }
    }
}

/// <summary>
/// An answer that could not be had: refused, unreachable, misconfigured. Thrown by an engine
/// instead of being streamed as if the model had said it, so a caller can tell the two apart
/// (the command line exits 1; the screen shows it as DipTick's line, not the model's).
/// </summary>
internal sealed class ChatException : Exception
{
    public ChatException()
    {
    }

    public ChatException(string message)
        : base(message)
    {
    }

    public ChatException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>What answers while no provider is set: it says so, and nothing else.</summary>
internal sealed class NoEngine(string why) : IChatEngine
{
    public string Name => "no model set";

    public async IAsyncEnumerable<EnginePart> StreamAsync(ChatRequest request, Gate gate, [EnumeratorCancellation] CancellationToken cancel)
    {
        await Task.Yield();
        cancel.ThrowIfCancellationRequested();
        if (why.Length > 0)
        {
            throw new ChatException(why);
        }

        yield break;
    }
}
