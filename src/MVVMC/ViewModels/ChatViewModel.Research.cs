using System.Globalization;
using System.Text.Json;
using DipTick.Models;
using DipTick.Models.Chat;
using DipTick.Models.Chat.Seam;
using DipTick.Models.Chat.Store;
using DipTick.Models.Research;

namespace DipTick.ViewModels;

/// <summary>
/// Research in the chat: the Research chip, on or off for this chat; the
/// chat's sources, listed under the answers they served; and a citation to a number the chat has no
/// source for, shown as not fetched. Each change is a <c>diptick chat</c> verb, run in this process.
/// </summary>
internal sealed partial class ChatViewModel
{
    /// <summary>This chat's sources as last read; null until read again.</summary>
    private IReadOnlyList<StoredSource>? _sources;

    /// <summary>Every source of the chat on screen (<c>chat sources</c>), by number; none for a chat nobody has spoken in.</summary>
    public IReadOnlyList<StoredSource> Sources
    {
        get
        {
            lock (_lock)
            {
                if (_sources is { } known)
                {
                    return known;
                }
            }

            IReadOnlyList<StoredSource> sources = [];
            if (_sessionId is { } id)
            {
                var (json, _, code) = Json(Private("chat", "sources", $"--session={id}"));
                if (code == Controllers.Exit.Ok)
                {
                    sources = ReadSources(json);
                }
            }

            lock (_lock)
            {
                _sources = sources;
            }

            return sources;
        }
    }

    /// <summary>The sources an answer served, by the answer's number, in the chat's order.</summary>
    public IReadOnlyList<StoredSource> SourcesOf(int seq) => [.. Sources.Where(s => s.Seq == seq)];

    /// <summary>An answer as it is shown: a citation to a number the chat has no source for marked as not fetched.</summary>
    public string Marked(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var known = Sources.Select(s => s.Id).ToHashSet();
        return Citations.Mark(text, known);
    }

    /// <summary>Whether agents at this chat's table may search and fetch: on, off by the chat's own rules, or not set up.</summary>
    public ResearchState Research
    {
        get
        {
            var agents = Table.Select(c => Models.Chat.Seam.Table.Resolve(Config, new TableSeat(c.Name, c.Agent, c.Model))).OfType<SeatConfig>().Where(a => a.Filled).ToList();
            return ResearchSwitch.State(Config, agents, ChatRules);
        }
    }

    /// <summary>The Research chip's words.</summary>
    public string ResearchWords => ResearchSwitch.Words(Research);

    /// <summary>
    /// Research on or off for this chat (<c>chat research on|off</c>): off keeps this chat's rules denying
    /// web.search and net.fetch, on takes them away. Runs on a worker thread.
    /// </summary>
    public async Task SetResearchAsync(bool on)
    {
        if (EnsureKept() is not { } id)
        {
            return;
        }

        await Task.Run(() => Run(false, Private("chat", "research", on ? "on" : "off", $"--session={id}"))).ConfigureAwait(false);
        lock (_lock)
        {
            _chatRules = null;
        }

        Changed?.Invoke();
    }

    /// <summary>/research [on|off]: the chat's research, or switches it.</summary>
    private async Task ResearchCommandAsync(string arg)
    {
        switch (arg.Trim())
        {
            case "on":
                await SetResearchAsync(true).ConfigureAwait(false);
                await Say(Role.System, "Research is on in this chat: agents may search and fetch, each through your permissions.").ConfigureAwait(false);
                break;
            case "off":
                await SetResearchAsync(false).ConfigureAwait(false);
                await Say(Role.System, "Research is off in this chat: no agent here searches or fetches until you switch it on.").ConfigureAwait(false);
                break;
            case "":
                var problem = SearchService.Problem(Config);
                var state = Research;
                await Say(Role.System, state == ResearchState.NotSetUp && problem is not null ? $"{ResearchWords}: {problem}" : $"{ResearchWords}. /research on or /research off switches it for this chat.").ConfigureAwait(false);
                break;
            default:
                await Say(Role.System, "/research, /research on, or /research off.").ConfigureAwait(false);
                break;
        }
    }

    /// <summary>/sources: the chat's sources (<c>chat sources</c>), or one answer's with its number.</summary>
    private async Task SourcesCommandAsync(string arg)
    {
        if (_sessionId is not { } id)
        {
            await Say(Role.System, "No sources yet: nothing has been said in this chat.").ConfigureAwait(false);
            return;
        }

        var seq = arg.Trim().TrimStart('#');
        await (seq.Length > 0 && int.TryParse(seq, NumberStyles.None, CultureInfo.InvariantCulture, out _)
            ? Verb("chat", "sources", $"--session={id}", "--seq", seq)
            : Verb("chat", "sources", $"--session={id}")).ConfigureAwait(false);
    }

    private static List<StoredSource> ReadSources(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return [.. doc.RootElement.GetProperty("sources").EnumerateArray().Select(s => new StoredSource(
                s.GetProperty("number").GetInt32(),
                s.TryGetProperty("seq", out var seq) && seq.ValueKind == JsonValueKind.Number ? seq.GetInt32() : null,
                s.GetProperty("address").GetString() ?? string.Empty,
                s.GetProperty("title").GetString() ?? string.Empty,
                s.GetProperty("kind").GetString() ?? string.Empty,
                s.TryGetProperty("fetched", out var at) && at.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var when) ? when : null,
                s.TryGetProperty("sha256", out var sha) && sha.ValueKind == JsonValueKind.String ? sha.GetString() : null,
                null))];
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return [];
        }
    }
}
