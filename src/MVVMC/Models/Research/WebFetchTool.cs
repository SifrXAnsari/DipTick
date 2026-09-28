using System.Globalization;
using System.Text.Json;
using DipTick.Models.Chat;
using DipTick.Models.Chat.Store;
using DipTick.Models.Harness;
using DipTick.Models.Tools;

namespace DipTick.Models.Research;

/// <summary>
/// <c>web_fetch</c>: one page fetched by DipTick itself, through
/// <see cref="PageFetcher"/>. The gate is asked for the address and for every redirect as a
/// <c>net.fetch</c> of its own; an address first seen in the person's messages, a search result or a
/// page fetched in the chat answers the settings' ask, and any other address is asked about. The page
/// becomes a numbered source of the chat, and its links addresses the chat has seen.
/// </summary>
internal sealed class WebFetchTool : IAgentTool
{
    public string Name => ProviderResearch.Fetch;

    public string Description =>
        "Fetch one web page and read its main text. Give an address exactly as it appeared in the person's messages, " +
        "in a search result, or in a page already fetched in this chat; any other address needs the person's yes. " +
        "Returns the page's text with its source number; cite it like [4].";

    public JsonElement Parameters { get; } = ToolArgs.Schema(("url", "string", "The page's full https address.", true));

    public async Task<ToolOutcome> RunAsync(ToolContext context, JsonElement arguments, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (ToolArgs.Text(arguments, "url") is not { } text || !Uri.TryCreate(text, UriKind.Absolute, out var address))
        {
            return ToolOutcome.Failed("give the page's full https address in 'url'");
        }

        var vouched = Vouch(context.Seen.Origin(address.AbsoluteUri));
        var previous = address;
        bool MayGo(Uri at, int hop)
        {
            var allowed = context.Gate.Request("fetch", Capability.NetFetch, at.AbsoluteUri, hop == 0 ? vouched : $"a redirect from {previous.Host}");
            previous = at;
            return allowed;
        }

        FetchedPage page;
        using (var fetcher = new PageFetcher())
        {
            try
            {
                page = await fetcher.FetchAsync(address, MayGo, cancel).ConfigureAwait(false);
            }
            catch (FetchException ex) when (ex.Refused)
            {
                return ToolOutcome.Refused("net.fetch");
            }
            catch (FetchException ex)
            {
                return ToolOutcome.Failed(ex.Message);
            }
        }

        var number = Keep(context, page);
        var canRead = page.Pdf is null || await ToolSupport.CanReadPdfAsync(context.Agent, cancel).ConfigureAwait(false);
        return Outcome(page, number, canRead);
    }

    /// <summary>What vouches for an address, in words for the audit log; null when the chat never saw it.</summary>
    internal static string? Vouch(SeenFrom? origin) => origin switch
    {
        SeenFrom.Person => "the person wrote it",
        SeenFrom.Search => "a search result",
        SeenFrom.Page => "a page fetched in this chat",
        _ => null,
    };

    /// <summary>The page kept as a source of the chat and its links seen; its number in the chat (0 outside one).</summary>
    internal static int Keep(ToolContext context, FetchedPage page)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(page);
        context.Seen.Add(page.Address.AbsoluteUri, SeenFrom.Page);
        foreach (var link in page.Links)
        {
            context.Seen.Add(link, SeenFrom.Page);
        }

        return context.Session.Length > 0
            ? context.Store.AddSource(context.Session, new StoredSource(0, null, page.Address.AbsoluteUri, page.Title, SourceKind.Fetched, page.Fetched, page.Sha256, page.Pdf is null ? page.Text : null))
            : 0;
    }

    /// <summary>What the model is given: the page's number, title, address and text; a PDF as a document where the model reads one, described where it does not.</summary>
    internal static ToolOutcome Outcome(FetchedPage page, int number, bool canReadPdf)
    {
        ArgumentNullException.ThrowIfNull(page);
        var label = number > 0 ? string.Create(CultureInfo.InvariantCulture, $"[{number}] ") : string.Empty;
        var redirects = page.Hops switch
        {
            0 => string.Empty,
            1 => ", after a redirect",
            _ => string.Create(CultureInfo.InvariantCulture, $", after {page.Hops} redirects"),
        };
        var head = string.Create(CultureInfo.InvariantCulture, $"{label}{page.Title}\n{page.Address.AbsoluteUri}\nfetched {page.Fetched:yyyy-MM-dd HH:mm} UTC{redirects}");
        var size = string.Create(CultureInfo.InvariantCulture, $"{(page.Bytes + 1023) / 1024} KiB");
        var summary = $"{SafeText.Line(page.Address.Host)}: {SafeText.Line(page.Title)} ({size})";
        var sources = number > 0 ? (IReadOnlyList<int>)[number] : [];
        if (page.Pdf is { } pdf)
        {
            return canReadPdf
                ? new ToolOutcome($"{head}\nA PDF of {size}, sent with this result as a document.", summary) { Sources = sources, File = new WireFile(FileName(page), "application/pdf", pdf) }
                : new ToolOutcome($"{head}\nA PDF of {size}. This model is not sent PDFs, so its text is not here; say so if it matters.", summary) { Sources = sources };
        }

        var body = page.Text.Length > 0 ? page.Text : "(the page has no text DipTick could read)";
        return new ToolOutcome($"{head}\n\n{body}", summary) { Sources = sources };
    }

    private static string FileName(FetchedPage page)
    {
        var last = page.Address.Segments.LastOrDefault()?.Trim('/') ?? string.Empty;
        return last.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? last : "page.pdf";
    }
}
