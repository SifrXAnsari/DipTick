using System.Globalization;
using System.Text;
using System.Text.Json;
using DipTick.Models.Files;
using DipTick.Models.Harness;
using DipTick.Models.Skills;
using DipTick.Models.Tools;

namespace DipTick.Models.Sandbox;

/// <summary>
/// <c>run_code</c>: a short program run in DipTick's sandbox, the person asked
/// first every time with the whole code shown (<c>shell.run</c>, which never goes without a person). What
/// comes back is its exit code, how long it took, its output and errors, and the files it made. In a kept
/// chat the run's folder is kept in the chat's folder; in a private chat it is removed after the run. With
/// <c>skill</c>, a skill on in the chat and approved as it is: its <c>script</c> runs with
/// its <c>args</c>, or the code runs with the skill's files beside it, read-only, at SKILL_DIR.
/// </summary>
internal sealed class RunCodeTool(IReadOnlyList<Skill>? usable = null) : IAgentTool
{
    private readonly IReadOnlyList<Skill> _usable = usable ?? [];

    public string Name => "run_code";

    public string Description =>
        "Run a short program in DipTick's sandbox and get back its exit code, output and errors: " + string.Join(", ", CodeSandbox.Present()) + ". " +
        "The sandbox has no network and none of the person's files; the program can write only in its own folder (in PowerShell, the drive Run:), and the files it makes are listed. " +
        "The person sees the whole code and is asked before every run. It stops at 60 seconds unless you ask for more, up to 600, and at 60 seconds of CPU." +
        (_usable.Count > 0 ? " With skill, one of the skills on in this chat: its script runs with args, or your code runs with the skill's files at SKILL_DIR, read-only." : string.Empty);

    public JsonElement Parameters { get; } = Schema(usable ?? []);

    /// <summary>The longest a run may take, and a little more to start and end it.</summary>
    public TimeSpan Limit => CodeSandbox.MostWall + TimeSpan.FromSeconds(30);

    public async Task<ToolOutcome> RunAsync(ToolContext context, JsonElement arguments, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(context);
        var language = ToolArgs.Text(arguments, "language");
        var code = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var skillName = ToolArgs.Text(arguments, "skill");
        var script = ToolArgs.Text(arguments, "script");
        List<string> args = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array
            ? [.. a.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : e.GetRawText())]
            : [];

        // A skill: read again now, so one that changed since this turn began is not run.
        SkillRun? skill = null;
        string target;
        string detail;
        if (skillName is not null)
        {
            if (context.Session.Length == 0 || SkillChat.Usable(context.Config, context.Store.ChatSkills(context.Session)).FirstOrDefault(s => s.Name == skillName) is not { } found)
            {
                return ToolOutcome.Failed("no skill of that name is on in this chat and approved as it is");
            }

            if (script is not null)
            {
                if (code is not null)
                {
                    return ToolOutcome.Failed("give 'script' or 'code', not both");
                }

                var (scriptLanguage, text, run, problem) = SkillScript.For(found, script, args);
                if (problem is not null)
                {
                    return ToolOutcome.Failed(problem);
                }

                language = scriptLanguage!;
                code = string.Empty;
                skill = run;
                target = SkillScript.Target(language, found.Name, run!.Script!, args);
                detail = SkillScript.Detail(found.Name, run.Script!, args, text!);
            }
            else
            {
                if (args.Count > 0)
                {
                    return ToolOutcome.Failed("args go with a skill's script");
                }

                skill = new SkillRun(found.Name, found.Folder, found.Hash, null, []);
                target = string.Empty;
                detail = string.Empty;
            }
        }
        else
        {
            if (script is not null || args.Count > 0)
            {
                return ToolOutcome.Failed("'script' and 'args' go with 'skill'");
            }

            target = string.Empty;
            detail = string.Empty;
        }

        if (skill?.Script is null)
        {
            if (language is null || code is null || code.Trim().Length == 0)
            {
                return ToolOutcome.Failed("give 'language' and 'code'");
            }

            if (Encoding.UTF8.GetByteCount(code) > CodeSandbox.MostCode)
            {
                return ToolOutcome.Failed("the code is larger than 256 KiB");
            }

            target = Target(language, code);
            detail = skill is null ? code : $"With the skill {skill.Name}'s files, read-only, at SKILL_DIR.\n\n{code}";
        }

        // What cannot run is said before the person is asked about it.
        if (CodeSandbox.Find(language!).Problem is { } missing)
        {
            return ToolOutcome.Failed(missing);
        }

        var seconds = ToolArgs.Number(arguments, "seconds") is { } asked ? TimeSpan.FromSeconds(Math.Clamp(asked, 1, (long)CodeSandbox.MostWall.TotalSeconds)) : CodeSandbox.DefaultWall;
        if (!context.Gate.Request("run", Capability.ShellRun, target, vouchedBy: null, detail: detail))
        {
            return ToolOutcome.Refused("shell.run");
        }

        if (context.Private || context.Session.Length == 0)
        {
            CodeSandbox.SweepLeftOvers();
        }

