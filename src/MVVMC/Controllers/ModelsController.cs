using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DipTick.Models;
using DipTick.Models.Chat;
using DipTick.Models.Chat.Store;
using DipTick.Models.Harness;

namespace DipTick.Controllers;

/// <summary>
/// <c>diptick models [--for main|&lt;seat&gt;]</c>: what the provider offers, from its own list.
/// It costs nothing; it is still a <c>model.call</c> through the gate, because it carries the key
/// to the provider.
/// <c>diptick models unload [--for main|&lt;seat&gt;] [--model &lt;id&gt;]</c>: asks the local
/// runtime to let go of the model now, so it stops holding memory, as Esc three times does when it
/// stops everything. The model is the section's own unless <c>--model</c> names another the same
/// runtime serves.
/// </summary>
internal static class ModelsController
{
    private const string Usage = "usage: diptick models [--for main|<seat>]  or  diptick models unload [--for main|<seat>] [--model <id>]";

    public static int Run(IReadOnlyList<string> args, Cli cli)
    {
        var unload = args.Count > 1 && args[1] == "unload";
        var section = ChatStores.MainSeat;
        string? model = null;
        for (var i = unload ? 2 : 1; i < args.Count; i++)
        {
            if (args[i] == "--for" && i + 1 < args.Count)
            {
                section = args[++i];
            }
            else if (unload && args[i] == "--model" && i + 1 < args.Count)
            {
                model = args[++i];
                if (!ModelLister.UsableId(model))
                {
                    return cli.Fail("--model takes a model's name as the runtime writes it: no spaces, only plain characters.", Exit.Usage);
                }
            }
            else
            {
                return cli.Fail(Usage, Exit.Usage);
            }
        }

        return unload ? Unload(section, model, cli) : List(section, cli);
    }

    private static int List(string section, Cli cli)
    {
        if (ModelLister.QueryFor(section, cli.Config, out var seat, out var why) is not { } query)
        {
            return cli.Fail(why, Exit.Usage);
        }

        section = query.Section;
        var gate = new Gate(cli.Config.Permissions, cli.GateFlags, AuditLog.ForThisUser(), cli.Who, cli.Err, cli.Question, seat);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cli.Stop);
        if (cli.Flags.NetTimeoutSeconds > 0)
        {
            cancel.CancelAfter(TimeSpan.FromSeconds(cli.Flags.NetTimeoutSeconds));
        }

        cli.StopsCleanly();
        IReadOnlyList<ModelInfo> models;
        try
        {
            models = ModelLister.Instance.ListAsync(query, gate, cancel.Token).GetAwaiter().GetResult();
        }
        catch (ChatException ex)
        {
            return cli.Fail(SafeText.Line(ex.Message));
        }
        catch (OperationCanceledException)
        {
            return cli.Fail(cli.Stop.IsCancellationRequested
                ? "stopped."
                : $"{query.Endpoint.Host} did not answer within {cli.Flags.NetTimeoutSeconds.ToString(CultureInfo.InvariantCulture)} s.");
        }

        if (cli.WriteJson(new
        {
            section,
            provider = query.Provider,
            models = models.Select(m => new { id = m.Id, name = m.Name, created = m.Created, context = m.Context }),
        }))
        {
            return Exit.Ok;
        }

        if (models.Count == 0)
        {
            cli.Note($"{Engines.DisplayName(query.Provider, query.Endpoint.AbsoluteUri)} offers no models{(string.Equals(query.Provider, "ollama", StringComparison.OrdinalIgnoreCase) ? " yet: 'ollama pull <name>' adds one (ollama.com/library lists them)" : string.Empty)}.");
            return Exit.Ok;
        }

        var width = Math.Min(60, models.Max(m => m.Id.Length));
        foreach (var m in models)
        {
            var when = m.Created is { } created ? created.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;
            var context = m.Context is { } tokens ? $"{tokens.ToString("N0", CultureInfo.InvariantCulture)} tokens" : string.Empty;
            cli.Data(SafeText.Line($"  {m.Id.PadRight(width)}  {when,-10}  {m.Name}{(context.Length > 0 ? $"  ({context})" : string.Empty)}").TrimEnd());
        }

