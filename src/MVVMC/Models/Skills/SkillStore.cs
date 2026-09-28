using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DipTick.Models.Skills;

/// <summary>Whether a skill can be used: approved as it is, changed since its yes, or never approved.</summary>
internal enum SkillState
{
    Approved,
    Changed,
    NotApproved,
}

/// <summary>
/// One skill as DipTick reads it (the Agent Skills specification, agentskills.io): its name and what it is
/// for, its optional fields, its body, its folder and files, whether it holds scripts, its hash now, and
/// what was not as the specification asks.
/// </summary>
internal sealed record Skill(
    string Name,
    string Description,
    string? License,
    string? Compatibility,
    IReadOnlyDictionary<string, string> Metadata,
    string Body,
    string Folder,
    IReadOnlyList<string> Files,
    bool HasScripts,
    string Hash,
    IReadOnlyList<string> Warnings);

/// <summary>What could not be done with a skill, in words.</summary>
internal sealed class SkillException : Exception
{
    public SkillException()
    {
    }

    public SkillException(string message)
        : base(message)
    {
    }

    public SkillException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// DipTick's skills: <c>skills\&lt;name&gt;</c> beside the settings file in use and
/// nowhere else, each approved by its hash in <c>skills.lock.json</c>. Loading is lenient, as the guide for
/// clients asks (a name that does not match its folder is a warning; a skill with no description is left
/// out); adding is strict, since what is added is DipTick's own copy.
/// </summary>
internal static partial class SkillStore
{
    public const string FolderName = "skills";
    public const string LockName = "skills.lock.json";

    /// <summary>The most files one skill may hold.</summary>
    public const int MostFiles = 500;

    /// <summary>The most bytes one skill may hold.</summary>
    public const long MostBytes = 10L * 1024 * 1024;

    /// <summary>The most of a SKILL.md read.</summary>
    private const int MostSkillFile = 1024 * 1024;

    private static readonly JsonWriterOptions Indented = new() { Indented = true };

    /// <summary>Where the skills are: beside the settings file in use; null with none.</summary>
    public static string? FolderFor(MasterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Path is { } settings && Path.GetDirectoryName(Path.GetFullPath(settings)) is { } folder ? Path.Combine(folder, FolderName) : null;
    }

    /// <summary>Where the approved hashes are: beside the settings file in use; null with none.</summary>
    public static string? LockFor(MasterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Path is { } settings && Path.GetDirectoryName(Path.GetFullPath(settings)) is { } folder ? Path.Combine(folder, LockName) : null;
    }

    /// <summary>Every skill in the folder, read leniently; what could not be read, in words.</summary>
    public static (IReadOnlyList<Skill> Skills, IReadOnlyList<string> Problems) All(MasterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var skills = new List<Skill>();
        var problems = new List<string>();
        if (FolderFor(config) is not { } root || !Directory.Exists(root))
        {
            return (skills, problems);
        }

        foreach (var folder in Directory.EnumerateDirectories(root).OrderBy(f => f, StringComparer.Ordinal))
        {
            if (!File.Exists(Path.Combine(folder, "SKILL.md")))
            {
                continue;
            }

            try
            {
                var skill = Read(folder, strict: false);
                if (skills.Any(s => s.Name == skill.Name))
                {
                    problems.Add($"two skills are called {skill.Name}; {Path.GetFileName(folder)} is left out.");
                    continue;
                }

                skills.Add(skill);
            }
            catch (SkillException ex)
            {
                problems.Add($"{Path.GetFileName(folder)}: {ex.Message}");
            }
        }

        return (skills, problems);
    }

    /// <summary>A skill by name, as it is on disk now; null when there is none.</summary>
    public static Skill? Find(MasterConfig config, string name) => All(config).Skills.FirstOrDefault(s => s.Name == name);

    /// <summary>Whether a skill is approved as it is now.</summary>
    public static SkillState StateOf(MasterConfig config, Skill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        return Approved(config).TryGetValue(skill.Name, out var hash)
            ? hash == skill.Hash ? SkillState.Approved : SkillState.Changed
            : SkillState.NotApproved;
    }

    /// <summary>The state in words.</summary>
    public static string Words(SkillState state) => state switch
    {
        SkillState.Approved => "approved",
        SkillState.Changed => "changed since your yes: 'diptick skills approve' shows it again",
        _ => "waiting for your yes",
    };

