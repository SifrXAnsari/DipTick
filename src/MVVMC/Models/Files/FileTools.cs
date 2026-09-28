using System.Globalization;
using System.Text;
using System.Text.Json;
using DipTick.Models.Harness;
using DipTick.Models.Tools;

namespace DipTick.Models.Files;

/// <summary>
/// Where agents' files are: each chat's own folder under <c>files.folder</c>
/// (Documents\DipTick unless set), which is always one of the folders the file capabilities may touch;
/// a bare name means a file there; any other path must be inside a folder the permissions open.
/// </summary>
internal static class AgentFolders
{
    /// <summary>A chat's own folder: <c>files.folder</c> (or Documents\DipTick), then the chat's id.</summary>
    public static string ChatFolder(MasterConfig config, string session)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(session);
        return Path.Combine(config.AgentFilesFolder, session.Length > 0 ? session : "outside-a-chat");
    }

    /// <summary>A path an agent gave, made full: relative to the chat's folder, or as it stands when it is full.</summary>
    /// <exception cref="SafeFileException">It is not a path Windows can use.</exception>
    public static string Resolve(ToolContext context, string path)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(ChatFolder(context.Config, context.Session), path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new SafeFileException("that is not a path Windows can use", ex);
        }
    }

    /// <summary>The folder the permissions open that holds this path, or null.</summary>
    public static string? RootOf(Permissions permissions, string full)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(full);
        return permissions.Roots.Select(Path.GetFullPath).FirstOrDefault(r => SafeFiles.Within(full, r));
    }

    /// <summary>Text for the model: the bytes as UTF-8, or null when they are not text (not UTF-8, or a NUL in them).</summary>
    public static string? Text(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IndexOf((byte)0) >= 0)
        {
            return null;
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>A size for a person: bytes, KiB or MiB.</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KiB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MiB"),
    };
}

/// <summary><c>list_folder</c> (<c>fs.read</c>): a folder's names, sizes and dates, through <see cref="SafeFiles"/>.</summary>
internal sealed class ListFolderTool : IAgentTool
{
    public string Name => "list_folder";

    public string Description =>
        "List a folder: its folders and files, with each file's size and when it was changed. A folder inside one the person opened to agents, " +
        "or a path relative to this chat's own folder (empty for the chat's folder itself). Links are shown, never followed.";

    public JsonElement Parameters { get; } = ToolArgs.Schema(("path", "string", "The folder: a full path, or relative to this chat's folder; empty for the chat's folder.", false));

    public Task<ToolOutcome> RunAsync(ToolContext context, JsonElement arguments, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var full = AgentFolders.Resolve(context, ToolArgs.Text(arguments, "path") ?? string.Empty);
            if (!context.Gate.Request("list", Capability.FsRead, full))
            {
                return Task.FromResult(ToolOutcome.Refused("fs.read"));
            }

            if (AgentFolders.RootOf(context.Config.Permissions, full) is not { } root)
            {
                return Task.FromResult(ToolOutcome.Failed("that folder is outside every folder agents may use"));
            }

            var entries = SafeFiles.List(full, root);
            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"{full}: {entries.Count} entr{(entries.Count == 1 ? "y" : "ies")}{(entries.Count >= SafeFiles.MostEntries ? " (the first ones only)" : string.Empty)}\n");
            foreach (var e in entries)
            {
                text.Append(e.Folder
                    ? $"{e.Name}\\{(e.Link ? "  (a link, not followed)" : string.Empty)}\n"
                    : string.Create(CultureInfo.InvariantCulture, $"{e.Name}  {AgentFolders.Size(e.Size)}  {e.Modified:yyyy-MM-dd HH:mm}{(e.Link ? "  (a link, not followed)" : string.Empty)}\n"));
            }

            return Task.FromResult(new ToolOutcome(text.ToString(), string.Create(CultureInfo.InvariantCulture, $"{SafeText.Line(full)}: {entries.Count} entries")));
        }
        catch (SafeFileException ex)
        {
            return Task.FromResult(ToolOutcome.Failed(ex.Message));
        }
    }
}

/// <summary><c>read_file</c> (<c>fs.read</c>): a text file's lines, from a line on, through <see cref="SafeFiles"/>.</summary>
internal sealed class ReadFileTool : IAgentTool
{
    /// <summary>The most of a file read at once: four times what one call may give back, so a part can be chosen by line.</summary>
    public const int MostRead = 4 * AgentTools.MostResult;

    public string Name => "read_file";

    public string Description =>
        "Read a text file (UTF-8: code, Markdown, CSV, JSON, plain text) inside a folder the person opened to agents, or in this chat's folder. " +
        "Long files come in parts: give from_line to read on from a line. A binary file is described, not shown.";

    public JsonElement Parameters { get; } = ToolArgs.Schema(
        ("path", "string", "The file: a full path, or relative to this chat's folder.", true),
        ("from_line", "integer", "The first line to give, from 1; 1 when left out.", false));

