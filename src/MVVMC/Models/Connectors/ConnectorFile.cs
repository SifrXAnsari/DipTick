using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DipTick.Models.Tools;

namespace DipTick.Models.Connectors;

/// <summary>How a remote connector signs in, as its file says: your own client ID, the port its answer comes back on, and the scopes to ask for when the service names none.</summary>
internal sealed record ConnectorOAuth(string? ClientId, int? CallbackPort, string? Scopes);

/// <summary>
/// One connector as <c>connectors.json</c> keeps it: Claude Code's
/// <c>mcpServers</c> fields, then DipTick's own: whether it is on, the SHA-256 of the exact command you
/// said yes to, whether it may reach your private network, which headers' values are in Credential
/// Manager, the known connector it was added from, and for a local one, its sandbox.
/// </summary>
internal sealed record Connector(
    string Name,
    string Type,
    string? Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env,
    string? Url,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyList<string> SecretHeaders,
    ConnectorOAuth? OAuth,
    int? TimeoutMs,
    bool Enabled,
    string? Consent,
    bool AllowPrivateNetwork,
    string? Catalogue,
    ConnectorSandbox? Sandbox = null)
{
    /// <summary>A program on this PC, spoken to over its standard input and output.</summary>
    public bool Local => Type == ConnectorFile.Stdio;

    /// <summary>Where a local connector runs: its entry's sandbox, or its own with no network and no folders.</summary>
    public ConnectorSandbox Box => Sandbox ?? ConnectorSandbox.Default;

    /// <summary>How long one call may take: its <c>timeout</c>, else a minute, at most ten.</summary>
    public TimeSpan Limit => TimeoutMs is { } ms ? TimeSpan.FromMilliseconds(Math.Clamp(ms, 1000, 600_000)) : Tools.AgentTools.CallLimit;
}

/// <summary>One entry of another app's list, as it would be imported: the connector, the secrets taken out of it, and the name it had there.</summary>
internal sealed record ImportedConnector(Connector Connector, IReadOnlyDictionary<string, string> Secrets, string? ClientSecret, bool Renamed, string Original);

/// <summary>Another app's list as it would be imported, and what could not be.</summary>
internal sealed record ImportedList(IReadOnlyList<ImportedConnector> Entries, IReadOnlyList<string> Problems);

/// <summary>The connectors in the file, what was wrong with it, and where it is (null with no settings file).</summary>
internal sealed record ConnectorList(IReadOnlyList<Connector> Connectors, IReadOnlyList<string> Problems, string? Path)
{
    public Connector? Find(string name) => Connectors.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
}

/// <summary>
/// <c>connectors.json</c>, beside the settings file in use: Claude Code's <c>.mcp.json</c> shape
/// (code.claude.com, MCP: <c>type</c> stdio, http, sse or ws, <c>command</c>, <c>args</c>, <c>env</c>,
/// <c>url</c>, <c>headers</c>, <c>headersHelper</c>, <c>timeout</c>, <c>oauth</c>, <c>alwaysLoad</c>;
/// <c>${VAR}</c> and <c>${VAR:-default}</c> in command, args, env, url and headers), so one imports as
/// it is, with DipTick's own keys beside. No secret is ever written here. An entry that cannot be
/// read is left out and said, never half used.
/// </summary>
internal static partial class ConnectorFile
{
    public const string FileName = "connectors.json";

    public const string Stdio = "stdio";
    public const string Http = "http";
    public const string Sse = "sse";

    /// <summary>The most connectors one file holds.</summary>
    public const int MostConnectors = 50;

    /// <summary>The longest a connector's name may be: it becomes part of tool names, at most 64 characters.</summary>
    public const int MostName = 32;

    private static readonly JsonWriterOptions Indented = new() { Indented = true };

