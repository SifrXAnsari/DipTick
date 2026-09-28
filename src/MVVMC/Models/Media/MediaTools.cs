using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using DipTick.Models.Chat.Store;
using DipTick.Models.Files;
using DipTick.Models.Harness;
using DipTick.Models.Tools;

namespace DipTick.Models.Media;

/// <summary>
/// What keeps a made image or video with its chat. A kept chat's image is an
/// attachment row with its bytes, and a copy in the chat's folder that Open and Show in folder use; a
/// kept chat's video is a file in the chat's folder that its row names. A private chat keeps both in this
/// process's memory only, and writes nothing: Save… writes one where the person says.
/// </summary>
internal static class Made
{
    /// <summary>An image kept with the chat by <paramref name="author"/>, named by when it was made; returns its id (0 outside a chat).</summary>
    public static long KeepImage(MasterConfig config, IChatStore store, string session, string author, MadeImage image, bool isPrivate)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(image);
        if (session.Length == 0)
        {
            return 0;
        }

        var extension = image.MediaType switch { "image/jpeg" => "jpg", "image/webp" => "webp", _ => "png" };
        var name = string.Create(CultureInfo.InvariantCulture, $"image-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}");
        string? saved = null;
        if (!isPrivate)
        {
            // The copy on disk is for opening it; the row keeps the bytes, so a copy that cannot be written loses nothing.
            var path = Path.Combine(AgentFolders.ChatFolder(config, session), name);
            try
            {
                SafeFiles.Write(path, config.AgentFilesFolder, image.Bytes, overwrite: false);
                saved = path;
            }
            catch (SafeFileException)
            {
                saved = null;
            }
        }

