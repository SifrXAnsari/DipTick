using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DipTick.Models.Chat.Store;
using DipTick.Models.Harness;

namespace DipTick.Models.Tools;

/// <summary>
/// What a tool gave back for one call: its words for the model (the loop puts them in an envelope),
/// one line for the chat's folded tool row, and whether it failed or was refused.
/// </summary>
internal sealed record ToolOutcome(string Content, string Summary, ToolEnd End = ToolEnd.Done)
{
    /// <summary>The chat's sources the call kept (its number for each), so the answer it served can name them.</summary>
    public IReadOnlyList<int> Sources { get; init; } = [];

    /// <summary>A file the call brought back for the model (a fetched PDF, for a model that reads one); null for none.</summary>
    public Chat.WireFile? File { get; init; }

    /// <summary>The chat's attachments the call made (an image, a video), so the answer it served shows them.</summary>
    public IReadOnlyList<long> Attachments { get; init; } = [];

    /// <summary>A call that could not be done: the model is told why in a few words.</summary>
    public static ToolOutcome Failed(string why) => new($"error: {why}", why, ToolEnd.Failed);

    /// <summary>
    /// A call the gate refused. The model is told only that: a refusal that explains itself is an
    /// oracle (<see cref="Gate"/>). The person reads why in the audit log.
    /// </summary>
    public static ToolOutcome Refused(string what) => new("not allowed", $"{what}: not allowed", ToolEnd.Denied);
}

/// <summary>How a tool call ended, as its row in the chat and its record say.</summary>
internal enum ToolEnd
{
    Done,
    Failed,
    Denied,
}

/// <summary>
/// Everything one tool call runs with: the settings, the chat, the agent
/// calling and its own gate, the store, whether the chat is private, and what the chat has seen, for
/// the tools that may only reach what the person or a search gave.
/// </summary>
internal sealed record ToolContext(
    MasterConfig Config,
    string Session,
    SeatConfig Agent,
    Gate Gate,
    IChatStore Store,
    bool Private,
    Seen Seen);

/// <summary>
/// One of the tools an agent may call. It describes itself to the model (a name, what it does, its
/// arguments as a JSON Schema object) and does one call's work, asking the agent's gate itself for
/// whatever it touches: a tool that fetches three pages asks three times.
/// </summary>
internal interface IAgentTool
{
    /// <summary>Its name, as the model calls it: a-z, 0-9 and _.</summary>
    string Name { get; }

    /// <summary>What it does and when to use it, for the model.</summary>
    string Description { get; }

    /// <summary>Its arguments, as a JSON Schema object.</summary>
    JsonElement Parameters { get; }

    /// <summary>The connector it belongs to, for the record; null for DipTick's own.</summary>
    string? Connector => null;

    /// <summary>How long one call may take: a minute, unless the tool's own work is known to take longer (a video).</summary>
    TimeSpan Limit => AgentTools.CallLimit;

    Task<ToolOutcome> RunAsync(ToolContext context, JsonElement arguments, CancellationToken cancel);
}

/// <summary>Building a tool's JSON Schema, reading its arguments, and a call's digest.</summary>
internal static class ToolArgs
{
    /// <summary>
    /// An object schema from its properties, each a name, a JSON type, what it is, and whether it is
    /// required; <c>additionalProperties</c> false, so the model is told there is nothing else.
    /// </summary>
    public static JsonElement Schema(params (string Name, string Type, string About, bool Required)[] properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("type", "object");
            json.WriteStartObject("properties");
            foreach (var (name, type, about, _) in properties)
            {
                json.WriteStartObject(name);
                json.WriteString("type", type);
                json.WriteString("description", about);
                json.WriteEndObject();
            }

            json.WriteEndObject();
            json.WriteStartArray("required");
            foreach (var (name, _, _, required) in properties)
            {
                if (required)
                {
                    json.WriteStringValue(name);
                }
            }

            json.WriteEndArray();
            json.WriteBoolean("additionalProperties", false);
            json.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(buffer.ToArray());
        return doc.RootElement.Clone();
    }

    /// <summary>A string argument, trimmed; null when it is missing, not a string, or empty.</summary>
    public static string? Text(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text && text.Trim().Length > 0
            ? text.Trim()
            : null;

    /// <summary>A whole-number argument, from a number or a string of digits; null when it is missing or not one.</summary>
    public static long? Number(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(value.GetString(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var s) => s,
            _ => null,
        };
    }

    /// <summary>The call's arguments as the model wrote them, as a JSON object; null when they are not one.</summary>
    public static JsonElement? Parse(string arguments)
    {
        try
        {
            using var doc = JsonDocument.Parse(arguments.Trim().Length == 0 ? "{}" : arguments, new JsonDocumentOptions { MaxDepth = 16 });
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>What the audit log keeps of a call's arguments: their SHA-256, never the arguments.</summary>
    public static string Digest(string arguments) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(arguments)));
}