        var folder = context.Private || context.Session.Length == 0
            ? Path.Combine(CodeSandbox.ScratchFolder, RunName())
            : Path.Combine(AgentFolders.ChatFolder(context.Config, context.Session), "runs", RunName());
        try
        {
            var run = await CodeSandbox.RunAsync(language!, code!, seconds, folder, cancel, skill).ConfigureAwait(false);
            return Outcome(run, keep: !context.Private && context.Session.Length > 0);
        }
        catch (SandboxException ex)
        {
            return ToolOutcome.Failed(ex.Message);
        }
        finally
        {
            if (context.Private || context.Session.Length == 0)
            {
                Remove(folder);
            }
        }
    }

    /// <summary>What a run is gated as: its language and its first line, so a rule and the record say what ran.</summary>
    internal static string Target(string language, string code)
    {
        var first = SafeText.Line(code.Split('\n', 2)[0].Trim());
        return $"{language}: {(first.Length > 120 ? first[..120] + "…" : first)}";
    }

    /// <summary>A run's folder name: when it started, to the millisecond.</summary>
    internal static string RunName() => string.Create(CultureInfo.InvariantCulture, $"run-{DateTime.Now:yyyyMMdd-HHmmss-fff}");

    /// <summary>A run as the model reads it: how it ended, then its output and errors, then what it made.</summary>
    internal static ToolOutcome Outcome(CodeRun run, bool keep)
    {
        ArgumentNullException.ThrowIfNull(run);
        var text = new StringBuilder();
        var how = run.ExitCode is { } exit ? string.Create(CultureInfo.InvariantCulture, $"exit {exit}") : run.Ended ?? "ended";
        text.Append(CultureInfo.InvariantCulture, $"{run.Language}: {how}, after {run.Took.TotalSeconds:0.0} s.\n");
        text.Append("--- output ---\n").Append(run.Output.Length == 0 ? "(none)" : run.Output).Append('\n');
        if (run.OutputDropped > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"[{run.OutputDropped} more characters of output not kept]\n");
        }

        if (run.Errors.Length > 0)
        {
            text.Append("--- errors ---\n").Append(run.Errors).Append('\n');
            if (run.ErrorsDropped > 0)
            {
                text.Append(CultureInfo.InvariantCulture, $"[{run.ErrorsDropped} more characters of errors not kept]\n");
            }
        }

        if (run.Made.Count > 0)
        {
            text.Append(keep ? $"--- files it made, kept in {run.Folder} ---\n" : "--- files it made (removed after the run, as this chat keeps nothing) ---\n");
            foreach (var (name, size) in run.Made)
            {
                text.Append(CultureInfo.InvariantCulture, $"{SafeText.Line(name)} ({AgentFolders.Size(size)})\n");
            }
        }

        var content = text.ToString();
        if (content.Length > AgentTools.MostResult)
        {
            content = content[..AgentTools.MostResult] + "\n[cut]";
        }

        var summary = string.Create(CultureInfo.InvariantCulture, $"{run.Language} · {how} · {run.Took.TotalSeconds:0.0} s");
        return run.ExitCode is 0 ? new ToolOutcome(content, summary) : new ToolOutcome(content, summary, ToolEnd.Failed);
    }

    private static void Remove(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still held as the job ends: the folder is DipTick's own, and the next private run sweeps it (CodeSandbox.SweepLeftOvers).
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
            json.WriteStartObject("language");
            json.WriteString("type", "string");
            json.WriteString("description", usable.Count > 0 ? "pwsh (PowerShell 7), python or node, whichever this PC has; not needed with a skill's script." : "pwsh (PowerShell 7), python or node, whichever this PC has.");
            json.WriteStartArray("enum");
            foreach (var language in CodeSandbox.Present())
            {
                json.WriteStringValue(language);
            }

            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteStartObject("code");
            json.WriteString("type", "string");
            json.WriteString("description", "The whole program. It runs in its own empty folder, with no network and no input.");
            json.WriteEndObject();
            json.WriteStartObject("seconds");
            json.WriteString("type", "integer");
            json.WriteString("description", "How long it may run, 1 to 600; 60 when left out.");
            json.WriteEndObject();
            if (usable.Count > 0)
            {
                json.WriteStartObject("skill");
                json.WriteString("type", "string");
                json.WriteString("description", "A skill on in this chat: its files are given to the run, read-only, at SKILL_DIR.");
                json.WriteStartArray("enum");
                foreach (var skill in usable)
                {
                    json.WriteStringValue(skill.Name);
                }

                json.WriteEndArray();
                json.WriteEndObject();
                json.WriteStartObject("script");
                json.WriteString("type", "string");
                json.WriteString("description", "One of the skill's scripts to run, by its path in the skill, like scripts/extract.py; in place of code.");
                json.WriteEndObject();
                json.WriteStartObject("args");
                json.WriteString("type", "array");
                json.WriteString("description", "The script's arguments, at most 32.");
                json.WriteStartObject("items");
                json.WriteString("type", "string");
                json.WriteEndObject();
                json.WriteEndObject();
            }

            json.WriteEndObject();
            if (usable.Count == 0)
            {
                json.WriteStartArray("required");
                json.WriteStringValue("language");
                json.WriteStringValue("code");
                json.WriteEndArray();
            }

            json.WriteBoolean("additionalProperties", false);
            json.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(buffer.ToArray());
        return doc.RootElement.Clone();
    }
}
