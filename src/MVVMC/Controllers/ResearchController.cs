using System.Globalization;
using DipTick.Models;
using DipTick.Models.Research;

namespace DipTick.Controllers;

/// <summary>
/// <c>diptick research [status] | search &lt;words&gt; | fetch &lt;url&gt;</c>: the search
/// service agents use and DipTick's own fetch, for the person. Here the person asks, so the gate does
/// not: a person's own search and fetch are theirs. The fetch still goes only to public addresses
/// (<see cref="SafeConnect"/>), over HTTPS, within the same caps as an agent's.
/// </summary>
internal static class ResearchController
{
    private const string Usage = "usage: diptick research [status] | search [--] <words…> | fetch [--] <https-url>";

    public static int Run(IReadOnlyList<string> args, Cli cli)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(cli);
        return (args.Count > 1 ? args[1] : "status") switch
        {
            "status" when args.Count <= 2 => Status(cli),
            "search" => Search(new VerbArgs(args, 2, cli.LiteralFrom), cli),
            "fetch" => Fetch(new VerbArgs(args, 2, cli.LiteralFrom), cli),
            _ => cli.Fail(Usage, Exit.Usage),
        };
    }

    /// <summary>Which search service is set, and what it still needs.</summary>
    private static int Status(Cli cli)
    {
        var problem = SearchService.Problem(cli.Config);
        var address = SearchService.Address(cli.Config);
        if (cli.WriteJson(new { service = cli.Config.ResearchSearch, address = address?.AbsoluteUri, ready = problem is null, problem }))
        {
            return Exit.Ok;
        }

        cli.Data($"  Search service: {SearchService.Name(cli.Config)}{(address is null ? string.Empty : $" ({address.GetLeftPart(UriPartial.Authority)})")}");
        cli.Data($"  {(problem is null ? "Ready." : problem)}");
        cli.Data("  Fetching: DipTick's own, to public HTTPS addresses only, at most 5 redirects, 5 MiB and 30 s a page.");
        return Exit.Ok;
    }

    /// <summary>A search through the person's service: the results, numbered, with their passages.</summary>
    private static int Search(VerbArgs words, Cli cli)
    {
        var query = string.Join(' ', words.Words()).Trim();
        if (words.Problem is not null || query.Length == 0)
        {
            return cli.Fail(Usage, Exit.Usage);
        }

        if (SearchService.QueryProblem(query) is { } bad)
        {
            return cli.Fail(bad + ".", Exit.Usage);
        }

        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would search {SearchService.Name(cli.Config)} for: {SafeText.Line(query)}; nothing sent.");
            return Exit.Ok;
        }

        SearchAnswer answer;
        try
        {
            answer = SearchService.SearchAsync(cli.Config, query, cli.Stop).GetAwaiter().GetResult();
        }
        catch (SearchException ex)
        {
            return cli.Fail(ex.Message);
        }

        if (cli.WriteJson(new
        {
            service = answer.Service,
            query = answer.Query,
            results = answer.Hits.Select(h => new { number = h.Number, address = h.Address, title = h.Title, date = h.Date.Length > 0 ? h.Date : null, passages = h.Passages }),
        }))
        {
            return Exit.Ok;
        }

        if (answer.Hits.Count == 0)
        {
            cli.Note($"{answer.Service} found nothing.");
            return Exit.Ok;
        }

        foreach (var hit in answer.Hits)
        {
            cli.Data(SafeText.Line(string.Create(CultureInfo.InvariantCulture, $"  [{hit.Number}] {hit.Title}")));
            cli.Data(SafeText.Line($"      {hit.Address}{(hit.Date.Length > 0 ? $" ({hit.Date})" : string.Empty)}"));
            foreach (var passage in hit.Passages.Take(2))
            {
                var line = SafeText.Line(passage);
                cli.Data($"      {(line.Length <= 300 ? line : line[..300] + "…")}");
            }
        }

        return Exit.Ok;
    }

    /// <summary>One page fetched as an agent's would be, and its readable text printed; each redirect said.</summary>
    private static int Fetch(VerbArgs words, Cli cli)
    {
        var rest = words.Words();
        if (words.Problem is not null || rest.Count != 1 || !Uri.TryCreate(rest[0], UriKind.Absolute, out var address))
        {
            return cli.Fail(Usage, Exit.Usage);
        }

        if (PageFetcher.Problem(address) is { } bad)
        {
            return cli.Fail(bad + ".", Exit.Usage);
        }

        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would fetch {SafeText.Line(address.AbsoluteUri)}; nothing sent.");
            return Exit.Ok;
        }

        FetchedPage page;
        using (var fetcher = new PageFetcher())
        {
            try
            {
                page = fetcher.FetchAsync(address, (at, hop) =>
                {
                    if (hop > 0)
                    {
                        cli.Note($"redirected to {SafeText.Line(at.AbsoluteUri)}");
                    }

                    return true;
                }, cli.Stop).GetAwaiter().GetResult();
            }
            catch (FetchException ex)
            {
                return cli.Fail(ex.Message);
            }
        }

        if (cli.WriteJson(new
        {
            address = page.Address.AbsoluteUri,
            title = page.Title,
            mediaType = page.MediaType,
            hops = page.Hops,
            bytes = page.Bytes,
            sha256 = page.Sha256,
            article = page.Article,
            fetched = page.Fetched,
            text = page.Pdf is null ? page.Text : null,
            links = page.Links,
        }))
        {
            return Exit.Ok;
        }

        cli.Data(SafeText.Line(page.Title));
        cli.Data(SafeText.Line(string.Create(CultureInfo.InvariantCulture, $"{page.Address.AbsoluteUri} · {page.MediaType} · {(page.Bytes + 1023) / 1024} KiB · {page.Fetched.ToLocalTime():yyyy-MM-dd HH:mm}")));
        cli.Data(string.Empty);
        cli.Data(page.Pdf is null
            ? SafeText.Block(page.Text.Length > 0 ? page.Text : "(the page has no text DipTick could read)")
            : "A PDF. DipTick sends a PDF as it is to a model that reads PDFs, and does not read its text itself.");
        return Exit.Ok;
    }
}
