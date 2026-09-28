using System.Globalization;
using DipTick.Models;
using DipTick.Models.Chat.Store;
using DipTick.Models.Files;
using DipTick.Models.Harness;
using DipTick.Models.Media;

namespace DipTick.Controllers;

/// <summary>
/// <c>diptick image "&lt;words&gt;"</c> and <c>diptick video "&lt;words&gt;"</c>: an image or
/// a video made by the provider <c>media.images</c> or <c>media.video</c> names, written to the file given,
/// else to Documents\DipTick (images, videos), or with <c>--session</c> kept in that chat as the person's.
/// Each costs money on the person's own key, so each goes through the gate as <c>media.make</c>, as an
/// agent's does, and is recorded. What was made is never thrown away for want of a yes: a file that is
/// already there is kept, and the new one takes the next free name beside it.
/// </summary>
internal static class MediaController
{
    private const string ImageUsage = "usage: diptick image [--provider openai|google|nscale|replicate|fal|wavespeed|comfyui|sdcpp] [--size WxH] [--session <id|last> [--private]] [--out <file>] [--] <words…>";
    private const string VideoUsage = "usage: diptick video [--seconds 4|6|8] [--resolution 720p|1080p|4k] [--portrait] [--session <id|last> [--private]] [--out <file>] [--] <words…>";
    private const string TranscribeUsage = "usage: diptick transcribe [--out <file>] [--] <recording>";

    public static int Image(IReadOnlyList<string> args, Cli cli)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(cli);
        var words = new VerbArgs(args, 1, cli.LiteralFrom);
        var provider = words.Option("--provider");
        var size = words.Option("--size");
        var session = words.Option("--session");
        var isPrivate = words.Switch("--private");
        var output = words.Option("--out");
        var prompt = string.Join(' ', words.Words()).Trim();
        if (words.Problem is not null || prompt.Length == 0 || (provider is not null && !ImageMaker.Providers.Contains(provider, StringComparer.Ordinal)) || (isPrivate && session is null))
        {
            return cli.Fail(ImageUsage, Exit.Usage);
        }

        if (ImageMaker.Problem(cli.Config, provider) is { } missing)
        {
            return cli.Fail(missing);
        }

        if (size is not null && (provider ?? ImageMaker.Provider(cli.Config)) == "openai" && ImageMaker.SizeProblem(size) is { } bad)
        {
            return cli.Fail($"--size {bad}.", Exit.Usage);
        }

