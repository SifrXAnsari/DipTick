using System.Globalization;
using System.Text.Json;
using DipTick.Models;
using DipTick.Models.Chat;
using DipTick.Models.Chat.Seam;
using DipTick.Models.Chat.Store;
using DipTick.Models.Files;
using DipTick.Models.Tools;

namespace DipTick.ViewModels;

/// <summary>
/// A file waiting in the composer to go with the next message: what
/// <c>--attach</c> names for it (its path, or a pasted image held in memory), what it is, who at the
/// table will be sent it and who will not and why, and for an image its bytes, to draw it.
/// </summary>
internal sealed record PendingFile(string Reference, string Name, string Type, long Size, string Sha256, IReadOnlyList<string> SeenBy, IReadOnlyList<string> NotSent, byte[]? Image);

/// <summary>
/// Files in the chat: the files waiting to go with the next message, and who
/// will see each; the chat's files, read from <c>chat files</c> and set on the lines they came with; the
/// images drawn, read once and kept while they fit; and <c>/attach</c>, <c>/detach</c>, <c>/files</c>,
/// <c>/save</c>, <c>/image</c> and <c>/video</c>, each a verb run in this process.
/// </summary>
internal sealed partial class ChatViewModel
{
    /// <summary>The most bytes of images kept drawn: the oldest go first, and are read again when shown.</summary>
    private const long MostImageBytes = 64L * 1024 * 1024;

    private readonly List<PendingFile> _pending = [];

    /// <summary>Images as last read, by SHA-256; <see cref="_imageOrder"/> holds them oldest first.</summary>
    private readonly Dictionary<string, byte[]> _images = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _imageOrder = new();
    private long _imageBytes;

    /// <summary>The files waiting to go with the next message, in the order they were attached.</summary>
    public IReadOnlyList<PendingFile> Pending
    {
        get
        {
            lock (_lock)
            {
                return [.. _pending];
            }
        }
    }

    /// <summary>
    /// A file attached to the next message (F7, the Attach button, a drop, <c>/attach</c>): read and
    /// checked now, and who at the table will be sent it worked out. Returns what stood in the way, or null.
    /// Runs on a worker thread: Ollama may be asked whether its model sees images.
    /// </summary>
    public async Task<string?> AttachAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        Attached file;
        try
        {
            file = Attachments.Load(path.Trim().Trim('"'));
        }
        catch (SafeFileException ex)
        {
            return ex.Message + ".";
        }

