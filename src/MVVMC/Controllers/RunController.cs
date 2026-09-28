using System.Globalization;
using System.Text;
using DipTick.Models;
using DipTick.Models.Harness;
using DipTick.Models.Sandbox;

namespace DipTick.Controllers;

/// <summary>
/// <c>diptick run [--lang pwsh|python|node] [--seconds &lt;n&gt;] &lt;file&gt;|-</c>:
/// your own code, run in DipTick's sandbox the way an agent's is: through <c>shell.run</c>, which always
/// asks and shows the whole code; its output printed, its errors on stderr, and the folder it wrote in named.
/// <c>--skill &lt;name&gt; &lt;script&gt; [--arg a]...</c> runs an approved skill's script the same way.
/// </summary>
internal static class RunController
{
    private const string Usage = "usage: diptick run [--lang pwsh|python|node] [--seconds <n>] [--] <file>|- | run --skill <name> [--seconds <n>] <script> [--arg <a>]...";

    public static int Run(IReadOnlyList<string> args, Cli cli)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(cli);
        var words = new VerbArgs(args, 1, cli.LiteralFrom);
        var language = words.Option("--lang");
        var secondsText = words.Option("--seconds");
        var skillName = words.Option("--skill");
        var scriptArgs = words.All("--arg");
        var fromStdin = words.Dash;
        var rest = words.Words();
        if (words.Problem is not null || (fromStdin ? rest.Count != 0 : rest.Count != 1) || (skillName is not null && (fromStdin || language is not null)) || (skillName is null && scriptArgs.Count > 0))
        {
            return cli.Fail(Usage, Exit.Usage);
        }

        var seconds = CodeSandbox.DefaultWall;
        if (secondsText is not null)
        {
            if (!int.TryParse(secondsText, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || n > CodeSandbox.MostWall.TotalSeconds)
            {
                return cli.Fail("--seconds takes 1 to 600.", Exit.Usage);
            }

            seconds = TimeSpan.FromSeconds(n);
        }

        if (skillName is not null)
        {
            return RunSkill(cli, skillName, rest[0], scriptArgs, seconds);
        }

        string code;
        if (fromStdin)
        {
            if (cli.Ask is not null)
            {
                return cli.Fail("a screen has no standard input to read; name a file.", Exit.Usage);
            }

            var buffer = new char[CodeSandbox.MostCode + 1];
            var read = Console.In.ReadBlock(buffer, 0, buffer.Length);
            if (read > CodeSandbox.MostCode)
            {
                return cli.Fail("the code on stdin is longer than 256 KiB.", Exit.Usage);
            }

            code = new string(buffer, 0, read);
        }
        else
        {
            try
            {
                var info = new FileInfo(rest[0]);
                if (!info.Exists || info.Length > CodeSandbox.MostCode)
                {
                    return cli.Fail(info.Exists ? "the file is larger than 256 KiB." : $"there is no file {SafeText.Line(rest[0])}.", Exit.Usage);
                }

                code = File.ReadAllText(info.FullName, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return cli.Fail($"{SafeText.Line(rest[0])} could not be read: {SafeText.Line(ex.Message)}");
            }

            language ??= Path.GetExtension(rest[0]).ToUpperInvariant() switch
            {
                ".PS1" => "pwsh",
                ".PY" => "python",
                ".JS" or ".MJS" or ".CJS" => "node",
                _ => null,
            };
        }

        if (language is null || !CodeSandbox.Languages.Contains(language))
        {
            return cli.Fail($"--lang takes {string.Join(", ", CodeSandbox.Languages)}; the file's own extension says it for .ps1, .py and .js.", Exit.Usage);
        }

        if (CodeSandbox.Find(language).Problem is { } missing)
        {
            return cli.Fail($"{missing}.");
        }

        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would run {language} code in the sandbox; nothing run.");
            return Exit.Ok;
        }

        return Execute(cli, language, code, RunCodeTool.Target(language, code), code, seconds, skill: null);
    }

    /// <summary><c>--skill</c>: an approved skill's script, by its path in the skill, with its arguments.</summary>
    private static int RunSkill(Cli cli, string name, string script, IReadOnlyList<string> args, TimeSpan seconds)
    {
        if (Models.Skills.SkillStore.Find(cli.Config, name) is not { } skill)
        {
            return cli.Fail($"there is no skill {SafeText.Line(name)}; 'diptick skills' lists them.", Exit.Usage);
        }

        if (Models.Skills.SkillStore.StateOf(cli.Config, skill) is var state && state != Models.Skills.SkillState.Approved)
        {
            return cli.Fail($"{skill.Name} is {Models.Skills.SkillStore.Words(state)}.");
        }

        var (language, text, run, problem) = SkillScript.For(skill, script, args);
        if (problem is not null)
        {
            return cli.Fail($"{problem}.", Exit.Usage);
        }

        if (CodeSandbox.Find(language!).Problem is { } missing)
        {
            return cli.Fail($"{missing}.");
        }

        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would run {skill.Name}'s {run!.Script} in the sandbox; nothing run.");
            return Exit.Ok;
        }

        return Execute(cli, language!, string.Empty, SkillScript.Target(language!, skill.Name, run!.Script!, args), SkillScript.Detail(skill.Name, run.Script!, args, text!), seconds, run);
    }

    /// <summary>Asked through shell.run, then run, then said: its output, its errors on stderr, how it ended and its folder.</summary>
    private static int Execute(Cli cli, string language, string code, string target, string detail, TimeSpan seconds, SkillRun? skill)
    {
        var gate = new Gate(cli.Config.Permissions, cli.GateFlags, AuditLog.ForThisUser(), cli.Who, cli.Err, cli.Question);
        if (!gate.Request("run", Capability.ShellRun, target, vouchedBy: null, detail: detail))
        {
            cli.Err.WriteLine("diptick: shell.run was not allowed; 'diptick policy' shows the rule (Standard keeps it Never), and 'diptick audit' the decision.");
            return Exit.Failed;
        }

        var folder = Path.Combine(CodeSandbox.ScratchFolder, RunCodeTool.RunName());
        cli.StopsCleanly();
        CodeRun run;
        try
        {
            run = CodeSandbox.RunAsync(language, code, seconds, folder, cli.Stop, skill).GetAwaiter().GetResult();
        }
        catch (SandboxException ex)
        {
            return cli.Fail($"{ex.Message}.");
        }

        if (cli.WriteJson(new { language = run.Language, exit = run.ExitCode, ended = run.Ended, seconds = run.Took.TotalSeconds, output = run.Output, outputDropped = run.OutputDropped, errors = run.Errors, errorsDropped = run.ErrorsDropped, folder = run.Folder, made = run.Made.Select(m => new { name = m.Name, size = m.Size }) }))
        {
            return run.ExitCode == 0 ? Exit.Ok : Exit.Failed;
        }

        cli.WriteText(run.Output);
        if (run.OutputDropped > 0)
        {
            cli.Note(string.Create(CultureInfo.InvariantCulture, $"[{run.OutputDropped} more characters of output not kept]"));
        }

        if (run.Errors.Length > 0)
        {
            cli.Err.Write(run.Errors);
        }

        var how = run.ExitCode is { } exit ? string.Create(CultureInfo.InvariantCulture, $"exit {exit}") : run.Ended ?? "ended";
        cli.Note(string.Create(CultureInfo.InvariantCulture, $"{run.Language}: {how}, after {run.Took.TotalSeconds:0.0} s; its folder: {run.Folder}"));
        return run.ExitCode == 0 ? Exit.Ok : Exit.Failed;
    }
}
