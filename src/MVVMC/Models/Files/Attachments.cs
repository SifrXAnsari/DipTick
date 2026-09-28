using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DipTick.Models.Chat;
using DipTick.Models.Providers;

namespace DipTick.Models.Files;

/// <summary>A file the person attached: its name, its media type, and its bytes.</summary>
internal sealed record Attached(string Name, string MediaType, byte[] Bytes)
{
    /// <summary>Its SHA-256, as the chat keeps it.</summary>
    public string Sha256 => Convert.ToHexStringLower(SHA256.HashData(Bytes));
}

/// <summary>
/// Files in: what the person may attach, and what each agent is sent of it.
/// Images (PNG, JPEG, GIF, WebP), PDFs and text files (UTF-8), at most 20 MB each and 20 MB a message,
/// Gemini's inline cap and the smallest of the APIs'. Each engine takes what its API documents:
/// Anthropic images and PDFs, 10 MB an image in base64 on its own API, AWS and Foundry, 5 MB on Bedrock
/// and Google Cloud; the OpenAI shape PNG, JPEG, WebP and GIF, no PDFs; Gemini PNG, JPEG and WebP (no
/// GIF) and PDFs. Text files go to every model, as text under their names.
/// </summary>
internal static class Attachments
{
    /// <summary>The most one file may be.</summary>
    public const int MostFile = 20 * 1024 * 1024;

    /// <summary>The most one message's files may be together.</summary>
    public const int MostMessage = 20 * 1024 * 1024;

    /// <summary>What <c>--attach</c> takes for an image pasted into a screen of this process: <c>pasted:&lt;name&gt;</c>.</summary>
    public const string PastedPrefix = "pasted:";

    /// <summary>
    /// Images pasted into a screen of this process, held in memory until they are sent or let go, so a
    /// pasted image is never written anywhere on the way (a private chat's least of all). Only this
    /// process can name one; a name from anywhere else finds nothing.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Attached> Pasted = new(StringComparer.Ordinal);

