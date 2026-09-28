using System.Globalization;
using System.Text;
using DipTick.Models.Files;
using DipTick.Models.Skills;
using DipTick.Models.Tools;

namespace DipTick.Models.Sandbox;

/// <summary>
/// A skill's script as a run takes it: one of the skill's own files, by
/// its path in the skill (the Agent Skills specification names files "relative paths from the skill root"),
/// a .ps1, .py or .js, which says its language; at most <see cref="MostArgs"/> arguments. Its whole text is
/// what the person is shown before it runs.
/// </summary>
internal static class SkillScript
{
    /// <summary>The most arguments a script is given.</summary>
    public const int MostArgs = 32;

    /// <summary>The most one argument may be, in characters.</summary>
    public const int MostArg = 4096;

    /// <summary>
    /// The script's language, its text, and what the run is given; or why it cannot run: a path that is not
    /// one of the skill's files, a file that is not a script the sandbox runs, or arguments past the caps.
    /// </summary>
    public static (string? Language, string? Text, SkillRun? Run, string? Problem) For(Skill skill, string script, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(skill);
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(args);
        var relative = script.Replace('\\', '/').TrimStart('/');
        if (relative.Split('/').Any(part => part is ".." or "") || relative.Contains(':', StringComparison.Ordinal) || !skill.Files.Contains(relative, StringComparer.Ordinal))
        {
            return (null, null, null, $"{skill.Name} has no file {SafeText.Line(script)}; its files are listed when it is loaded");
        }

        var language = Path.GetExtension(relative).ToLowerInvariantAscii() switch
        {
            ".ps1" => "pwsh",
            ".py" => "python",
            ".js" or ".mjs" or ".cjs" => "node",
            _ => null,
        };
        if (language is null)
        {
            return (null, null, null, $"{relative} is not a script the sandbox runs: a .ps1, .py or .js");
        }

        if (args.Count > MostArgs || args.Any(a => a.Length > MostArg || a.Contains('\0', StringComparison.Ordinal)))
        {
            return (null, null, null, string.Create(CultureInfo.InvariantCulture, $"a script takes at most {MostArgs} arguments, each at most {MostArg} characters"));
        }

        try
        {
            var (bytes, size) = SafeFiles.Read(Path.Combine(skill.Folder, relative), skill.Folder, CodeSandbox.MostCode);
            if (size > bytes.Length)
            {
                return (null, null, null, $"{relative} is larger than 256 KiB, more than a run shows and takes");
            }

            return AgentFolders.Text(bytes) is { } text
                ? (language, text, new SkillRun(skill.Name, skill.Folder, skill.Hash, relative, args), null)
                : (null, null, null, $"{relative} is not text");
        }
        catch (SafeFileException ex)
        {
            return (null, null, null, $"{relative} could not be read: {ex.Message}");
        }
    }

    /// <summary>What such a run is gated as: its language, the skill and the script, and its arguments, in one line.</summary>
    public static string Target(string language, string skill, string script, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var line = SafeText.Line($"{skill}/{script} {string.Join(' ', args.Select(Shown))}".TrimEnd());
        return $"{language}: {(line.Length > 120 ? line[..120] + "…" : line)}";
    }

    /// <summary>What the question shows whole: the skill, the script, each argument, and the script's text.</summary>
    public static string Detail(string skill, string script, IReadOnlyList<string> args, string text)
    {
        ArgumentNullException.ThrowIfNull(args);
        var detail = new StringBuilder();
        detail.Append(CultureInfo.InvariantCulture, $"The skill {skill}'s script {script}, ");
        detail.Append(args.Count == 0 ? "with no arguments" : "with " + string.Join(' ', args.Select(Shown)));
        detail.Append(". The skill's files are there, read-only.\n\n").Append(text);
        return detail.ToString();
    }

    /// <summary>An argument as the question shows it: in brackets, so a space or nothing at all is seen.</summary>
    private static string Shown(string argument) => $"[{SafeText.Line(argument)}]";
}
