using System.Globalization;
using System.Text;
using DipTick.Models;
using DipTick.Models.Chat.Store;
using DipTick.Models.Files;

namespace DipTick.Controllers;

/// <summary>
/// A chat's files: <c>chat files</c>, what was attached or made in it; and
/// <c>chat save</c>, an answer's words, one of its code blocks, or a file of the chat, written where the
/// person says.
/// </summary>
internal static partial class ChatController
{
    private const string FilesUsage = "usage: diptick chat files [--session <id|last>] [--seq <n>] [--file <id>] [--bytes]";
    private const string SaveUsage = "usage: diptick chat save [--session <id|last>] (--seq <n> [--block <k>] | --file <id>) [--] <file>";

    /// <summary>
    /// <c>chat files</c>: every file of the chat, of one line, or one file: its id, the line, who brought
    /// it, its name, type and size, and where it was saved. With <c>--bytes</c> (JSON only), each image's
    /// bytes in base64, for a screen that draws them.
    /// </summary>
    private static int Files(IChatStore store, Cli cli, VerbArgs words)
    {
        var session = words.Option("--session") ?? "last";
        var seqText = words.Option("--seq");
        var fileText = words.Option("--file");
        var withBytes = words.Switch("--bytes");
        if (words.Problem is not null || words.Words().Count > 0)
        {
            return cli.Fail(FilesUsage, Exit.Usage);
        }

        int? seq = null;
        if (seqText is not null)
        {
            if (!int.TryParse(seqText, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1)
            {
                return cli.Fail("--seq takes a line's number.", Exit.Usage);
            }

            seq = n;
        }

        long? only = null;
        if (fileText is not null)
        {
            if (!long.TryParse(fileText, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 1)
            {
                return cli.Fail("--file takes a file's number, as 'diptick chat files' lists them.", Exit.Usage);
            }

            only = id;
        }

        if (withBytes && !cli.Json)
        {
            return cli.Fail("--bytes is for --output json: it adds each image's bytes, in base64.", Exit.Usage);
        }

        if (store.Find(session) is not { } found)
        {
            return cli.Fail(NoSuch(session));
        }

        var files = store.Attachments(found.Id, withBytes).Where(f => (seq is null || f.Seq == seq) && (only is null || f.Id == only)).ToList();
        if (only is not null && files.Count == 0)
        {
            return cli.Fail($"no file {only.Value.ToString(CultureInfo.InvariantCulture)} in this chat; 'diptick chat files' lists them.");
        }

        if (cli.WriteJson(new
        {
            session = found.Id,
            files = files.Select(f => new
            {
                id = f.Id,
                seq = f.Seq,
                author = f.Author,
                name = f.Name,
                type = f.Type,
                size = f.Size,
                sha256 = f.Sha256,
                saved = f.SavedPath,
                created = f.Created,
                bytes = withBytes && Attachments.IsImage(f.Type) && f.Bytes is { } b ? Convert.ToBase64String(b) : null,
            }),
        }))
        {
            return Exit.Ok;
        }

        if (files.Count == 0)
        {
            cli.Note(seq is null ? "No files in this chat yet." : "No files with that line.");
            return Exit.Ok;
        }

        foreach (var f in files)
        {
            var line = f.Seq is { } n ? string.Create(CultureInfo.InvariantCulture, $" · line #{n}") : string.Empty;
            var saved = f.SavedPath is { } path ? $" · {path}" : string.Empty;
            cli.Data(SafeText.Line(string.Create(CultureInfo.InvariantCulture, $"  {f.Id,4}  {f.Name}  {f.Type}  {AgentFolders.Size(f.Size)} · {(f.Author == Author.Person ? "you" : f.Author)}{line}{saved}")));
        }

        return Exit.Ok;
    }

    /// <summary>
    /// <c>chat save</c>: a line's words (an answer as it was written), with <c>--block</c> one of its code
    /// blocks, or with <c>--file</c> one of the chat's files, written to a file the person names; an
    /// existing file is replaced only after a yes.
    /// </summary>
    private static int Save(IChatStore store, Cli cli, VerbArgs words)
    {
        var session = words.Option("--session") ?? "last";
        var seqText = words.Option("--seq");
        var blockText = words.Option("--block");
        var fileText = words.Option("--file");
        var rest = words.Words();
        if (words.Problem is not null || rest.Count != 1 || (seqText is null) == (fileText is null) || (blockText is not null && seqText is null))
        {
            return cli.Fail(SaveUsage, Exit.Usage);
        }

        if (store.Find(session) is not { } found)
        {
            return cli.Fail(NoSuch(session));
        }

        byte[] bytes;
        string what;
        if (fileText is not null)
        {
            if (!long.TryParse(fileText, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || store.Attachments(found.Id, withBytes: true).FirstOrDefault(f => f.Id == id) is not { } file)
            {
                return cli.Fail($"no file {SafeText.Line(fileText)} in this chat; 'diptick chat files' lists them.", Exit.Usage);
            }

            try
            {
                // A file saved to the chats' folder is read back from it only while it is still safely inside it.
                bytes = file.Bytes ?? (file.SavedPath is { } saved ? SafeFiles.Read(saved, cli.Config.AgentFilesFolder, Models.Media.VideoMaker.MostVideo, whole: true).Bytes : []);
            }
            catch (SafeFileException ex)
            {
                return cli.Fail($"{file.Name} could not be read from {file.SavedPath}: {ex.Message}");
            }

            what = file.Name;
        }
        else
        {
            if (!int.TryParse(seqText, NumberStyles.None, CultureInfo.InvariantCulture, out var seq) || store.Messages(found.Id).FirstOrDefault(m => m.Seq == seq) is not { } line)
            {
                return cli.Fail($"no line #{SafeText.Line(seqText ?? string.Empty)} in this chat.", Exit.Usage);
            }

            if (blockText is null)
            {
                bytes = Encoding.UTF8.GetBytes(line.Text);
                what = string.Create(CultureInfo.InvariantCulture, $"line #{seq}");
            }
            else
            {
                var blocks = MarkdownText.CodeBlocks(line.Text);
                if (!int.TryParse(blockText, NumberStyles.None, CultureInfo.InvariantCulture, out var k) || k < 1 || k > blocks.Count)
                {
                    return cli.Fail(blocks.Count == 0
                        ? string.Create(CultureInfo.InvariantCulture, $"line #{seq} has no code block.")
                        : string.Create(CultureInfo.InvariantCulture, $"--block takes 1 to {blocks.Count}: line #{seq} has {blocks.Count} code {(blocks.Count == 1 ? "block" : "blocks")}."), Exit.Usage);
                }

                bytes = Encoding.UTF8.GetBytes(blocks[k - 1]);
                what = string.Create(CultureInfo.InvariantCulture, $"code block {k} of line #{seq}");
            }
        }

        string target;
        try
        {
            target = Path.GetFullPath(rest[0]);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return cli.Fail($"not a file's path: {SafeText.Line(rest[0])}", Exit.Usage);
        }

        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would write {what} ({AgentFolders.Size(bytes.Length)}) to {target}; nothing written.");
            return Exit.Ok;
        }

        if (File.Exists(target))
        {
            var answer = cli.Confirm($"Replace {target}?");
            if (answer != Answer.Yes)
            {
                return cli.NotConfirmed(answer, $"replacing {target}");
            }
        }

        try
        {
            File.WriteAllBytes(target, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return cli.Fail($"{target} could not be written: {SafeText.Line(ex.Message)}");
        }

        if (!cli.WriteJson(new { saved = target, bytes = bytes.Length }))
        {
            cli.Note($"Wrote {what} to {target}.");
        }

        return Exit.Ok;
    }
}
