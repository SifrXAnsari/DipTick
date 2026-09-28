using DipTick.Models.Harness;

namespace DipTick.Models.Tools;

/// <summary>
/// What an agent is offered in a turn: DipTick's own tools, and by DipTick's names the provider's own
/// research tools that take the place of DipTick's search and fetch.
/// </summary>
internal sealed record ToolOffer(IReadOnlyList<IAgentTool> Own, IReadOnlyList<string> Provider)
{
    public static ToolOffer None { get; } = new([], []);

    /// <summary>Whether the agent is offered any tool at all.</summary>
    public bool Any => Own.Count > 0 || Provider.Count > 0;

    /// <summary>Whether the agent can search or fetch, by either road.</summary>
    public bool CanResearch => Provider.Count > 0 || Own.Any(t => t.Name is Models.Research.ProviderResearch.Search or Models.Research.ProviderResearch.Fetch);
}

/// <summary>
/// The tools an agent is offered in a chat, in the order it is told of them. A tool is offered only
/// when something could allow it: its capability is in the agent's lane,
/// no rule denies it outright, the settings' or the chat's, and a rule allows or asks for it; and when
/// what it needs is set up (a search service, a folder, a connector on in the chat, a sandbox). Whether
/// the agent's model can call tools at all is asked first (<see cref="ToolSupport"/>).
/// </summary>
internal static class AgentTools
{
    /// <summary>The most tool calls one turn may make.</summary>
    public const int MostCallsATurn = 20;

    /// <summary>The most one call may give back to the model, in characters.</summary>
    public const int MostResult = 64 * 1024;

    /// <summary>How long one call may take.</summary>
    public static readonly TimeSpan CallLimit = TimeSpan.FromSeconds(60);

    /// <summary>
    /// What an agent's briefing says about tools before its turn, without asking anyone (for "What
    /// they're told"): whether it is offered tools, whether it can search or fetch, and whether by its
    /// provider's own tools. The turn decides for itself (<see cref="ForAsync"/>).
    /// </summary>
    public static (bool Tools, bool Research, bool ByProvider) Preview(MasterConfig config, SeatConfig agent, IReadOnlyList<ChatRule> rules)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(rules);
        if (!ToolSupport.Likely(agent))
        {
            return (false, false, false);
        }

        bool Could(Capability cap) => Policy.CouldAllow(cap, config.Permissions, agent, rules);
        var provider = Research.ProviderResearch.For(config, agent, rules, dryRun: false);
        var own = (Research.SearchService.Ready(config) && Could(Capability.WebSearch)) || Could(Capability.NetFetch);
        return (true, provider.Count > 0 || own, provider.Count > 0);
    }

    /// <summary>The tools this agent is offered in this chat; none when its model cannot call tools.</summary>
    public static async Task<ToolOffer> ForAsync(ToolContext context, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!await ToolSupport.CanCallToolsAsync(context.Agent, cancel).ConfigureAwait(false))
        {
            return ToolOffer.None;
        }

        var config = context.Config;
        var rules = context.Session.Length > 0 ? context.Store.ChatRules(context.Session) : [];
        bool Could(Capability cap) => Policy.CouldAllow(cap, config.Permissions, context.Agent, rules);

        // DipTick's own: the clock, the calendar and the calculator read nothing of yours and send nothing.
        var tools = new List<IAgentTool> { new TimeNowTool(), new CalendarDaysTool(), new CalcTool() };
        if (Could(Capability.NetProbe))
        {
            tools.Add(new NetCheckTool());
        }

        // Research: the provider's own search and fetch where it has them and nothing would
        // need a yes; otherwise DipTick's own, through the service the person chose, each call gated.
        var provider = Research.ProviderResearch.For(context, rules);
        if (!provider.Contains(Research.ProviderResearch.Search) && Research.SearchService.Ready(config) && Could(Capability.WebSearch))
        {
            tools.Add(new Research.WebSearchTool());
        }

        if (!provider.Contains(Research.ProviderResearch.Fetch) && Could(Capability.NetFetch))
        {
            tools.Add(new Research.WebFetchTool());
        }

        // Files: only inside the folders the permissions open, and the chat's own folder for what agents make.
        if (Could(Capability.FsRead) && config.Permissions.Roots.Count > 0)
        {
            tools.Add(new Files.ListFolderTool());
            tools.Add(new Files.ReadFileTool());
        }

        if (Could(Capability.FsWrite))
        {
            tools.Add(new Files.WriteFileTool());
        }

        if (Could(Capability.MediaMake))
        {
            if (Media.ImageMaker.Ready(config))
            {
                tools.Add(new Media.MakeImageTool());
            }

            if (Media.VideoMaker.Ready(config))
            {
                tools.Add(new Media.MakeVideoTool());
            }
        }

        // Speech made text: a recording in the folders the person opened, to their own speech server.
        if (Could(Capability.FsRead) && Could(Capability.ModelCall) && config.Permissions.Roots.Count > 0 && Media.SpeechMaker.Ready(config))
        {
            tools.Add(new Media.TranscribeTool());
        }

        // Connectors: the tools of the connectors on in this chat that the rules could allow.
        if (context.Session.Length > 0 && Could(Capability.McpCall))
        {
            tools.AddRange(await Connectors.ConnectorTools.ForAsync(context, rules, cancel).ConfigureAwait(false));
        }

        // Skills: the full text of a skill on in this chat and approved as it is, when the agent
        // asks for it; running code only inside DipTick's own sandbox, and asked every time, which
        // also runs a usable skill's scripts.
        IReadOnlyList<Skills.Skill> usable = context.Session.Length > 0 ? Skills.SkillChat.Usable(config, context.Store.ChatSkills(context.Session)) : [];
        var runs = Could(Capability.ShellRun) && Sandbox.CodeSandbox.Available && Sandbox.CodeSandbox.Present().Count > 0;
        if (usable.Count > 0)
        {
            tools.Add(new Skills.LoadSkillTool(usable, runs));
        }

        if (runs)
        {
            tools.Add(new Sandbox.RunCodeTool(usable));
        }

        return new ToolOffer(tools, provider);
    }
}
