using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DipTick.Models.Chat;

/// <summary>
/// One answer's content blocks as Anthropic streams them, rebuilt from the events: each block as
/// <c>content_block_start</c> gave it, with its text, input, citations, thinking and signature
/// filled in from the deltas, so it can be sent back as it came. On the way it
/// says what the stream holds for DipTick: the client tool calls, a provider's own tool results (which
/// arrive whole in their start event), and citations to what those tools read.
/// </summary>
/// <remarks>
/// Built from the raw JSON of each event (every SDK union keeps it as <c>Json</c>), so the beta and
/// plain streams share one reader, and a block type DipTick does not know is still kept and sent back.
/// One instance serves one engine call, across its <c>pause_turn</c> continuations: a server tool's
/// call in one response and its result in the next are paired by id.
/// </remarks>
internal sealed class AnthropicBlocks
{
    private readonly SortedDictionary<long, JsonObject> _open = [];
    private readonly Dictionary<long, StringBuilder> _texts = [];
    private readonly Dictionary<long, StringBuilder> _thinking = [];
    private readonly Dictionary<long, StringBuilder> _inputs = [];
    private readonly List<JsonObject> _done = [];
    private readonly Dictionary<string, (string Name, string Input)> _serverCalls = new(StringComparer.Ordinal);
    private readonly List<(string Address, string Title)> _documents = [];

    /// <summary>Every finished block of the answer so far, as it came, in order.</summary>
    public IReadOnlyList<JsonElement> Blocks => [.. _done.Select(b => JsonSerializer.SerializeToElement(b))];

    /// <summary>Whether any finished block is a provider's own tool call or result, or thinking: what cannot be rebuilt from words and calls.</summary>
    public bool NeedsEcho => _done.Any(b => Type(b) is not ("text" or "tool_use"));

    /// <summary>A block starts: kept; a provider's tool result, which comes whole, is read at once.</summary>
    public ProviderToolPart? Start(long index, JsonElement block)
    {
        var node = JsonNode.Parse(block.GetRawText()) as JsonObject ?? [];
        _open[index] = node;
        var type = Type(node);
        switch (type)
        {
            case "text":
                _texts[index] = new StringBuilder(Text(node, "text"));
                break;
            case "thinking":
                _thinking[index] = new StringBuilder(Text(node, "thinking"));
                break;
            case "tool_use" or "server_tool_use":
                _inputs[index] = new StringBuilder();
                break;
            case "web_search_tool_result" or "web_fetch_tool_result":
                return Result(block, type);
        }

        return null;
    }

    /// <summary>A delta: more of a block's text (returned, to show), input, thinking or signature, or a citation (returned).</summary>
    public (string Text, CitationPart? Cited) Delta(long index, JsonElement delta)
    {
        var type = delta.ValueKind == JsonValueKind.Object && delta.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        switch (type)
        {
            case "text_delta":
                var text = Str(delta, "text");
                if (_texts.TryGetValue(index, out var words))
                {
                    words.Append(text);
                }

                return (text, null);
            case "input_json_delta":
                if (_inputs.TryGetValue(index, out var input))
                {
                    input.Append(Str(delta, "partial_json"));
                }

                break;
            case "thinking_delta":
                if (_thinking.TryGetValue(index, out var thought))
                {
                    thought.Append(Str(delta, "thinking"));
                }

                break;
            case "signature_delta":
                if (_open.TryGetValue(index, out var signed))
                {
                    signed["signature"] = Str(delta, "signature");
                }

                break;
            case "citations_delta" when delta.TryGetProperty("citation", out var citation) && citation.ValueKind == JsonValueKind.Object:
                if (_open.TryGetValue(index, out var cited))
                {
                    var list = cited["citations"] as JsonArray ?? [];
                    list.Add(JsonNode.Parse(citation.GetRawText()));
                    cited["citations"] = list;
                }

                return (string.Empty, Cited(citation));
        }

        return (string.Empty, null);
    }

    /// <summary>A block ends: finished and kept; a client tool call returned, a provider's call remembered for its result.</summary>
    public ToolCall? Stop(long index)
    {
        if (!_open.Remove(index, out var block))
        {
            return null;
        }

        if (_texts.Remove(index, out var text))
        {
            block["text"] = text.ToString();
        }

        if (_thinking.Remove(index, out var thought))
        {
            block["thinking"] = thought.ToString();
        }

        // The input streams as JSON pieces after an empty start; a start that already carried it whole keeps it.
        string? arguments = null;
        if (_inputs.Remove(index, out var input))
        {
            arguments = input.Length > 0 ? input.ToString() : block["input"] is JsonObject given ? given.ToJsonString() : "{}";
            block["input"] = Object(arguments);
        }

        // A block goes back as it came, less the empty fields a start event carries.
        foreach (var name in block.Where(p => p.Value is null).Select(p => p.Key).ToList())
        {
            block.Remove(name);
        }

        _done.Add(block);
        var type = Type(block);
        var id = Text(block, "id");
        if (type == "tool_use")
        {
            return new ToolCall(id, Text(block, "name"), arguments ?? "{}");
        }

        if (type == "server_tool_use" && id.Length > 0)
        {
            _serverCalls[id] = (Text(block, "name"), arguments ?? "{}");
        }

        return null;
    }

