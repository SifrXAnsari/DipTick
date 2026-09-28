using System.Text.RegularExpressions;
using DipTick.Models.Harness;
using DipTick.Models.Providers;
using DipTick.Models.Tools;

namespace DipTick.Models.Research;

/// <summary>
/// Whether an agent searches and fetches through its provider's own tools rather than DipTick's.
/// The provider runs them on its side,
/// so DipTick cannot stop to ask before each one: they are offered only where DipTick itself would not
/// ask either. Anthropic's search and fetch where the platform has them; Google Search, and URL context
/// on the Gemini API, for Gemini 3 and later, which take them beside DipTick's tools.
/// </summary>
internal static partial class ProviderResearch
{
    /// <summary>DipTick's name for a search, the provider's or its own.</summary>
    public const string Search = "web_search";

    /// <summary>DipTick's name for a fetch, the provider's or its own.</summary>
    public const string Fetch = "web_fetch";

    /// <summary>The address a fetch is judged by when none is asked for yet: any public page, as a search would give it.</summary>
    private const string SampleAddress = "https://example.com/";

    /// <summary>The provider's own research tools this agent is given in this chat, by DipTick's names; none where they may not be used.</summary>
    public static IReadOnlyList<string> For(ToolContext context, IReadOnlyList<ChatRule> rules)
    {
        ArgumentNullException.ThrowIfNull(context);
        return For(context.Config, context.Agent, rules, context.Gate.Flags.DryRun);
    }

    /// <summary>The same, from the settings, the agent and the chat's rules; under --dry-run none, since they would do what cannot be undone.</summary>
    public static IReadOnlyList<string> For(MasterConfig config, SeatConfig agent, IReadOnlyList<ChatRule> rules, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(rules);
        var offers = Offers(agent);
        if (offers.Count == 0 || dryRun)
        {
            return [];
        }

        var permissions = config.Permissions;
        var chosen = new List<string>();

        // Asked with no one to ask: what would need a yes is not given, so only an outright allow counts.
        var outright = RunFlags.Default;
        if (offers.Contains(Search) && Policy.Decide(Capability.WebSearch, null, permissions, outright, agent, rules).Verdict == Verdict.Allow)
        {
            chosen.Add(Search);
        }

        // The provider fetches only addresses already in the conversation, the rule under which DipTick
        // itself fetches without asking; any public site must be open, since it cannot ask for a host.
        if (offers.Contains(Fetch) && permissions.Hosts.Contains("*")
            && Policy.Decide(Capability.NetFetch, SampleAddress, permissions, outright, agent, rules, "a search result").Verdict == Verdict.Allow)
        {
            chosen.Add(Fetch);
        }

        return chosen;
    }

    /// <summary>What this agent's provider and model offer of their own, before the rules: search, fetch, both or neither.</summary>
    public static IReadOnlyList<string> Offers(SeatConfig agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (ProviderCatalogue.Find(agent.Provider) is not { } entry || !entry.Can.HasFlag(Can.Search))
        {
            return [];
        }

        return entry.Shape switch
        {
            ProviderShape.Anthropic => entry.Flavor switch
            {
                // Anthropic's pages: both on its own API, on AWS and in Foundry (the basic versions on
                // Azure); search only on Google Cloud; neither on Bedrock.
                AnthropicFlavor.Api or AnthropicFlavor.Aws or AnthropicFlavor.Foundry => [Search, Fetch],
                AnthropicFlavor.Vertex => [Search],
                _ => [],
            },
            ProviderShape.Gemini when GeminiThreeOrLater(agent.Model) => entry.Id == "google" ? [Search, Fetch] : [Search],
            _ => [],
        };
    }

    /// <summary>
    /// Whether a model is Gemini 3 or later, which takes Google Search beside function calling (Google:
    /// "Gemini 3 models also support combining these built-in tools with custom tools"). Read from its
    /// name, with or without <c>models/</c> or <c>google/</c> before it.
    /// </summary>
    public static bool GeminiThreeOrLater(string model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var name = model.Trim();
        foreach (var prefix in (string[])["models/", "google/", "publishers/google/models/"])
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[prefix.Length..];
            }
        }

        var match = GeminiVersion().Match(name);
        return match.Success && int.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major) && major >= 3;
    }

    [GeneratedRegex(@"^gemini-(\d+)(?:[.-]|$)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex GeminiVersion();
}