    /// <summary>The approved hashes, by skill; none when there is no lock yet or it cannot be read.</summary>
    public static IReadOnlyDictionary<string, string> Approved(MasterConfig config)
    {
        var approved = new Dictionary<string, string>(StringComparer.Ordinal);
        if (LockFor(config) is not { } path || !File.Exists(path))
        {
            return approved;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (doc.RootElement.TryGetProperty("skills", out var skills) && skills.ValueKind == JsonValueKind.Object)
            {
                foreach (var skill in skills.EnumerateObject().Where(s => s.Value.ValueKind == JsonValueKind.String))
                {
                    approved[skill.Name] = skill.Value.GetString()!;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A lock that cannot be read approves nothing: every skill waits for a yes again.
            approved.Clear();
        }

        return approved;
    }

    /// <summary>A skill's hash kept as approved, or taken away with null.</summary>
    /// <exception cref="IOException">The lock could not be written.</exception>
    public static void Approve(MasterConfig config, string name, string? hash)
    {
        var path = LockFor(config) ?? throw new IOException("no settings file is in use, so there is nowhere to keep approvals");
        var approved = new Dictionary<string, string>(Approved(config), StringComparer.Ordinal);
        if (hash is null)
        {
            approved.Remove(name);
        }
        else
        {
            approved[name] = hash;
        }

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, Indented))
        {
            json.WriteStartObject();
            json.WriteStartObject("skills");
            foreach (var (skill, value) in approved.OrderBy(a => a.Key, StringComparer.Ordinal))
            {
                json.WriteString(skill, value);
            }

            json.WriteEndObject();
            json.WriteEndObject();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
        File.WriteAllBytes(temporary, buffer.ToArray());
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// A skill folder read: its <c>SKILL.md</c> parsed, its files listed and hashed. <paramref name="strict"/>
    /// holds it to the specification (adding); otherwise a name that does not match is a warning (loading).
    /// </summary>
    /// <exception cref="SkillException">It is not a skill DipTick can use, and why.</exception>
    public static Skill Read(string folder, bool strict)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var file = Path.Combine(folder, "SKILL.md");
        string text;
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists)
            {
                throw new SkillException("it has no SKILL.md");
            }

            if (info.Length > MostSkillFile)
            {
                throw new SkillException("its SKILL.md is larger than 1 MB");
            }

            text = File.ReadAllText(file, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SkillException($"its SKILL.md could not be read: {SafeText.Line(ex.Message)}", ex);
        }

        var (fields, metadata, body) = Frontmatter(text);
        var warnings = new List<string>();
        var name = fields.GetValueOrDefault("name") ?? string.Empty;
        var description = fields.GetValueOrDefault("description") ?? string.Empty;
        if (description.Trim().Length == 0)
        {
            throw new SkillException("its description is missing, and a skill is chosen by its description");
        }

        if (NameProblem(name) is { } bad)
        {
            if (strict || name.Length == 0)
            {
                throw new SkillException(bad);
            }

            warnings.Add(bad);
        }

        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        if (name != folderName)
        {
            if (strict)
            {
                throw new SkillException($"its name, {SafeText.Line(name)}, must match its folder, {SafeText.Line(folderName)}");
            }

            warnings.Add($"its name, {SafeText.Line(name)}, does not match its folder, {SafeText.Line(folderName)}");
        }

        if (description.Length > 1024)
        {
            if (strict)
            {
                throw new SkillException("its description is longer than 1024 characters");
            }

            warnings.Add("its description is longer than 1024 characters");
        }

        if (fields.GetValueOrDefault("compatibility") is { Length: > 500 })
        {
            warnings.Add("its compatibility is longer than 500 characters");
        }

        var (files, hash) = Contents(folder);
        var hasScripts = files.Any(f => f.StartsWith("scripts/", StringComparison.Ordinal) || Path.GetExtension(f) is ".py" or ".ps1" or ".sh" or ".js" or ".bat" or ".cmd");
        return new Skill(name, description.Trim(), fields.GetValueOrDefault("license"), fields.GetValueOrDefault("compatibility"), metadata, body, Path.GetFullPath(folder), files, hasScripts, hash, warnings);
    }

    /// <summary>Why a name is not one the specification takes, or null: 1 to 64 of a-z, 0-9 and -, no - at either end, no --.</summary>
    public static string? NameProblem(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Length is < 1 or > 64 || !NamePattern().IsMatch(name) || name.Contains("--", StringComparison.Ordinal)
            ? "a skill's name is 1 to 64 of a-z, 0-9 and -, with no - at either end and no --"
            : null;
    }