        return store.AddAttachment(session, new StoredAttachment(0, null, author, name, image.MediaType, image.Bytes.Length, Hash(image.Bytes), image.Bytes, saved, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// A video kept with the chat: in a kept chat, written to the chat's folder through
    /// <see cref="SafeFiles"/> and named by its row (its bytes are not copied into the store); in a private
    /// chat, its bytes in the row, in memory. Returns where it was written (null in a private chat) and the id.
    /// </summary>
    /// <exception cref="SafeFileException">The file could not be written safely.</exception>
    public static (string? Path, long Id) KeepVideo(MasterConfig config, IChatStore store, string session, string author, MadeVideo video, bool isPrivate)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(video);
        var name = string.Create(CultureInfo.InvariantCulture, $"video-{DateTime.Now:yyyyMMdd-HHmmss}{video.Extension}");
        if (isPrivate)
        {
            var kept = session.Length == 0 ? 0 : store.AddAttachment(session, new StoredAttachment(0, null, author, name, video.MediaType, video.Bytes.Length, Hash(video.Bytes), video.Bytes, null, DateTimeOffset.UtcNow));
            return (null, kept);
        }

        var path = Path.Combine(AgentFolders.ChatFolder(config, session), name);
        SafeFiles.Write(path, config.AgentFilesFolder, video.Bytes, overwrite: false);
        var id = session.Length == 0 ? 0 : store.AddAttachment(session, new StoredAttachment(0, null, author, name, video.MediaType, video.Bytes.Length, Hash(video.Bytes), null, path, DateTimeOffset.UtcNow));
        return (path, id);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

/// <summary>
/// <c>make_image</c> (<c>media.make</c>, Ask by default: it costs money and makes content): one image from
/// the provider <c>media.images</c> names, kept with the answer in the chat.
/// </summary>
internal sealed class MakeImageTool : IAgentTool
{
    public string Name => "make_image";

    public string Description =>
        "Make one image from a description, with the person's image provider; it costs money on their key and they are asked first unless they allowed it. " +
        "The image appears in the chat with the answer. Describe it fully: subject, setting, style, light, composition.";

    public JsonElement Parameters { get; } = ToolArgs.Schema(
        ("prompt", "string", "What the image shows, in full.", true),
        ("size", "string", "WIDTHxHEIGHT, like 1024x1024 (the default), 1536x1024 or 1024x1536.", false));

    /// <summary>Images take longer than a minute at high quality.</summary>
    public TimeSpan Limit => TimeSpan.FromMinutes(3);

    public async Task<ToolOutcome> RunAsync(ToolContext context, JsonElement arguments, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (ToolArgs.Text(arguments, "prompt") is not { } prompt)
        {
            return ToolOutcome.Failed("describe the image in 'prompt'");
        }

        var size = ToolArgs.Text(arguments, "size");
        if (!context.Gate.Request("image", Capability.MediaMake, $"image: {Clip(prompt)}"))
        {
            return ToolOutcome.Refused("media.make");
        }

        MadeImage image;
        try
        {
            image = await ImageMaker.MakeAsync(context.Config, prompt, size, null, cancel).ConfigureAwait(false);
        }
        catch (MediaException ex)
        {
            return ToolOutcome.Failed(ex.Message);
        }

        var id = Made.KeepImage(context.Config, context.Store, context.Session, context.Agent.Name, image, context.Private);
        var words = string.Create(CultureInfo.InvariantCulture, $"Made an image ({image.Size}, {image.MediaType}, {AgentFolders.Size(image.Bytes.Length)}) with {image.Provider} {image.Model}; it is in the chat with your answer.");
        return new ToolOutcome(words, string.Create(CultureInfo.InvariantCulture, $"image, {image.Size}, {image.Model}")) { Attachments = id > 0 ? [id] : [] };
    }

    internal static string Clip(string text)
    {
        var line = SafeText.Line(text);
        return line.Length <= 120 ? line : line[..120] + "…";
    }
}

/// <summary>
/// <c>transcribe</c>: the words said in a recording, by the speech server
/// <c>media.speech</c> names. The file is read as <c>read_file</c> reads one (<c>fs.read</c>, inside the
/// folders the person opened, through <see cref="SafeFiles"/>), and sent as a call to a model
/// (<c>model.call</c>, to the server's own address).
/// </summary>
internal sealed class TranscribeTool : IAgentTool
{
    public string Name => "transcribe";

    public string Description =>
        "Turn a recording into text with the person's speech server (whisper.cpp): a WAV, FLAC, MP3 or Ogg file inside a folder the person opened to agents, or in this chat's folder. " +
        "What comes back is the words said.";

    public JsonElement Parameters { get; } = ToolArgs.Schema(("path", "string", "The recording: a full path, or relative to this chat's folder.", true));

    /// <summary>A long recording takes the server a while.</summary>
    public TimeSpan Limit => TimeSpan.FromMinutes(10);

    public async Task<ToolOutcome> RunAsync(ToolContext context, JsonElement arguments, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (ToolArgs.Text(arguments, "path") is not { } asked)
        {
            return ToolOutcome.Failed("give the recording in 'path'");
        }

        try
        {
            var full = AgentFolders.Resolve(context, asked);
            if (SpeechMaker.FileProblem(full) is { } kind)
            {
                return ToolOutcome.Failed(kind);
            }

            if (!context.Gate.Request("read", Capability.FsRead, full))
            {
                return ToolOutcome.Refused("fs.read");
            }

            if (AgentFolders.RootOf(context.Config.Permissions, full) is not { } root)
            {
                return ToolOutcome.Failed("that file is outside every folder agents may use");
            }

            var (bytes, size) = SafeFiles.Read(full, root, SpeechMaker.MostAudio);
            if (size > bytes.Length)
            {
                return ToolOutcome.Failed("the recording is larger than 200 MiB, more than DipTick sends at once");
            }

            if (!context.Gate.Request("transcribe", Capability.ModelCall, SpeechMaker.Target(context.Config)))
            {
                return ToolOutcome.Refused("model.call");
            }

            var text = await SpeechMaker.TranscribeAsync(context.Config, full, bytes, cancel).ConfigureAwait(false);
            var shown = text.Length > AgentTools.MostResult - 256 ? text[..(AgentTools.MostResult - 256)] + "\n[the rest not shown]" : text;
            return new ToolOutcome(
                $"[the words said in {Path.GetFileName(full)}, by {SpeechMaker.Provider(context.Config)}]\n{SafeText.ForModel(shown)}",
                string.Create(CultureInfo.InvariantCulture, $"{SafeText.Line(Path.GetFileName(full))}: {text.Length} characters"));
        }
        catch (SafeFileException ex)
        {
            return ToolOutcome.Failed(ex.Message);
        }
        catch (MediaException ex)
        {
            return ToolOutcome.Failed(ex.Message);
        }
    }
}

/// <summary>
/// <c>make_video</c> (<c>media.make</c>, Ask by default): one video from the provider <c>media.video</c>
/// names (Veo, through the Gemini API, unless the person chose another), saved to the chat's folder. Veo
/// takes from 11 seconds to 6 minutes, so this tool's own cap is 7 minutes, inside the round's.
/// </summary>
internal sealed class MakeVideoTool : IAgentTool
{
    public string Name => "make_video";

    public string Description =>
        "Make one short video from a description, with the person's video provider (Google's Veo unless they chose another); it costs money on their key and they are asked first unless they allowed it. " +
        "It takes from a few seconds to several minutes. The video is saved to this chat's folder and shows in the chat.";

    public JsonElement Parameters { get; } = ToolArgs.Schema(
        ("prompt", "string", "What happens in the video, and any dialogue or sound, in full.", true),
        ("seconds", "integer", "4, 6 or 8 (the default).", false),
        ("resolution", "string", "720p (the default), 1080p or 4k; 1080p and 4k are 8 seconds only.", false),
        ("portrait", "boolean", "Upright (9:16) instead of wide (16:9).", false));

    public TimeSpan Limit => TimeSpan.FromMinutes(7);

    public async Task<ToolOutcome> RunAsync(ToolContext context, JsonElement arguments, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (ToolArgs.Text(arguments, "prompt") is not { } prompt)
        {
            return ToolOutcome.Failed("describe the video in 'prompt'");
        }

        var seconds = (int)(ToolArgs.Number(arguments, "seconds") ?? 8);
        var resolution = ToolArgs.Text(arguments, "resolution");
        var portrait = arguments.TryGetProperty("portrait", out var p) && p.ValueKind == JsonValueKind.True;

        // Veo's own lengths and resolutions; another provider's model takes its own fields (media.videoInput).
        if (VideoMaker.Provider(context.Config) == "google" && VideoMaker.Problem(seconds, resolution ?? "720p", VideoMaker.Model(context.Config)) is { } bad)
        {
            return ToolOutcome.Failed(bad);
        }

        if (!context.Gate.Request("video", Capability.MediaMake, $"video: {MakeImageTool.Clip(prompt)}"))
        {
            return ToolOutcome.Refused("media.make");
        }

        try
        {
            var video = await VideoMaker.MakeAsync(context.Config, prompt, seconds, resolution, portrait, null, cancel).ConfigureAwait(false);
            var (path, id) = Made.KeepVideo(context.Config, context.Store, context.Session, context.Agent.Name, video, context.Private);
            var where = path is null ? "kept in this private chat's memory" : $"saved to {path}";
            var words = VideoMaker.Provider(context.Config) switch
            {
                "google" => string.Create(CultureInfo.InvariantCulture, $"Made a video ({video.Seconds} s, {video.Resolution}, {(video.Portrait ? "9:16" : "16:9")}, {AgentFolders.Size(video.Bytes.Length)}) with {video.Model}, {where}; it shows in the chat."),
                "sdcpp" or "comfyui" => string.Create(CultureInfo.InvariantCulture, $"Made a video ({video.Seconds} s asked, {video.Resolution}, {video.MediaType}, {AgentFolders.Size(video.Bytes.Length)}) with {video.Model}, {where}; it shows in the chat."),
                _ => string.Create(CultureInfo.InvariantCulture, $"Made a video ({video.MediaType}, {AgentFolders.Size(video.Bytes.Length)}) with {video.Model}, {where}; it shows in the chat. Its model took the words and the person's own fields for it (media.videoInput), not the length or the shape asked here."),
            };
            return new ToolOutcome(words, string.Create(CultureInfo.InvariantCulture, $"video, {video.Seconds} s, {video.Resolution}")) { Attachments = id > 0 ? [id] : [] };
        }
        catch (MediaException ex)
        {
            return ToolOutcome.Failed(ex.Message);
        }
        catch (SafeFileException ex)
        {
            return ToolOutcome.Failed($"the video was made, but could not be saved: {ex.Message}");
        }
    }
}
