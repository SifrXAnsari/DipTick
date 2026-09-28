using System.Globalization;
using System.Text;
using System.Text.Json;
using DipTick.Models.Chat.Store;
using DipTick.Models.Harness;
using DipTick.Models.Tools;

namespace DipTick.Models.Research;

/// <summary>
/// <c>web_search</c>: a search through the service the person chose, gated
/// as <c>web.search</c> with the words as its target, since they leave the PC. Each result is kept as
/// a source of the chat, numbered, and its address is seen as from a search, so the agent may fetch it.
/// </summary>
internal sealed class WebSearchTool : IAgentTool
{
    public string Name => ProviderResearch.Search;

    public string Description =>
        "Search the web for current information: news, prices, releases, facts newer than what you know. " +
        "Returns numbered results, each with its address and passages from the page. Cite a result by its number, like [3]. " +
        "Read a whole page with web_fetch, giving a result's address exactly.";

    public JsonElement Parameters { get; } = ToolArgs.Schema(("query", "string", "What to search for, as you would type it into a search engine; at most 75 words.", true));

    public async Task<ToolOutcome> RunAsync(ToolContext context, JsonElement arguments, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (ToolArgs.Text(arguments, "query") is not { } query)
        {
            return ToolOutcome.Failed("give the words to search for in 'query'");
        }

        if (SearchService.QueryProblem(query) is { } bad)
        {
            return ToolOutcome.Failed(bad);
        }

        if (!context.Gate.Request("search", Capability.WebSearch, query))
        {
            return ToolOutcome.Refused("web.search");
        }

        SearchAnswer answer;
        try
        {
            answer = await SearchService.SearchAsync(context.Config, query, cancel).ConfigureAwait(false);
        }
        catch (SearchException ex)
        {
            return ToolOutcome.Failed(ex.Message);
        }

        var numbers = Keep(context, answer);
        return new ToolOutcome(Words(answer, numbers), Summary(answer)) { Sources = numbers };
    }

    /// <summary>Each result kept as a source of the chat and seen as from a search; its number in the chat, in the results' order.</summary>
    internal static List<int> Keep(ToolContext context, SearchAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(answer);
        var numbers = new List<int>();
        foreach (var hit in answer.Hits)
        {
            context.Seen.Add(hit.Address, SeenFrom.Search);
            numbers.Add(context.Session.Length > 0
                ? context.Store.AddSource(context.Session, new StoredSource(0, null, hit.Address, hit.Title, SourceKind.Search, DateTimeOffset.UtcNow, null, string.Join("\n", hit.Passages)))
                : hit.Number);
        }

        return numbers;
    }

    /// <summary>The results as the model reads them: number, title, address and date, then the passages.</summary>
    internal static string Words(SearchAnswer answer, IReadOnlyList<int> numbers)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(numbers);
        if (answer.Hits.Count == 0)
        {
            return $"{answer.Service} found nothing for: {answer.Query}";
        }

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{answer.Hits.Count} results from {answer.Service} for: {answer.Query}\n");
        for (var i = 0; i < answer.Hits.Count; i++)
        {
            var hit = answer.Hits[i];
            text.Append(CultureInfo.InvariantCulture, $"\n[{numbers[i]}] {hit.Title}\n{hit.Address}");
            if (hit.Date.Length > 0)
            {
                text.Append(CultureInfo.InvariantCulture, $" ({hit.Date})");
            }

            text.Append('\n');
            foreach (var passage in hit.Passages)
            {
                text.Append(passage.Trim()).Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>One line for the chat's tool row.</summary>
    internal static string Summary(SearchAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return string.Create(CultureInfo.InvariantCulture, $"{answer.Hits.Count} result{(answer.Hits.Count == 1 ? string.Empty : "s")} from {answer.Service} for \"{SafeText.Line(answer.Query)}\"");
    }
}
