using System.Globalization;
using System.Text;
using System.Text.Json;
using DipTick.Models.Chat.Store;
using DipTick.Models.Files;
using DipTick.Models.Tools;

namespace DipTick.Models.Skills;

/// <summary>The skills of a chat as its agents get them.</summary>
internal static class SkillChat
{
    /// <summary>
    /// The skills on in a chat that can be used now: in DipTick's skills folder and approved as they are.
    /// A skill changed since its yes is offered to nobody.
    /// </summary>
    public static IReadOnlyList<Skill> Usable(MasterConfig config, IReadOnlyList<ChatSkill> on)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(on);
        if (on.Count == 0)
        {
            return [];
        }

        var approved = SkillStore.Approved(config);
        var all = SkillStore.All(config).Skills;
        return [.. on.Select(o => all.FirstOrDefault(s => s.Name == o.Skill)).OfType<Skill>().Where(s => approved.TryGetValue(s.Name, out var hash) && hash == s.Hash)];
    }

    /// <summary>A skill's body for an agent that cannot run code: its scripts are read, not run.</summary>
    public static string Wrapped(Skill skill) => Wrapped(skill, scriptsRun: false);

    /// <summary>
    /// A skill's body as an agent reads it (the guide for clients' structured wrapping): between
    /// <c>&lt;skill_content&gt;</c> marks, with its folder, its files listed but not read, and how its scripts
    /// run where <c>run_code</c> is offered.
    /// </summary>
    public static string Wrapped(Skill skill, bool scriptsRun)
    {
        ArgumentNullException.ThrowIfNull(skill);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"<skill_content name=\"{skill.Name}\">\n");
        text.Append(SafeText.ForModel(skill.Body)).Append("\n\n");
        var others = skill.Files.Where(f => f != "SKILL.md").ToList();
        if (others.Count > 0)
        {
            text.Append("Its other files, which load_skill reads with file, by their path here:\n");
            foreach (var file in others.Take(100))
            {
                text.Append("  ").Append(SafeText.Line(file)).Append('\n');
            }

            if (others.Count > 100)
            {
                text.Append(CultureInfo.InvariantCulture, $"  and {others.Count - 100} more\n");
            }
        }

        if (skill.HasScripts)
        {
            text.Append(scriptsRun
                ? $"Its scripts (.ps1, .py, .js) run in DipTick's sandbox through run_code, with skill \"{skill.Name}\", script as its path here, and args; the person is asked every time. The sandbox has no network, and the skill's files are there, read-only, where SKILL_DIR points.\n"
                : "Its scripts cannot run here, since running code is not allowed in this chat (shell.run): read them as text, and do what they would do with your own tools where you have them.\n");
        }

        text.Append("</skill_content>");
        return text.ToString();
    }
}

/// <summary>
/// <c>load_skill</c>: a skill on in this chat read in full, or one of its text
/// files. It reads only what the person approved and switched on here, so it asks nothing more; each load
/// is a tool call in the chat and the audit log. Its <c>name</c> is limited to the skills it can load, as
/// the guide for clients advises. <paramref name="scriptsRun"/>: <c>run_code</c> is offered too, so what it
/// reads says how a skill's scripts run.
/// </summary>
internal sealed class LoadSkillTool(IReadOnlyList<Skill> usable, bool scriptsRun = false) : IAgentTool
{
    public const string ToolName = "load_skill";

    /// <summary>The most of one of a skill's files read.</summary>
    public const int MostFile = 256 * 1024;

    /// <summary>The skills it can load this turn.</summary>
    public IReadOnlyList<Skill> Usable => usable;

    public string Name => ToolName;

    public string Description =>
        "Read one of the skills switched on in this chat, in full, before you use it: its instructions and the list of its files. " +
        "With file, one of its text files, by its path in the skill's folder (a reference its instructions name). " +
        (scriptsRun ? "A skill's scripts run through run_code, with skill and script." : "A skill's scripts are read here, not run.");

    public JsonElement Parameters { get; } = Schema(usable);

    public Task<ToolOutcome> RunAsync(ToolContext context, JsonElement arguments, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(context);
        var name = ToolArgs.Text(arguments, "name");

        // Read again now: a skill that changed since this turn began is not loaded.
        var skill = name is null ? null : SkillChat.Usable(context.Config, context.Store.ChatSkills(context.Session)).FirstOrDefault(s => s.Name == name);
        if (skill is null)
        {
            return Task.FromResult(ToolOutcome.Failed("no skill of that name is on in this chat and approved as it is"));
        }

        if (ToolArgs.Text(arguments, "file") is not { } file)
        {
            return Task.FromResult(new ToolOutcome(SkillChat.Wrapped(skill, scriptsRun), $"skill {skill.Name}"));
        }

        var relative = file.Replace('\\', '/').TrimStart('/');
        if (relative.Split('/').Any(part => part is ".." or "") || Path.IsPathRooted(relative) || relative.Contains(':', StringComparison.Ordinal) || !skill.Files.Contains(relative, StringComparer.Ordinal))
        {
            return Task.FromResult(ToolOutcome.Failed($"{skill.Name} has no file {SafeText.Line(file)}; its files are listed when it is loaded"));
        }

        try
        {
            var (bytes, size) = SafeFiles.Read(Path.Combine(skill.Folder, relative), skill.Folder, MostFile);
            if (AgentFolders.Text(bytes) is not { } text)
            {
                return Task.FromResult(ToolOutcome.Failed($"{relative} is not text"));
            }

            var cut = size > bytes.Length ? string.Create(CultureInfo.InvariantCulture, $"\n[the rest, {size - bytes.Length} bytes, not shown]") : string.Empty;
            return Task.FromResult(new ToolOutcome($"[{relative}, a file of the skill {skill.Name}]\n{SafeText.ForModel(text)}{cut}", $"skill {skill.Name}: {relative}"));
        }
        catch (SafeFileException ex)
        {
            return Task.FromResult(ToolOutcome.Failed($"{relative} could not be read: {ex.Message}"));
        }
    }

    private static JsonElement Schema(IReadOnlyList<Skill> usable)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("type", "object");
            json.WriteStartObject("properties");
            json.WriteStartObject("name");
            json.WriteString("type", "string");
            json.WriteString("description", "The skill's name, as the list of skills on in this chat gives it.");
            json.WriteStartArray("enum");
            foreach (var skill in usable)
            {
                json.WriteStringValue(skill.Name);
            }

            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteStartObject("file");
            json.WriteString("type", "string");
            json.WriteString("description", "One of the skill's files, by its path in the skill's folder, like references/REFERENCE.md. Leave it out to read the skill itself.");
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteStartArray("required");
            json.WriteStringValue("name");
            json.WriteEndArray();
            json.WriteBoolean("additionalProperties", false);
            json.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(buffer.ToArray());
        return doc.RootElement.Clone();
    }
}