    private static int _pastedCount;

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".pdf"] = "application/pdf",
        [".md"] = "text/markdown",
        [".markdown"] = "text/markdown",
        [".csv"] = "text/csv",
        [".json"] = "application/json",
        [".txt"] = "text/plain",
        [".log"] = "text/plain",
        [".xml"] = "text/xml",
        [".yaml"] = "text/plain",
        [".yml"] = "text/plain",
        [".toml"] = "text/plain",
        [".ini"] = "text/plain",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".css"] = "text/css",
        [".sql"] = "text/plain",
        [".cs"] = "text/plain",
        [".csproj"] = "text/xml",
        [".py"] = "text/plain",
        [".js"] = "text/javascript",
        [".ts"] = "text/plain",
        [".tsx"] = "text/plain",
        [".jsx"] = "text/plain",
        [".java"] = "text/plain",
        [".kt"] = "text/plain",
        [".c"] = "text/plain",
        [".h"] = "text/plain",
        [".cpp"] = "text/plain",
        [".hpp"] = "text/plain",
        [".rs"] = "text/plain",
        [".go"] = "text/plain",
        [".rb"] = "text/plain",
        [".php"] = "text/plain",
        [".ps1"] = "text/plain",
        [".psm1"] = "text/plain",
        [".sh"] = "text/plain",
        [".bat"] = "text/plain",
        [".cmd"] = "text/plain",
        [".swift"] = "text/plain",
        [".lua"] = "text/plain",
        [".r"] = "text/plain",
    };

    /// <summary>The extensions of the files DipTick attaches, each with its dot: what an open dialog offers.</summary>
    public static IEnumerable<string> Extensions => Types.Keys;

    /// <summary>The media type DipTick gives a file by its name, or null when it is not one DipTick sends.</summary>
    public static string? TypeOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Types.GetValueOrDefault(Path.GetExtension(name));
    }

    /// <summary>Whether a media type is an image.</summary>
    public static bool IsImage(string type) => type.StartsWith("image/", StringComparison.Ordinal);

    /// <summary>
    /// A file the person picked, read and checked: a type DipTick sends, no larger than
    /// <see cref="MostFile"/>, and a text file really text.
    /// </summary>
    /// <exception cref="SafeFileException">It cannot be attached, and why.</exception>
    public static Attached Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.StartsWith(PastedPrefix, StringComparison.Ordinal))
        {
            return Pasted.TryGetValue(path[PastedPrefix.Length..], out var held)
                ? held
                : throw new SafeFileException($"{SafeText.Line(path)}: no image pasted in this window is held under that name; paste it again");
        }

        var name = Path.GetFileName(path);
        if (TypeOf(name) is not { } type)
        {
            throw new SafeFileException($"{name}: DipTick attaches images (PNG, JPEG, GIF, WebP), PDFs and text files");
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw new SafeFileException($"{name}: there is no such file");
            }

            if (info.Length > MostFile)
            {
                throw new SafeFileException($"{name} is {AgentFolders.Size(info.Length)}; a file may be at most {AgentFolders.Size(MostFile)}");
            }

            var bytes = File.ReadAllBytes(path);
            if (!IsImage(type) && type != "application/pdf" && AgentFolders.Text(bytes) is null)
            {
                throw new SafeFileException($"{name} is not UTF-8 text");
            }

            return new Attached(name, type, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SafeFileException($"{name} could not be read: {SafeText.Line(ex.Message)}", ex);
        }
    }

    /// <summary>An image pasted into a screen, held in memory; returns what <c>--attach</c> takes for it.</summary>
    public static string HoldPasted(Attached image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var name = string.Create(CultureInfo.InvariantCulture, $"{Interlocked.Increment(ref _pastedCount)}-{image.Name}");
        Pasted[name] = image;
        return PastedPrefix + name;
    }

    /// <summary>A pasted image let go: sent and kept by its chat, or taken off the message. Anything else is left as it is.</summary>
    public static void LetGo(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.StartsWith(PastedPrefix, StringComparison.Ordinal))
        {
            Pasted.TryRemove(reference[PastedPrefix.Length..], out _);
        }
    }

    /// <summary>What a message of files alone says, so no line of the chat is empty: the files' names.</summary>
    public static string Wordless(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return $"({string.Join(", ", names)})";
    }

    /// <summary>Why a message's files will not go together, or null: more than <see cref="MostMessage"/> in all.</summary>
    public static string? MessageProblem(IReadOnlyList<Attached> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var total = files.Sum(f => (long)f.Bytes.Length);
        return total > MostMessage ? $"the files come to {AgentFolders.Size(total)}; a message's files may be at most {AgentFolders.Size(MostMessage)} together" : null;
    }

    /// <summary>
    /// What one agent is sent of the files, and each file it is not sent with why: its engine's formats
    /// and sizes, whether its model sees images (<paramref name="sees"/>) and reads PDFs (<paramref name="readsPdf"/>).
    /// </summary>
    public static (IReadOnlyList<WireFile> Sent, IReadOnlyList<string> NotSent) For(SeatConfig agent, IReadOnlyList<Attached> files, bool sees, bool readsPdf)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(files);
        var sent = new List<WireFile>();
        var notSent = new List<string>();
        foreach (var file in files)
        {
            if (RefusedFor(agent, file, sees, readsPdf) is { } why)
            {
                notSent.Add($"{file.Name} ({why})");
            }
            else
            {
                sent.Add(new WireFile(file.Name, file.MediaType, file.Bytes));
            }
        }

        return (sent, notSent);
    }

    /// <summary>
    /// The last message of the person's side with the files it takes, and a line saying what was
    /// attached, and what this agent is not sent, so its answer can say so.
    /// </summary>
    public static ChatRequest Attach(ChatRequest request, IReadOnlyList<Attached> files, IReadOnlyList<WireFile> sent, IReadOnlyList<string> notSent)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(sent);
        ArgumentNullException.ThrowIfNull(notSent);
        if (files.Count == 0 || request.Messages.Count == 0 || request.Messages[^1].Role != WireRole.User)
        {
            return request;
        }

        var note = new StringBuilder($"\n\n(Attached: {string.Join(", ", files.Select(f => f.Name))}.");
        if (notSent.Count > 0)
        {
            note.Append(System.Globalization.CultureInfo.InvariantCulture, $" Not sent to you: {string.Join("; ", notSent)}.");
        }

        note.Append(')');
        var last = request.Messages[^1];
        List<WireMessage> messages = [.. request.Messages.Take(request.Messages.Count - 1), last with { Text = last.Text + note, Files = sent }];
        return request with { Messages = messages };
    }

    /// <summary>Why this agent is not sent this file, in words, or null when it is: its engine's formats and sizes, and what its model takes.</summary>
    public static string? RefusedFor(SeatConfig agent, Attached file, bool sees, bool readsPdf)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(file);
        var entry = ProviderCatalogue.Find(agent.Provider);
        return Refused(file, entry?.Shape ?? ProviderShape.OpenAi, entry?.Flavor ?? AnthropicFlavor.None, sees, readsPdf);
    }

    /// <summary>Why this engine is not sent this file, or null when it is.</summary>
    private static string? Refused(Attached file, ProviderShape shape, AnthropicFlavor flavor, bool sees, bool readsPdf)
    {
        if (!IsImage(file.MediaType) && file.MediaType != "application/pdf")
        {
            return null;
        }

        if (IsImage(file.MediaType))
        {
            if (!sees)
            {
                return "this model does not see images";
            }

            // Anthropic: 10 MB in base64 on its own API, AWS and Foundry; 5 MB on Bedrock and Google Cloud.
            var base64 = (file.Bytes.Length + 2) / 3 * 4L;
            return shape switch
            {
                ProviderShape.Anthropic when base64 > (flavor is AnthropicFlavor.Bedrock or AnthropicFlavor.Vertex ? 5 : 10) * 1024L * 1024 => "larger than this platform takes for an image",
                ProviderShape.Gemini when file.MediaType == "image/gif" => "Gemini takes no GIF",
                _ => null,
            };
        }

        return !readsPdf ? "this model is not sent PDFs"
            : shape == ProviderShape.OpenAi ? "PDFs do not go through the OpenAI chat shape"
            : null;
    }
}
