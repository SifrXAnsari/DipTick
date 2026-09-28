using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DipTick.Models.Chat;
using DipTick.Models.Harness;
using DipTick.Models.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DipTick.Models.Connectors;

/// <summary>
/// A connector's tools as agents see them: only those of connectors on in the
/// chat, that a rule could allow (<c>mcp.call:&lt;connector&gt;/&lt;tool&gt;</c>), whose schema DipTick can
/// pass on; each named <c>mcp__&lt;connector&gt;__&lt;tool&gt;</c> in what every engine takes (Anthropic
/// <c>^[a-zA-Z0-9_-]{1,128}$</c>; OpenAI and Gemini at most 64, Gemini starting with a letter or _), and
/// described as the connector's words, cleaned and cut short.
/// </summary>
internal static class ConnectorTools
{
    /// <summary>The longest a tool's name may be for every engine.</summary>
    public const int MostName = 64;

    /// <summary>The most of a tool's description passed on: its words are the connector's, not DipTick's.</summary>
    public const int MostDescription = 2000;

    /// <summary>The largest schema passed on, in bytes.</summary>
    public const int MostSchema = 32 * 1024;

    /// <summary>The tools this agent may be offered in this chat, from the connectors on in it; a connector that cannot be reached now offers none.</summary>
    public static async Task<IEnumerable<IAgentTool>> ForAsync(ToolContext context, IReadOnlyList<ChatRule> rules, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rules);
        var on = context.Store.ChatConnectors(context.Session);
        if (on.Count == 0)
        {
            return [];
        }