        return await AddPendingAsync(Path.GetFullPath(path.Trim().Trim('"')), file).ConfigureAwait(false);
    }

    /// <summary>
    /// An image pasted into the composer, as PNG: held in this process's memory until it is sent or taken
    /// off, never written anywhere. Returns what stood in the way, or null.
    /// </summary>
    public async Task<string?> AttachPastedAsync(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length > Attachments.MostFile)
        {
            return $"The pasted image is {AgentFolders.Size(png.Length)}; a file may be at most {AgentFolders.Size(Attachments.MostFile)}.";
        }

        var image = new Attached(string.Create(CultureInfo.InvariantCulture, $"pasted-image-{DateTime.Now:yyyyMMdd-HHmmss}.png"), "image/png", png);
        var reference = Attachments.HoldPasted(image);
        var problem = await AddPendingAsync(reference, image).ConfigureAwait(false);
        if (problem is not null)
        {
            Attachments.LetGo(reference);
        }

        return problem;
    }

    /// <summary>A waiting file taken off the next message, by its place in <see cref="Pending"/>.</summary>
    public void Detach(int index)
    {
        PendingFile gone;
        lock (_lock)
        {
            if (index < 0 || index >= _pending.Count)
            {
                return;
            }

            gone = _pending[index];
            _pending.RemoveAt(index);
        }

        Attachments.LetGo(gone.Reference);
        Changed?.Invoke();
    }

    /// <summary>A chip's words for a waiting file: its name and size, and who will see it.</summary>
    public static string ChipWords(PendingFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var who = file.NotSent.Count == 0 ? string.Empty
            : file.SeenBy.Count == 0 ? " · nobody here takes it"
            : $" · not {string.Join(", ", file.NotSent.Select(n => n.Split(':')[0]))}";
        return $"{file.Name} · {AgentFolders.Size(file.Size)}{who}";
    }

    /// <summary>A chip's pointer words for a waiting file: who sees it, and each agent it is not sent to, with why.</summary>
    public static string ChipTip(PendingFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var lines = new List<string> { $"{file.Name} ({file.Type}, {AgentFolders.Size(file.Size)})" };
        if (file.SeenBy.Count > 0)
        {
            lines.Add($"Sent to: {string.Join(", ", file.SeenBy)}");
        }

        lines.AddRange(file.NotSent.Select(n => $"Not sent to {n}"));
        lines.Add("A click on ✕ takes it off the message.");
        return string.Join('\n', lines);
    }

    /// <summary>An image's bytes to draw, by its SHA-256: kept from the composer, or read once from the chat (<c>chat files --bytes</c>). Null when it cannot be read. Runs on a worker thread.</summary>
    public byte[]? ImageOf(ChatFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        lock (_lock)
        {
            if (_images.TryGetValue(file.Sha256, out var known))
            {
                return known;
            }
        }

        if (file.Id <= 0 || _sessionId is not { } id)
        {
            return null;
        }

        var (json, _, code) = Json(Private("chat", "files", $"--session={id}", "--file", file.Id.ToString(CultureInfo.InvariantCulture), "--bytes"));
        if (code != Controllers.Exit.Ok)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var first = doc.RootElement.GetProperty("files").EnumerateArray().FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Object || !first.TryGetProperty("bytes", out var b64) || b64.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var bytes = Convert.FromBase64String(b64.GetString()!);
            KeepImage(file.Sha256, bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Save… on a file of the chat: where it goes (the window's save dialog), then <c>chat save --file</c>,
    /// which asks before it replaces. Runs on a worker thread.
    /// </summary>
    public async Task SaveFileAsync(ChatFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Id <= 0 || _sessionId is not { } id)
        {
            await Say(Role.System, "That file is not kept in the chat yet; it can be saved once its message is.").ConfigureAwait(false);
            return;
        }

        var extension = Path.GetExtension(file.Name).TrimStart('.');
        if (await PathToSaveAsync($"Save {file.Name}", file.Name, file.Type, extension.Length > 0 ? extension : "bin").ConfigureAwait(false) is not { } path)
        {
            return;
        }

        Run(false, Private("chat", "save", $"--session={id}", "--file", file.Id.ToString(CultureInfo.InvariantCulture), "--", path));
    }

    /// <summary>
    /// Save… on an answer, or on one of its code blocks (<paramref name="block"/>, from 1): where it goes,
    /// then <c>chat save --seq [--block]</c>. Runs on a worker thread.
    /// </summary>
    public async Task SaveWordsAsync(ChatLine line, int? block)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Seq is not { } seq || _sessionId is not { } id)
        {
            await Say(Role.System, "That line is not kept in the chat yet; it can be saved once it is.").ConfigureAwait(false);
            return;
        }

        var name = block is { } k
            ? string.Create(CultureInfo.InvariantCulture, $"line-{seq}-code-{k}.txt")
            : string.Create(CultureInfo.InvariantCulture, $"line-{seq}.md");
        if (await PathToSaveAsync(block is null ? $"Save line #{seq.ToString(CultureInfo.InvariantCulture)}" : "Save code", name, block is null ? "Markdown" : "Text", block is null ? "md" : "txt").ConfigureAwait(false) is not { } path)
        {
            return;
        }

        string[] args = block is { } b
            ? ["chat", "save", $"--session={id}", "--seq", seq.ToString(CultureInfo.InvariantCulture), "--block", b.ToString(CultureInfo.InvariantCulture), "--", path]
            : ["chat", "save", $"--session={id}", "--seq", seq.ToString(CultureInfo.InvariantCulture), "--", path];
        Run(false, Private(args));
    }

    /// <summary>A file of the chat opened in Windows' own app for it: only one saved on disk. Returns why it could not be, or null.</summary>
    public static string? OpenFile(ChatFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Path is not { } path || !File.Exists(path))
        {
            return $"{file.Name} is kept in the chat, not in a file of its own; Save… writes it where you say.";
        }

        try
        {
            using var opened = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"{file.Name} could not be opened: {SafeText.Line(ex.Message)}";
        }
    }

    /// <summary>A file of the chat shown in Explorer, selected. Returns why it could not be, or null.</summary>
    public static string? ShowFile(ChatFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return file.Path is { } path ? SettingsViewModel.Reveal(path) : $"{file.Name} is kept in the chat, not in a file of its own.";
    }

    /// <summary>F7 in the terminal: a file's path asked at the prompt, then attached as /attach attaches it. Runs on a worker thread.</summary>
    public async Task AskAttachAsync()
    {
        if (Asker is not { } asker)
        {
            return;
        }

        var typed = asker.Text(new Question("Attach", "Which file goes with your next message?", "An image (PNG, JPEG, GIF, WebP), a PDF or a text file, at most 20 MB: its full path."), string.Empty, text => text.Trim().Length == 0 ? "a file's path" : null);
        if (typed.Reply == Reply.Answered)
        {
            await AttachCommandAsync(typed.Value.Trim()).ConfigureAwait(false);
        }
    }

    /// <summary>/attach &lt;path&gt;: a file for the next message; alone, what is attached.</summary>
    private async Task AttachCommandAsync(string arg)
    {
        if (arg.Length == 0)
        {
            var pending = Pending;
            await Say(Role.System, pending.Count == 0
                ? "Nothing is attached. '/attach <file>' attaches an image, a PDF or a text file to your next message (F7 in the window)."
                : string.Join('\n', pending.Select((f, i) => string.Create(CultureInfo.InvariantCulture, $"{i + 1}. {ChipWords(f)}")))).ConfigureAwait(false);
            return;
        }

        if (await AttachAsync(arg).ConfigureAwait(false) is { } problem)
        {
            await Say(Role.System, problem).ConfigureAwait(false);
            return;
        }

        var added = Pending[^1];
        await Say(Role.System, $"Attached {ChipWords(added)}. It goes with your next message; /detach takes it off.").ConfigureAwait(false);
    }

    /// <summary>/detach [n|all]: the last file attached, the n-th, or all of them, taken off the next message.</summary>
    private async Task DetachCommandAsync(string arg)
    {
        var pending = Pending;
        if (pending.Count == 0)
        {
            await Say(Role.System, "Nothing is attached.").ConfigureAwait(false);
            return;
        }

        if (arg.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            for (var i = pending.Count - 1; i >= 0; i--)
            {
                Detach(i);
            }

            await Say(Role.System, "Every file is taken off the message.").ConfigureAwait(false);
            return;
        }

        var n = pending.Count;
        if (arg.Length > 0 && (!int.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out n) || n < 1 || n > pending.Count))
        {
            await Say(Role.System, string.Create(CultureInfo.InvariantCulture, $"/detach, /detach all, or /detach 1 to {pending.Count}.")).ConfigureAwait(false);
            return;
        }

        Detach(n - 1);
        await Say(Role.System, $"{pending[n - 1].Name} is taken off the message.").ConfigureAwait(false);
    }

    /// <summary>/files: the chat's files (<c>chat files</c>).</summary>
    private async Task FilesCommandAsync(string arg)
    {
        if (_sessionId is not { } id)
        {
            await Say(Role.System, "No files yet: nothing has been said in this chat.").ConfigureAwait(false);
            return;
        }

        var seq = arg.Trim().TrimStart('#');
        await (seq.Length > 0 ? Verb(Private("chat", "files", $"--session={id}", "--seq", seq)) : Verb(Private("chat", "files", $"--session={id}"))).ConfigureAwait(false);
    }

    /// <summary>
    /// /save &lt;n&gt; &lt;file&gt;: line n's words; /save &lt;n&gt; --block &lt;k&gt; &lt;file&gt;, one of its code blocks;
    /// /save --file &lt;id&gt; &lt;file&gt;, one of the chat's files (<c>chat save</c>).
    /// </summary>
    private async Task SaveCommandAsync(string arg)
    {
        const string Words = "/save <n> <file> writes line n's words; /save <n> --block <k> <file>, its k-th code block; /save --file <id> <file>, a file of the chat (/files lists them).";
        if (_sessionId is not { } id)
        {
            await Say(Role.System, "Nothing has been said in this chat yet, so there is nothing to save.").ConfigureAwait(false);
            return;
        }

        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var args = new List<string> { "chat", "save", $"--session={id}" };
        switch (parts)
        {
            case ["--file", var file, .. var rest] when rest.Length > 0:
                args.AddRange(["--file", file, "--", string.Join(' ', rest)]);
                break;
            case [var n, "--block", var k, .. var rest] when rest.Length > 0:
                args.AddRange(["--seq", n.TrimStart('#'), "--block", k, "--", string.Join(' ', rest)]);
                break;
            case [var n, .. var rest] when rest.Length > 0 && n.TrimStart('#').All(char.IsAsciiDigit):
                args.AddRange(["--seq", n.TrimStart('#'), "--", string.Join(' ', rest)]);
                break;
            default:
                await Say(Role.System, Words).ConfigureAwait(false);
                return;
        }

        Run(false, Private([.. args]));
    }

    /// <summary>
    /// /image &lt;words&gt;: an image made from the words (<c>diptick image --session</c>), kept in this
    /// chat as yours, under a line that says so. It costs money on your key, and asks first unless
    /// media.make is allowed. Runs on a worker thread.
    /// </summary>
    private Task ImageCommandAsync(string arg) => MakeAsync("image", arg);

    /// <summary>/video &lt;words&gt;: a video made by Veo (<c>diptick video --session</c>), as /image. Veo takes from 11 seconds to 6 minutes; Esc stops waiting.</summary>
    private Task VideoCommandAsync(string arg) => MakeAsync("video", arg);

    private async Task MakeAsync(string verb, string arg)
    {
        if (arg.Trim().Length == 0)
        {
            await Say(Role.System, verb == "image"
                ? "/image <words> makes an image from them, with the provider media.images names; it costs money on your key."
                : "/video <words> makes a short video with Veo; it costs money on your key, and takes from 11 seconds to 6 minutes.").ConfigureAwait(false);
            return;
        }

        if (Busy)
        {
            await Say(Role.System, "Still answering. Esc or /stop first.").ConfigureAwait(false);
            return;
        }

        if (EnsureKept() is not { } id)
        {
            return;
        }

        using var turn = new CancellationTokenSource();
        _turn = turn;
        Working = verb == "image" ? "making an image…" : "Veo is making a video: from 11 seconds to 6 minutes…";
        Changed?.Invoke();
        string output;
        string error;
        int code;
        try
        {
            using var outWriter = new StringWriter(CultureInfo.InvariantCulture);
            using var errWriter = new StringWriter(CultureInfo.InvariantCulture);
            var args = new List<string> { verb, $"--session={id}" };
            if (_private)
            {
                args.Add("--private");
            }

            args.AddRange(["--", arg.Trim()]);
            var running = Task.Run(() => RunVerb(args, outWriter, errWriter, yes: false, "json", turn.Token));
            _running = running;
            code = await running.ConfigureAwait(false);
            output = outWriter.ToString();
            error = errWriter.ToString();
        }
        finally
        {
            _turn = null;
            Working = null;
        }

        if (code != Controllers.Exit.Ok)
        {
            await Say(Role.System, Plain(error) is { Length: > 0 } said ? said : string.Create(CultureInfo.InvariantCulture, $"diptick {verb} ended with exit {code}.")).ConfigureAwait(false);
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(output);
            var seq = doc.RootElement.GetProperty("seq").GetInt32();
            Add(new ChatLine(Role.System, verb == "image" ? "You made an image." : "You made a video.", DateTimeOffset.Now) { Seq = seq });
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Add(new ChatLine(Role.System, output.Trim(), DateTimeOffset.Now));
        }

        ReadFiles();
        Changed?.Invoke();
    }

    /// <summary>
    /// Where a file goes: the window's save dialog, whose title says what the file holds; in the terminal,
    /// a path typed at the prompt. Null when the person does not say.
    /// </summary>
    private async Task<string?> PathToSaveAsync(string title, string suggested, string kind, string extension)
    {
        if (SaveHook is { } save)
        {
            return await save(title, suggested, kind, extension).ConfigureAwait(false);
        }

        if (Asker is not { } asker)
        {
            return null;
        }

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var typed = asker.Text(new Question(title, "Where should it go?", "A file's full path. A file already there is replaced only after your yes."), Path.Combine(documents, suggested), text => text.Trim().Length == 0 ? "a file's path" : null);
        return typed.Reply == Reply.Answered ? typed.Value.Trim() : null;
    }

    /// <summary>The files waiting, taken for a send: none wait after it.</summary>
    private List<PendingFile> TakePending()
    {
        lock (_lock)
        {
            var taken = _pending.ToList();
            _pending.Clear();
            return taken;
        }
    }

    /// <summary>After a send: the files kept with it are let go of here; when nothing was kept, they wait again for the next.</summary>
    private async Task AfterSendAsync(List<PendingFile> files, bool kept)
    {
        if (files.Count == 0)
        {
            return;
        }

        if (kept)
        {
            foreach (var file in files)
            {
                Attachments.LetGo(file.Reference);
            }

            return;
        }

        lock (_lock)
        {
            _pending.InsertRange(0, files);
        }

        await Say(Role.System, "Nothing was sent, so the files are still attached.").ConfigureAwait(false);
    }

    /// <summary>The files a new chat, or another opened, does not take: let go of, and said.</summary>
    private void DropPending()
    {
        var gone = TakePending();
        foreach (var file in gone)
        {
            Attachments.LetGo(file.Reference);
        }

        if (gone.Count > 0)
        {
            Add(new ChatLine(Role.System, $"Not sent, and taken off: {string.Join(", ", gone.Select(f => f.Name))}.", DateTimeOffset.Now));
        }
    }

    /// <summary>A waiting file as its line shows it until the chat keeps it.</summary>
    private static ChatFile Shown(PendingFile file) =>
        new(0, file.Name, file.Type, file.Size, file.Sha256, Author.Person, file.Reference.StartsWith(Attachments.PastedPrefix, StringComparison.Ordinal) ? null : file.Reference);

    /// <summary>
    /// The chat's files, read again (<c>chat files</c>) and set on the lines they came with, so each line
    /// draws its own. Runs on a worker thread.
    /// </summary>
    private void ReadFiles()
    {
        if (_sessionId is not { } id)
        {
            return;
        }

        var (json, _, code) = Json(Private("chat", "files", $"--session={id}"));
        if (code != Controllers.Exit.Ok)
        {
            return;
        }

        Dictionary<int, IReadOnlyList<ChatFile>> bySeq;
        try
        {
            using var doc = JsonDocument.Parse(json);
            bySeq = doc.RootElement.GetProperty("files").EnumerateArray()
                .Where(f => f.TryGetProperty("seq", out var s) && s.ValueKind == JsonValueKind.Number)
                .GroupBy(f => f.GetProperty("seq").GetInt32())
                .ToDictionary(g => g.Key, g => (IReadOnlyList<ChatFile>)[.. g.Select(f => new ChatFile(
                    f.GetProperty("id").GetInt64(),
                    f.GetProperty("name").GetString() ?? string.Empty,
                    f.GetProperty("type").GetString() ?? string.Empty,
                    f.GetProperty("size").GetInt64(),
                    f.GetProperty("sha256").GetString() ?? string.Empty,
                    f.GetProperty("author").GetString() ?? string.Empty,
                    f.TryGetProperty("saved", out var saved) && saved.ValueKind == JsonValueKind.String ? saved.GetString() : null))]);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return;
        }

        lock (_lines)
        {
            for (var i = 0; i < _transcript.Count; i++)
            {
                var line = _transcript[i];
                if (line.Seq is { } seq && bySeq.TryGetValue(seq, out var files) && !line.Files.SequenceEqual(files))
                {
                    _transcript[i] = line with { Files = files };
                }
            }
        }
    }

    /// <summary>An image's bytes kept to draw, the oldest let go once they come to more than <see cref="MostImageBytes"/>.</summary>
    private void KeepImage(string sha256, byte[] bytes)
    {
        lock (_lock)
        {
            if (sha256.Length == 0 || _images.ContainsKey(sha256) || bytes.Length > MostImageBytes)
            {
                return;
            }

            _images[sha256] = bytes;
            _imageOrder.AddLast(sha256);
            _imageBytes += bytes.Length;
            while (_imageBytes > MostImageBytes && _imageOrder.First is { } oldest)
            {
                _imageBytes -= _images[oldest.Value].Length;
                _images.Remove(oldest.Value);
                _imageOrder.RemoveFirst();
            }
        }
    }

    /// <summary>A file added to the waiting ones, once the message's files still fit together; who at the table will see it, worked out.</summary>
    private async Task<string?> AddPendingAsync(string reference, Attached file)
    {
        lock (_lock)
        {
            var total = _pending.Sum(p => p.Size) + file.Bytes.Length;
            if (total > Attachments.MostMessage)
            {
                return $"With {file.Name}, the files come to {AgentFolders.Size(total)}; a message's files may be at most {AgentFolders.Size(Attachments.MostMessage)} together.";
            }
        }

        var seen = new List<string>();
        var notSent = new List<string>();
        foreach (var chip in Table)
        {
            if (Models.Chat.Seam.Table.Resolve(Config, new TableSeat(chip.Name, chip.Agent, chip.Model)) is not { Filled: true } agent)
            {
                continue;
            }

            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var sees = await ToolSupport.CanSeeAsync(agent, limit.Token).ConfigureAwait(false);
            var reads = await ToolSupport.CanReadPdfAsync(agent, limit.Token).ConfigureAwait(false);
            if (Attachments.RefusedFor(agent, file, sees, reads) is { } why)
            {
                notSent.Add($"{chip.Name}: {why}");
            }
            else
            {
                seen.Add(chip.Name);
            }
        }

        var image = Attachments.IsImage(file.MediaType) ? file.Bytes : null;
        var pending = new PendingFile(reference, file.Name, file.MediaType, file.Bytes.Length, file.Sha256, seen, notSent, image);
        if (image is not null)
        {
            KeepImage(pending.Sha256, image);
        }

        lock (_lock)
        {
            _pending.Add(pending);
        }

        Changed?.Invoke();
        return null;
    }
}
