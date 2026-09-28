namespace DipTick.Models.Connectors;

/// <summary>
/// A connector DipTick knows before you add it: its name, its address (a
/// part in braces is yours to give, like Outlook's tenant), how it signs in, what it needs, and the
/// pages that say so, read on the day given.
/// </summary>
internal sealed record KnownConnector(string Id, string Title, string Url, string SignIn, string Needs, bool NeedsClientId, IReadOnlyList<string> Parts, IReadOnlyList<string> Sources, string Read);

/// <summary>The connectors Settings > Connectors lists before any is added, as read on 2026-09-27.</summary>
internal static class ConnectorCatalogue
{
    public static IReadOnlyList<KnownConnector> All { get; } =
    [
        new(
            "atlassian",
            "Atlassian: Jira, Confluence, Jira Service Management, Bitbucket and more",
            "https://mcp.atlassian.com/v2/mcp",
            "OAuth 2.1 in your browser, or an API token",
            "Atlassian's own server. Its use draws on your organisation's Rovo credits.",
            false,
            [],
            ["https://support.atlassian.com/atlassian-ai-gateway/docs/get-started-with-the-atlassian-remote-mcp-server/"],
            "2026-09-27"),
        new(
            "clickup",
            "ClickUp",
            "https://mcp.clickup.com/mcp",
            "OAuth 2.1 with PKCE in your browser, nothing else",
            "In public beta, on every plan: 50 calls in 24 hours on Free, 300 on Unlimited and up; ClickUp's paid add-on changes those numbers.",
            false,
            [],
            ["https://developer.clickup.com/docs/connect-an-ai-assistant-to-clickups-mcp-server"],
            "2026-09-27"),
        new(
            "gmail",
            "Gmail",
            "https://gmailmcp.googleapis.com/mcp/v1",
            "OAuth 2.0 through your own Google Cloud client (a Web application client, its ID and secret)",
            "Google's Developer Preview: membership of the Google Workspace Developer Preview Program, and a Google Cloud project with the Gmail API and the Gmail MCP API on. Its client's redirect address must be the one DipTick shows. Its tools read, search and label mail and write drafts; none sends.",
            true,
            [],
            ["https://developers.google.com/workspace/gmail/api/guides/configure-mcp-server"],
            "2026-09-27"),
        new(
            "outlook",
            "Outlook (Microsoft Work IQ Mail)",
            "https://agent365.svc.cloud.microsoft/agents/tenants/{tenant}/servers/mcp_MailTools",
            "your own Microsoft Entra app (its client ID)",
            "Microsoft's Work IQ Mail, in preview: a Microsoft 365 Copilot licence and a Microsoft Entra account. The tenant is your organisation's Entra tenant ID.",
            true,
            ["tenant"],
            [
                "https://learn.microsoft.com/en-us/microsoft-agent-365/tooling-servers-overview",
                "https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/work-iq/mcp/overview",
            ],
            "2026-09-27"),
    ];

    public static KnownConnector? Find(string id) => All.FirstOrDefault(k => string.Equals(k.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The address with its parts filled in; why not, when one is missing or not a plain value.</summary>
    public static (string? Url, string? Problem) Address(KnownConnector known, IReadOnlyDictionary<string, string> parts)
    {
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(parts);
        var url = known.Url;
        foreach (var part in known.Parts)
        {
            if (!parts.TryGetValue(part, out var value) || value.Length == 0)
            {
                return (null, $"{known.Title} needs --{part}");
            }

            if (!value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.'))
            {
                return (null, $"--{part} takes letters, digits, - and . only");
            }

            url = url.Replace("{" + part + "}", value, StringComparison.Ordinal);
        }

        return (url, null);
    }
}
