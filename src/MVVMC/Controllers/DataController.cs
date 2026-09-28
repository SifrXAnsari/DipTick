using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DipTick.Models;
using DipTick.Models.Chat.Store;
using DipTick.Models.Harness;

namespace DipTick.Controllers;

/// <summary>
/// <c>diptick data</c>: where DipTick keeps each thing, and <c>diptick data export</c>: all of it in
/// one zip, since it is the person's and DipTick keeps nothing it cannot give back.
/// </summary>
/// <remarks>
/// The zip holds <c>README.txt</c>, <c>manifest.json</c> (the version, the time, each file's size and
/// SHA-256), <c>settings/diptick.master.json</c> (no key is ever in it, and the password's hash is
/// replaced by "set" or "not set"), <c>chats/&lt;id&gt;.md</c> and <c>.jsonl</c> for every kept chat,
/// <c>audit/audit.jsonl</c> and <c>audit/verify.txt</c>, and <c>ui.json</c>. The audit key stays
/// behind: Windows ties it to the account. Written with .NET's own System.IO.Compression. A
/// person's act, like <c>chat export</c>: it honours a dry run and never overwrites without a yes.
/// </remarks>
internal static class DataController
{
    private const string Usage = "usage: diptick data | data export <file.zip> [--chats all|none|<id>,...] [--no-audit]";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static int Run(IReadOnlyList<string> args, Cli cli)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(cli);
        var sub = args.Count > 1 ? args[1] : "show";
        return sub switch
        {
            "show" when args.Count <= 2 => Show(cli),
            "export" => Export(new VerbArgs(args, 2, cli.LiteralFrom), cli),
            _ => cli.Fail(Usage, Exit.Usage),
        };
    }

    private static int Show(Cli cli)
    {
        var places = DataPlaces.For(cli.Config, cli.Config.Path);
        if (cli.WriteJson(places.Select(p => new { name = p.Label, path = p.Path, about = p.Help })))
        {
            return Exit.Ok;
        }

        foreach (var place in places)
        {
            // A path in full: a clipped one is no use to go and look at.
            cli.Data(SafeText.Line($"{place.Label.PadRight(16)} {place.Path}"));
            cli.Data(SafeText.Line($"  {place.Help}"));
        }

        cli.Note("'diptick data export <file.zip>' writes all of it into one file; 'diptick docs your-data' says what each is for.");
        return Exit.Ok;
    }

    private static int Export(VerbArgs words, Cli cli)
    {
        var chosen = words.Option("--chats") ?? "all";
        var noAudit = words.Switch("--no-audit");
        var rest = words.Words();
        if (words.Problem is not null || rest.Count != 1)
        {
            return cli.Fail(Usage, Exit.Usage);
        }

        string path;
        try
        {
            path = Path.GetFullPath(rest[0]);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return cli.Fail($"not a path: {SafeText.Line(rest[0])}", Exit.Usage);
        }

        var files = new List<(string Name, byte[] Bytes)>();
        var notes = new List<string>();

        if (cli.Config.Path is { } settings && File.Exists(settings))
        {
            if (Sanitized(settings, cli.Config.UserPasswordHash.Length > 0) is { } bytes)
            {
                files.Add(("settings/diptick.master.json", bytes));
            }
            else
            {
                notes.Add($"The settings file ({settings}) could not be read as JSON, so it is not in here.");
            }
        }
        else
        {
            notes.Add("No settings file was in use, so every setting was its default and none is in here.");
        }

        int chats;
        try
        {
            using var store = ChatStores.Open(cli.Config);
            var sessions = store.List(includeClosed: true);
            var picked = Picked(sessions, chosen, out var unknown);
            if (unknown is not null)
            {
                return cli.Fail($"no chat '{SafeText.Line(unknown)}'. 'diptick chat list --all' shows them.", Exit.Usage);
            }

            foreach (var session in picked)
            {
                var lines = store.Messages(session.Id);
                var name = FileSafe(session.Id);
                files.Add(($"chats/{name}.md", Utf8(ChatController.MarkdownOf(session, lines))));
                files.Add(($"chats/{name}.jsonl", Utf8(ChatController.JsonLinesOf(lines))));
            }

            chats = picked.Count;
            if (!cli.Config.Chat.Save)
            {
                notes.Add("Keep sessions was off (Settings > Chats), so no chat was kept to put in here.");
            }
            else if (chosen == "none")
            {
                notes.Add("Chats were left out (--chats none).");
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            return cli.Fail($"the chats could not be read: {ex.Message}");
        }

        if (!noAudit)
        {
            var log = AuditLog.ForThisUser();
            if (File.Exists(log.Path))
            {
                using (var stream = new FileStream(log.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var copy = new MemoryStream())
                {
                    stream.CopyTo(copy);
                    files.Add(("audit/audit.jsonl", copy.ToArray()));
                }

                var problem = log.Verify();
                files.Add(("audit/verify.txt", Utf8(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Checked {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} by DipTick {CommandController.VersionText}: {(problem is null ? "the chain is whole, keyed and anchored" : $"the chain is broken: {problem}")}.\nIts key (audit.key) stays behind: Windows ties it to the account that wrote it, so the chain can be checked again only there, with 'diptick audit verify'.\n"))));
            }
        }
        else
        {
            notes.Add("The audit log was left out (--no-audit).");
        }

        var ui = Path.Combine(AuditLog.DataFolder(), UiState.FileName);
        if (File.Exists(ui))
        {
            files.Add(("ui.json", File.ReadAllBytes(ui)));
        }

        if (cli.Flags.DryRun)
        {
            cli.Note(string.Create(CultureInfo.InvariantCulture, $"[dry run] would write {files.Count + 2} files ({Chats(chats)}) to {path}; nothing written."));
            return Exit.Ok;
        }

        if (File.Exists(path))
        {
            var answer = cli.Confirm($"{path} exists. Overwrite it?");
            if (answer != Answer.Yes)
            {
                return cli.NotConfirmed(answer, $"overwriting {path}");
            }
        }

        var made = DateTimeOffset.UtcNow;
        files.Insert(0, ("README.txt", Utf8(Readme(made, notes))));
        var manifest = Utf8(JsonSerializer.Serialize(
            new
            {
                diptick = CommandController.VersionText,
                made,
                files = files.Select(f => new { path = f.Name, bytes = f.Bytes.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(f.Bytes)) }),
            },
            Indented));
        files.Insert(1, ("manifest.json", manifest));

        // Written beside itself first, so a zip cut short never takes the name.
        var partial = path + ".partial";
        try
        {
            using (var zip = ZipFile.Open(partial, ZipArchiveMode.Create))
            {
                foreach (var (name, bytes) in files)
                {
                    using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                    entry.Write(bytes);
                }
            }

            File.Move(partial, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(partial);
            }
            catch (Exception gone) when (gone is IOException or UnauthorizedAccessException)
            {
                // Left for the person to see; the message names the file it was writing.
            }

            return cli.Fail($"could not write {path}: {ex.Message}");
        }

        if (!cli.WriteJson(new { path, chats, files = files.Count, bytes = new FileInfo(path).Length }))
        {
            cli.Note(string.Create(CultureInfo.InvariantCulture, $"Wrote {files.Count} files ({Chats(chats)}) to {path}. It holds your chats as plain text: keep it where you would keep them."));
        }

        return Exit.Ok;
    }

    private static string Chats(int count) => count switch
    {
        0 => "no chats",
        1 => "1 chat",
        _ => count.ToString(CultureInfo.InvariantCulture) + " chats",
    };

    /// <summary>The chats <c>--chats</c> names: all, none, or ids separated by commas; <paramref name="unknown"/> is the first id no chat has.</summary>
    private static List<SessionInfo> Picked(IReadOnlyList<SessionInfo> sessions, string chosen, out string? unknown)
    {
        unknown = null;
        if (chosen == "all")
        {
            return [.. sessions];
        }

        if (chosen == "none")
        {
            return [];
        }

        var picked = new List<SessionInfo>();
        foreach (var id in chosen.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (sessions.FirstOrDefault(s => s.Id == id) is not { } session)
            {
                unknown = id;
                return [];
            }

            picked.Add(session);
        }

        return picked;
    }

    /// <summary>The settings file with the password's hash replaced by "set" or "not set". Null when it is not JSON.</summary>
    private static byte[]? Sanitized(string path, bool passwordSet)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (root is not JsonObject settings)
        {
            return null;
        }

        if (settings["user"] is not JsonObject user)
        {
            user = [];
            settings["user"] = user;
        }

        user["passwordHash"] = passwordSet ? "set" : "not set";
        return Utf8(settings.ToJsonString(Indented));
    }

    private static string Readme(DateTimeOffset made, List<string> notes)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"DipTick: all your data\nMade {made.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz} by DipTick {CommandController.VersionText}.\n\n");
        text.Append("What is inside:\n\n");
        text.Append("- settings/diptick.master.json: your settings. No key is ever in it (keys stay in Windows Credential Manager), and the password's hash is replaced by \"set\" or \"not set\".\n");
        text.Append("- chats/<id>.md: each kept chat, to read. chats/<id>.jsonl: the same, one JSON object a line, for programs.\n");
        text.Append("- audit/audit.jsonl: every decision DipTick's gate made. audit/verify.txt: whether its chain was whole when this was made. Its key stays behind: Windows ties it to your account.\n");
        text.Append("- ui.json: what the window remembers: that setup has opened once, and unsent drafts.\n");
        text.Append("- manifest.json: DipTick's version, when this was made, and each file's size and SHA-256, so a changed file shows.\n\n");
        foreach (var note in notes)
        {
            text.Append(note).Append('\n');
        }

        text.Append(notes.Count > 0 ? "\n" : string.Empty);
        text.Append("This file holds your chats as plain text. Keep it where you would keep them.\n");
        return text.ToString();
    }

    private static string FileSafe(string id) => new([.. id.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_')]);

    private static byte[] Utf8(string text) => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
}