    /// <summary>
    /// A skill added from a folder or a zip: read strictly, copied into DipTick's skills folder (a zip
    /// never writes outside it), its SKILL.md's invisible characters taken out. Returns the copy, read, and
    /// how many invisible characters went. Nothing is approved here.
    /// </summary>
    /// <exception cref="SkillException">It is not one DipTick adds, and why.</exception>
    public static (Skill Skill, int Invisible) Add(MasterConfig config, string source, bool replace)
    {
        ArgumentNullException.ThrowIfNull(source);
        var root = FolderFor(config) ?? throw new SkillException("no settings file is in use (--config none), so there is nowhere to keep skills");
        var staging = Path.Combine(root, ".adding-" + Guid.NewGuid().ToString("N"));
        try
        {
            string folder;
            if (File.Exists(source) && Path.GetExtension(source).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                folder = Unzip(source, staging);
            }
            else if (Directory.Exists(source))
            {
                folder = Path.Combine(staging, Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(source))));
                Copy(Path.GetFullPath(source), folder);
            }
            else
            {
                throw new SkillException($"{SafeText.Line(source)} is not a skill's folder or a .zip");
            }

            var invisible = Clean(Path.Combine(folder, "SKILL.md"));
            var skill = Read(folder, strict: true);
            var target = Path.Combine(root, skill.Name);
            if (Directory.Exists(target))
            {
                if (!replace)
                {
                    throw new SkillException($"there is already a skill called {skill.Name}");
                }

                Directory.Delete(target, recursive: true);
            }

            Directory.Move(folder, target);
            return (Read(target, strict: true), invisible);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new SkillException($"it could not be added: {SafeText.Line(ex.Message)}", ex);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    /// <summary>A skill's folder removed, and its approval with it.</summary>
    /// <exception cref="SkillException">It could not be removed.</exception>
    public static void Remove(MasterConfig config, Skill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        try
        {
            var root = Path.GetFullPath(FolderFor(config) ?? throw new SkillException("no settings file is in use"));
            if (!skill.Folder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new SkillException("it is not in DipTick's skills folder");
            }

            Directory.Delete(skill.Folder, recursive: true);
            Approve(config, skill.Name, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SkillException($"it could not be removed: {SafeText.Line(ex.Message)}", ex);
        }
    }

    /// <summary>
    /// The specification's frontmatter as DipTick reads it: between the first two <c>---</c> lines,
    /// <c>key: value</c>, a quoted value, a <c>|</c> or <c>&gt;</c> block, and <c>metadata</c>'s one level
    /// of <c>key: value</c>; an unquoted value holding a colon runs to its line's end. The rest is the body.
    /// </summary>
    internal static (Dictionary<string, string> Fields, Dictionary<string, string> Metadata, string Body) Frontmatter(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return (fields, metadata, text.Trim());
        }

        var end = Array.FindIndex(lines, 1, l => l.Trim() == "---");
        if (end < 0)
        {
            return (fields, metadata, text.Trim());
        }

        for (var i = 1; i < end; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#') || char.IsWhiteSpace(line[0]))
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (key == "metadata" && value.Length == 0)
            {
                for (i++; i < end && lines[i].Length > 0 && char.IsWhiteSpace(lines[i][0]); i++)
                {
                    var inner = lines[i].Trim();
                    var at = inner.IndexOf(':', StringComparison.Ordinal);
                    if (at > 0)
                    {
                        metadata[inner[..at].Trim()] = Unquote(inner[(at + 1)..].Trim());
                    }
                }

                i--;
                continue;
            }

            if (value is "|" or ">" or "|-" or ">-" or "|+" or ">+")
            {
                var block = new List<string>();
                for (i++; i < end && (lines[i].Length == 0 || char.IsWhiteSpace(lines[i][0])); i++)
                {
                    block.Add(lines[i].Trim());
                }

                i--;
                fields[key] = value.StartsWith('|') ? string.Join('\n', block).Trim() : string.Join(' ', block.Where(b => b.Length > 0)).Trim();
                continue;
            }

            fields[key] = Unquote(value);
        }

        return (fields, metadata, string.Join('\n', lines.Skip(end + 1)).Trim());
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            var inner = value[1..^1];
            return value[0] == '\'' ? inner.Replace("''", "'", StringComparison.Ordinal) : inner.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\n", "\n", StringComparison.Ordinal);
        }

