using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DipTick.Models.Chat;

/// <summary>
/// A large paste kept in a text file, as Hermes Agent keeps one. Hermes saves a paste of 5 lines or more under
/// <c>~/.hermes/pastes</c>, shows "[Pasted text #n: X lines → file]" in the input instead of the
/// text, and puts the file's text back into the message when it is sent (hermes_cli/cli_tui_mixin.py,
/// <c>_tui_collapse_paste</c>; cli.py, <c>_expand_paste_references</c>; read 2026-09-27). DipTick
/// does the same in its data folder, with two differences: what was already typed stays (Hermes'
/// issue #5626 reports its fallback replacing it), and a paste's file goes when the screen that
/// made it closes unless a kept draft still names it, so a pasted secret does not pile up on disk.
/// </summary>
internal static partial class Pastes
{
    /// <summary>The folder under DipTick's data folder that holds pastes.</summary>
    public const string FolderName = "pastes";

    /// <summary>Hermes' threshold: a paste of this many lines or more goes to a file.</summary>
    public const int DefaultLines = 5;

    /// <summary>A file left by a screen that ended without closing (a crash) goes after this long, unless a kept draft names it.</summary>
    public static readonly TimeSpan LeftOver = TimeSpan.FromDays(1);

    public static string Folder(string dataFolder) => Path.Combine(dataFolder, FolderName);

    /// <summary>The lines in a paste; a last line break does not start another.</summary>
    public static int Lines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normal = Normal(text);
        if (normal.EndsWith('\n'))
        {
            normal = normal[..^1];
        }

        return normal.Length == 0 ? 0 : normal.Count(c => c == '\n') + 1;
    }

    /// <summary>
    /// Keeps <paramref name="text"/> in a new file in <paramref name="folder"/> and returns the
    /// reference that stands for it in the composer, and the file's path. Nothing is overwritten.
    /// </summary>
    public static (string Reference, string Path) Keep(string folder, int number, string text, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(text);
        Directory.CreateDirectory(folder);
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(Normal(text));
        var stem = string.Create(CultureInfo.InvariantCulture, $"paste-{now:yyyyMMdd-HHmmss}-{number}");
        for (var again = 0; ; again++)
        {
            var name = again == 0 ? stem + ".txt" : string.Create(CultureInfo.InvariantCulture, $"{stem}-{again}.txt");
            var path = Path.Combine(folder, name);
            try
            {
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                file.Write(bytes);
            }
            catch (IOException) when (File.Exists(path) && again < 100)
            {
                // Another screen made one with this name in this second: the next name.
                continue;
            }

            var lines = Lines(text);
            return (string.Create(CultureInfo.InvariantCulture, $"[Pasted text #{number}: {lines} {(lines == 1 ? "line" : "lines")} → {name}]"), path);
        }
    }

    /// <summary>
    /// The message as sent: each reference replaced by its file's text, in one pass, so a
    /// reference inside a pasted text stays as it was typed. A reference names a file in
    /// <paramref name="folder"/> only, by a name DipTick makes; one whose file cannot be read is
    /// left as it is and listed in Missing.
    /// </summary>
    public static (string Text, IReadOnlyList<string> Missing) Expand(string text, string folder)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(folder);
        var missing = new List<string>();
        var expanded = Reference().Replace(text, match =>
        {
            var name = match.Groups["name"].Value;
            try
            {
                return File.ReadAllText(Path.Combine(folder, name), Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                missing.Add(name);
                return match.Value;
            }
        });
        return (expanded, missing);
    }

    /// <summary>The paste numbers <paramref name="texts"/> already use, so a new paste is numbered after them.</summary>
    public static int Highest(IEnumerable<string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        return texts.SelectMany(t => Reference().Matches(t))
            .Select(m => int.TryParse(m.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();
    }

    /// <summary>
    /// Removes the files in <paramref name="made"/> that no text in <paramref name="kept"/> names:
    /// what a closing screen made and no kept draft needs. Returns how many went.
    /// </summary>
    public static int Sweep(IEnumerable<string> made, IEnumerable<string> kept)
    {
        ArgumentNullException.ThrowIfNull(made);
        var named = Named(kept);
        var gone = 0;
        foreach (var path in made)
        {
            if (!named.Contains(Path.GetFileName(path)) && Delete(path))
            {
                gone++;
            }
        }

        return gone;
    }

    /// <summary>
    /// Removes pastes older than <see cref="LeftOver"/> that no text in <paramref name="kept"/>
    /// names: files a screen left when it ended without closing. A younger one may be another
    /// screen's, still open. Returns how many went.
    /// </summary>
    public static int SweepLeftOvers(string folder, IEnumerable<string> kept, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        var named = Named(kept);
        var gone = 0;
        foreach (var path in Directory.EnumerateFiles(folder, "paste-*.txt"))
        {
            var name = Path.GetFileName(path);
            if (IsName(name) && !named.Contains(name) && nowUtc - File.GetLastWriteTimeUtc(path) > LeftOver && Delete(path))
            {
                gone++;
            }
        }

        return gone;
    }

    private static HashSet<string> Named(IEnumerable<string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        return [.. texts.SelectMany(t => Reference().Matches(t)).Select(m => m.Groups["name"].Value)];
    }

    private static bool IsName(string name) => FileName().IsMatch(name);

    private static bool Delete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Open in an editor, or held by the virus scanner: it goes on a later sweep.
            return false;
        }
    }

    private static string Normal(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    // The names DipTick makes, and nothing else: no folder, no other file, can be named by a reference.
    [GeneratedRegex(@"^paste-\d{8}-\d{6}-\d+(?:-\d+)?\.txt$", RegexOptions.CultureInvariant)]
    private static partial Regex FileName();

    [GeneratedRegex(@"\[Pasted text #(?<number>\d+): \d+ lines? → (?<name>paste-\d{8}-\d{6}-\d+(?:-\d+)?\.txt)\]", RegexOptions.CultureInvariant)]
    private static partial Regex Reference();
}
