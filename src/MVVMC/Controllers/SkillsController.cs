using System.Globalization;
using DipTick.Models;
using DipTick.Models.Skills;
using DipTick.Models.Tools;

namespace DipTick.Controllers;

/// <summary>
/// <c>diptick skills</c>: the skills in DipTick's own folder, each with whether
/// it is approved as it is; one shown in full; one added from a folder or a zip, shown and approved;
/// one approved again after a change; one removed; and every one checked against its approval.
/// </summary>
internal static class SkillsController
{
    private const string Usage = "usage: diptick skills [list] | show <name> | add <folder|zip> [--replace] | approve <name> | remove <name> | check";

    public static int Run(IReadOnlyList<string> args, Cli cli)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(cli);
        var sub = args.Count > 1 ? args[1] : "list";
        var words = new VerbArgs(args, 2, cli.LiteralFrom);
        return sub switch
        {
            "list" or "check" => List(cli, words, check: sub == "check"),
            "show" => Show(cli, words),
            "add" => Add(cli, words),
            "approve" => Approve(cli, words),
            "remove" => Remove(cli, words),
            _ => cli.Fail(Usage, Exit.Usage),
        };
    }

    private static int List(Cli cli, VerbArgs words, bool check)
    {
        if (words.Problem is not null || words.Words().Count > 0)
        {
            return cli.Fail(Usage, Exit.Usage);
        }

        var (skills, problems) = SkillStore.All(cli.Config);
        var rows = skills.Select(s => (Skill: s, State: SkillStore.StateOf(cli.Config, s))).ToList();
        if (cli.WriteJson(new
        {
            folder = SkillStore.FolderFor(cli.Config),
            skills = rows.Select(r => new { name = r.Skill.Name, description = r.Skill.Description, state = r.State.ToString().ToLowerInvariantAscii(), scripts = r.Skill.HasScripts, files = r.Skill.Files.Count, warnings = r.Skill.Warnings }),
            problems,
        }))
        {
            return check && rows.Any(r => r.State != SkillState.Approved) ? Exit.Failed : Exit.Ok;
        }

        if (rows.Count == 0)
        {
            cli.Note($"No skills yet. 'diptick skills add <folder|zip>' adds one; they are kept in {SkillStore.FolderFor(cli.Config) ?? "the folder beside your settings file"}.");
        }

        foreach (var (skill, state) in rows)
        {
            var about = SafeText.Line(skill.Description);
            cli.Data(SafeText.Line($"  {skill.Name,-24} {SkillStore.Words(state)}{(skill.HasScripts ? " · has scripts: run only in the sandbox" : string.Empty)}"));
            cli.Data($"      {(about.Length > 100 ? about[..100] + "…" : about)}");
            foreach (var warning in skill.Warnings)
            {
                cli.Note($"  {skill.Name}: {warning}");
            }
        }

        foreach (var problem in problems)
        {
            cli.Note(problem);
        }

        return check && rows.Any(r => r.State != SkillState.Approved) ? Exit.Failed : Exit.Ok;
    }

    private static int Show(Cli cli, VerbArgs words)
    {
        if (One(cli, words) is not { } skill)
        {
            return Exit.Usage;
        }

        if (!cli.WriteJson(new { name = skill.Name, description = skill.Description, license = skill.License, compatibility = skill.Compatibility, metadata = skill.Metadata, body = skill.Body, files = skill.Files, scripts = skill.HasScripts, state = SkillStore.StateOf(cli.Config, skill).ToString().ToLowerInvariantAscii(), folder = skill.Folder }))
        {
            Describe(cli, skill);
        }

        return Exit.Ok;
    }

    /// <summary><c>add</c>: read strictly, copied into DipTick's folder, shown whole, and approved with your yes; with no yes it stays, unapproved.</summary>
    private static int Add(Cli cli, VerbArgs words)
    {
        var replace = words.Switch("--replace");
        var rest = words.Words();
        if (words.Problem is not null || rest.Count != 1)
        {
            return cli.Fail(Usage, Exit.Usage);
        }

        if (cli.Flags.DryRun)
        {
            try
            {
                var preview = Directory.Exists(rest[0]) ? SkillStore.Read(rest[0], strict: true) : null;
                cli.Note(preview is null ? $"[dry run] would add the skill in {SafeText.Line(rest[0])}; nothing added." : $"[dry run] would add {preview.Name}; nothing added.");
                return Exit.Ok;
            }
            catch (SkillException ex)
            {
                return cli.Fail($"{SafeText.Line(rest[0])} is not a skill DipTick adds: {ex.Message}.");
            }
        }

        Skill skill;
        int invisible;
        try
        {
            (skill, invisible) = SkillStore.Add(cli.Config, rest[0], replace);
        }
        catch (SkillException ex)
        {
            return cli.Fail($"{SafeText.Line(rest[0])} is not a skill DipTick adds: {ex.Message}{(ex.Message.StartsWith("there is already", StringComparison.Ordinal) ? "; --replace replaces it" : string.Empty)}.");
        }

        if (invisible > 0)
        {
            cli.Note(string.Create(CultureInfo.InvariantCulture, $"Took {invisible} invisible characters out of its SKILL.md: text a person cannot see is where hidden instructions hide."));
        }

        return AskApproval(cli, skill, "Added");
    }

    private static int Approve(Cli cli, VerbArgs words)
    {
        if (One(cli, words) is not { } skill)
        {
            return Exit.Usage;
        }

        if (SkillStore.StateOf(cli.Config, skill) == SkillState.Approved)
        {
            cli.Note($"{skill.Name} is approved as it is.");
            return Exit.Ok;
        }

        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would ask you to approve {skill.Name}; nothing approved.");
            return Exit.Ok;
        }

        return AskApproval(cli, skill, "Read");
    }

    private static int Remove(Cli cli, VerbArgs words)
    {
        if (One(cli, words) is not { } skill)
        {
            return Exit.Usage;
        }

        var answer = cli.Confirm($"Remove the skill {skill.Name}, and DipTick's copy of it?");
        if (answer != Answer.Yes)
        {
            return cli.NotConfirmed(answer, $"removing {skill.Name}");
        }

        try
        {
            SkillStore.Remove(cli.Config, skill);
        }
        catch (SkillException ex)
        {
            return cli.Fail($"{skill.Name}: {ex.Message}.");
        }

        cli.Note($"Removed {skill.Name}.");
        return Exit.Ok;
    }

    /// <summary>The whole skill shown, then your yes asked for; the yes keeps its hash as approved.</summary>
    private static int AskApproval(Cli cli, Skill skill, string done)
    {
        Describe(cli, skill);
        var answer = cli.Confirm($"Approve {skill.Name}, as shown, for chats you switch it on in?");
        if (answer != Answer.Yes)
        {
            cli.Note($"{done} {skill.Name}, not approved: no chat is offered it until 'diptick skills approve {skill.Name}'.");
            return answer == Answer.NoOneToAsk ? cli.NotConfirmed(answer, $"approving {skill.Name}") : Exit.Ok;
        }

        try
        {
            SkillStore.Approve(cli.Config, skill.Name, skill.Hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return cli.Fail($"the approval could not be kept: {SafeText.Line(ex.Message)}");
        }

        if (!cli.WriteJson(new { approved = skill.Name, hash = skill.Hash }))
        {
            cli.Note($"{skill.Name} is approved. 'diptick chat skills add {skill.Name}' switches it on in a chat.");
        }

        return Exit.Ok;
    }

    /// <summary>A skill in full on stderr, as the person reads it before a yes: its fields, its files, and its SKILL.md body.</summary>
    private static void Describe(Cli cli, Skill skill)
    {
        cli.Note($"── {skill.Name} ──");
        cli.Note($"What it is for: {SafeText.Line(skill.Description)}");
        if (skill.License is { } license)
        {
            cli.Note($"License: {SafeText.Line(license)}");
        }

        if (skill.Compatibility is { } compatibility)
        {
            cli.Note($"Needs: {SafeText.Line(compatibility)}");
        }

        cli.Note($"Files ({skill.Files.Count.ToString(CultureInfo.InvariantCulture)}): {string.Join(", ", skill.Files.Take(40).Select(SafeText.Line))}{(skill.Files.Count > 40 ? ", …" : string.Empty)}");
        if (skill.HasScripts)
        {
            cli.Note("It holds scripts. They run only in DipTick's sandbox, with the skill's files read-only beside them, through run_code or 'diptick run --skill', each run asked.");
        }

        cli.Note("── SKILL.md ──");
        cli.Note(SafeText.Block(skill.Body));
        cli.Note("── end ──");
    }

    private static Skill? One(Cli cli, VerbArgs words)
    {
        var rest = words.Words();
        if (words.Problem is not null || rest.Count != 1)
        {
            cli.Fail(Usage, Exit.Usage);
            return null;
        }

        if (SkillStore.Find(cli.Config, rest[0]) is { } skill)
        {
            return skill;
        }

        cli.Fail($"no skill {SafeText.Line(rest[0])}; 'diptick skills' lists them.", Exit.Usage);
        return null;
    }
}