        return value;
    }

    /// <summary>
    /// Every file of a skill by its path from the skill's folder, with / between parts, and the hash of them
    /// all: what a yes is given to, and what a run's copy of the skill is checked against.
    /// </summary>
    /// <exception cref="SkillException">It holds more than a skill may.</exception>
    internal static (IReadOnlyList<string> Files, string Hash) Contents(string folder)
    {
        var root = Path.GetFullPath(folder);
        var files = new List<string>();
        long total = 0;
        using var all = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }).OrderBy(p => p, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            files.Add(relative);
            if (files.Count > MostFiles)
            {
                throw new SkillException(string.Create(CultureInfo.InvariantCulture, $"it holds more than {MostFiles} files"));
            }

            var bytes = File.ReadAllBytes(path);
            total += bytes.Length;
            if (total > MostBytes)
            {
                throw new SkillException("it is larger than 10 MB");
            }

            all.AppendData(Encoding.UTF8.GetBytes(relative + "\0"));
            all.AppendData(SHA256.HashData(bytes));
        }

        return (files, Convert.ToHexStringLower(all.GetHashAndReset()));
    }

    /// <summary>
    /// A zip's one skill written under <paramref name="staging"/>, never outside it; returns the skill's
    /// folder: the one folder at the zip's root that holds a SKILL.md, or, with SKILL.md at the root, a
    /// folder named as the skill names itself.
    /// </summary>
    private static string Unzip(string zip, string staging)
    {
        var unpacked = Path.Combine(staging, "unpacked");
        Directory.CreateDirectory(unpacked);
        using (var archive = ZipFile.OpenRead(zip))
        {
            if (archive.Entries.Count > MostFiles * 2)
            {
                throw new SkillException(string.Create(CultureInfo.InvariantCulture, $"the zip holds more than {MostFiles * 2} entries"));
            }

            long total = 0;
            var root = Path.GetFullPath(unpacked) + Path.DirectorySeparatorChar;
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                var target = Path.GetFullPath(Path.Combine(unpacked, name));
                if (name.Length == 0 || name.StartsWith('/') || name.Contains(':', StringComparison.Ordinal) || name.Split('/').Any(part => part == "..") || !target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new SkillException($"the zip names {SafeText.Line(entry.FullName)}, which would land outside its folder");
                }

                if (name.EndsWith('/'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                // The bytes that come out are counted, not the size the zip claims for them.
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
                var buffer = new byte[81920];
                int read;
                while ((read = input.Read(buffer)) > 0)
                {
                    total += read;
                    if (total > MostBytes)
                    {
                        throw new SkillException("the zip unpacks to more than 10 MB");
                    }

                    output.Write(buffer, 0, read);
                }
            }
        }

        var atRoot = Path.Combine(unpacked, "SKILL.md");
        if (File.Exists(atRoot))
        {
            var named = Frontmatter(File.ReadAllText(atRoot, Encoding.UTF8)).Fields.GetValueOrDefault("name") ?? string.Empty;
            if (NameProblem(named) is { } bad)
            {
                throw new SkillException(bad);
            }

            var folder = Path.Combine(staging, named);
            Directory.Move(unpacked, folder);
            return folder;
        }

        var folders = Directory.GetDirectories(unpacked).Where(d => File.Exists(Path.Combine(d, "SKILL.md"))).ToList();
        return folders.Count == 1 ? folders[0] : throw new SkillException("the zip does not hold one skill: a SKILL.md at its root, or in one folder at its root");
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(from, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            if (++count > MostFiles)
            {
                throw new SkillException(string.Create(CultureInfo.InvariantCulture, $"it holds more than {MostFiles} files"));
            }

            var target = Path.Combine(to, Path.GetRelativePath(from, path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target, overwrite: false);
        }
    }

    /// <summary>Invisible characters taken out of a file, the count returned.</summary>
    private static int Clean(string file)
    {
        if (!File.Exists(file))
        {
            return 0;
        }

        var text = File.ReadAllText(file, Encoding.UTF8);
        var count = SafeText.InvisibleIn(text);
        if (count > 0)
        {
            File.WriteAllText(file, SafeText.ForModel(text), new UTF8Encoding(false));
        }

        return count;
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