        cli.Note($"{models.Count.ToString(CultureInfo.InvariantCulture)} model(s). 'diptick config set {section}.model <id>' picks one, or /model in the chat.");
        return Exit.Ok;
    }

    /// <summary>
    /// Unloads a section's model from the runtime serving it, when that runtime can be told to:
    /// Ollama, with a generate request that names the model and a <c>keep_alive</c> of 0, which its
    /// FAQ gives as the way to unload a model at once (docs.ollama.com/faq; <c>ollama stop</c> does
    /// the same from a terminal). Any other provider is not asked: a remote one holds its own
    /// models, and another local runtime has no such request DipTick knows. That is said, and it is
    /// not a failure. A dry run says what it would ask and asks nothing.
    /// </summary>
    private static int Unload(string section, string? named, Cli cli)
    {
        if (ModelLister.QueryFor(section, cli.Config, out var seat, out var why) is not { } query)
        {
            return cli.Fail(why, Exit.Usage);
        }

        var model = named ?? seat?.Model ?? cli.Config.MainModel;
        if (!string.Equals(query.Provider, "ollama", StringComparison.OrdinalIgnoreCase))
        {
            var local = query.Endpoint.IsLoopback;
            if (!cli.WriteJson(new { section = query.Section, provider = query.Provider, model, unloaded = false, reason = local ? "not-ollama" : "not-local" }))
            {
                cli.Note(local
                    ? $"{Engines.DisplayName(query.Provider, query.Endpoint.AbsoluteUri)} runs on this PC, and DipTick knows no way to ask it to unload a model; it is left running."
                    : $"{Engines.DisplayName(query.Provider, query.Endpoint.AbsoluteUri)} holds its own models; nothing on this PC to unload.");
            }

            return Exit.Ok;
        }

        if (model.Length == 0)
        {
            return cli.Fail($"{query.Section} has no model set, so there is nothing to unload.", Exit.Usage);
        }

        if (cli.Flags.DryRun)
        {
            if (!cli.WriteJson(new { section = query.Section, provider = query.Provider, model, unloaded = false, reason = "dry-run" }))
            {
                cli.Note($"[dry run] would ask Ollama to unload {SafeText.Line(model)}; nothing asked.");
            }

            return Exit.Ok;
        }

        // Ollama's own API sits at the root of the address its OpenAI-shaped API lives under (/v1).
        var target = new Uri(query.Endpoint.GetLeftPart(UriPartial.Authority) + "/api/generate");
        var gate = new Gate(cli.Config.Permissions, cli.GateFlags, AuditLog.ForThisUser(), cli.Who, cli.Err, cli.Question, seat);
        if (!gate.Request("models unload", Capability.ModelCall, target.AbsoluteUri))
        {
            return cli.Fail("the harness did not allow this call (model.call). 'diptick audit' says why.");
        }

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cli.Stop);
        cancel.CancelAfter(TimeSpan.FromSeconds(cli.Flags.NetTimeoutSeconds == 0 ? 60 : cli.Flags.NetTimeoutSeconds));
        cli.StopsCleanly();
        try
        {
            var status = PostUnloadAsync(target, model, cancel.Token).GetAwaiter().GetResult();
            if ((int)status is < 200 or > 299)
            {
                return cli.Fail($"{target.Host} answered {((int)status).ToString(CultureInfo.InvariantCulture)} to the unload.");
            }
        }
        catch (OperationCanceledException)
        {
            return cli.Fail(cli.Stop.IsCancellationRequested ? "stopped; the model may still be loaded." : $"{target.Host} did not answer the unload in time.");
        }
        catch (HttpRequestException ex)
        {
            return cli.Fail($"{target.Host} could not be reached: {SafeText.Line(ex.Message)}");
        }

        if (!cli.WriteJson(new { section = query.Section, provider = query.Provider, model, unloaded = true }))
        {
            cli.Note($"Asked Ollama to unload {SafeText.Line(model)}.");
        }

        return Exit.Ok;
    }

    /// <summary>The unload request itself: the model's name and a <c>keep_alive</c> of 0; its answer's status.</summary>
    private static async Task<System.Net.HttpStatusCode> PostUnloadAsync(Uri target, string model, CancellationToken cancel)
    {
        using var client = OpenAiCompatibleEngine.NewClient();
        using var body = new StringContent(JsonSerializer.Serialize(new { model, keep_alive = 0 }), Encoding.UTF8);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.PostAsync(target, body, cancel).ConfigureAwait(false);
        return response.StatusCode;
    }
}