        var tools = new List<IAgentTool>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var connector in ConnectorFile.Load(context.Config).Connectors.Where(c => on.Contains(c.Name, StringComparer.Ordinal) && c.Enabled))
        {
            if (ConnectorHub.LocalProblem(connector) is not null)
            {
                continue;
            }

            IReadOnlyList<McpClientTool> offered;
            try
            {
                offered = await ConnectorHub.ToolsAsync(context.Config, connector, interactive: false, say: null, cancel).ConfigureAwait(false);
            }
            catch (ConnectorException)
            {
                // Not reachable, or not signed in: Settings > Connectors says which; this turn goes on without it.
                continue;
            }

            foreach (var tool in offered)
            {
                var target = Target(connector.Name, tool.Name);
                if (Policy.Decide(Capability.McpCall, target, context.Config.Permissions, context.Gate.Flags, context.Agent, rules).Verdict == Verdict.Deny)
                {
                    continue;
                }

                if (Schema(tool.ProtocolTool.InputSchema) is not { } schema)
                {
                    continue;
                }

                tools.Add(new ConnectorTool(connector, tool.Name, Name(connector.Name, tool.Name, taken), Describe(connector.Name, tool), schema));
            }
        }

        return tools;
    }

    /// <summary>What a call is gated as: the connector and the tool, <c>clickup/create_task</c>.</summary>
    public static string Target(string connector, string tool) => $"{connector}/{tool}";

    /// <summary>
    /// A tool's name for the model: <c>mcp__&lt;connector&gt;__&lt;tool&gt;</c>, anything outside a-z, A-Z,
    /// 0-9, _ and - made _; one longer than 64, or already taken, cut and given six hex digits of its
    /// SHA-256 so two never meet.
    /// </summary>
    public static string Name(string connector, string tool, HashSet<string> taken)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(taken);
        var clean = string.Concat(tool.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
        var name = $"mcp__{connector}__{clean}";
        if (name.Length > MostName || taken.Contains(name))
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Target(connector, tool))))[..6];
            name = $"{name[..Math.Min(name.Length, MostName - 7)]}_{hash}";
        }

        taken.Add(name);
        return name;
    }

    /// <summary>A tool's description as the model is given it: marked as the connector's, its title first, cleaned of control and invisible characters, cut at <see cref="MostDescription"/>.</summary>
    public static string Describe(string connector, McpClientTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var words = SafeText.Line($"{(tool.Title is { Length: > 0 } title && title != tool.Name ? title + ". " : string.Empty)}{tool.Description}");
        if (words.Length > MostDescription)
        {
            words = words[..MostDescription] + "…";
        }

        return $"A tool of the {connector} connector, in its own words: {(words.Length == 0 ? "(it says nothing about itself)" : words)}";
    }

    /// <summary>
    /// A tool's schema as DipTick passes it on: an object schema of at most <see cref="MostSchema"/> bytes;
    /// one that names no type is taken as an object; anything else is not offered.
    /// </summary>
    public static JsonElement? Schema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (schema.TryGetProperty("type", out var type) && !(type.ValueKind == JsonValueKind.String && type.GetString() == "object"))
        {
            return null;
        }

        var raw = schema.GetRawText();
        if (Encoding.UTF8.GetByteCount(raw) > MostSchema)
        {
            return null;
        }

        if (schema.TryGetProperty("type", out _))
        {
            return schema.Clone();
        }

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("type", "object");
            foreach (var property in schema.EnumerateObject())
            {
                property.WriteTo(json);
            }

            json.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(buffer.ToArray());
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// What a call gave back, for the model: its text, a resource's text under its address, a link named,
    /// the first image as a file for a model that sees (the rest described), structured content as JSON
    /// when there is no text; all at most <see cref="AgentTools.MostResult"/> characters. A result marked
    /// as an error is the server's words, as a failed call.
    /// </summary>
    public static ToolOutcome Outcome(string connector, string tool, CallToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        // Lines end in a line feed alone, as everything DipTick sends a model does.
        var text = new StringBuilder();
        WireFile? image = null;
        foreach (var block in result.Content ?? [])
        {
            switch (block)
            {
                case TextContentBlock words:
                    text.Append(words.Text).Append('\n');
                    break;
                case ImageContentBlock picture when image is null && picture.DecodedData.Length <= Files.Attachments.MostFile && Files.Attachments.IsImage(picture.MimeType ?? string.Empty):
                    image = new WireFile(string.Create(CultureInfo.InvariantCulture, $"{connector}-{tool}-image"), picture.MimeType!, picture.DecodedData.ToArray());
                    text.Append(CultureInfo.InvariantCulture, $"[an image, {picture.MimeType}, sent with this result]").Append('\n');
                    break;
                case ImageContentBlock picture:
                    text.Append(CultureInfo.InvariantCulture, $"[an image, {SafeText.Line(picture.MimeType ?? "of no type")}, {picture.DecodedData.Length} bytes, not sent]").Append('\n');
                    break;
                case AudioContentBlock sound:
                    text.Append(CultureInfo.InvariantCulture, $"[audio, {SafeText.Line(sound.MimeType ?? "of no type")}, {sound.DecodedData.Length} bytes, not sent]").Append('\n');
                    break;
                case EmbeddedResourceBlock { Resource: TextResourceContents resource }:
                    text.Append(CultureInfo.InvariantCulture, $"[{SafeText.Line(resource.Uri)}]").Append('\n').Append(resource.Text).Append('\n');
                    break;
                case EmbeddedResourceBlock { Resource: BlobResourceContents blob }:
                    text.Append(CultureInfo.InvariantCulture, $"[a file, {SafeText.Line(blob.Uri)}, {SafeText.Line(blob.MimeType ?? "of no type")}, {blob.DecodedData.Length} bytes, not sent]").Append('\n');
                    break;
                case ResourceLinkBlock link:
                    text.Append(CultureInfo.InvariantCulture, $"[a link: {SafeText.Line(link.Name)} {SafeText.Line(link.Uri)}]").Append('\n');
                    break;
                default:
                    text.Append(CultureInfo.InvariantCulture, $"[a {SafeText.Line(block.Type)} block, not shown]").Append('\n');
                    break;
            }
        }

        if (text.Length == 0 && result.StructuredContent is { } structured)
        {
            text.Append(structured.GetRawText());
        }

        var content = text.ToString().Trim();
        if (content.Length > AgentTools.MostResult)
        {
            content = content[..AgentTools.MostResult] + string.Create(CultureInfo.InvariantCulture, $"\n[cut: {content.Length - AgentTools.MostResult} more characters]");
        }

        var first = SafeText.Line(content.Split('\n', 2)[0].TrimEnd('\r'));
        var summary = $"{Target(connector, tool)}: {(first.Length > 80 ? first[..80] + "…" : first)}";
        return result.IsError == true
            ? new ToolOutcome($"error: {(content.Length == 0 ? "the tool failed and said nothing" : content)}", summary, ToolEnd.Failed)
            : new ToolOutcome(content.Length == 0 ? "(the tool gave back nothing)" : content, summary) { File = image };
    }
}

/// <summary>One of a connector's tools, as an agent calls it: gated as <c>mcp.call</c>, its arguments shown to you when it asks, and never recorded.</summary>
internal sealed class ConnectorTool(Connector connector, string tool, string name, string description, JsonElement parameters) : IAgentTool
{
    public string Name => name;

    public string Description => description;

    public JsonElement Parameters => parameters;

    public string? Connector => connector.Name;

    public TimeSpan Limit => connector.Limit;

    public async Task<ToolOutcome> RunAsync(ToolContext context, JsonElement arguments, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(context);
        var target = ConnectorTools.Target(connector.Name, tool);
        if (!context.Gate.Request("call", Capability.McpCall, target, vouchedBy: null, detail: arguments.ValueKind == JsonValueKind.Object ? arguments.GetRawText() : null))
        {
            return ToolOutcome.Refused($"mcp.call {target}");
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in arguments.EnumerateObject())
            {
                values[property.Name] = property.Value.Clone();
            }
        }

        try
        {
            var result = await ConnectorHub.CallAsync(context.Config, connector, tool, values, cancel).ConfigureAwait(false);
            return ConnectorTools.Outcome(connector.Name, tool, result);
        }
        catch (ConnectorException ex)
        {
            return ToolOutcome.Failed(ex.Message);
        }
    }
}