    /// <summary>
    /// A provider's search or fetch result (Anthropic's pages): a list of <c>web_search_result</c>s or
    /// one <c>web_search_tool_result_error</c>; one <c>web_fetch_result</c> with its document or one
    /// <c>web_fetch_tool_result_error</c>; named for the call it answers, when this answer made it.
    /// </summary>
    private ProviderToolPart? Result(JsonElement block, string type)
    {
        var search = type == "web_search_tool_result";
        var tool = search ? Research.ProviderResearch.Search : Research.ProviderResearch.Fetch;
        var asked = _serverCalls.TryGetValue(Str(block, "tool_use_id"), out var call) ? Asked(call.Input, search ? "query" : "url") : string.Empty;
        if (!block.TryGetProperty("content", out var content))
        {
            return null;
        }

        if (content.ValueKind == JsonValueKind.Object && content.TryGetProperty("error_code", out var code))
        {
            return new ProviderToolPart(tool, asked, $"failed: {ErrorWords(code.GetString() ?? string.Empty)}", [], Failed: true);
        }

        if (search && content.ValueKind == JsonValueKind.Array)
        {
            var pages = content.EnumerateArray()
                .Where(r => Str(r, "url").Length > 0)
                .Select(r => new FoundPage(Str(r, "url"), Str(r, "title"), Read: false))
                .ToList();
            var words = asked.Length > 0 ? $" for \"{SafeText.Line(asked)}\"" : string.Empty;
            return new ProviderToolPart(tool, asked, string.Create(CultureInfo.InvariantCulture, $"{pages.Count} result{(pages.Count == 1 ? string.Empty : "s")}{words}"), pages);
        }

        if (!search && content.ValueKind == JsonValueKind.Object && Str(content, "type") == "web_fetch_result")
        {
            var address = Str(content, "url");
            var title = content.TryGetProperty("content", out var document) ? Str(document, "title") : string.Empty;
            _documents.Add((address, title.Length > 0 ? title : address));
            var host = Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Host : address;
            return new ProviderToolPart(tool, asked.Length > 0 ? asked : address, $"read {SafeText.Line(host)}{(title.Length > 0 ? $": {SafeText.Line(title)}" : string.Empty)}", [new FoundPage(address, title, Read: true)]);
        }

        return null;
    }

    /// <summary>
    /// A citation in the answer: a search result's (its address, title and up to 150 characters of what
    /// it says), or a fetched document's, found by its title, else by its place among the documents
    /// fetched in this answer.
    /// </summary>
    private CitationPart? Cited(JsonElement citation)
    {
        var cited = Str(citation, "cited_text");
        switch (Str(citation, "type"))
        {
            case "web_search_result_location":
                var address = Str(citation, "url");
                return address.Length > 0 ? new CitationPart(address, Str(citation, "title"), cited) : null;
            case "char_location" or "page_location" or "content_block_location":
                var title = Str(citation, "document_title");
                var at = title.Length > 0 ? _documents.FindLastIndex(d => d.Title == title) : -1;
                if (at < 0 && citation.TryGetProperty("document_index", out var index) && index.ValueKind == JsonValueKind.Number && index.TryGetInt32(out var i) && i >= 0 && i < _documents.Count)
                {
                    at = i;
                }

                return at < 0 ? null : new CitationPart(_documents[at].Address, _documents[at].Title, cited);
            default:
                return null;
        }
    }

    /// <summary>What a provider's tool was asked: the search's words or the page's address, from the call's input.</summary>
    private static string Asked(string input, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(input);
            return Str(doc.RootElement, field);
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>An error code of Anthropic's search or fetch, in words (its pages list them).</summary>
    internal static string ErrorWords(string code) => code switch
    {
        "too_many_requests" => "too many searches for now",
        "invalid_tool_input" or "invalid_input" => "what it asked was not valid",
        "max_uses_exceeded" => "this answer reached its cap of searches or fetches",
        "query_too_long" => "the search was too long",
        "request_too_large" => "the search asked too much",
        "url_too_long" => "the address was too long",
        "url_not_allowed" => "the address is not allowed (a private address, robots.txt, or a credential in it)",
        "url_not_in_prior_context" => "the address was not in the conversation",
        "url_not_accessible" => "the page could not be fetched",
        "unsupported_content_type" => "the page is not text, HTML or PDF",
        "unavailable" => "the provider's tool was unavailable",
        _ => SafeText.Line(code),
    };

    /// <summary>A tool call's input as the object it must go back as: the model's JSON object, or an empty one.</summary>
    private static JsonObject Object(string arguments)
    {
        try
        {
            return JsonNode.Parse(arguments) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Type(JsonObject block) => Text(block, "type");

    private static string Text(JsonObject node, string name) =>
        node.TryGetPropertyValue(name, out var value) && value is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;

    private static string Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
}