        if (size is not null && ImageMaker.Parse(size) is null)
        {
            return cli.Fail("--size is WIDTHxHEIGHT, like 1024x1024.", Exit.Usage);
        }

        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would make an image of: {Clip(prompt)}; nothing sent.");
            return Exit.Ok;
        }

        if (!Allowed(cli, $"image: {Clip(prompt)}"))
        {
            return Exit.Failed;
        }

        MadeImage image;
        try
        {
            image = ImageMaker.MakeAsync(cli.Config, prompt, size, provider, cli.Stop).GetAwaiter().GetResult();
        }
        catch (MediaException ex)
        {
            return cli.Fail(ex.Message);
        }
        catch (OperationCanceledException)
        {
            return cli.Fail("stopped; no image was kept.");
        }

        var extension = image.MediaType switch { "image/jpeg" => ".jpg", "image/webp" => ".webp", _ => ".png" };
        var what = $"an image ({image.Size}, {image.Model})";
        if (session is not null && output is null)
        {
            return KeepInChat(cli, session, isPrivate, what, store => (Made.KeepImage(cli.Config, store.Store, store.Id, Author.Person, image, isPrivate), null));
        }

        return Write(cli, output, image.Bytes, "images", extension, what);
    }

    public static int Video(IReadOnlyList<string> args, Cli cli)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(cli);
        var words = new VerbArgs(args, 1, cli.LiteralFrom);
        var secondsText = words.Option("--seconds") ?? "8";
        var resolution = words.Option("--resolution");
        var portrait = words.Switch("--portrait");
        var session = words.Option("--session");
        var isPrivate = words.Switch("--private");
        var output = words.Option("--out");
        var prompt = string.Join(' ', words.Words()).Trim();
        if (words.Problem is not null || prompt.Length == 0 || !int.TryParse(secondsText, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || (isPrivate && session is null))
        {
            return cli.Fail(VideoUsage, Exit.Usage);
        }

        if (VideoMaker.Problem(cli.Config) is { } missing)
        {
            return cli.Fail(missing);
        }

        var provider = VideoMaker.Provider(cli.Config);
        if (provider == "google" && VideoMaker.Problem(seconds, resolution ?? "720p", VideoMaker.Model(cli.Config)) is { } bad)
        {
            return cli.Fail(bad + ".", Exit.Usage);
        }

        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would make a video of: {Clip(prompt)}; nothing sent.");
            return Exit.Ok;
        }

        if (!Allowed(cli, $"video: {Clip(prompt)}"))
        {
            return Exit.Failed;
        }

        MadeVideo video;
        try
        {
            cli.StopsCleanly();
            var who = Models.Providers.ProviderCatalogue.Find(provider ?? "google")?.Name ?? provider;
            cli.Note(provider == "google" ? "Veo is making it: from 11 seconds to 6 minutes. Ctrl+C stops waiting." : $"{who} is making it. Ctrl+C stops waiting.");
            video = VideoMaker.MakeAsync(cli.Config, prompt, seconds, resolution, portrait, waited => cli.Note(string.Create(CultureInfo.InvariantCulture, $"  {waited.TotalSeconds:0} s…")), cli.Stop).GetAwaiter().GetResult();
        }
        catch (MediaException ex)
        {
            return cli.Fail(ex.Message);
        }
        catch (OperationCanceledException)
        {
            return cli.Fail(provider == "google"
                ? "stopped waiting; Google may still finish it, and keeps it two days, but DipTick no longer follows it."
                : "stopped waiting; the provider may still finish it, but DipTick no longer follows it.");
        }

        var what = $"a video ({video.Seconds} s, {video.Resolution}, {video.Model})";
        if (session is not null && output is null)
        {
            return KeepInChat(cli, session, isPrivate, what, store =>
            {
                var (path, id) = Made.KeepVideo(cli.Config, store.Store, store.Id, Author.Person, video, isPrivate);
                return (id, path);
            });
        }

        return Write(cli, output, video.Bytes, "videos", video.Extension, what);
    }

    /// <summary>
    /// <c>diptick transcribe &lt;recording&gt;</c>: the words said in it, by the
    /// speech server <c>media.speech</c> names, through <c>model.call</c> as a question to a model goes.
    /// </summary>
    public static int Transcribe(IReadOnlyList<string> args, Cli cli)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(cli);
        var words = new VerbArgs(args, 1, cli.LiteralFrom);
        var output = words.Option("--out");
        var rest = words.Words();
        if (words.Problem is not null || rest.Count != 1)
        {
            return cli.Fail(TranscribeUsage, Exit.Usage);
        }

        if (SpeechMaker.Problem(cli.Config) is { } missing)
        {
            return cli.Fail(missing);
        }

        var file = rest[0];
        if (SpeechMaker.FileProblem(file) is { } kind)
        {
            return cli.Fail($"{kind}.", Exit.Usage);
        }

        byte[] audio;
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > SpeechMaker.MostAudio)
            {
                return cli.Fail(info.Exists ? "the recording is larger than 200 MiB, more than DipTick sends at once." : $"there is no file {SafeText.Line(file)}.", Exit.Usage);
            }

            audio = File.ReadAllBytes(info.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return cli.Fail($"{SafeText.Line(file)} could not be read: {SafeText.Line(ex.Message)}");
        }

        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would send {SafeText.Line(Path.GetFileName(file))} to {SpeechMaker.Target(cli.Config)}; nothing sent.");
            return Exit.Ok;
        }

        var gate = new Gate(cli.Config.Permissions, cli.GateFlags, AuditLog.ForThisUser(), cli.Who, cli.Err, cli.Question);
        if (!gate.Request("transcribe", Capability.ModelCall, SpeechMaker.Target(cli.Config)))
        {
            cli.Err.WriteLine("diptick: model.call was not allowed; 'diptick policy' shows the rule, and 'diptick audit' the decision.");
            return Exit.Failed;
        }

        string text;
        try
        {
            cli.StopsCleanly();
            text = SpeechMaker.TranscribeAsync(cli.Config, file, audio, cli.Stop).GetAwaiter().GetResult();
        }
        catch (MediaException ex)
        {
            return cli.Fail(ex.Message);
        }
        catch (OperationCanceledException)
        {
            return cli.Fail("stopped.");
        }

        string? saved = null;
        if (output is not null)
        {
            try
            {
                saved = Place(cli, output);
                Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                File.WriteAllText(saved, text + "\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return cli.Fail($"the words could not be written to {SafeText.Line(output)}: {SafeText.Line(ex.Message)}");
            }
        }

        if (!cli.WriteJson(new { file = Path.GetFullPath(file), text, saved }))
        {
            if (saved is null)
            {
                cli.WriteText(text + "\n");
            }
            else
            {
                cli.Note($"The words are in {saved}.");
            }
        }

        return Exit.Ok;
    }

    /// <summary>The person's own media.make, through the gate like an agent's, and recorded.</summary>
    private static bool Allowed(Cli cli, string target)
    {
        var gate = new Gate(cli.Config.Permissions, cli.GateFlags, AuditLog.ForThisUser(), cli.Who, cli.Err, cli.Question);

        // The person asks for it here, so a yes is theirs: the rule still decides, and every call is recorded.
        if (!gate.Request("make", Capability.MediaMake, target))
        {
            cli.Err.WriteLine("diptick: media.make was not allowed; 'diptick policy' shows the rule, and 'diptick audit' the decision.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Kept in a chat as the person's (<c>--session</c>): a line saying what was made, numbered, with the
    /// file under it. A private chat is this process's memory (<c>--private</c>).
    /// </summary>
    private static int KeepInChat(Cli cli, string session, bool isPrivate, string what, Func<(IChatStore Store, string Id), (long Id, string? Path)> keep)
    {
        (long Id, string? Path) kept;
        int seq;
        string id;
        try
        {
            using var store = ChatStores.Open(cli.Config, isPrivate);
            if (store.Find(session) is not { } found)
            {
                return cli.Fail($"no chat '{SafeText.Line(session)}'; {what} was made and is not kept. 'diptick chat list' shows the chats.");
            }

            id = found.Id;
            kept = keep((store, id));
            seq = store.Notice(id, 0, $"You made {what}.");
            store.NumberAttachments(id, [kept.Id], seq);
        }
        catch (SafeFileException ex)
        {
            return cli.Fail($"{what} was made, and could not be kept: {ex.Message}");
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            return cli.Fail($"{what} was made, and the session store could not be used: {ex.Message}");
        }

        if (!cli.WriteJson(new { session = id, file = kept.Id, seq, saved = kept.Path }))
        {
            cli.Note(kept.Path is { } path
                ? $"Kept {what} in {id} as file {kept.Id.ToString(CultureInfo.InvariantCulture)}: {path}."
                : $"Kept {what} in {id} as file {kept.Id.ToString(CultureInfo.InvariantCulture)}.");
        }

        return Exit.Ok;
    }

    /// <summary>
    /// Written to <c>--out</c>, else to Documents\DipTick\<paramref name="folder"/>. A file already there is
    /// replaced only after a yes; without one, the new file takes the next free name beside it, since what
    /// was made cost money.
    /// </summary>
    private static int Write(Cli cli, string? output, byte[] bytes, string folder, string extension, string what)
    {
        var target = output ?? Path.Combine(cli.Config.AgentFilesFolder, folder, string.Create(CultureInfo.InvariantCulture, $"{folder[..^1]}-{DateTime.Now:yyyyMMdd-HHmmss}{extension}"));
        try
        {
            target = Place(cli, target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return cli.Fail($"{what} was made, and could not be written: {SafeText.Line(ex.Message)}");
        }

        if (!cli.WriteJson(new { saved = target, bytes = bytes.Length }))
        {
            cli.Note($"Made {what}: {target} ({AgentFolders.Size(bytes.Length)}).");
        }

        return Exit.Ok;
    }

    /// <summary>Where a file goes: the path itself, or, when a file is there and the person does not say to replace it, the first free name beside it.</summary>
    /// <exception cref="ArgumentException">The path is not one.</exception>
    private static string Place(Cli cli, string path)
    {
        var target = Path.GetFullPath(path);
        return File.Exists(target) && cli.Confirm($"Replace {target}?") != Answer.Yes ? FreeName(target) : target;
    }

    /// <summary>The first of "name (2).ext", "name (3).ext"… that is not taken.</summary>
    private static string FreeName(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var n = 2; ; n++)
        {
            var next = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{stem} ({n}){extension}"));
            if (!File.Exists(next))
            {
                return next;
            }
        }
    }

    private static string Clip(string text)
    {
        var line = SafeText.Line(text);
        return line.Length <= 120 ? line : line[..120] + "…";
    }
}