    /// <summary>Where the file is: beside the settings file in use; null with none (<c>--config none</c>).</summary>
    public static string? PathFor(MasterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Path is { } settings && System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(settings)) is { } folder
            ? System.IO.Path.Combine(folder, FileName)
            : null;
    }

    /// <summary>The connectors of the settings in use; none, and nothing wrong, when there is no file yet.</summary>
    public static ConnectorList Load(MasterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return PathFor(config) is { } path ? Read(path) : new ConnectorList([], [], null);
    }

    /// <summary>A file read and checked, entry by entry.</summary>
    public static ConnectorList Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
        {
            return new ConnectorList([], [], path);
        }

        string text;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 1024 * 1024)
            {
                return new ConnectorList([], [$"{path} is larger than a list of connectors can be; nothing in it is used."], path);
            }

            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ConnectorList([], [$"{path} could not be read: {SafeText.Line(ex.Message)}"], path);
        }

        var (connectors, problems) = Parse(text, source: path);
        return new ConnectorList(connectors, problems, path);
    }

    /// <summary>
    /// The connectors in a text of this shape, each checked; what was wrong with any, in words. An entry
    /// with a problem that would change what runs or where it connects is left out.
    /// </summary>
    public static (IReadOnlyList<Connector> Connectors, IReadOnlyList<string> Problems) Parse(string text, string source)
    {
        ArgumentNullException.ThrowIfNull(text);
        var problems = new List<string>();
        var connectors = new List<Connector>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 16 });
        }
        catch (JsonException ex)
        {
            return ([], [$"{source} is not JSON ({SafeText.Line(ex.Message)}); nothing in it is used."]);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("mcpServers", out var servers) || servers.ValueKind != JsonValueKind.Object)
            {
                return ([], [$"{source} has no \"mcpServers\" object; nothing in it is used."]);
            }

            foreach (var server in servers.EnumerateObject())
            {
                if (connectors.Count >= MostConnectors)
                {
                    problems.Add(string.Create(CultureInfo.InvariantCulture, $"{source} has more than {MostConnectors} connectors; the rest are left out."));
                    break;
                }

                if (connectors.Any(c => c.Name == server.Name))
                {
                    problems.Add($"{SafeText.Line(server.Name)} is in {source} twice; the second is left out.");
                    continue;
                }

                if (Entry(server.Name, server.Value, out var problem) is { } connector)
                {
                    connectors.Add(connector);
                }

                if (problem is not null)
                {
                    problems.Add($"{SafeText.Line(server.Name)}: {problem}");
                }
            }
        }

        return (connectors, problems);
    }

    /// <summary>
    /// Another app's list, read for <c>connectors import</c> (Claude Code's <c>.mcp.json</c> shape): each
    /// entry named as DipTick takes names, a local one off until its own yes, and the secrets another app
    /// keeps in its file (a secret header's value, <c>oauth.clientSecret</c>) taken out for Credential Manager.
    /// </summary>
    public static ImportedList Imported(string text, string source)
    {
        ArgumentNullException.ThrowIfNull(text);
        var problems = new List<string>();
        var entries = new List<ImportedConnector>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 16 });
        }
        catch (JsonException ex)
        {
            return new ImportedList([], [$"{source} is not JSON ({SafeText.Line(ex.Message)})."]);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("mcpServers", out var servers) || servers.ValueKind != JsonValueKind.Object)
            {
                return new ImportedList([], [$"{source} has no \"mcpServers\" object."]);
            }

            foreach (var server in servers.EnumerateObject())
            {
                var name = NameFrom(server.Name);
                var value = server.Value;
                if (value.ValueKind != JsonValueKind.Object)
                {
                    problems.Add($"{SafeText.Line(server.Name)}: not an object; not imported.");
                    continue;
                }

                // The secrets come out before the entry is read, so what is read holds none.
                var secrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string? clientSecret = null;
                using var cleaned = new MemoryStream();
                using (var json = new Utf8JsonWriter(cleaned))
                {
                    json.WriteStartObject();
                    foreach (var property in value.EnumerateObject())
                    {
                        if (property.Name == "headers" && property.Value.ValueKind == JsonValueKind.Object)
                        {
                            json.WriteStartObject("headers");
                            foreach (var header in property.Value.EnumerateObject().Where(h => h.Value.ValueKind == JsonValueKind.String))
                            {
                                if (LooksSecret(header.Name, header.Value.GetString()!))
                                {
                                    secrets[header.Name] = header.Value.GetString()!;
                                }
                                else
                                {
                                    header.WriteTo(json);
                                }
                            }

                            json.WriteEndObject();
                            if (secrets.Count > 0)
                            {
                                json.WriteStartArray("secretHeaders");
                                foreach (var secretName in secrets.Keys)
                                {
                                    json.WriteStringValue(secretName);
                                }

                                json.WriteEndArray();
                            }
                        }
                        else if (property.Name == "oauth" && property.Value.ValueKind == JsonValueKind.Object)
                        {
                            json.WriteStartObject("oauth");
                            foreach (var part in property.Value.EnumerateObject())
                            {
                                if (part.Name == "clientSecret" && part.Value.ValueKind == JsonValueKind.String)
                                {
                                    clientSecret = part.Value.GetString();
                                }
                                else if (part.Name is "clientId" or "callbackPort" or "scopes")
                                {
                                    part.WriteTo(json);
                                }
                            }

                            json.WriteEndObject();
                        }
                        else if (property.Name is not ("enabled" or "consent" or "secretHeaders"))
                        {
                            property.WriteTo(json);
                        }
                    }

                    json.WriteEndObject();
                }

                using var readable = JsonDocument.Parse(cleaned.ToArray());
                if (Entry(name, readable.RootElement, out var problem) is { } connector)
                {
                    entries.Add(new ImportedConnector(connector with { Enabled = !connector.Local, Consent = null }, secrets, clientSecret, name != server.Name, server.Name));
                }

                if (problem is not null)
                {
                    problems.Add($"{SafeText.Line(server.Name)}: {problem}");
                }
            }
        }

        return new ImportedList(entries, problems);
    }

    /// <summary>Writes the whole list, through a file beside it, so a crash never leaves half a file.</summary>
    /// <exception cref="IOException">It could not be written.</exception>
    public static void Write(string path, IReadOnlyList<Connector> connectors)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(connectors);
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, Indented))
        {
            json.WriteStartObject();
            json.WriteStartObject("mcpServers");
            foreach (var c in connectors)
            {
                json.WriteStartObject(c.Name);
                json.WriteString("type", c.Type);
                if (c.Local)
                {
                    json.WriteString("command", c.Command);
                    json.WriteStartArray("args");
                    foreach (var arg in c.Args)
                    {
                        json.WriteStringValue(arg);
                    }

                    json.WriteEndArray();
                    WriteMap(json, "env", c.Env);
                    c.Box.WriteTo(json);
                }
                else
                {
                    json.WriteString("url", c.Url);
                    WriteMap(json, "headers", c.Headers);
                    if (c.OAuth is { } oauth && (oauth.ClientId is not null || oauth.CallbackPort is not null || oauth.Scopes is not null))
                    {
                        json.WriteStartObject("oauth");
                        if (oauth.ClientId is { } id)
                        {
                            json.WriteString("clientId", id);
                        }

                        if (oauth.CallbackPort is { } port)
                        {
                            json.WriteNumber("callbackPort", port);
                        }

                        if (oauth.Scopes is { } scopes)
                        {
                            json.WriteString("scopes", scopes);
                        }

                        json.WriteEndObject();
                    }
                }

                if (c.TimeoutMs is { } timeout)
                {
                    json.WriteNumber("timeout", timeout);
                }

                json.WriteBoolean("enabled", c.Enabled);
                if (c.Consent is { } consent)
                {
                    json.WriteString("consent", consent);
                }

                if (c.AllowPrivateNetwork)
                {
                    json.WriteBoolean("allowPrivateNetwork", true);
                }

                if (c.SecretHeaders.Count > 0)
                {
                    json.WriteStartArray("secretHeaders");
                    foreach (var header in c.SecretHeaders)
                    {
                        json.WriteStringValue(header);
                    }

                    json.WriteEndArray();
                }

                if (c.Catalogue is { } from)
                {
                    json.WriteString("catalogue", from);
                }

                json.WriteEndObject();
            }

            json.WriteEndObject();
            json.WriteEndObject();
        }

        var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
        Directory.CreateDirectory(folder);
        var temporary = System.IO.Path.Combine(folder, $".{FileName}.{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}.tmp");
        File.WriteAllBytes(temporary, buffer.ToArray());
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Why a name cannot be a connector's, or null: 1 to 32 of a-z, 0-9 and -, starting with a letter; it becomes part of tool names and rules.</summary>
    public static string? NameProblem(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return NamePattern().IsMatch(name) ? null : string.Create(CultureInfo.InvariantCulture, $"a connector's name is 1 to {MostName} of a-z, 0-9 and -, starting with a letter");
    }

    /// <summary>A name from another app's list made one DipTick takes: lower case, anything else made -, cut to fit.</summary>
    public static string NameFrom(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var made = string.Concat(name.ToLowerInvariantAscii().Select(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') ? c : '-')).Trim('-');
        while (made.Contains("--", StringComparison.Ordinal))
        {
            made = made.Replace("--", "-", StringComparison.Ordinal);
        }

        if (made.Length == 0 || made[0] is < 'a' or > 'z')
        {
            made = "c-" + made;
        }

        return made.Length <= MostName ? made : made[..MostName].TrimEnd('-');
    }

    /// <summary>
    /// What a local connector's yes is given to: the program as found, every argument, each variable's
    /// name and value as the file writes them, and where it runs: its sandbox's network and each folder with
    /// its access, or that it runs as you. A change to any of it needs a new yes.
    /// </summary>
    public static string ConsentHash(string program, Connector connector)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(connector);
        var text = new StringBuilder(program.ToUpperInvariant()).Append('\0');
        foreach (var arg in connector.Args)
        {
            text.Append(arg).Append('\0');
        }

        foreach (var (name, value) in connector.Env.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            text.Append(name).Append('=').Append(value).Append('\0');
        }

        text.Append(connector.Box.ConsentText()).Append('\0');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// <c>${VAR}</c> and <c>${VAR:-default}</c> expanded from <paramref name="variable"/>, as Claude Code
    /// expands them; a variable with no value and no default is named in <paramref name="missing"/>.
    /// </summary>
    public static string Expand(string text, Func<string, string?> variable, List<string> missing)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(variable);
        ArgumentNullException.ThrowIfNull(missing);
        return Reference().Replace(text, match =>
        {
            var name = match.Groups["name"].Value;
            if (variable(name) is { Length: > 0 } value)
            {
                return value;
            }

            if (match.Groups["default"].Success)
            {
                return match.Groups["default"].Value;
            }

            missing.Add(name);
            return string.Empty;
        });
    }

    /// <summary>Whether a header's value names a variable rather than holding a secret itself: every secret-looking header must.</summary>
    public static bool LooksSecret(string header, string value)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(value);
        var name = header.ToLowerInvariantAscii();
        var secretName = name is "authorization" or "cookie" or "proxy-authorization" || name.Contains("token", StringComparison.Ordinal) || name.Contains("key", StringComparison.Ordinal) || name.Contains("secret", StringComparison.Ordinal);
        // What is left once the variables are taken out, and a scheme word before them ("Bearer ${TOKEN}").
        var rest = Reference().Replace(value, string.Empty).Trim();
        foreach (var scheme in (string[])["Bearer", "Basic", "Token"])
        {
            if (rest.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                rest = rest[scheme.Length..].Trim();
                break;
            }
        }

        return secretName && rest.Length > 0;
    }

    private static Connector? Entry(string name, JsonElement value, out string? problem)
    {
        problem = null;
        if (NameProblem(name) is { } bad)
        {
            problem = $"{bad} ('diptick connectors import' can rename it); left out.";
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            problem = "not an object; left out.";
            return null;
        }

        var unknown = value.EnumerateObject().Select(p => p.Name).Where(k => k is not ("type" or "command" or "args" or "env" or "url" or "headers" or "oauth" or "timeout" or "alwaysLoad" or "enabled" or "consent" or "allowPrivateNetwork" or "secretHeaders" or "catalogue" or "headersHelper" or "sandbox")).ToList();
        if (value.TryGetProperty("headersHelper", out _))
        {
            problem = "headersHelper runs a program to make headers, which DipTick does not do; left out.";
            return null;
        }

        var type = Text(value, "type") ?? (value.TryGetProperty("command", out _) ? Stdio : Http);
        if (type is "ws")
        {
            problem = "a WebSocket connector (type ws) is not one DipTick can reach; left out.";
            return null;
        }

        if (type is not (Stdio or Http or Sse))
        {
            problem = $"type '{SafeText.Line(type)}' is not stdio, http or sse; left out.";
            return null;
        }

        var enabled = value.TryGetProperty("enabled", out var on) ? on.ValueKind == JsonValueKind.True : type != Stdio;
        var consent = Text(value, "consent");
        var catalogue = Text(value, "catalogue");
        int? timeout = value.TryGetProperty("timeout", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var ms) && ms >= 1000 ? ms : null;
        var secretHeaders = Strings(value, "secretHeaders");
        if (type == Stdio)
        {
            if (Text(value, "command") is not { } command)
            {
                problem = "a local connector names the program to run in \"command\"; left out.";
                return null;
            }

            var args = Strings(value, "args");
            var env = Map(value, "env");
            if (ConnectorSandbox.Parse(value, out var sandboxProblem) is not { } sandbox)
            {
                problem = sandboxProblem;
                return null;
            }

            problem = unknown.Count > 0 ? $"{string.Join(", ", unknown.Select(SafeText.Line))} ignored." : null;
            return new Connector(name, type, command, args, env, null, new Dictionary<string, string>(), [], null, timeout, enabled, consent, false, catalogue, sandbox);
        }

        if (Text(value, "url") is not { } url)
        {
            problem = "a remote connector gives its address in \"url\"; left out.";
            return null;
        }

        var headers = Map(value, "headers");
        if (headers.FirstOrDefault(h => LooksSecret(h.Key, h.Value)) is { Key: { } secretHeader })
        {
            problem = $"its header {SafeText.Line(secretHeader)} holds a secret in the file; 'diptick connectors secret {name} {SafeText.Line(secretHeader)}' keeps it in Credential Manager instead. Left out until then.";
            return null;
        }

        ConnectorOAuth? oauth = null;
        if (value.TryGetProperty("oauth", out var o) && o.ValueKind == JsonValueKind.Object)
        {
            if (o.TryGetProperty("clientSecret", out _))
            {
                problem = $"oauth.clientSecret is a secret in the file; 'diptick connectors add {name} --client-id <id>' asks for it and keeps it in Credential Manager. Left out until then.";
                return null;
            }

            int? port = o.TryGetProperty("callbackPort", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) && n is >= 1024 and <= 65535 ? n : null;
            oauth = new ConnectorOAuth(Text(o, "clientId"), port, Text(o, "scopes"));
        }

        problem = unknown.Count > 0 ? $"{string.Join(", ", unknown.Select(SafeText.Line))} ignored." : null;
        var allowPrivate = value.TryGetProperty("allowPrivateNetwork", out var ap) && ap.ValueKind == JsonValueKind.True;
        return new Connector(name, type, null, [], new Dictionary<string, string>(), url, headers, secretHeaders, oauth, timeout, enabled, consent, allowPrivate, catalogue);
    }

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } text ? text : null;

    private static List<string> Strings(JsonElement value, string name) =>
        value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array
            ? [.. p.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
            : [];

    private static Dictionary<string, string> Map(JsonElement value, string name)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in p.EnumerateObject().Where(e => e.Value.ValueKind == JsonValueKind.String))
            {
                map[entry.Name] = entry.Value.GetString()!;
            }
        }

        return map;
    }

    private static void WriteMap(Utf8JsonWriter json, string name, IReadOnlyDictionary<string, string> map)
    {
        if (map.Count == 0)
        {
            return;
        }

        json.WriteStartObject(name);
        foreach (var (key, value) in map)
        {
            json.WriteString(key, value);
        }

        json.WriteEndObject();
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(?::-(?<default>[^}]*))?\}", RegexOptions.CultureInvariant)]
    private static partial Regex Reference();
}
