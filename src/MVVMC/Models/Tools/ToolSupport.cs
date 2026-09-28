using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using DipTick.Models.Chat;
using DipTick.Models.Providers;

namespace DipTick.Models.Tools;

/// <summary>
/// What an agent's model can do beyond words: call tools, see
/// images, read PDFs. The agent's own setting decides first (<c>&lt;id&gt;.tools</c>: on, off, or auto);
/// on auto, Ollama says for its model (<c>POST /api/show</c> answers with the model's
/// <c>capabilities</c>, "tools" and "vision" among them: Ollama's API docs), and the catalogue says for
/// every other provider. A server the catalogue does not know is offered none.
/// </summary>
internal static class ToolSupport
{
    /// <summary>The most of /api/show's answer read: it carries the model's template and details, not its weights.</summary>
    private const int MostShow = 1024 * 1024;

    /// <summary>Ollama's answers for this run, by origin and model: a model's abilities do not change while DipTick runs.</summary>
    private static readonly ConcurrentDictionary<string, Task<IReadOnlyList<string>>> Shown = new(StringComparer.Ordinal);

    /// <summary>Whether this agent may be offered tools.</summary>
    public static Task<bool> CanCallToolsAsync(SeatConfig agent, CancellationToken cancel) => AbleAsync(agent, Can.Tools, "tools", cancel);

    /// <summary>
    /// Whether this agent would be offered tools, without asking anyone: its own setting, Ollama's
    /// answer where this run already has it, else the catalogue. For showing what an agent is told
    /// before a turn; the turn itself asks (<see cref="CanCallToolsAsync"/>).
    /// </summary>
    public static bool Likely(SeatConfig agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (agent.Tools is "on" or "off")
        {
            return agent.Tools == "on";
        }

        if (ProviderCatalogue.Find(agent.Provider) is not { } entry)
        {
            return false;
        }

        if (entry.Id == "ollama" && Engines.Endpoint(agent.Provider, agent.Endpoint) is { } endpoint && agent.Model.Length > 0
            && Shown.TryGetValue($"{endpoint.GetLeftPart(UriPartial.Authority)}|{agent.Model}", out var asked) && asked.IsCompletedSuccessfully)
        {
            return asked.Result.Contains("tools", StringComparer.OrdinalIgnoreCase);
        }

        return entry.Can.HasFlag(Can.Tools);
    }

    /// <summary>Whether this agent's model sees images.</summary>
    public static Task<bool> CanSeeAsync(SeatConfig agent, CancellationToken cancel) => AbleAsync(agent, Can.Vision, "vision", cancel);

    /// <summary>Whether this agent's model reads PDFs sent to it; Ollama's models do not take documents.</summary>
    public static Task<bool> CanReadPdfAsync(SeatConfig agent, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var entry = ProviderCatalogue.Find(agent.Provider);
        return Task.FromResult(entry is not null && entry.Id != "ollama" && entry.Can.HasFlag(Can.Pdf));
    }

    private static async Task<bool> AbleAsync(SeatConfig agent, Can ability, string ollamaWord, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (ability == Can.Tools && agent.Tools is "on" or "off")
        {
            return agent.Tools == "on";
        }

        var entry = ProviderCatalogue.Find(agent.Provider);
        if (entry is null)
        {
            return false;
        }

        if (entry.Id == "ollama" && Engines.Endpoint(agent.Provider, agent.Endpoint) is { } endpoint && agent.Model.Length > 0)
        {
            var capabilities = await OllamaAsync(endpoint, agent.Model, cancel).ConfigureAwait(false);
            return capabilities.Contains(ollamaWord, StringComparer.OrdinalIgnoreCase);
        }

        return entry.Can.HasFlag(ability);
    }

    /// <summary>
    /// A model's capabilities as Ollama reports them, asked once a run; none when Ollama cannot be
    /// reached or says nothing, so an agent is offered nothing it may not take.
    /// </summary>
    private static async Task<IReadOnlyList<string>> OllamaAsync(Uri endpoint, string model, CancellationToken cancel)
    {
        var origin = endpoint.GetLeftPart(UriPartial.Authority);
        var key = $"{origin}|{model}";
        var capabilities = await Shown.GetOrAdd(key, _ => AskAsync(new Uri(origin + "/api/show"), model, cancel)).ConfigureAwait(false);
        if (capabilities.Count == 0)
        {
            // Not an answer to keep: Ollama may just not be running yet.
            Shown.TryRemove(key, out _);
        }

        return capabilities;
    }

    private static async Task<IReadOnlyList<string>> AskAsync(Uri show, string model, CancellationToken cancel)
    {
        try
        {
            using var http = OpenAiCompatibleEngine.NewClient();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            using var request = new HttpRequestMessage(HttpMethod.Post, show)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { model }), Encoding.UTF8, "application/json"),
            };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var body = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            await using var bodyScope = body.ConfigureAwait(false);
            var bytes = new byte[MostShow];
            var read = await body.ReadAtLeastAsync(bytes, MostShow, throwOnEndOfStream: false, limit.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(bytes.AsMemory(0, read));
            return doc.RootElement.TryGetProperty("capabilities", out var list) && list.ValueKind == JsonValueKind.Array
                ? [.. list.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!)]
                : [];
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            return [];
        }
    }
}
