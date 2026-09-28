using DipTick.Models.Harness;

namespace DipTick.Models.Research;

/// <summary>Whether agents in a chat may search and fetch (the Research chip).</summary>
internal enum ResearchState
{
    /// <summary>They may: a search service or a provider's own search is there, and the chat has not switched it off.</summary>
    On,

    /// <summary>The chat switched it off: its own rules deny searching and fetching.</summary>
    Off,

    /// <summary>Neither a search service nor a provider's own search is set up; fetching a page named in the chat still works.</summary>
    NotSetUp,
}

/// <summary>
/// The Research switch of a chat: Off is
/// the chat's own rules <c>web.search deny</c> and <c>net.fetch deny</c>, so nothing new stands between
/// an agent and the gate; On takes those two away.
/// </summary>
internal static class ResearchSwitch
{
    /// <summary>The chat rules the switch keeps while it is off.</summary>
    public static IReadOnlyList<string> Rules { get; } = ["web.search", "net.fetch"];

    /// <summary>Whether the chat's rules have research off: both rules deny.</summary>
    public static bool IsOff(IReadOnlyList<ChatRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return Rules.All(r => rules.Any(c => c.Verdict == Verdict.Deny && string.Equals(c.Rule, r, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Whether research is set up for these agents: a search service, or a provider's own search for one of them.</summary>
    public static bool SetUp(MasterConfig config, IEnumerable<SeatConfig> agents)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(agents);
        return SearchService.Ready(config) || agents.Any(a => ProviderResearch.Offers(a).Contains(ProviderResearch.Search));
    }

    /// <summary>The chat's state: off by its rules, else on where research is set up.</summary>
    public static ResearchState State(MasterConfig config, IEnumerable<SeatConfig> agents, IReadOnlyList<ChatRule> rules) =>
        IsOff(rules) ? ResearchState.Off : SetUp(config, agents) ? ResearchState.On : ResearchState.NotSetUp;

    /// <summary>The chip's words.</summary>
    public static string Words(ResearchState state) => state switch
    {
        ResearchState.On => "Research on",
        ResearchState.Off => "Research off",
        _ => "Set up research",
    };
}