    public Task<ToolOutcome> RunAsync(ToolContext context, JsonElement arguments, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (ToolArgs.Text(arguments, "path") is not { } asked)
        {
            return Task.FromResult(ToolOutcome.Failed("give the file in 'path'"));
        }

        try
        {
            var full = AgentFolders.Resolve(context, asked);
            if (!context.Gate.Request("read", Capability.FsRead, full))
            {
                return Task.FromResult(ToolOutcome.Refused("fs.read"));
            }

            if (AgentFolders.RootOf(context.Config.Permissions, full) is not { } root)
            {
                return Task.FromResult(ToolOutcome.Failed("that file is outside every folder agents may use"));
            }

            var (bytes, size) = SafeFiles.Read(full, root, MostRead);
            if (AgentFolders.Text(bytes) is not { } text)
            {
                return Task.FromResult(new ToolOutcome($"{full}: a binary file of {AgentFolders.Size(size)}; its bytes are not text, so they are not shown.", $"{SafeText.Line(Path.GetFileName(full))}: binary, {AgentFolders.Size(size)}"));
            }

            var lines = text.ReplaceLineEndings("\n").Split('\n');
            var from = (int)Math.Clamp(ToolArgs.Number(arguments, "from_line") ?? 1, 1, Math.Max(1, lines.Length));
            var shown = new StringBuilder();
            var last = from - 1;
            for (var i = from - 1; i < lines.Length && shown.Length + lines[i].Length + 1 <= AgentTools.MostResult - 512; i++)
            {
                shown.Append(lines[i]).Append('\n');
                last = i + 1;
            }

            var partial = size > bytes.Length ? string.Create(CultureInfo.InvariantCulture, $" (only its first {AgentFolders.Size(bytes.Length)} are read)") : string.Empty;
            var more = last < lines.Length ? string.Create(CultureInfo.InvariantCulture, $"\n[lines {last + 1} to {lines.Length} not shown: read on with from_line {last + 1}]") : string.Empty;
            var head = string.Create(CultureInfo.InvariantCulture, $"{full}: {AgentFolders.Size(size)}, lines {from} to {last} of {lines.Length}{partial}\n\n");
            return Task.FromResult(new ToolOutcome(head + shown + more, string.Create(CultureInfo.InvariantCulture, $"{SafeText.Line(Path.GetFileName(full))}: lines {from} to {last} of {lines.Length}")));
        }
        catch (SafeFileException ex)
        {
            return Task.FromResult(ToolOutcome.Failed(ex.Message));
        }
    }
}

/// <summary>
/// <c>write_file</c> (<c>fs.write</c>, Ask by default): a text file written through <see cref="SafeFiles"/>;
/// a bare name goes to this chat's own folder. An existing file is replaced only when asked to.
/// </summary>
internal sealed class WriteFileTool : IAgentTool
{
    /// <summary>The most one write may be.</summary>
    public const int MostWrite = 4 * 1024 * 1024;

    public string Name => "write_file";

    public string Description =>
        "Write a text file. A bare name, like notes.md, goes to this chat's own folder; a full path must be inside a folder the person opened to agents. " +
        "The person is asked first unless they allowed it. An existing file is replaced only with overwrite true.";

    public JsonElement Parameters { get; } = ToolArgs.Schema(
        ("path", "string", "The file: a name for this chat's folder, or a full path.", true),
        ("content", "string", "The whole text to write, UTF-8.", true),
        ("overwrite", "boolean", "Replace a file that is already there; false when left out.", false));

    public Task<ToolOutcome> RunAsync(ToolContext context, JsonElement arguments, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (ToolArgs.Text(arguments, "path") is not { } asked)
        {
            return Task.FromResult(ToolOutcome.Failed("give the file in 'path'"));
        }

        var content = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? string.Empty : null;
        if (content is null)
        {
            return Task.FromResult(ToolOutcome.Failed("give the text to write in 'content'"));
        }

        var bytes = Encoding.UTF8.GetBytes(content);
        if (bytes.Length > MostWrite)
        {
            return Task.FromResult(ToolOutcome.Failed($"that is {AgentFolders.Size(bytes.Length)}, more than the {AgentFolders.Size(MostWrite)} one write may be"));
        }

        var overwrite = arguments.TryGetProperty("overwrite", out var o) && o.ValueKind == JsonValueKind.True;
        try
        {
            var full = AgentFolders.Resolve(context, asked);
            if (!context.Gate.Request("write", Capability.FsWrite, full))
            {
                return Task.FromResult(ToolOutcome.Refused("fs.write"));
            }

            if (AgentFolders.RootOf(context.Config.Permissions, full) is not { } root)
            {
                return Task.FromResult(ToolOutcome.Failed("that file is outside every folder agents may use"));
            }

            SafeFiles.Write(full, root, bytes, overwrite);
            return Task.FromResult(new ToolOutcome($"Wrote {AgentFolders.Size(bytes.Length)} to {full}.", $"wrote {SafeText.Line(Path.GetFileName(full))} ({AgentFolders.Size(bytes.Length)})"));
        }
        catch (SafeFileException ex)
        {
            return Task.FromResult(ToolOutcome.Failed(ex.Message));
        }
    }
}
