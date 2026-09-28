using System.Globalization;
using System.Text;
using DipTick.Models;

namespace DipTick.Controllers;

/// <summary>
/// <c>diptick docs</c>: DipTick's official pages, the same the window's and the terminal screen's
/// Help show (<see cref="Docs"/>). <c>docs</c> lists them, <c>docs &lt;page&gt;</c> prints one,
/// <c>docs search &lt;words&gt;</c> finds lines, <c>docs export &lt;folder&gt;</c> writes every page as a
/// Markdown file, to keep or print.
/// </summary>
internal static class DocsController
{
    private const string Usage = "usage: diptick docs | docs <page> | docs search <words…> | docs export <folder>";

    public static int Run(IReadOnlyList<string> args, Cli cli)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(cli);
        if (args.Count == 1)
        {
            return List(cli);
        }

        return args[1] switch
        {
            "search" when args.Count > 2 => Search(string.Join(' ', args.Skip(2)), cli),
            "export" when args.Count == 3 => Export(args[2], cli),
            "search" or "export" => cli.Fail(Usage, Exit.Usage),
            _ => Show(string.Join(' ', args.Skip(1)), cli),
        };
    }

    private static int List(Cli cli)
    {
        if (cli.WriteJson(Docs.Pages.Select(p => new { page = p.Id, p.Title })))
        {
            return Exit.Ok;
        }

        var width = Docs.Pages.Max(p => p.Id.Length) + 2;
        foreach (var page in Docs.Pages)
        {
            cli.Data($"  {page.Id.PadRight(width)}{page.Title}");
        }

        cli.Note("'diptick docs <page>' prints one; 'diptick docs search <words>' finds them.");
        return Exit.Ok;
    }

    private static int Show(string wanted, Cli cli)
    {
        if (Docs.Find(wanted) is not { } page)
        {
            return cli.Fail($"no page '{SafeText.Line(wanted)}'. 'diptick docs' lists them.", Exit.Usage);
        }

        if (!cli.WriteJson(new { page = page.Id, page.Title, text = page.Text }))
        {
            cli.Data(page.Text.TrimEnd());
        }

        return Exit.Ok;
    }

    private static int Search(string words, Cli cli)
    {
        var hits = Docs.Search(words);
        if (cli.WriteJson(hits.Select(h => new { page = h.Page.Id, line = h.Line, h.Text })))
        {
            return Exit.Ok;
        }

        if (hits.Count == 0)
        {
            cli.Note($"No page holds '{SafeText.Line(words)}'.");
            return Exit.Ok;
        }

        foreach (var hit in hits)
        {
            cli.Data(SafeText.Line(string.Create(CultureInfo.InvariantCulture, $"{hit.Page.Id}:{hit.Line}  {hit.Text}")));
        }

        return Exit.Ok;
    }

    /// <summary>Every page as <c>&lt;page&gt;.md</c> in <paramref name="folder"/>: a person's act, so it honours a dry run and asks before it overwrites.</summary>
    private static int Export(string folder, Cli cli)
    {
        string path;
        try
        {
            path = Path.GetFullPath(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return cli.Fail($"not a path: {SafeText.Line(folder)}", Exit.Usage);
        }

        var files = Docs.Pages.Select(p => (Page: p, File: Path.Combine(path, p.Id + ".md"))).ToList();
        if (cli.Flags.DryRun)
        {
            cli.Note(string.Create(CultureInfo.InvariantCulture, $"[dry run] would write {files.Count} pages to {path}; nothing written."));
            return Exit.Ok;
        }

        var there = files.Count(f => File.Exists(f.File));
        if (there > 0)
        {
            var answer = cli.Confirm(there == 1
                ? $"One of the pages is in {path} already. Overwrite it?"
                : string.Create(CultureInfo.InvariantCulture, $"{there} of the pages are in {path} already. Overwrite them?"));
            if (answer != Answer.Yes)
            {
                return cli.NotConfirmed(answer, $"overwriting pages in {path}");
            }
        }

        try
        {
            Directory.CreateDirectory(path);
            foreach (var (page, file) in files)
            {
                File.WriteAllText(file, page.Text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return cli.Fail($"could not write {path}: {ex.Message}");
        }

        if (!cli.WriteJson(new { folder = path, pages = files.Select(f => f.File) }))
        {
            cli.Note(string.Create(CultureInfo.InvariantCulture, $"Wrote {files.Count} pages to {path}."));
        }

        return Exit.Ok;
    }
}
